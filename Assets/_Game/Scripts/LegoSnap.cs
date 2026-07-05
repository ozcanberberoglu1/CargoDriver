using System.Collections.Generic;
using UnityEngine;

public class LegoSnap : MonoBehaviour
{
    [Header("Snap Settings")]
    [SerializeField] private float snapDistance = 3f;
    [SerializeField] private float snapDepth = 0.05f;

    private static readonly List<LegoSnap> allLegos = new();

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
        if (snapColliders == null || snapColliders.Length == 0)
        {
            Debug.Log($"[LegoSnap] {gameObject.name}: No snap colliders found!");
            return false;
        }

        Debug.Log($"[LegoSnap] {gameObject.name}: TrySnap called. MyColliders={snapColliders.Length}, AllLegos={allLegos.Count}");

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

        if (bestTarget == null)
        {
            Debug.Log($"[LegoSnap] {gameObject.name}: No compatible target found within {snapDistance}");
            return false;
        }

        Debug.Log($"[LegoSnap] {gameObject.name}: Snapping to {bestTarget.gameObject.name} dist={bestDist:F3}");
        SnapTo(bestTarget, bestMine, bestOther);
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
