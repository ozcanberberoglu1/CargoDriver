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

    private void Awake()
    {
        CacheColliders();
    }

    private void OnEnable()
    {
        if (!allLegos.Contains(this))
            allLegos.Add(this);
    }

    private void OnDisable()
    {
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
            if (child.name == "TopCollider" || child.name == "DownCollider")
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

            foreach (LegoSnap other in allLegos)
            {
                if (other == this) continue;
                if (IsInSameGroup(other)) continue;

                foreach (Collider otherCol in other.snapColliders)
                {
                    if (otherCol == null) continue;
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

        SnapTo(bestTarget, bestMine, bestOther);
        BroadcastSnap(bestTarget);
        return true;
    }

    private bool IsCompatible(Collider a, Collider b)
    {
        bool aIsTop = a.transform.name == "TopCollider";
        bool bIsTop = b.transform.name == "TopCollider";
        return aIsTop != bIsTop;
    }

    private void SnapTo(LegoSnap target, Collider myCol, Collider targetCol)
    {
        Vector3 offset = targetCol.bounds.center - myCol.bounds.center;

        bool myIsDown = myCol.transform.name == "DownCollider";
        if (myIsDown)
            offset.y += snapDepth;
        else
            offset.y -= snapDepth;

        transform.position += offset;

        parentLego = target;
        target.childLegos.Add(this);

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            Destroy(rb);
        }

        transform.SetParent(target.transform, true);
    }

    public void SnapToByName(string targetName)
    {
        LegoSnap target = null;
        foreach (var lego in allLegos)
        {
            if (lego != this && lego.gameObject.name == targetName)
            {
                target = lego;
                break;
            }
        }
        if (target == null) return;

        Collider bestMine = null, bestOther = null;
        float bestDist = float.MaxValue;

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

        if (bestMine != null && bestOther != null)
            SnapTo(target, bestMine, bestOther);
    }

    #endregion

    #region Detach

    public void DetachFromParent()
    {
        if (parentLego == null) return;

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

    private void BroadcastSnap(LegoSnap target)
    {
        if (!PhotonNetwork.InRoom) return;

        snapEventCounter++;
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable
        {
            { "legoSnap", $"{gameObject.name}|{target.gameObject.name}|{snapEventCounter}" }
        });
    }

    private void BroadcastDetach()
    {
        if (!PhotonNetwork.InRoom) return;

        snapEventCounter++;
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable
        {
            { "legoDetach", $"{gameObject.name}|{snapEventCounter}" }
        });
    }

    public override void OnRoomPropertiesUpdate(Hashtable changedProps)
    {
        if (changedProps.ContainsKey("legoSnap"))
        {
            string data = changedProps["legoSnap"].ToString();
            string[] parts = data.Split('|');
            if (parts.Length >= 2 && parts[0] == gameObject.name)
            {
                SnapToByName(parts[1]);

                var ptv = GetComponent<PhotonTransformView>();
                if (ptv != null) ptv.enabled = false;
                var cbs = GetComponent<CargoBoxSync>();
                if (cbs != null) cbs.enabled = false;
            }
        }

        if (changedProps.ContainsKey("legoDetach"))
        {
            string data = changedProps["legoDetach"].ToString();
            string[] parts = data.Split('|');
            if (parts.Length >= 1 && parts[0] == gameObject.name && HasParent)
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

                var ptv = GetComponent<PhotonTransformView>();
                if (ptv != null) ptv.enabled = true;
                var cbs = GetComponent<CargoBoxSync>();
                if (cbs != null) cbs.enabled = true;
            }
        }
    }

    #endregion

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

    #endregion
}
