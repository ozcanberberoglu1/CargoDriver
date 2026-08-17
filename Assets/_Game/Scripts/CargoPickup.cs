using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player-side cargo handling: grab detection, carry target and arm IK.
///
/// This component never touches cargo physics directly. It asks
/// <see cref="NetworkedCargoBody"/> to grab or release, then streams the desired hand
/// pose so whichever client is the writer can servo the body toward it.
/// </summary>
public class CargoPickup : MonoBehaviourPun, IPunObservable
{
    [Header("Grab")]
    public float detectRange = 5f;
    public float grabDistance = 1.5f;
    [SerializeField] private float holdForward = 0.7f;
    [SerializeField] private float holdUp = 0.6f;
    [SerializeField] private LayerMask cargoLayer = ~0;

    [Header("Hold Distance (Scroll)")]
    [SerializeField] private float minHoldDist = 1f;
    [SerializeField] private float maxHoldDist = 4f;
    [SerializeField] private float scrollSpeed = 0.5f;

    [Header("IK")]
    [SerializeField] private float ikBlendSpeed = 10f;

    public float ScrollSpeed
    {
        get => scrollSpeed;
        set => scrollSpeed = value;
    }

    public bool IsRotating => isRotating;

    private Transform rShoulder, rElbow, rHand;
    private Transform lShoulder, lElbow, lHand;
    private float rUpperLen, rLowerLen;
    private float lUpperLen, lLowerLen;

    private float ikWeight;
    private bool isRotating;
    private float snapCooldown;

    // Local-only snap hint bookkeeping (never networked).
    private readonly List<LegoSnap.SnapPreviewHit> previewHits = new List<LegoSnap.SnapPreviewHit>();
    private readonly HashSet<LegoSnapPreview> activePreviews = new HashSet<LegoSnapPreview>();

    private NetworkedCargoBody grabIntent;
    private float currentHoldDist;
    private Quaternion holdRotTarget = Quaternion.identity;
    private Vector3 frozenHoldPos;

    private Vector3 streamedTargetPos;
    private Quaternion streamedTargetRot = Quaternion.identity;

    private int OwnerActor => photonView.Owner != null ? photonView.Owner.ActorNumber : -1;

    /// <summary>The box this player is carrying, valid on every client.</summary>
    private NetworkedCargoBody CarriedBody => NetworkedCargoBody.HeldBy(OwnerActor);

    private void Start()
    {
        rShoulder = FindBone("mixamorig:RightArm");
        rElbow = FindBone("mixamorig:RightForeArm");
        rHand = FindBone("mixamorig:RightHand");

        lShoulder = FindBone("mixamorig:LeftArm");
        lElbow = FindBone("mixamorig:LeftForeArm");
        lHand = FindBone("mixamorig:LeftHand");

        if (rShoulder && rElbow) rUpperLen = Vector3.Distance(rShoulder.position, rElbow.position);
        if (rElbow && rHand) rLowerLen = Vector3.Distance(rElbow.position, rHand.position);
        if (lShoulder && lElbow) lUpperLen = Vector3.Distance(lShoulder.position, lElbow.position);
        if (lElbow && lHand) lLowerLen = Vector3.Distance(lElbow.position, lHand.position);
    }

    private void Update()
    {
        if (!photonView.IsMine) return;

        if (snapCooldown > 0f)
            snapCooldown -= Time.deltaTime;

        ToyController tc = GetComponent<ToyController>();
        if (tc != null && tc.IsPaused) return;

        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        bool pressing = mouse.leftButton.isPressed;
        NetworkedCargoBody carried = CarriedBody;

        if (carried == null && grabIntent == null)
        {
            ClearSnapPreview();
            if (pressing && snapCooldown <= 0f)
                TryStartGrab(tc);
            return;
        }

        if (!pressing)
        {
            ReleaseGrab(carried);
            return;
        }

        NetworkedCargoBody active = carried != null ? carried : grabIntent;
        HandleRotateInput(mouse, active);
        HandleScrollInput(mouse);
        HandleSnapKeys(carried);
        UpdateSnapPreview(carried);

        active = CarriedBody != null ? CarriedBody : grabIntent;
        if (active != null)
            PushHoldTarget(active, tc);
    }

    #region Grab flow

    private void TryStartGrab(ToyController tc)
    {
        bool fps = tc != null && tc.IsFPS;
        Transform hit;

        if (fps)
        {
            hit = FindLookedAtBox();
        }
        else
        {
            hit = FindClosestBox();
            if (hit != null && rHand != null &&
                Vector3.Distance(rHand.position, hit.position) >= grabDistance)
                hit = null;
        }

        if (hit == null) return;

        NetworkedCargoBody body = FindGrabbableBody(hit);
        if (body == null || body.IsHeld) return;

        if (!body.RequestGrab()) return;

        grabIntent = body;
        currentHoldDist = holdForward;
        holdRotTarget = body.transform.rotation;
        isRotating = false;
    }

    private void ReleaseGrab(NetworkedCargoBody carried)
    {
        if (carried != null)
            carried.RequestRelease();

        grabIntent = null;
        isRotating = false;
        ClearSnapPreview();
    }

    /// <summary>
    /// Drives the green/red grid hints on whatever target studs the carried lego is hovering
    /// over. Purely local — runs only under the IsMine guard in Update, and toggles child
    /// GameObjects that are never networked, so remote players see nothing of this.
    /// </summary>
    private void UpdateSnapPreview(NetworkedCargoBody carried)
    {
        if (carried == null || !carried.IsHeld)
        {
            ClearSnapPreview();
            return;
        }

        LegoSnap snap = carried.GetComponent<LegoSnap>();
        if (snap == null)
        {
            ClearSnapPreview();
            return;
        }

        // Wipe last frame's hints first, then relight only what still matches this frame.
        foreach (LegoSnapPreview p in activePreviews)
            if (p != null) p.HideAll();
        activePreviews.Clear();

        snap.EvaluatePreview(previewHits);

        // Two passes so green wins on any stud where the block can actually snap: draw the
        // reds first, then let greens overwrite the same stud.
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantGreen = pass == 1;
            foreach (LegoSnap.SnapPreviewHit hit in previewHits)
            {
                if (hit.green != wantGreen || hit.targetLego == null) continue;

                LegoSnapPreview preview = hit.targetLego.GetComponent<LegoSnapPreview>();
                if (preview == null) continue;

                preview.Show(hit.targetTop, hit.green);
                activePreviews.Add(preview);
            }
        }
    }

    private void ClearSnapPreview()
    {
        if (activePreviews.Count == 0) return;

        foreach (LegoSnapPreview p in activePreviews)
            if (p != null) p.HideAll();
        activePreviews.Clear();
    }

    private void HandleRotateInput(Mouse mouse, NetworkedCargoBody body)
    {
        bool rightPressed = mouse.rightButton.isPressed;

        if (rightPressed && !isRotating)
        {
            isRotating = true;
            if (body != null)
                frozenHoldPos = body.transform.position;
        }
        else if (!rightPressed && isRotating)
        {
            isRotating = false;
        }

        if (!isRotating) return;

        Vector2 delta = mouse.delta.ReadValue();
        float rotX = delta.y * 0.5f;
        float rotY = -delta.x * 0.5f;

        Camera cam = GetComponentInChildren<Camera>();
        Vector3 up = cam != null && cam.isActiveAndEnabled ? cam.transform.up : Vector3.up;
        Vector3 right = cam != null && cam.isActiveAndEnabled ? cam.transform.right : Vector3.right;

        holdRotTarget = Quaternion.AngleAxis(rotY, up) * Quaternion.AngleAxis(rotX, right) * holdRotTarget;
    }

    private void HandleScrollInput(Mouse mouse)
    {
        float scroll = mouse.scroll.y.ReadValue();
        if (Mathf.Abs(scroll) <= 0.01f) return;

        currentHoldDist += scroll * scrollSpeed * Time.deltaTime;
        currentHoldDist = Mathf.Clamp(currentHoldDist, minHoldDist, maxHoldDist);
    }

    private void HandleSnapKeys(NetworkedCargoBody carried)
    {
        if (carried == null) return;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        LegoSnap snap = carried.GetComponent<LegoSnap>();
        if (snap == null) return;

        if (kb.eKey.wasPressedThisFrame)
        {
            bool snapped = false;
            foreach (LegoSnap member in snap.GetAllConnected())
            {
                if (member.TrySnap())
                {
                    snapped = true;
                    break;
                }
            }

            if (snapped)
            {
                ReleaseGrab(CarriedBody);
                snapCooldown = 1f;
            }
        }
        else if (kb.xKey.wasPressedThisFrame)
        {
            snap.DetachFromParent();
        }
        else if (kb.zKey.wasPressedThisFrame)
        {
            snap.DetachAll();
        }
    }

    /// <summary>
    /// Computes where the carried box should be and hands it to the body. The writer may
    /// be a different machine, so the same value also goes out on the network stream.
    /// </summary>
    private void PushHoldTarget(NetworkedCargoBody body, ToyController tc)
    {
        Vector3 holdPos;

        if (isRotating)
        {
            holdPos = frozenHoldPos;
        }
        else
        {
            Camera cam = GetComponentInChildren<Camera>();
            bool fps = tc != null && tc.IsFPS;

            if (fps && cam != null && cam.isActiveAndEnabled)
            {
                holdPos = cam.transform.position + cam.transform.forward * currentHoldDist;
            }
            else
            {
                holdPos = transform.position
                          + transform.forward * currentHoldDist
                          + Vector3.up * holdUp;
            }
        }

        streamedTargetPos = holdPos;
        streamedTargetRot = holdRotTarget;

        body.SetHoldTarget(holdPos, holdRotTarget);
    }

    #endregion

    #region Box lookup

    private bool IsCargoBox(Transform t)
    {
        if (t.CompareTag("CargoBox")) return true;
        if (t.GetComponentInParent<NetworkedCargoBody>() != null) return true;
        return false;
    }

    private NetworkedCargoBody FindGrabbableBody(Transform hit)
    {
        LegoSnap snap = hit.GetComponent<LegoSnap>();
        if (snap == null) snap = hit.GetComponentInParent<LegoSnap>();

        if (snap != null)
        {
            NetworkedCargoBody rootBody = snap.GetRoot().GetComponent<NetworkedCargoBody>();
            if (rootBody != null) return rootBody;
        }

        return hit.GetComponentInParent<NetworkedCargoBody>();
    }

    private Transform FindLookedAtBox()
    {
        Camera cam = GetComponentInChildren<Camera>();
        if (cam == null || !cam.isActiveAndEnabled) return null;

        Ray ray = new(cam.transform.position, cam.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, detectRange, cargoLayer))
        {
            if (IsCargoBox(hit.collider.transform))
                return hit.collider.transform;
        }
        return null;
    }

    private Transform FindClosestBox()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, detectRange, cargoLayer);
        Transform best = null;
        float bestDist = float.MaxValue;

        foreach (Collider col in hits)
        {
            if (!IsCargoBox(col.transform)) continue;
            float d = Vector3.Distance(transform.position, col.transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = col.transform;
            }
        }
        return best;
    }

    #endregion

    #region IK

    private void LateUpdate()
    {
        if (rShoulder == null) return;

        Vector3 target = Vector3.zero;
        bool wantIK = false;

        NetworkedCargoBody carried = CarriedBody;
        if (carried != null)
        {
            target = carried.transform.position;
            wantIK = true;
        }
        else if (photonView.IsMine)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.isPressed)
            {
                ToyController tc = GetComponent<ToyController>();
                bool fps = tc != null && tc.IsFPS;
                Transform box = fps ? FindLookedAtBox() : FindClosestBox();
                if (box != null)
                {
                    float maxReach = rUpperLen + rLowerLen;
                    if (Vector3.Distance(rShoulder.position, box.position) <= maxReach + 0.2f)
                    {
                        target = box.position;
                        wantIK = true;
                    }
                }
            }
        }

        ikWeight = Mathf.MoveTowards(ikWeight, wantIK ? 1f : 0f, Time.deltaTime * ikBlendSpeed);

        if (ikWeight > 0.01f)
        {
            SolveTwoBoneIK(rShoulder, rElbow, rHand, rUpperLen, rLowerLen, target, ikWeight, true);
            SolveTwoBoneIK(lShoulder, lElbow, lHand, lUpperLen, lLowerLen, target, ikWeight, false);
        }
    }

    private void SolveTwoBoneIK(Transform root, Transform mid, Transform tip,
        float upperL, float lowerL, Vector3 target, float weight, bool isRight)
    {
        if (root == null || mid == null || tip == null) return;

        Vector3 rootPos = root.position;
        Vector3 toTarget = target - rootPos;
        float dist = toTarget.magnitude;

        if (dist < 0.01f) return;

        float maxReach = upperL + lowerL - 0.01f;
        float minReach = Mathf.Abs(upperL - lowerL) + 0.01f;
        dist = Mathf.Clamp(dist, minReach, maxReach);
        Vector3 targetDir = toTarget.normalized;

        float cosA = (upperL * upperL + dist * dist - lowerL * lowerL)
                     / (2f * upperL * dist);
        cosA = Mathf.Clamp(cosA, -1f, 1f);
        float angleA = Mathf.Acos(cosA);

        // Stable bend direction: always use character's down-forward
        Vector3 stableHint = -transform.up + transform.forward * 0.3f;
        Vector3 bendAxis = Vector3.Cross(targetDir, stableHint).normalized;

        if (bendAxis.sqrMagnitude < 0.001f)
            bendAxis = Vector3.Cross(targetDir, transform.right).normalized;

        Vector3 newUpperDir = Quaternion.AngleAxis(angleA * Mathf.Rad2Deg, bendAxis) * targetDir;

        Vector3 curUpperDir = mid.position - root.position;
        Quaternion rootRot = Quaternion.FromToRotation(curUpperDir, newUpperDir) * root.rotation;
        root.rotation = Quaternion.Slerp(root.rotation, rootRot, weight);

        Vector3 curLowerDir = tip.position - mid.position;
        Vector3 wantLowerDir = target - mid.position;
        Quaternion midRot = Quaternion.FromToRotation(curLowerDir, wantLowerDir) * mid.rotation;
        mid.rotation = Quaternion.Slerp(mid.rotation, midRot, weight);
    }

    #endregion

    #region Network

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(CarriedBody != null || grabIntent != null);
            stream.SendNext(streamedTargetPos);
            stream.SendNext(streamedTargetRot);
        }
        else
        {
            bool remoteHolding = (bool)stream.ReceiveNext();
            Vector3 pos = (Vector3)stream.ReceiveNext();
            Quaternion rot = (Quaternion)stream.ReceiveNext();

            if (!remoteHolding) return;

            NetworkedCargoBody body = CarriedBody;
            if (body != null)
                body.SetHoldTarget(pos, rot);
        }
    }

    #endregion

    #region Util

    private Transform FindBone(string n) => FindR(transform, n);

    private Transform FindR(Transform p, string n)
    {
        if (p.name == n) return p;
        foreach (Transform c in p)
        {
            Transform f = FindR(c, n);
            if (f != null) return f;
        }
        return null;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, detectRange);
    }

    #endregion
}
