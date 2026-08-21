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
    [Tooltip("Past snapDistance but within this, the local grid hint shows red (near but not snap-able).")]
    [SerializeField] private float previewRedDistance = 5f;

    private static readonly List<LegoSnap> allLegos = new();
    private static readonly HashSet<Collider> usedColliders = new();

    private LegoSnap parentLego;
    private readonly List<LegoSnap> childLegos = new();
    private Collider[] snapColliders;

    private NetworkedCargoBody body;

    public bool HasParent => parentLego != null;
    public float SnapDistance => snapDistance;
    public float PreviewRedDistance => previewRedDistance;

    /// <summary>One target stud the carried lego is hovering over, for the local grid hint.</summary>
    public struct SnapPreviewHit
    {
        public LegoSnap targetLego;   // box that owns the stud (and its LegoSnapPreview)
        public Collider targetTop;    // the TopCollider the carried lego would land on
        public bool green;            // true = close enough to snap; false = near but too far
    }

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
                if (!ScalesMatch(transform, other.transform)) continue; // only equal-scale bricks snap

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

        NetworkedCargoBody childBody = snapChild.GetComponent<NetworkedCargoBody>();
        if (childBody == null) return false;

        Transform parentT = snapParent.transform;
        Transform childT = snapChild.transform;

        // Square the brick to the parent's stud grid (nearest 90°) so it never welds crooked,
        // no matter how the player was holding/rotating it.
        Quaternion localRot = SnapTo90(Quaternion.Inverse(parentT.rotation) * childT.rotation);
        Quaternion targetWorldRot = parentT.rotation * localRot;

        bool childIsDown = childCol.transform.name.StartsWith("DownCollider");
        float depth = (childIsDown ? snapDepth : -snapDepth) * parentT.lossyScale.y;
        Vector3 targetColCenter = parentCol.bounds.center + parentT.up * depth;

        Vector3 colLocal = Quaternion.Inverse(childT.rotation) * (childCol.bounds.center - childT.position);
        Vector3 snappedWorldPos = targetColCenter - targetWorldRot * colLocal;
        Vector3 localPos = parentT.InverseTransformPoint(snappedWorldPos);

        // TEMP diagnostics — measure the real geometry so we can compute the exact flush offset.
        Collider pBody = OwnerBody(parentCol);
        Collider cBody = OwnerBody(childCol);
        Debug.Log($"[Snap] scale={parentT.lossyScale.y:0.00} snapDepth={snapDepth} " +
                  $"parentTop(center.y={parentCol.bounds.center.y:0.000} h={parentCol.bounds.size.y:0.000}) " +
                  $"childDown(center.y={childCol.bounds.center.y:0.000} h={childCol.bounds.size.y:0.000}) " +
                  $"parentBody(top={(pBody != null ? pBody.bounds.max.y : 0):0.000} h={(pBody != null ? pBody.bounds.size.y : 0):0.000}) " +
                  $"childBody(bottom={(cBody != null ? cBody.bounds.min.y : 0):0.000} h={(cBody != null ? cBody.bounds.size.y : 0):0.000}) " +
                  $"childRoot.y={childT.position.y:0.000} snappedY={snappedWorldPos.y:0.000}");

        childBody.AuthorityStow(parentT, localPos, localRot);

        PlaySnapSound();
        return true;
    }

    /// <summary>
    /// Non-committing sibling of <see cref="TrySnap"/> used to drive the local grid hints.
    /// For this carried structure, returns the target studs the player is hovering over,
    /// each flagged green (would snap, matching TrySnap's snapDistance) or red (near but too
    /// far). Only Down→Top matches are reported, since the grids live on the target's top
    /// studs. Empty when nothing is within range. Touches no state — pure read.
    /// </summary>
    public void EvaluatePreview(List<SnapPreviewHit> results)
    {
        results.Clear();

        List<LegoSnap> members = GetAllConnected();

        // Each of the carried block's exposed bottom studs claims the nearest target stud.
        // Several studs of the block may claim the SAME target stud; the CargoPickup driver
        // then lets green win, so a stud the bottom lego can actually snap onto reads green
        // even while another, farther lego of the block also hovers over it.
        foreach (LegoSnap member in members)
        {
            if (member.snapColliders == null) continue;

            foreach (Collider down in member.snapColliders)
            {
                if (down == null || usedColliders.Contains(down)) continue;
                if (!down.name.StartsWith("DownCollider")) continue; // carried lands via its bottom studs

                Collider bestTop = null;
                LegoSnap bestLego = null;
                float bestDist = float.MaxValue;

                foreach (LegoSnap other in allLegos)
                {
                    if (other == null || members.Contains(other)) continue; // skip our own structure
                    if (other.snapColliders == null) continue;
                    if (!ScalesMatch(member.transform, other.transform)) continue; // only equal-scale bricks

                    foreach (Collider top in other.snapColliders)
                    {
                        if (top == null || usedColliders.Contains(top)) continue;
                        if (!top.name.StartsWith("TopCollider")) continue;

                        float dist = Vector3.Distance(down.bounds.center, top.bounds.center);
                        if (dist <= previewRedDistance && dist < bestDist)
                        {
                            bestDist = dist;
                            bestTop = top;
                            bestLego = other;
                        }
                    }
                }

                if (bestTop == null) continue;

                results.Add(new SnapPreviewHit
                {
                    targetLego = bestLego,
                    targetTop = bestTop,
                    green = bestDist <= snapDistance
                });
            }
        }
    }

    /// <summary>Only bricks scaled to the same size may connect — their stud grids line up.</summary>
    private static bool ScalesMatch(Transform a, Transform b)
    {
        return Mathf.Abs(a.lossyScale.x - b.lossyScale.x) < 0.05f;
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

    /// <summary>The main body collider of the brick that owns a stud collider.</summary>
    private static Collider OwnerBody(Collider stud)
    {
        LegoSnap owner = stud.GetComponentInParent<LegoSnap>();
        return owner != null ? owner.GetComponent<Collider>() : null;
    }

    /// <summary>Rounds a rotation to the nearest 90° on each axis so bricks weld square to the grid.</summary>
    private static Quaternion SnapTo90(Quaternion q)
    {
        Vector3 e = q.eulerAngles;
        e.x = Mathf.Round(e.x / 90f) * 90f;
        e.y = Mathf.Round(e.y / 90f) * 90f;
        e.z = Mathf.Round(e.z / 90f) * 90f;
        return Quaternion.Euler(e);
    }

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
