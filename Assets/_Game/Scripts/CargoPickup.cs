using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

public class CargoPickup : MonoBehaviourPun, IPunObservable
{
    [Header("Grab")]
    [SerializeField] private float detectRange = 2.5f;
    [SerializeField] private float grabDistance = 0.5f;
    [SerializeField] private float holdForward = 0.7f;
    [SerializeField] private float holdUp = 0.6f;
    [SerializeField] private LayerMask cargoLayer = ~0;

    [Header("Hold Distance (Scroll)")]
    [SerializeField] private float minHoldDist = 1f;
    [SerializeField] private float maxHoldDist = 4f;
    [SerializeField] private float scrollSpeed = 0.5f;

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

        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        bool pressing = mouse.leftButton.isPressed;

        if (!isHolding && pressing)
        {
            ToyController tc = GetComponent<ToyController>();
            bool fps = tc != null && tc.IsFPS;

            Transform box = fps ? FindLookedAtBox() : FindClosestBox();
            if (box != null)
            {
                float d = Vector3.Distance(rHand.position, box.position);
                if (d < grabDistance)
                    StartGrab(box.GetComponent<Rigidbody>());
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
        if (heldPV != null)
            heldPV.TransferOwnership(PhotonNetwork.LocalPlayer);

        CargoBoxSync sync = heldRb.GetComponent<CargoBoxSync>();
        if (sync != null)
            sync.SetGrabbed(true);
        else
        {
            heldRb.useGravity = false;
            heldRb.linearDamping = 12f;
            heldRb.angularDamping = 8f;
        }
    }

    private void StopGrab()
    {
        if (heldRb != null)
        {
            CargoBoxSync sync = heldRb.GetComponent<CargoBoxSync>();
            if (sync != null)
                sync.SetGrabbed(false);
            else
            {
                heldRb.useGravity = true;
                heldRb.linearDamping = 0f;
                heldRb.angularDamping = 0.05f;
            }
        }
        heldRb = null;
        heldPV = null;
        isHolding = false;
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

        Vector3 diff = holdPos - heldRb.position;
        heldRb.linearVelocity = diff * 12f;
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

    private void RemoteSync()
    {
        float targetW = syncHolding ? 1f : 0f;
        ikWeight = Mathf.MoveTowards(ikWeight, targetW, Time.deltaTime * ikBlendSpeed);

        if (syncHolding && syncHeldId >= 0 && heldRb == null)
        {
            PhotonView pv = PhotonView.Find(syncHeldId);
            if (pv != null)
            {
                heldRb = pv.GetComponent<Rigidbody>();
                isHolding = true;
            }
        }
        else if (!syncHolding && isHolding)
        {
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
        }
        else
        {
            syncHolding = (bool)stream.ReceiveNext();
            syncHeldId = (int)stream.ReceiveNext();
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
