using System.Collections;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;
using Hashtable = ExitGames.Client.Photon.Hashtable;

/// <summary>
/// Lobby cargo dispenser. Players click the 3D ButtonObject; the MASTER then spawns
/// random lego boxes at the spawn point over time until the target count is reached,
/// and they fall out under gravity.
///
/// Multiplayer model (see CLAUDE.md §8):
/// - Authority: only the master spawns (InstantiateRoomObject), so the count is
///   authoritative and never multiplied by the number of clients. A click is just an
///   input — non-master clients raise an event to the master, which arbitrates.
/// - Everyone sees the same boxes: the random prefab + tint are chosen ONCE on the
///   master and travel to every client through the instantiation data. NetworkedCargoBody
///   applies the color locally in OnPhotonInstantiate, so late joiners get it too (the
///   instantiate event is buffered by the room).
/// - Progress lives in a room property, so it survives a master switch and reaches late
///   joiners; on a master switch the new master resumes an unfinished run.
/// - Falling is simulated by the writer (the master owns the room object), streamed to
///   the rest — the same Free-state physics the lobby already uses.
/// </summary>
public class CargoMachine : MonoBehaviourPunCallbacks, IOnEventCallback
{
    [Header("Scene references")]
    [Tooltip("Spawn origin — boxes appear here and fall down.")]
    [SerializeField] private Transform legoSpawner;
    [Tooltip("Collider of the 3D ButtonObject players click.")]
    [SerializeField] private Collider buttonCollider;
    [Tooltip("Optional. Camera used to raycast the click. Falls back to Camera.main.")]
    [SerializeField] private Camera clickCamera;

    [Header("Lego pool  (prefabs MUST live in a Resources folder)")]
    [SerializeField] private List<GameObject> legoPrefabs = new List<GameObject>();
    [Tooltip("Random tint pool. Only each material's base color is networked, not the whole material.")]
    [SerializeField] private List<Material> legoMaterials = new List<Material>();

    [Header("Spawning")]
    [SerializeField] private int maxLegos = 50;
    [SerializeField] private float spawnInterval = 0.25f;
    [Tooltip("Random horizontal scatter at the nozzle so boxes don't spawn perfectly stacked.")]
    [SerializeField] private float spawnRadius = 0.3f;
    [SerializeField] private float clickRayDistance = 100f;

    private const byte SPAWN_REQUEST_EVENT = 70;
    private const string PROP_COUNT = "legoCount";
    private const string PROP_ACTIVE = "machineActive";
    // Turns true only once the whole batch is out, so the lobby's "cargo loaded" check
    // won't fire on a handful of early boxes while the rest are still being dispensed.
    public const string PROP_READY = "legosReady";

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private bool isSpawning;
    private Camera cachedCam;

    // MonoBehaviourPunCallbacks already registers this object with PhotonNetwork in its
    // OnEnable/OnDisable, which also wires up IOnEventCallback — no manual AddCallbackTarget.

    private void Update()
    {
        // Click detection is purely local: each player raycasts from their own camera.
        if (!PhotonNetwork.InRoom) return;

        Mouse mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
        if (buttonCollider == null) return;

        Camera cam = ResolveLocalCamera();
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, clickRayDistance)) return;
        if (hit.collider != buttonCollider) return;

        RequestSpawn();
    }

    /// <summary>Local click → ask the master to run the machine.</summary>
    private void RequestSpawn()
    {
        if (CurrentCount() >= maxLegos) return;

        if (PhotonNetwork.IsMasterClient)
            StartMachine();
        else
            PhotonNetwork.RaiseEvent(SPAWN_REQUEST_EVENT, null,
                new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient },
                new SendOptions { DeliveryMode = DeliveryMode.Reliable });
    }

    public void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != SPAWN_REQUEST_EVENT) return;
        StartMachine(); // ignored on non-master by the guard below
    }

    public override void OnMasterClientSwitched(Player newMaster)
    {
        // If the old master left mid-run, the new master picks the run back up.
        if (!PhotonNetwork.IsMasterClient) return;
        if (IsActive() && !isSpawning && CurrentCount() < maxLegos)
            StartCoroutine(SpawnLoop());
    }

    private void StartMachine()
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (isSpawning) return;                 // already dispensing
        if (CurrentCount() >= maxLegos) return; // machine is full

        if (legoPrefabs == null || legoPrefabs.Count == 0)
        {
            Debug.LogError("[CargoMachine] No lego prefabs assigned.");
            return;
        }
        if (legoSpawner == null)
        {
            Debug.LogError("[CargoMachine] LegoSpawner transform not assigned.");
            return;
        }

        SetReadyProp(false);
        SetActiveProp(true);
        StartCoroutine(SpawnLoop());
    }

    private IEnumerator SpawnLoop()
    {
        isSpawning = true;

        while (CurrentCount() < maxLegos)
        {
            if (!PhotonNetwork.IsMasterClient) break; // lost authority mid-run

            SpawnOne();
            SetCountProp(CurrentCount() + 1);

            yield return new WaitForSeconds(spawnInterval);
        }

        isSpawning = false;
        if (PhotonNetwork.IsMasterClient)
        {
            SetActiveProp(false);
            // Only announce "ready" if the batch actually completed (not a mid-run abort).
            if (CurrentCount() >= maxLegos)
                SetReadyProp(true);
        }
    }

    private void SpawnOne()
    {
        GameObject prefab = legoPrefabs[Random.Range(0, legoPrefabs.Count)];
        if (prefab == null) return;

        Vector2 off = Random.insideUnitCircle * spawnRadius;
        Vector3 pos = legoSpawner.position + new Vector3(off.x, 0f, off.y);
        Quaternion rot = Random.rotationUniform;
        Vector3 scale = prefab.transform.localScale;
        Color tint = PickColor();

        // Same 8-slot layout NetworkedCargoBody.OnPhotonInstantiate already understands:
        // scale (x,y,z), legoParentViewId (-1 = not welded to anything), then RGBA tint.
        object[] data = { scale.x, scale.y, scale.z, -1, tint.r, tint.g, tint.b, tint.a };

        // InstantiateRoomObject → the box is owned by the room (master), which matches how
        // the lobby's scene-placed boxes behave; DistributedOwnership hands it to whoever
        // grabs it later. The prefab name must resolve under a Resources folder.
        PhotonNetwork.InstantiateRoomObject(prefab.name, pos, rot, 0, data);
    }

    /// <summary>
    /// The local player's camera. The Toy1 camera is Untagged (so Camera.main is unreliable)
    /// and only the local player's camera is enabled, so we pull it off the IsMine ToyController.
    /// Only runs on a click frame, so the scene scan is cheap.
    /// </summary>
    private Camera ResolveLocalCamera()
    {
        if (clickCamera != null) return clickCamera;
        if (cachedCam != null && cachedCam.isActiveAndEnabled) return cachedCam;

        foreach (ToyController tc in FindObjectsByType<ToyController>(FindObjectsSortMode.None))
        {
            if (tc.photonView.IsMine && tc.PlayerCamera != null)
            {
                cachedCam = tc.PlayerCamera;
                return cachedCam;
            }
        }
        return Camera.main; // last resort
    }

    private Color PickColor()
    {
        if (legoMaterials == null || legoMaterials.Count == 0) return Color.white;

        Material m = legoMaterials[Random.Range(0, legoMaterials.Count)];
        if (m == null) return Color.white;
        return m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId) : m.color;
    }

    private int CurrentCount()
    {
        if (PhotonNetwork.CurrentRoom == null) return 0;
        return PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(PROP_COUNT, out object v) ? (int)v : 0;
    }

    private bool IsActive()
    {
        if (PhotonNetwork.CurrentRoom == null) return false;
        return PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(PROP_ACTIVE, out object v) && (bool)v;
    }

    private void SetCountProp(int value)
    {
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { PROP_COUNT, value } });
    }

    private void SetActiveProp(bool value)
    {
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { PROP_ACTIVE, value } });
    }

    private void SetReadyProp(bool value)
    {
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { PROP_READY, value } });
    }
}
