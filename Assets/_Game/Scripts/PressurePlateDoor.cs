using System.Collections.Generic;
using Photon.Pun;
using TMPro;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

/// <summary>
/// A sliding double door opened by resting legos on a floor button. Players drop legos onto the
/// button; every few legos the door steps a little further open (with a shake). When ALL required
/// legos have sat there for a couple of seconds, the button presses fully down and the door opens
/// all the way. Remove legos and it slides back down through the same stages.
///
/// Multiplayer model (see CLAUDE.md §8):
/// - Authority: only the MASTER counts the legos, using its own authoritative physics, so a client
///   can't fake it. Legos being carried (Held) or pinned in the air (Frozen) don't count, nor do
///   ones still flying — only Free, settled bricks in the button volume. The full-open confirm
///   (all legos held for a couple seconds) is also decided by the master. Count and confirm go into
///   room properties, so they're synced and survive a master switch / late join.
/// - Everyone sees the same door: each client derives the open amount from the shared count/confirm
///   and slides its own doors + button toward it. The shake is a local cosmetic on top.
/// - Welded blocks count per brick: each CargoBox is its own body, so a 4-brick block counts as 4.
///
/// Setup: place the two doors CLOSED and the button UP, then enter each door's open local position
/// and the button's pressed-down local position. Assign the button's trigger collider and, if you
/// want it, a world-space count text. Play()/Stop() are a preview: they open/close the door with no
/// legos, for quickly checking the motion in the editor (right-click the component, or wire buttons).
/// </summary>
public class PressurePlateDoor : MonoBehaviour
{
    [Header("Doors (place them CLOSED; enter the open local positions)")]
    [SerializeField] private Transform leftDoor;
    [SerializeField] private Transform rightDoor;
    [SerializeField] private Vector3 leftOpenLocalPos;
    [SerializeField] private Vector3 rightOpenLocalPos;

    [Header("Button (place it UP; enter the pressed-down local position)")]
    [Tooltip("The pressable button model. It sinks toward buttonDownLocalPos as the door opens.")]
    [SerializeField] private Transform buttonTransform;
    [SerializeField] private Vector3 buttonDownLocalPos;

    [Header("Detection")]
    [Tooltip("Trigger volume on the button. Legos resting inside it are counted.")]
    [SerializeField] private Collider buttonZone;

    [Header("Requirement")]
    [SerializeField] private int requiredLegos = 12;
    [Tooltip("Door steps open once per this many legos (12 required / 4 per step = 3 steps).")]
    [SerializeField] private int legosPerStage = 4;
    [Tooltip("Once ALL legos are on, keep them this long before the button presses fully and the door opens all the way.")]
    [SerializeField] private float fullConfirmDelay = 2f;
    [Tooltip("A dip below the requirement shorter than this is ignored (stacked bricks jitter), so the door won't flicker. Only a sustained drop closes it.")]
    [SerializeField] private float releaseGrace = 0.5f;

    [Header("Motion")]
    [Tooltip("Slide speed, in open-fraction per second (1 = fully open in one second).")]
    [SerializeField] private float openSpeed = 0.6f;
    [SerializeField] private bool shakeWhileOpening = true;
    [SerializeField] private float shakeAmplitude = 0.03f;
    [SerializeField] private float shakeDuration = 0.4f;

    [Header("Count text (optional)")]
    [SerializeField] private TMP_Text countText;
    [Tooltip("Stays fully visible this long after the count changes.")]
    [SerializeField] private float textFadeDelay = 2f;
    [Tooltip("Then fades out over this long.")]
    [SerializeField] private float textFadeDuration = 1f;

    [Header("Networking")]
    [Tooltip("Unique per door — the room property base for this door. Give each door its own.")]
    [SerializeField] private string doorKey = "door1";
    [Tooltip("How often the master re-counts the button, in seconds.")]
    [SerializeField] private float evalInterval = 0.25f;

    private Vector3 leftClosedLocalPos, rightClosedLocalPos, buttonUpLocalPos;
    private float currentFraction;
    private int lastCount = -1;
    private int lastStage;
    private bool lastFull;
    private float evalTimer;
    private float fullConfirmTimer;
    private float belowTimer;
    private float shakeTimer;
    private float textAge;

    private bool testActive;    // Play/Stop preview override (local only)
    private float testTarget;

    private readonly Collider[] overlap = new Collider[128];
    private readonly HashSet<NetworkedCargoBody> counted = new HashSet<NetworkedCargoBody>();

    private string CountKey => "door_" + doorKey;
    private string FullKey => "door_" + doorKey + "_full";
    private int TotalStages => Mathf.Max(1, Mathf.CeilToInt(requiredLegos / (float)Mathf.Max(1, legosPerStage)));

    private void Start()
    {
        if (leftDoor != null) leftClosedLocalPos = leftDoor.localPosition;
        if (rightDoor != null) rightClosedLocalPos = rightDoor.localPosition;
        if (buttonTransform != null) buttonUpLocalPos = buttonTransform.localPosition;

        // Snap to whatever the door already is (matters for late joiners).
        currentFraction = TargetFraction();
        lastCount = ReadCount();
        lastStage = StageFor(lastCount);
        lastFull = ReadFull();
        ApplyMotion(currentFraction, Vector3.zero);

        if (countText != null)
        {
            textAge = textFadeDelay + textFadeDuration; // start hidden
            SetTextAlpha(0f);
        }
    }

    private void Update()
    {
        // Master owns the count and the full-open confirm — a client can't open the door by
        // holding legos over the button or by faking the timer.
        if (PhotonNetwork.IsMasterClient && PhotonNetwork.CurrentRoom != null)
        {
            evalTimer -= Time.deltaTime;
            if (evalTimer <= 0f)
            {
                evalTimer = evalInterval;
                int c = CountLegos();

                // Debounce: a brief dip below the requirement (jittering stack, the button pressing
                // the bricks) shouldn't reset the confirm or slam the door — only a sustained drop.
                if (c >= requiredLegos) belowTimer = 0f;
                else belowTimer += evalInterval;
                bool atRequired = belowTimer < releaseGrace;

                if (atRequired) fullConfirmTimer += evalInterval;
                else fullConfirmTimer = 0f;
                bool confirmed = fullConfirmTimer >= fullConfirmDelay;

                var props = new Hashtable();
                if (c != ReadCount()) props[CountKey] = c;
                if (confirmed != ReadFull()) props[FullKey] = confirmed;
                if (props.Count > 0) PhotonNetwork.CurrentRoom.SetCustomProperties(props);
            }
        }

        int count = ReadCount();
        if (count != lastCount)
        {
            int stage = StageFor(count);
            if (shakeWhileOpening && stage != lastStage) shakeTimer = shakeDuration;
            lastStage = stage;
            lastCount = count;
            if (countText != null) textAge = 0f; // pop the text back up on any change
        }

        // The full-open confirm is a big move (and no count change), so shake on it too.
        bool full = ReadFull();
        if (full != lastFull)
        {
            if (shakeWhileOpening) shakeTimer = shakeDuration;
            lastFull = full;
        }

        float target = TargetFraction();
        currentFraction = Mathf.MoveTowards(currentFraction, target, openSpeed * Time.deltaTime);

        Vector3 shake = Vector3.zero;
        if (shakeTimer > 0f)
        {
            shakeTimer -= Time.deltaTime;
            float amp = shakeAmplitude * (shakeTimer / shakeDuration);
            shake = new Vector3(Mathf.PerlinNoise(Time.time * 45f, 0.3f) - 0.5f,
                                Mathf.PerlinNoise(0.7f, Time.time * 45f) - 0.5f, 0f) * (2f * amp);
        }

        ApplyMotion(currentFraction, shake);
        UpdateText(count);
    }

    /// <summary>The open amount the door should be heading to right now.</summary>
    private float TargetFraction()
    {
        if (testActive) return testTarget;

        // Full stays until the master releases it (with its debounce), so a momentary count dip
        // from jittering / pressed bricks never drops the door back down.
        if (ReadFull()) return 1f;

        int count = ReadCount();
        // All legos are on but the confirm hasn't landed yet — hold one step short of full.
        if (count >= requiredLegos) return (float)(TotalStages - 1) / TotalStages;

        return FractionFor(count);
    }

    private int CountLegos()
    {
        if (buttonZone == null) return 0;

        counted.Clear();
        int n = Physics.OverlapBoxNonAlloc(buttonZone.bounds.center, buttonZone.bounds.extents,
            overlap, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);

        int count = 0;
        for (int i = 0; i < n; i++)
        {
            NetworkedCargoBody body = overlap[i].GetComponentInParent<NetworkedCargoBody>();
            if (body == null || counted.Contains(body)) continue;
            counted.Add(body);
            if (IsPlaced(body)) count++;
        }
        return count;
    }

    /// <summary>
    /// True only for a brick actually placed on the button — not carried (Held) or pinned in the
    /// air (Frozen). Deliberately does NOT check velocity: stacked bricks jitter forever, and a
    /// velocity gate makes the count (and the door) flicker.
    /// </summary>
    private bool IsPlaced(NetworkedCargoBody body)
    {
        LegoSnap snap = body.GetComponent<LegoSnap>();
        NetworkedCargoBody root = snap != null ? snap.GetRoot().GetComponent<NetworkedCargoBody>() : body;
        if (root == null) root = body;
        return root.State == CargoState.Free;
    }

    private int StageFor(int count) => Mathf.Clamp(count / Mathf.Max(1, legosPerStage), 0, TotalStages);
    private float FractionFor(int count) => (float)StageFor(count) / TotalStages;

    private int ReadCount()
    {
        if (PhotonNetwork.CurrentRoom == null) return 0;
        return PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(CountKey, out object v) ? (int)v : 0;
    }

    private bool ReadFull()
    {
        if (PhotonNetwork.CurrentRoom == null) return false;
        return PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(FullKey, out object v) && (bool)v;
    }

    private void ApplyMotion(float fraction, Vector3 shake)
    {
        if (leftDoor != null)
            leftDoor.localPosition = Vector3.Lerp(leftClosedLocalPos, leftOpenLocalPos, fraction) + shake;
        if (rightDoor != null)
            rightDoor.localPosition = Vector3.Lerp(rightClosedLocalPos, rightOpenLocalPos, fraction) + shake;
        if (buttonTransform != null)
            buttonTransform.localPosition = Vector3.Lerp(buttonUpLocalPos, buttonDownLocalPos, fraction);
    }

    private void UpdateText(int count)
    {
        if (countText == null) return;

        countText.text = $"{count}/{requiredLegos}";
        textAge += Time.deltaTime;

        float alpha = textAge <= textFadeDelay
            ? 1f
            : 1f - (textAge - textFadeDelay) / Mathf.Max(0.0001f, textFadeDuration);
        SetTextAlpha(Mathf.Clamp01(alpha));
    }

    private void SetTextAlpha(float a)
    {
        Color c = countText.color;
        c.a = a;
        countText.color = c;
    }

    // ---- Preview controls (editor testing; local only, no legos needed) ----

    /// <summary>Preview: slide the door fully open with no legos. Wire to a button or right-click.</summary>
    [ContextMenu("▶ Play (preview open)")]
    public void Play()
    {
        testActive = true;
        testTarget = 1f;
    }

    /// <summary>Preview: slide the door back closed and reset. Wire to a button or right-click.</summary>
    [ContextMenu("■ Stop (reset closed)")]
    public void Stop()
    {
        testActive = true;
        testTarget = 0f;
    }
}
