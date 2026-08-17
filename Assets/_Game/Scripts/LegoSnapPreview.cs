using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Local-only snap hint for a cargo box. While the local player carries a lego, the driver
/// (<see cref="CargoPickup"/>) lights the green/red grid on the TARGET box's stud the carried
/// lego would connect to.
///
/// Multiplayer note: the grids are plain child GameObjects toggled with SetActive, which is
/// never networked (PhotonView/PhotonTransformView don't replicate child active state). Only
/// the carrying player's client runs the driver, so no one ever sees another player's hints.
///
/// Wire one slot per TopCollider in the inspector:
///   CargoBox  -> 1 slot: TopCollider  = GridImage-Green  / GridImage-Red
///   CargoBox2 -> 2 slots: TopCollider1 = GridImage-Green  / GridImage-Red
///                         TopCollider2 = GridImage-Green2 / GridImage-Red2
/// (match each grid to the stud it physically sits above).
/// </summary>
public class LegoSnapPreview : MonoBehaviour
{
    [System.Serializable]
    public struct StudSlot
    {
        [Tooltip("The TopCollider this stud represents (same one LegoSnap uses).")]
        public Collider topCollider;
        [Tooltip("Shown when a snap onto this stud is valid.")]
        public GameObject green;
        [Tooltip("Shown when the carried lego is near this stud but too far to snap.")]
        public GameObject red;
    }

    [SerializeField] private List<StudSlot> slots = new List<StudSlot>();

    private void Awake()
    {
        Validate();
        HideAll();
    }

    /// <summary>Warns if a slot's Top Collider isn't actually a TopCollider stud (common wiring mistake).</summary>
    private void Validate()
    {
        for (int i = 0; i < slots.Count; i++)
        {
            Collider top = slots[i].topCollider;
            if (top == null)
                Debug.LogWarning($"[LegoSnapPreview] {name} slot {i}: Top Collider is not assigned.", this);
            else if (!top.name.StartsWith("TopCollider"))
                Debug.LogWarning($"[LegoSnapPreview] {name} slot {i}: Top Collider is '{top.name}', expected the child named 'TopCollider'. " +
                                 "Drag the TopCollider child object into this field, not the box body collider.", this);
        }
    }

    /// <summary>Turns every grid on this box off.</summary>
    public void HideAll()
    {
        foreach (StudSlot s in slots)
        {
            if (s.green != null) s.green.SetActive(false);
            if (s.red != null) s.red.SetActive(false);
        }
    }

    /// <summary>Lights the grid for one stud: green=true shows green, otherwise red. Returns whether a slot matched.</summary>
    public bool Show(Collider topCollider, bool green)
    {
        if (topCollider == null) return false;

        foreach (StudSlot s in slots)
        {
            if (s.topCollider == null) continue;
            // Match by instance, or by name as a fallback (robust to prefab/clone reference quirks).
            bool match = s.topCollider == topCollider || s.topCollider.name == topCollider.name;
            if (!match) continue;

            if (s.green != null) s.green.SetActive(green);
            if (s.red != null) s.red.SetActive(!green);
            return true;
        }
        return false;
    }

}
