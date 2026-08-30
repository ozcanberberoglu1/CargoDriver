using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Persistent loading overlay covering room-join / scene-change waits.
///
/// A session starts when the player commits to entering a room (call <see cref="Begin"/> from the
/// create/join button) or when PUN begins syncing a scene ("curScn" room property). The bar creeps
/// up on a timer and holds near full while we wait; once the destination scene has actually loaded
/// it fills to 100% and hides. This covers BOTH the connect/create wait (before any scene load) and
/// the async scene load itself.
///
/// 15 empty circles fill one-by-one (emptySprite → fullSprite).
///
/// Put this on the LoadingPanel prefab (needs its OWN Canvas) and drop one ACTIVE instance into
/// SampleScene. It hides via Canvas.enabled, so the GameObject must stay active.
/// </summary>
public class LoadingScreen : MonoBehaviourPunCallbacks
{
    public static LoadingScreen Instance { get; private set; }

    [Header("Bar segments (in order, empty → full)")]
    [SerializeField] private Image[] segments;         // 15 circle images
    [SerializeField] private Sprite emptySprite;       // Spr_GameAtlas_14
    [SerializeField] private Sprite fullSprite;        // Spr_GameAtlas_13

    [Header("Visual root to show / hide (defaults to the Canvas on this object)")]
    [SerializeField] private Canvas canvas;

    [Header("Timing")]
    [Tooltip("Seconds for the bar to visually travel 0 → 1.")]
    [SerializeField] private float fillTime = 1.6f;
    [Tooltip("Never show the overlay for less than this.")]
    [SerializeField] private float minShowTime = 1.2f;
    [Tooltip("Bar holds here while waiting, until the destination scene actually loads.")]
    [Range(0.5f, 0.98f)][SerializeField] private float creepCap = 0.9f;
    [Tooltip("Auto-cancel if a load never completes (e.g. create/join failed).")]
    [SerializeField] private float safetyTimeout = 20f;
    [SerializeField] private bool debugLog = true;

    private float displayed;
    private int lastFilled = -1;
    private bool active;
    private bool arrived;
    private float elapsed;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (canvas == null) canvas = GetComponent<Canvas>();
        if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
        if (canvas == null) Debug.LogError("[Loading] No Canvas found — can't show/hide.");

        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        if (debugLog) Debug.Log("[Loading] Ready.");
        HideImmediate();
    }

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
    }

    /// <summary>Start the overlay now — call the instant the player commits to a room.</summary>
    public void Begin()
    {
        if (active) return;
        active = true;
        arrived = false;
        elapsed = 0f;
        displayed = 0f;
        lastFilled = -1;
        ApplySegments(0f);
        if (canvas != null) canvas.enabled = true;
        if (debugLog) Debug.Log("[Loading] Begin.");
    }

    /// <summary>Abort the overlay (call on create/join failure).</summary>
    public void Cancel()
    {
        if (!active) return;
        if (debugLog) Debug.Log("[Loading] Cancel.");
        HideImmediate();
    }

    // PUN starts syncing a scene → make sure the overlay is up (covers clients that didn't click).
    public override void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable propertiesThatChanged)
    {
        if (propertiesThatChanged != null && propertiesThatChanged.ContainsKey("curScn"))
            Begin();
    }

    private void OnActiveSceneChanged(Scene from, Scene to)
    {
        if (active)
        {
            arrived = true;
            if (debugLog) Debug.Log($"[Loading] Arrived: {to.name}");
        }
    }

    private void Update()
    {
        if (!active) return;
        elapsed += Time.unscaledDeltaTime;

        // Prefer the real async load progress when a scene is actually loading — this makes the bar
        // track a slow scene load (e.g. heavy GameScene) instead of hitting the cap and freezing.
        // Otherwise (connect/create wait, no scene load yet) creep on a timer.
        float real = PhotonNetwork.LevelLoadingProgress;   // (0,1) only while loading
        bool loading = real > 0.001f && real < 0.999f;

        float goal;
        if (arrived) goal = 1f;
        else if (loading) goal = Mathf.Clamp01(real);
        else goal = Mathf.Min(creepCap, elapsed / fillTime);

        if (goal < displayed) goal = displayed; // never run backwards

        float step = (1f / Mathf.Max(0.01f, fillTime)) * Time.unscaledDeltaTime;
        displayed = Mathf.MoveTowards(displayed, goal, step);
        ApplySegments(displayed);

        if (arrived && displayed >= 1f && elapsed >= minShowTime)
        {
            if (debugLog) Debug.Log("[Loading] Done → hide.");
            HideImmediate();
        }
        else if (elapsed >= safetyTimeout)
        {
            Debug.LogWarning("[Loading] Timeout → hide.");
            HideImmediate();
        }
    }

    private void HideImmediate()
    {
        active = false;
        arrived = false;
        displayed = 0f;
        lastFilled = -1;
        elapsed = 0f;
        if (canvas != null) canvas.enabled = false;
    }

    private void ApplySegments(float t)
    {
        if (segments == null || segments.Length == 0) return;

        int filled = Mathf.Clamp(Mathf.FloorToInt(t * segments.Length), 0, segments.Length);
        if (filled == lastFilled) return;
        lastFilled = filled;

        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i] == null) continue;
            segments[i].sprite = i < filled ? fullSprite : emptySprite;
        }
    }
}
