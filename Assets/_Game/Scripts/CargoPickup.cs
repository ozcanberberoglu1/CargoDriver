using System.Collections.Generic;
using System.Globalization;
using Photon.Pun;
using TMPro;
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

    [Header("Rotation (hold right mouse and drag)")]
    [SerializeField] private float rotateSensitivity = 0.4f;

    [Header("Scale (1 = bigger, 2 = smaller)")]
    [SerializeField] private float scaleStep = 0.1f;
    [SerializeField] private float minScale = 0.3f;
    [SerializeField] private float maxScale = 2.0f;

    private TextMeshPro scaleLabel;
    private NetworkedCargoBody scaleLabelBox;
    private float scaleLabelAge;
    private const float scaleLabelHold = 2f;   // fully visible for this long
    private const float scaleLabelFade = 1f;    // then fades out over this long

    private Vector3 streamedTargetPos;
    private Quaternion streamedTargetRot = Quaternion.identity;

    private int OwnerActor => photonView.Owner != null ? photonView.Owner.ActorNumber : -1;

    /// <summary>The box this player is carrying, valid on every client.</summary>
    private NetworkedCargoBody CarriedBody => NetworkedCargoBody.HeldBy(OwnerActor);

    private void OnDestroy()
    {
        if (scaleLabel != null) Destroy(scaleLabel.gameObject);
    }

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

        Keyboard kb = Keyboard.current;
        bool pressing = mouse.leftButton.isPressed;
        NetworkedCargoBody carried = CarriedBody;

        // Q pins the held brick in place, or unpins a frozen one you're aiming at.
        if (kb != null && kb.qKey.wasPressedThisFrame)
            HandleFreezeKey(tc, mouse);

        // 1 / 2 grow / shrink the held brick (works whether or not you're rotating).
        if (kb != null && carried != null && carried.IsHeld)
        {
            if (kb.digit1Key.wasPressedThisFrame) ChangeScale(carried, scaleStep);
            else if (kb.digit2Key.wasPressedThisFrame) ChangeScale(carried, -scaleStep);
        }

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

    /// <summary>
    /// Q while holding pins the brick (and any welded block) where it is. Q while aiming at
    /// a pinned brick with the grab button held unpins it back into free physics.
    /// </summary>
    private void HandleFreezeKey(ToyController tc, Mouse mouse)
    {
        NetworkedCargoBody held = CarriedBody;
        if (held != null && held.IsHeld)
        {
            held.RequestFreeze();
            grabIntent = null;
            isRotating = false;
            snapCooldown = 0.3f;
            ClearSnapPreview();
            return;
        }

        // Unpin: must be aiming at a pinned brick while holding the grab button.
        if (!mouse.leftButton.isPressed) return;

        bool fps = tc != null && tc.IsFPS;
        Transform hit = fps ? FindLookedAtBox() : FindClosestBox();
        if (hit == null) return;

        NetworkedCargoBody body = FindGrabbableBody(hit);
        if (body != null && body.IsFrozen)
            body.RequestUnfreeze();
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

    /// <summary>
    /// Hold right mouse and drag to rotate the brick: horizontal → yaw, vertical → pitch, both
    /// around the camera's axes so it always turns the way the mouse moves. Position is frozen
    /// while rotating so the box turns in place. Snap-on-placement squares it to the grid, so the
    /// player only needs to get roughly the right orientation. holdRotTarget streams via PushHoldTarget.
    /// </summary>
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
        if (delta.sqrMagnitude < 0.0001f) return;

        Camera cam = GetComponentInChildren<Camera>();
        Vector3 up = cam != null && cam.isActiveAndEnabled ? cam.transform.up : Vector3.up;
        Vector3 right = cam != null && cam.isActiveAndEnabled ? cam.transform.right : Vector3.right;

        float yaw = -delta.x * rotateSensitivity;   // drag left/right → yaw
        float pitch = delta.y * rotateSensitivity;  // drag up/down    → pitch
        holdRotTarget = Quaternion.AngleAxis(yaw, up) * Quaternion.AngleAxis(pitch, right) * holdRotTarget;
    }

    private void HandleScrollInput(Mouse mouse)
    {
        float scroll = mouse.scroll.y.ReadValue();
        if (Mathf.Abs(scroll) <= 0.01f) return;

        currentHoldDist += scroll * scrollSpeed * Time.deltaTime;
        currentHoldDist = Mathf.Clamp(currentHoldDist, minHoldDist, maxHoldDist);
    }

    /// <summary>Steps the held brick's uniform scale and networks it, then shows the label.</summary>
    private void ChangeScale(NetworkedCargoBody box, float delta)
    {
        float cur = box.transform.localScale.x;
        float next = Mathf.Clamp(cur + delta, minScale, maxScale);
        next = Mathf.Round(next * 10f) / 10f; // keep it on clean 0.1 steps

        box.RequestScale(next);
        ShowScaleLabel(box, next);
    }

    private void EnsureScaleLabel()
    {
        if (scaleLabel != null) return;

        var go = new GameObject("ScaleLabel");
        scaleLabel = go.AddComponent<TextMeshPro>();            // 3D world-space text (default font)
        scaleLabel.alignment = TextAlignmentOptions.Center;
        scaleLabel.fontSize = 8f;
        scaleLabel.color = Color.white;
        scaleLabel.outlineWidth = 0.2f;
        scaleLabel.outlineColor = Color.black;
        scaleLabel.rectTransform.sizeDelta = new Vector2(4f, 1.5f);
        go.transform.localScale = Vector3.one * 0.3f;          // keep it a sensible size above the box
        go.SetActive(false);
    }

    private void ShowScaleLabel(NetworkedCargoBody box, float scale)
    {
        EnsureScaleLabel();
        scaleLabelBox = box;
        scaleLabel.text = scale.ToString("0.0", CultureInfo.InvariantCulture) + "x";
        scaleLabel.gameObject.SetActive(true);
        scaleLabelAge = 0f;
    }

    /// <summary>Local-only: floats the scale label above the box, billboarded, then fades it out.</summary>
    private void UpdateScaleLabel()
    {
        if (scaleLabel == null || !scaleLabel.gameObject.activeSelf) return;

        scaleLabelAge += Time.deltaTime;
        if (scaleLabelBox == null || scaleLabelAge > scaleLabelHold + scaleLabelFade)
        {
            scaleLabel.gameObject.SetActive(false);
            return;
        }

        // Sit above the TOP of the whole structure (box + welded children), not one box, so it
        // never ends up buried inside a stack. Mesh renderers only — the grid Images use
        // CanvasRenderer and are skipped, so they don't inflate the bounds.
        Renderer[] rends = scaleLabelBox.GetComponentsInChildren<Renderer>();
        Vector3 pos;
        if (rends.Length > 0)
        {
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            pos = new Vector3(b.center.x, b.max.y + 0.4f, b.center.z);
        }
        else
        {
            pos = scaleLabelBox.transform.position + Vector3.up * 1.2f;
        }
        scaleLabel.transform.position = pos;

        Camera cam = GetComponentInChildren<Camera>();
        if (cam != null)
            scaleLabel.transform.rotation = cam.transform.rotation; // billboard toward the viewer

        float alpha = scaleLabelAge <= scaleLabelHold
            ? 1f
            : 1f - (scaleLabelAge - scaleLabelHold) / scaleLabelFade;
        Color c = scaleLabel.color;
        c.a = Mathf.Clamp01(alpha);
        scaleLabel.color = c;
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
        UpdateScaleLabel();

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
