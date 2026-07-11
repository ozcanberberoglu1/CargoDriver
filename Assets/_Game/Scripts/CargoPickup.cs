using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

public class CargoPickup : MonoBehaviourPun, IPunObservable
{
    public static readonly System.Collections.Generic.HashSet<Transform> heldByPickup = new();
    [Header("Grab")]
    [SerializeField] public float detectRange = 5f;
    [SerializeField] public float grabDistance = 1.5f;
    [SerializeField] private float holdForward = 0.7f;
    [SerializeField] private float holdUp = 0.6f;
    [SerializeField] private LayerMask cargoLayer = ~0;

    [Header("Hold Distance (Scroll)")]
    [SerializeField] private float minHoldDist = 1f;
    [SerializeField] private float maxHoldDist = 4f;
    [SerializeField] private float scrollSpeed = 0.5f;

    public float ScrollSpeed
    {
        get => scrollSpeed;
        set => scrollSpeed = value;
    }

    [Header("IK")]
    [SerializeField] private float ikBlendSpeed = 10f;

    private Transform rShoulder, rElbow, rHand;
    private Transform lShoulder, lElbow, lHand;
    private float rUpperLen, rLowerLen;
    private float lUpperLen, lLowerLen;

    private Rigidbody heldRb;
    private PhotonView heldPV;
    private float ikWeight;
    private bool isHolding;
    private float currentHoldDist;
    public bool IsRotating => isRotating;
    private bool isRotating;
    private float snapCooldown;
    private Vector3 frozenHoldPos;

    private bool syncHolding;
    private int syncHeldId = -1;

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
        if (!photonView.IsMine)
        {
            RemoteSync();
            return;
        }

        if (snapCooldown > 0f)
            snapCooldown -= Time.deltaTime;

        ToyController tc = GetComponent<ToyController>();
        if (tc != null && tc.IsPaused) return;

        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        bool pressing = mouse.leftButton.isPressed;

        if (!isHolding && pressing && snapCooldown <= 0f)
        {
            bool fps = tc != null && tc.IsFPS;

            if (fps)
            {
                Transform box = FindLookedAtBox();
                if (box != null)
                    StartGrab(FindGrabbableRb(box));
            }
            else
            {
                Transform box = FindClosestBox();
                if (box != null)
                {
                    float d = Vector3.Distance(rHand.position, box.position);
                    if (d < grabDistance)
                        StartGrab(FindGrabbableRb(box));
                }
            }
        }
        else if (isHolding)
        {
            bool rightPressed = mouse.rightButton.isPressed;

            if (rightPressed && !isRotating)
            {
                isRotating = true;
                if (heldRb != null)
                    frozenHoldPos = heldRb.transform.position;
            }
            else if (!rightPressed && isRotating)
            {
                isRotating = false;
            }

            if (isRotating && heldRb != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                float rotX = delta.y * 0.5f;
                float rotY = -delta.x * 0.5f;

                Camera cam = GetComponentInChildren<Camera>();
                if (cam != null && cam.isActiveAndEnabled)
                {
                    heldRb.transform.Rotate(cam.transform.up, rotY, Space.World);
                    heldRb.transform.Rotate(cam.transform.right, rotX, Space.World);
                }
                else
                {
                    heldRb.transform.Rotate(Vector3.up, rotY, Space.World);
                    heldRb.transform.Rotate(Vector3.right, rotX, Space.World);
                }
            }

            bool isGameScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "GameScene";

            if (!isGameScene)
            {
                Keyboard kb = Keyboard.current;
                if (kb != null)
                {
                    if (kb.eKey.wasPressedThisFrame)
                    {
                        if (TrySnapHeld())
                            return;
                    }
                    else if (kb.xKey.wasPressedThisFrame)
                        DetachHeldFromBelow();
                    else if (kb.zKey.wasPressedThisFrame)
                        DetachAllHeld();
                }
            }

            if (!pressing)
                StopGrab();
        }
    }

    private void LateUpdate()
    {
        if (rShoulder == null) return;

        Vector3 target = Vector3.zero;
        bool wantIK = false;

        if (isHolding && heldRb != null)
        {
            if (photonView.IsMine)
                CarryObject();

            target = heldRb.position;
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
                    float dist = Vector3.Distance(rShoulder.position, box.position);
                    if (dist <= maxReach + 0.2f)
                    {
                        target = box.position;
                        wantIK = true;
                    }
                }
            }
        }

        float targetW = wantIK ? 1f : 0f;
        ikWeight = Mathf.MoveTowards(ikWeight, targetW, Time.deltaTime * ikBlendSpeed);

        if (ikWeight > 0.01f)
        {
            SolveTwoBoneIK(rShoulder, rElbow, rHand, rUpperLen, rLowerLen, target, ikWeight, true);
            SolveTwoBoneIK(lShoulder, lElbow, lHand, lUpperLen, lLowerLen, target, ikWeight, false);
        }
    }

    #region Grab

    private bool IsCargoBox(Transform t)
    {
        if (t.CompareTag("CargoBox")) return true;
        if (t.GetComponent<LegoSnap>() != null) return true;
        if (t.GetComponentInParent<LegoSnap>() != null) return true;
        return false;
    }

    private Rigidbody FindGrabbableRb(Transform box)
    {
        Rigidbody rb = box.GetComponent<Rigidbody>();
        if (rb != null) return rb;

        LegoSnap snap = box.GetComponent<LegoSnap>();
        if (snap == null)
            snap = box.GetComponentInParent<LegoSnap>();

        if (snap != null)
        {
            LegoSnap root = snap.GetRoot();
            rb = root.GetComponent<Rigidbody>();
            if (rb != null) return rb;
        }

        rb = box.GetComponentInParent<Rigidbody>();
        return rb;
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

    private void StartGrab(Rigidbody rb)
    {
        if (rb == null) return;
        heldRb = rb;
        isHolding = true;
        currentHoldDist = holdForward;

        heldPV = heldRb.GetComponent<PhotonView>();

        if (heldPV != null)
            heldPV.TransferOwnership(PhotonNetwork.LocalPlayer);

        LegoSnap snap = heldRb.GetComponent<LegoSnap>();
        if (snap != null)
        {
            LegoSnap root = snap.GetRoot();
            if (root != snap)
            {
                heldRb = root.GetComponent<Rigidbody>();
                heldPV = root.GetComponent<PhotonView>();
            }
        }

        heldByPickup.Add(heldRb.transform);
        heldRb.transform.SetParent(null, true);

        DisableBoxSyncComponents(heldRb.gameObject);

        heldRb.isKinematic = false;
        heldRb.useGravity = false;
        heldRb.linearDamping = 12f;
        heldRb.angularDamping = 8f;
    }

    private void DisableBoxSyncComponents(GameObject box)
    {
        PhotonTransformView ptv = box.GetComponent<PhotonTransformView>();
        if (ptv != null) ptv.enabled = false;

        CargoBoxSync cbs = box.GetComponent<CargoBoxSync>();
        if (cbs != null) cbs.enabled = false;
    }

    private void EnableBoxSyncComponents(GameObject box)
    {
        PhotonTransformView ptv = box.GetComponent<PhotonTransformView>();
        if (ptv != null) ptv.enabled = true;

        CargoBoxSync cbs = box.GetComponent<CargoBoxSync>();
        if (cbs != null) cbs.enabled = true;
    }

    private void StopGrab()
    {
        if (heldRb != null)
        {
            heldByPickup.Remove(heldRb.transform);
            ReleasePhysics(heldRb);
        }
        heldRb = null;
        heldPV = null;
        isHolding = false;
        isRotating = false;
    }

    // Decides the physics state of a box right after it is released.
    // GameScene: the MasterClient is the sole physics authority; everyone
    // else keeps the box kinematic and mirrors the master via CarControl.
    // LobbyScene: every box owns its physics + PhotonTransformView sync.
    private void ReleasePhysics(Rigidbody box)
    {
        bool isGameScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "GameScene";

        if (isGameScene)
        {
            if (PhotonNetwork.IsMasterClient)
                SetDynamic(box);
            else
                SetKinematic(box);
        }
        else
        {
            EnableBoxSyncComponents(box.gameObject);
            SetDynamic(box);
        }
    }

    private void SetDynamic(Rigidbody box)
    {
        box.isKinematic = false;
        box.useGravity = true;
        box.linearDamping = 0f;
        box.angularDamping = 0.05f;
        box.interpolation = RigidbodyInterpolation.Interpolate;
        box.collisionDetectionMode = CollisionDetectionMode.Continuous;
    }

    private void SetKinematic(Rigidbody box)
    {
        box.isKinematic = true;
        box.useGravity = false;
    }

    private bool TrySnapHeld()
    {
        if (heldRb == null) return false;
        LegoSnap snap = heldRb.GetComponent<LegoSnap>();
        if (snap == null) return false;

        bool snapped = false;
        var group = snap.GetAllConnected();
        foreach (var member in group)
        {
            if (member.TrySnap())
            {
                snapped = true;
                break;
            }
        }

        if (!snapped) return false;

        if (heldRb != null)
            heldByPickup.Remove(heldRb.transform);

        heldRb = null;
        heldPV = null;
        isHolding = false;
        isRotating = false;
        snapCooldown = 1f;
        return true;
    }

    private void DetachHeldFromBelow()
    {
        if (heldRb == null) return;
        LegoSnap snap = heldRb.GetComponent<LegoSnap>();
        if (snap == null) return;
        snap.DetachFromParent();
    }

    private void DetachAllHeld()
    {
        if (heldRb == null) return;
        LegoSnap snap = heldRb.GetComponent<LegoSnap>();
        if (snap == null) return;
        snap.DetachAll();
    }

    private void CarryObject()
    {
        if (heldRb == null) return;

        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            float scroll = mouse.scroll.y.ReadValue();
            if (Mathf.Abs(scroll) > 0.01f)
            {
                currentHoldDist += scroll * scrollSpeed * Time.deltaTime;
                currentHoldDist = Mathf.Clamp(currentHoldDist, minHoldDist, maxHoldDist);
            }
        }

        ToyController tc = GetComponent<ToyController>();
        Camera cam = GetComponentInChildren<Camera>();
        bool fps = tc != null && tc.IsFPS;

        Vector3 holdPos;
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

        if (isRotating)
        {
            if (heldRb.isKinematic)
                heldRb.transform.position = frozenHoldPos;
            else
            {
                Vector3 diff = frozenHoldPos - heldRb.position;
                heldRb.linearVelocity = diff * 12f;
            }
            return;
        }

        if (heldRb.isKinematic)
        {
            heldRb.transform.position = holdPos;
        }
        else
        {
            Vector3 diff = holdPos - heldRb.position;
            heldRb.linearVelocity = diff * 12f;
        }
    }

    #endregion

    #region Two Bone IK

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

        // Rotate shoulder
        Vector3 curUpperDir = mid.position - root.position;
        Quaternion rootRot = Quaternion.FromToRotation(curUpperDir, newUpperDir) * root.rotation;
        root.rotation = Quaternion.Slerp(root.rotation, rootRot, weight);

        // Rotate elbow
        Vector3 curLowerDir = tip.position - mid.position;
        Vector3 wantLowerDir = target - mid.position;
        Quaternion midRot = Quaternion.FromToRotation(curLowerDir, wantLowerDir) * mid.rotation;
        mid.rotation = Quaternion.Slerp(mid.rotation, midRot, weight);
    }

    #endregion

    #region Network

    private Vector3 syncHeldTargetPos;
    private Quaternion syncHeldTargetRot = Quaternion.identity;
    private Vector3 heldSmoothVel;
    private string syncHeldName = "";

    // Runs on every client that does NOT own this player. Mirrors the box the
    // remote player is holding. When they release it, the box is handed back to
    // its physics authority (master in GameScene, owner in LobbyScene).
    private void RemoteSync()
    {
        float targetW = syncHolding ? 1f : 0f;
        ikWeight = Mathf.MoveTowards(ikWeight, targetW, Time.deltaTime * ikBlendSpeed);

        if (syncHolding && (syncHeldId >= 0 || !string.IsNullOrEmpty(syncHeldName)))
        {
            if (heldRb == null)
                AcquireRemoteHeld();

            if (heldRb != null)
            {
                heldRb.transform.position = Vector3.SmoothDamp(
                    heldRb.transform.position, syncHeldTargetPos, ref heldSmoothVel, 0.04f);
                heldRb.transform.rotation = Quaternion.Slerp(
                    heldRb.transform.rotation, syncHeldTargetRot, Time.deltaTime * 15f);
            }
        }
        else if (!syncHolding && isHolding)
        {
            if (heldRb != null)
            {
                heldByPickup.Remove(heldRb.transform);
                ReleasePhysics(heldRb);
            }
            heldRb = null;
            heldPV = null;
            isHolding = false;
        }

        if (ikWeight > 0.01f && heldRb != null)
        {
            SolveTwoBoneIK(rShoulder, rElbow, rHand, rUpperLen, rLowerLen, heldRb.position, ikWeight, true);
            SolveTwoBoneIK(lShoulder, lElbow, lHand, lUpperLen, lLowerLen, heldRb.position, ikWeight, false);
        }
    }

    private void AcquireRemoteHeld()
    {
        GameObject found = null;
        if (syncHeldId >= 0)
        {
            PhotonView pv = PhotonView.Find(syncHeldId);
            if (pv != null) found = pv.gameObject;
        }
        if (found == null && !string.IsNullOrEmpty(syncHeldName))
            found = GameObject.Find(syncHeldName);

        if (found == null) return;

        heldRb = found.GetComponent<Rigidbody>();
        isHolding = true;

        if (heldRb != null)
        {
            heldRb.transform.SetParent(null, true);
            heldRb.isKinematic = true;
            heldRb.useGravity = false;
            heldRb.linearDamping = 0f;
            heldByPickup.Add(heldRb.transform);
            DisableBoxSyncComponents(heldRb.gameObject);
        }
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            Transform tracked = isHolding && heldRb != null ? heldRb.transform : null;

            stream.SendNext(isHolding);
            stream.SendNext(heldPV != null ? heldPV.ViewID : -1);
            stream.SendNext(tracked != null ? tracked.gameObject.name : "");

            if (tracked != null)
            {
                stream.SendNext(tracked.position);
                stream.SendNext(tracked.rotation);
            }
            else
            {
                stream.SendNext(Vector3.zero);
                stream.SendNext(Quaternion.identity);
            }
        }
        else
        {
            syncHolding = (bool)stream.ReceiveNext();
            syncHeldId = (int)stream.ReceiveNext();
            syncHeldName = (string)stream.ReceiveNext();
            syncHeldTargetPos = (Vector3)stream.ReceiveNext();
            syncHeldTargetRot = (Quaternion)stream.ReceiveNext();
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
