using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

public class LegoSnap : MonoBehaviourPunCallbacks
{
    [Header("Snap Settings")]
    [SerializeField] private float snapDistance = 3f;
    [SerializeField] private float snapDepth = 0.05f;

    private static readonly List<LegoSnap> allLegos = new();
    private static int snapEventCounter;

    private LegoSnap parentLego;
    private readonly List<LegoSnap> childLegos = new();
    private Collider[] snapColliders;
    private static readonly HashSet<Collider> usedColliders = new();

    private void Awake()
    {
        CacheColliders();
    }

    public override void OnEnable()
    {
        base.OnEnable();
        if (!allLegos.Contains(this))
            allLegos.Add(this);
    }

    public override void OnDisable()
    {
        base.OnDisable();
        allLegos.Remove(this);
    }

    private void CacheColliders()
    {
        var list = new List<Collider>();
        FindSnapColliders(transform, list);
        snapColliders = list.ToArray();
    }

    private void FindSnapColliders(Transform parent, List<Collider> list)
    {
        foreach (Transform child in parent)
        {
            if (child.name.StartsWith("TopCollider") || child.name.StartsWith("DownCollider"))
            {
                Collider col = child.GetComponent<Collider>();
                if (col != null) list.Add(col);
            }
            if (child.GetComponent<LegoSnap>() == null)
                FindSnapColliders(child, list);
        }
    }

    public bool HasParent => parentLego != null;

    public void SetParentDirect(LegoSnap parent)
    {
        parentLego = parent;
        parent.childLegos.Add(this);
    }

    #region Snap

    public bool TrySnap()
    {
        if (snapColliders == null || snapColliders.Length == 0) return false;

        LegoSnap bestTarget = null;
        float bestDist = float.MaxValue;
        Collider bestMine = null;
        Collider bestOther = null;

        foreach (Collider myCol in snapColliders)
        {
            if (myCol == null) continue;
            if (usedColliders.Contains(myCol)) continue;

            foreach (LegoSnap other in allLegos)
            {
                if (other == this) continue;

                foreach (Collider otherCol in other.snapColliders)
                {
                    if (otherCol == null) continue;
                    if (usedColliders.Contains(otherCol)) continue;
                    if (!IsCompatible(myCol, otherCol)) continue;

                    float dist = Vector3.Distance(myCol.bounds.center, otherCol.bounds.center);
                    if (dist < snapDistance && dist < bestDist)
                    {
                        bestDist = dist;
                        bestTarget = other;
                        bestMine = myCol;
                        bestOther = otherCol;
                    }
                }
            }
        }

        if (bestTarget == null) return false;

        if (IsInSameGroup(bestTarget))
        {
            usedColliders.Add(bestMine);
            usedColliders.Add(bestOther);
            PlaySnapSound();
            return true;
        }

        LegoSnap snapChild;
        LegoSnap snapParent;
        Collider childCol, parentCol;

        if (HasParent)
        {
            snapChild = bestTarget.GetRoot();
            snapParent = this;
            childCol = bestOther;
            parentCol = bestMine;
        }
        else
        {
            snapChild = GetRoot();
            snapParent = bestTarget;
            childCol = bestMine;
            parentCol = bestOther;
        }

        Vector3 offset = parentCol.bounds.center - childCol.bounds.center;
        bool childIsDown = childCol.transform.name.StartsWith("DownCollider");
        if (childIsDown)
            offset.y += snapDepth;
        else
            offset.y -= snapDepth;

        snapChild.transform.position += offset;

        snapChild.parentLego = snapParent;
        snapParent.childLegos.Add(snapChild);

        usedColliders.Add(bestMine);
        usedColliders.Add(bestOther);

        Rigidbody rb = snapChild.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            Destroy(rb);
        }

        snapChild.transform.SetParent(snapParent.transform, true);

        snapChild.BroadcastSnap(snapParent, snapChild.transform.localPosition, snapChild.transform.localRotation);
        PlaySnapSound();
        return true;
    }

    private bool SnapSimple_Unused(LegoSnap bestTarget, Collider bestMine, Collider bestOther)
    {
        return false;
    }

    private bool IsCompatible(Collider a, Collider b)
    {
        bool aIsTop = a.transform.name.StartsWith("TopCollider");
        bool bIsTop = b.transform.name.StartsWith("TopCollider");
        return aIsTop != bIsTop;
    }

    public void SnapTo(LegoSnap target, Collider myCol, Collider targetCol)
    {
        Vector3 offset = targetCol.bounds.center - myCol.bounds.center;

        bool myIsDown = myCol.transform.name.StartsWith("DownCollider");
        if (myIsDown)
            offset.y += snapDepth;
        else
            offset.y -= snapDepth;

        transform.position += offset;

        parentLego = target;
        target.childLegos.Add(this);

        usedColliders.Add(myCol);
        usedColliders.Add(targetCol);

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            Destroy(rb);
        }

        transform.SetParent(target.transform, true);
    }

    

    #endregion

    #region Detach

    public void DetachFromParent()
    {
        if (parentLego == null) return;

        foreach (Collider col in snapColliders)
        {
            if (col != null) usedColliders.Remove(col);
        }
        if (parentLego != null)
        {
            foreach (Collider col in parentLego.snapColliders)
            {
                if (col != null) usedColliders.Remove(col);
            }
        }

        parentLego.childLegos.Remove(this);
        parentLego = null;

        transform.SetParent(null, true);

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>();

        rb.isKinematic = false;
        rb.useGravity = true;
        rb.mass = 2f;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.linearDamping = 0f;
        rb.angularDamping = 0.05f;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        BroadcastDetach();
    }

    public void DetachAll()
    {
        var children = new List<LegoSnap>(childLegos);
        foreach (LegoSnap child in children)
        {
            child.DetachAll();
            child.DetachFromParent();
        }

        DetachFromParent();
    }

    #endregion

    #region Network Sync

    private string GetLegoId()
    {
        var pv = GetComponent<PhotonView>();
        if (pv != null) return $"v{pv.ViewID}";
        return $"i{GetInstanceID()}";
    }

    private static LegoSnap FindById(string id)
    {
        foreach (var lego in allLegos)
        {
            if (lego.GetLegoId() == id) return lego;
        }
        return null;
    }

    public void BroadcastSnap(LegoSnap target, Vector3 localPos, Quaternion localRot)
    {
        if (!PhotonNetwork.InRoom) return;

        snapEventCounter++;
        string myId = GetLegoId();
        string targetId = target.GetLegoId();
        string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string posRot = $"{F(localPos.x)},{F(localPos.y)},{F(localPos.z)},{F(localRot.x)},{F(localRot.y)},{F(localRot.z)},{F(localRot.w)}";

        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable
        {
            { "legoSnap", $"{myId}|{targetId}|{snapEventCounter}|{posRot}" }
        });
    }

    private void BroadcastDetach()
    {
        if (!PhotonNetwork.InRoom) return;

        snapEventCounter++;
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable
        {
            { "legoDetach", $"{GetLegoId()}|{snapEventCounter}" }
        });
    }

    public override void OnRoomPropertiesUpdate(Hashtable changedProps)
    {
        if (changedProps.ContainsKey("legoSnap"))
        {
            string data = changedProps["legoSnap"].ToString();
            string[] parts = data.Split('|');
            if (parts.Length >= 2 && parts[0] == GetLegoId())
            {
                LegoSnap target = FindById(parts[1]);
                if (target != null)
                {
                    parentLego = target;
                    target.childLegos.Add(this);

                    MarkClosestCollidersUsed(target);

                    Rigidbody rb = GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        Destroy(rb);
                    }

                    transform.SetParent(target.transform, true);

                    if (parts.Length >= 4)
                    {
                        string[] pr = parts[3].Split(',');
                        if (pr.Length >= 7)
                        {
                            var ci = System.Globalization.CultureInfo.InvariantCulture;
                            transform.localPosition = new Vector3(
                                float.Parse(pr[0], ci), float.Parse(pr[1], ci), float.Parse(pr[2], ci));
                            transform.localRotation = new Quaternion(
                                float.Parse(pr[3], ci), float.Parse(pr[4], ci),
                                float.Parse(pr[5], ci), float.Parse(pr[6], ci));
                        }
                    }

                    var ptv = GetComponent<PhotonTransformView>();
                    if (ptv != null) ptv.enabled = false;
                    var cbs = GetComponent<CargoBoxSync>();
                    if (cbs != null) cbs.enabled = false;
                }
            }
        }

        if (changedProps.ContainsKey("legoDetach"))
        {
            string data = changedProps["legoDetach"].ToString();
            string[] parts = data.Split('|');
            if (parts.Length >= 1 && parts[0] == GetLegoId() && HasParent)
            {
                parentLego.childLegos.Remove(this);
                parentLego = null;
                transform.SetParent(null, true);

                Rigidbody rb = GetComponent<Rigidbody>();
                if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.mass = 2f;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

                var ptv = GetComponent<PhotonTransformView>();
                if (ptv != null) ptv.enabled = true;
                var cbs = GetComponent<CargoBoxSync>();
                if (cbs != null) cbs.enabled = true;
            }
        }
    }

    #endregion

    private void MarkClosestCollidersUsed(LegoSnap target)
    {
        float bestDist = float.MaxValue;
        Collider bestMine = null, bestOther = null;

        foreach (Collider myCol in snapColliders)
        {
            if (myCol == null) continue;
            foreach (Collider otherCol in target.snapColliders)
            {
                if (otherCol == null) continue;
                if (!IsCompatible(myCol, otherCol)) continue;
                float dist = Vector3.Distance(myCol.bounds.center, otherCol.bounds.center);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestMine = myCol;
                    bestOther = otherCol;
                }
            }
        }

        if (bestMine != null) usedColliders.Add(bestMine);
        if (bestOther != null) usedColliders.Add(bestOther);
    }

    private void PlaySnapSound()
    {
        LobbyController lobby = FindAnyObjectByType<LobbyController>();
        if (lobby != null)
            lobby.PlayLegoSnapSound();
    }

    #region Helpers

    private bool IsInSameGroup(LegoSnap other)
    {
        LegoSnap myRoot = GetRoot();
        LegoSnap otherRoot = other.GetRoot();
        return myRoot == otherRoot;
    }

    public LegoSnap GetRoot()
    {
        LegoSnap current = this;
        while (current.parentLego != null)
            current = current.parentLego;
        return current;
    }

    public List<LegoSnap> GetAllConnected()
    {
        var result = new List<LegoSnap>();
        GetRoot().CollectAll(result);
        return result;
    }

    private void CollectAll(List<LegoSnap> result)
    {
        result.Add(this);
        foreach (LegoSnap child in childLegos)
            child.CollectAll(result);
    }

    #endregion
}
