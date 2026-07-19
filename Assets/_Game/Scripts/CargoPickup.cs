using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

public class CargoPickup : MonoBehaviourPun, IPunObservable
{
    public static readonly System.Collections.Generic.HashSet<Transform> heldByPickup = new();
    public static readonly System.Collections.Generic.HashSet<Transform> recentlyDroppedSet = new();
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

    private Transform recentlyDropped;
    private float droppedTimer;
    public Transform recentlyDroppedTransform => droppedTimer > 0f ? recentlyDropped : null;

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

        if (droppedTimer > 0f)
        {
            droppedTimer -= Time.deltaTime;
            if (droppedTimer <= 0f)
            {
                // Drop-tracking window ended. On non-master clients in GameScene
                // hand the box back to the master's authority: turn it into a
                // kinematic puppet again so CarControl's stream drives it and
                // local gravity no longer fights the synced position.
                if (recentlyDropped != null &&
                    !(isHolding && heldRb != null && heldRb.transform == recentlyDropped))
                {
                    bool gs = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "GameScene";
                    if (gs && !PhotonNetwork.IsMasterClient)
                    {
                        Rigidbody drb = recentlyDropped.GetComponent<Rigidbody>();
                        if (drb != null)
                        {
                            drb.isKinematic = true;
                            drb.useGravity = false;
                        }
                    }
                }
                recentlyDropped = null;
            }
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

        Debug.Log($"[CargoPickup] START GRAB: obj={rb.gameObject.name} ViewID={heldPV?.ViewID} currentOwner={heldPV?.Owner?.NickName ?? "null"} myName={PhotonNetwork.LocalPlayer.NickName} IsMaster={PhotonNetwork.IsMasterClient}");

        if (heldPV != null)
        {
            Debug.Log($"[CargoPickup] TransferOwnership called. OwnershipTransfer={heldPV.OwnershipTransfer}");
            heldPV.TransferOwnership(PhotonNetwork.LocalPlayer);
        }
        else
        {
            Debug.LogError($"[CargoPickup] NO PhotonView on {rb.gameObject.name}!");
        }

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
            UpdateCargoTarget(heldRb.transform);
            heldByPickup.Remove(heldRb.transform);

            recentlyDropped = heldRb.transform;
            droppedTimer = 3f;

            // Drop identically for everyone (master & non-master): let the box
            // fall under gravity. The grabber streams its position for the
            // droppedTimer window; afterwards authority is handed back to the
            // master (see Update) so it becomes a stream-driven puppet again.
            heldRb.isKinematic = false;
            heldRb.useGravity = true;
            heldRb.linearDamping = 0f;
            heldRb.angularDamping = 0.05f;
            heldRb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        }
        heldRb = null;
        heldPV = null;
        isHolding = false;
        isRotating = false;
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

    private void UpdateCargoTarget(Transform box)
    {
        CarControl cc = FindAnyObjectByType<CarControl>();
        if (cc == null) return;

        var field = typeof(CarControl).GetField("cargoTargetPos",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var fieldT = typeof(CarControl).GetField("cargoBoxTransforms",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null || fieldT == null) return;

        var targets = (Vector3[])field.GetValue(cc);
        var transforms = (Transform[])fieldT.GetValue(cc);
        if (targets == null || transforms == null) return;

        for (int i = 0; i < transforms.Length; i++)
        {
            if (transforms[i] == box)
            {
                targets[i] = box.position;
                break;
            }
        }
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
    private bool syncDropTracking;
    private GameObject dropTrackObj;
    private Vector3 dropSmoothVel;

    private void RemoteSync()
    {
        float targetW = syncHolding ? 1f : 0f;
        ikWeight = Mathf.MoveTowards(ikWeight, targetW, Time.deltaTime * ikBlendSpeed);

        if (syncHolding && (syncHeldId >= 0 || !string.IsNullOrEmpty(syncHeldName)))
        {
            if (heldRb == null)
            {
                GameObject found = null;
                if (syncHeldId >= 0)
                {
                    PhotonView pv = PhotonView.Find(syncHeldId);
                    if (pv != null) found = pv.gameObject;
                }
                if (found == null && !string.IsNullOrEmpty(syncHeldName))
                    found = GameObject.Find(syncHeldName);

                if (found != null)
                {
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
            }

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
                UpdateCargoTarget(heldRb.transform);
                heldByPickup.Remove(heldRb.transform);
                EnableBoxSyncComponents(heldRb.gameObject);
                heldRb.isKinematic = false;
                heldRb.useGravity = true;
                heldRb.linearDamping = 0f;
                heldRb.angularDamping = 0.05f;
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

        if (syncDropTracking && dropTrackObj != null)
        {
            PhotonTransformView ptv = dropTrackObj.GetComponent<PhotonTransformView>();
            if (ptv != null && ptv.enabled) ptv.enabled = false;
            CargoBoxSync cbs = dropTrackObj.GetComponent<CargoBoxSync>();
            if (cbs != null && cbs.enabled) cbs.enabled = false;

            Rigidbody dropRb = dropTrackObj.GetComponent<Rigidbody>();
            if (dropRb != null) dropRb.isKinematic = true;

            dropTrackObj.transform.position = Vector3.SmoothDamp(
                dropTrackObj.transform.position, syncHeldTargetPos, ref dropSmoothVel, 0.06f);
            dropTrackObj.transform.rotation = Quaternion.Slerp(
                dropTrackObj.transform.rotation, syncHeldTargetRot, Time.deltaTime * 15f);
        }
        else if (!syncDropTracking && dropTrackObj != null)
        {
            Rigidbody dropRb = dropTrackObj.GetComponent<Rigidbody>();

            bool gs = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "GameScene";
            if (gs && !PhotonNetwork.IsMasterClient)
            {
                // Non-master: the master owns the box physics and streams it via
                // CarControl, so keep it as a kinematic puppet (no local gravity
                // fighting the synced position).
                if (dropRb != null)
                {
                    dropRb.isKinematic = true;
                    dropRb.useGravity = false;
                }
            }
            else if (dropRb != null)
            {
                dropRb.isKinematic = false;
                dropRb.useGravity = true;
            }

            PhotonTransformView ptv = dropTrackObj.GetComponent<PhotonTransformView>();
            if (ptv != null) ptv.enabled = true;
            CargoBoxSync cbs = dropTrackObj.GetComponent<CargoBoxSync>();
            if (cbs != null) cbs.enabled = true;

            dropSmoothVel = Vector3.zero;
            dropTrackObj = null;
        }
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            bool tracking = isHolding || (droppedTimer > 0f && recentlyDropped != null);
            Transform tracked = isHolding && heldRb != null ? heldRb.transform : recentlyDropped;

            stream.SendNext(isHolding);
            stream.SendNext(heldPV != null ? heldPV.ViewID : -1);
            stream.SendNext(tracked != null ? tracked.gameObject.name : "");

            if (tracking && tracked != null)
            {
                stream.SendNext(tracked.position);
                stream.SendNext(tracked.rotation);
            }
            else
            {
                stream.SendNext(Vector3.zero);
                stream.SendNext(Quaternion.identity);
            }

            stream.SendNext(droppedTimer > 0f && !isHolding);
        }
        else
        {
            syncHolding = (bool)stream.ReceiveNext();
            syncHeldId = (int)stream.ReceiveNext();
            syncHeldName = (string)stream.ReceiveNext();
            syncHeldTargetPos = (Vector3)stream.ReceiveNext();
            syncHeldTargetRot = (Quaternion)stream.ReceiveNext();
            syncDropTracking = (bool)stream.ReceiveNext();

            if (syncDropTracking && dropTrackObj == null && !string.IsNullOrEmpty(syncHeldName))
            {
                if (syncHeldId >= 0)
                {
                    PhotonView pv = PhotonView.Find(syncHeldId);
                    if (pv != null) dropTrackObj = pv.gameObject;
                }
                if (dropTrackObj == null)
                    dropTrackObj = GameObject.Find(syncHeldName);
            }

            if (!syncDropTracking)
                dropTrackObj = null;
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
