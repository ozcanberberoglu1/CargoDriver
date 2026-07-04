using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

public class CargoPickup : MonoBehaviourPun, IPunObservable
{
    public static readonly System.Collections.Generic.HashSet<Transform> heldByPickup = new();
    [Header("Grab")]
    [SerializeField] public float detectRange = 2.5f;
    [SerializeField] public float grabDistance = 0.5f;
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

        ToyController tc = GetComponent<ToyController>();
        if (tc != null && tc.IsPaused) return;

        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        bool pressing = mouse.leftButton.isPressed;

        if (!isHolding && pressing)
        {
            bool fps = tc != null && tc.IsFPS;

            if (fps)
            {
                Transform box = FindLookedAtBox();
                if (box != null)
                    StartGrab(box.GetComponent<Rigidbody>());
            }
            else
            {
                Transform box = FindClosestBox();
                if (box != null)
                {
                    float d = Vector3.Distance(rHand.position, box.position);
                    if (d < grabDistance)
                        StartGrab(box.GetComponent<Rigidbody>());
                }
            }
        }
        else if (isHolding && !pressing)
        {
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

    private Transform FindLookedAtBox()
    {
        Camera cam = GetComponentInChildren<Camera>();
        if (cam == null || !cam.isActiveAndEnabled) return null;

        Ray ray = new(cam.transform.position, cam.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, detectRange, cargoLayer))
        {
            if (hit.collider.CompareTag("CargoBox"))
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
            if (!col.CompareTag("CargoBox")) continue;
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

        heldByPickup.Add(heldRb.transform);

        DisableBoxSyncComponents(heldRb.gameObject);

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
    private Vector3 heldSmoothVel;
    private string syncHeldName = "";

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
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(isHolding);
            stream.SendNext(heldPV != null ? heldPV.ViewID : -1);
            stream.SendNext(heldRb != null ? heldRb.gameObject.name : "");

            if (isHolding && heldRb != null)
                stream.SendNext(heldRb.position);
            else
                stream.SendNext(Vector3.zero);
        }
        else
        {
            syncHolding = (bool)stream.ReceiveNext();
            syncHeldId = (int)stream.ReceiveNext();
            syncHeldName = (string)stream.ReceiveNext();
            syncHeldTargetPos = (Vector3)stream.ReceiveNext();
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
