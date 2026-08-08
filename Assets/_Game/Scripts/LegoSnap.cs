using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Geometry and local hierarchy bookkeeping for stud-based lego snapping.
///
/// This component owns no networking. A snap is expressed as "this box becomes Stowed
/// under that box with this local pose", which is exactly a <see cref="NetworkedCargoBody"/>
/// state transition, so replication happens through that single channel.
/// </summary>
[RequireComponent(typeof(NetworkedCargoBody))]
public class LegoSnap : MonoBehaviour
{
    [Header("Snap Settings")]
    [SerializeField] private float snapDistance = 3f;
    [SerializeField] private float snapDepth = 0.05f;

    private static readonly List<LegoSnap> allLegos = new();
    private static readonly HashSet<Collider> usedColliders = new();

    private LegoSnap parentLego;
    private readonly List<LegoSnap> childLegos = new();
    private Collider[] snapColliders;

    private NetworkedCargoBody body;

    public bool HasParent => parentLego != null;

    private void Awake()
    {
        body = GetComponent<NetworkedCargoBody>();
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
            if (child.name.StartsWith("TopCollider") || child.name.StartsWith("DownCollider"))
            {
                Collider col = child.GetComponent<Collider>();
                if (col != null) list.Add(col);
            }
            if (child.GetComponent<LegoSnap>() == null)
                FindSnapColliders(child, list);
        }
    }

    #region Hierarchy links (driven by NetworkedCargoBody)

    /// <summary>Called on every client when the body enters Stowed under a lego parent.</summary>
    public void AttachToParent(LegoSnap parent)
    {
        if (parent == null || parent == this || parentLego == parent) return;

        ClearParentLink();

        parentLego = parent;
        if (!parent.childLegos.Contains(this))
            parent.childLegos.Add(this);

        MarkClosestCollidersUsed(parent);
    }

    /// <summary>Called on every client when the body leaves Stowed.</summary>
    public void ClearParentLink()
    {
        if (parentLego == null) return;

        ReleaseColliders(snapColliders);
        ReleaseColliders(parentLego.snapColliders);

        parentLego.childLegos.Remove(this);
        parentLego = null;
    }

    private static void ReleaseColliders(Collider[] colliders)
    {
        if (colliders == null) return;
        foreach (Collider col in colliders)
        {
            if (col != null) usedColliders.Remove(col);
        }
    }

    #endregion

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
                if (other.snapColliders == null) continue;

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

        NetworkedCargoBody childBody = snapChild.GetComponent<NetworkedCargoBody>();
        if (childBody == null) return false;

        Vector3 snappedWorldPos = snapChild.transform.position + offset;
        Vector3 localPos = snapParent.transform.InverseTransformPoint(snappedWorldPos);
        Quaternion localRot = Quaternion.Inverse(snapParent.transform.rotation) * snapChild.transform.rotation;

        childBody.AuthorityStow(snapParent.transform, localPos, localRot);

        PlaySnapSound();
        return true;
    }

    private bool IsCompatible(Collider a, Collider b)
    {
        bool aIsTop = a.transform.name.StartsWith("TopCollider");
        bool bIsTop = b.transform.name.StartsWith("TopCollider");
        return aIsTop != bIsTop;
    }

    private void MarkClosestCollidersUsed(LegoSnap target)
    {
        if (snapColliders == null || target.snapColliders == null) return;

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

    #endregion

    #region Detach

    public void DetachFromParent()
    {
        if (parentLego == null) return;
        if (body != null) body.AuthorityFree();
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

    #region Helpers

    private void PlaySnapSound()
    {
        LobbyController lobby = FindAnyObjectByType<LobbyController>();
        if (lobby != null)
            lobby.PlayLegoSnapSound();
    }

    private bool IsInSameGroup(LegoSnap other)
    {
        return GetRoot() == other.GetRoot();
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
