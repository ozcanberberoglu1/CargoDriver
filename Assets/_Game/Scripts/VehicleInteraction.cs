using System.Collections;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;
using Hashtable = ExitGames.Client.Photon.Hashtable;

/// <summary>
/// Local-only controller for getting in and out of the truck with F.
///
/// Two kinds of occupant share this flow. The driver has no avatar while seated and spawns
/// one on the ground when leaving. The rear passenger always has an avatar and simply stops
/// being parented to the seat. Either way the rule is the same: a seated player has no
/// collider, and a player on foot is a normal character that the truck can not be entered by.
/// </summary>
public class VehicleInteraction : MonoBehaviour, IOnEventCallback
{
    [SerializeField] private float enterRange = 3.5f;
    [SerializeField] private float exitClearance = 1.4f;
    [SerializeField] private float groundProbeHeight = 6f;
    [SerializeField] private string playerPrefabName = "Toy1";

    private const string RidingKey = "riding";
    private const string BehindKey = "ctrl_Behind";
    private const byte BoardEvent = 60;
    private static readonly string[] DriveKeys = { "ctrl_W", "ctrl_A", "ctrl_S", "ctrl_D", "ctrl_Space" };

    private GameObject spawnedPlayer;
    private GameObject cursorUI;
    private bool isInCar = true;
    private bool isBehindPlayer;

    private readonly List<string> savedControls = new();
    private float deathCooldown;
    private bool everSeated;

    #region Lifecycle

    private void OnEnable() => PhotonNetwork.AddCallbackTarget(this);

    private void OnDisable() => PhotonNetwork.RemoveCallbackTarget(this);

    private IEnumerator Start()
    {
        cursorUI = FindCursorUI();
        if (cursorUI != null)
            cursorUI.SetActive(false);

        StartCoroutine(ReconcileRidersLoop());

        if (!IsBehindRole()) yield break;

        isBehindPlayer = true;
        SpawnBehindVehicle();

        GameObject pickup;
        while ((pickup = FindPickup()) == null)
            yield return null;

        yield return null;
        SeatLocalPlayer(pickup);
    }

    private void Update()
    {
        if (deathCooldown > 0f)
            deathCooldown -= Time.deltaTime;

        if (!isInCar)
            CheckFellOffMap();

        Keyboard kb = Keyboard.current;
        if (kb == null || !kb.fKey.wasPressedThisFrame) return;

        if (isInCar)
            ExitCar();
        else
            TryEnterCar();
    }

    #endregion

    #region Enter and exit

    private void ExitCar()
    {
        GameObject pickup = FindPickup();
        if (pickup == null) return;

        Vector3 exitPos = FindGroundExitPoint(pickup, out Quaternion exitRot);

        if (isBehindPlayer)
        {
            ToyController tc = spawnedPlayer != null ? spawnedPlayer.GetComponent<ToyController>() : null;
            if (tc == null) return;

            tc.DetachFromVehicle();
            tc.TeleportTo(exitPos, exitRot);
        }
        else
        {
            spawnedPlayer = PhotonNetwork.Instantiate(playerPrefabName, exitPos, exitRot);

            ToyController fresh = spawnedPlayer.GetComponent<ToyController>();
            if (fresh != null)
                fresh.SetPhysicsGhost(true);

            SetCarCameraActive(false);
        }

        SetRidingProperty(false);
        SaveAndClearMyControls();

        isInCar = false;
    }

    private void TryEnterCar()
    {
        if (spawnedPlayer == null) return;

        GameObject pickup = FindPickup();
        if (pickup == null) return;
        if (!IsWithinEnterRange(pickup, spawnedPlayer.transform.position)) return;

        Board(pickup);
    }

    private void Board(GameObject pickup)
    {
        if (spawnedPlayer == null) return;

        if (isBehindPlayer)
        {
            SeatLocalPlayer(pickup);
        }
        else
        {
            SetCarCameraActive(true);

            if (cursorUI != null)
                cursorUI.SetActive(false);

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            PhotonNetwork.Destroy(spawnedPlayer);
            spawnedPlayer = null;
        }

        SetRidingProperty(true);
        RestoreMyControls();

        isInCar = true;
    }

    private void SpawnBehindVehicle()
    {
        spawnedPlayer = PhotonNetwork.Instantiate(playerPrefabName, new Vector3(0f, -100f, 0f), Quaternion.identity);
        isInCar = false;

        // Parked out of sight until the truck exists; freezing it stops the avatar falling
        // through the world in the meantime.
        ToyController tc = spawnedPlayer.GetComponent<ToyController>();
        if (tc != null)
        {
            tc.SetMovementLocked(true);
            tc.SetPhysicsGhost(true);
        }

        CargoPickup cp = spawnedPlayer.GetComponent<CargoPickup>();
        if (cp != null)
        {
            cp.grabDistance = 1.5f;
            cp.detectRange = 5f;
        }
    }

    private void SeatLocalPlayer(GameObject pickup)
    {
        if (spawnedPlayer == null) return;

        ToyController tc = spawnedPlayer.GetComponent<ToyController>();
        if (tc == null) return;

        SetCarCameraActive(false);
        tc.AttachToVehicle(SeatOf(pickup));

        SetRidingProperty(true);
        isInCar = true;
        everSeated = true;
    }

    private bool IsWithinEnterRange(GameObject pickup, Vector3 from)
    {
        Bounds bounds = VehicleBounds(pickup);
        return Vector3.Distance(from, bounds.ClosestPoint(from)) <= enterRange;
    }

    #endregion

    #region Checkpoint respawn

    /// <summary>
    /// Host-side entry point for a checkpoint reset. A respawn restores the whole convoy,
    /// not just the truck, so anyone who wandered off on foot is put back in their seat.
    /// The host is included in the broadcast so every client runs the same path.
    /// </summary>
    public static void BoardEveryone()
    {
        if (!PhotonNetwork.InRoom) return;

        PhotonNetwork.RaiseEvent(BoardEvent, null,
            new RaiseEventOptions { Receivers = ReceiverGroup.All },
            SendOptions.SendReliable);
    }

    public void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != BoardEvent) return;
        if (isInCar) return;

        GameObject pickup = FindPickup();
        if (pickup == null) return;

        // Range is deliberately not checked: the truck is about to be teleported to the
        // checkpoint, so how far away the player currently stands is irrelevant.
        Board(pickup);
    }

    #endregion

    #region Exit placement

    /// <summary>
    /// Walks a ring of directions around the truck and returns the first spot with ground
    /// under it and room for the character to stand. Nothing here may land inside the
    /// bodywork, which is exactly what the old seat anchor did.
    /// </summary>
    private Vector3 FindGroundExitPoint(GameObject pickup, out Quaternion rotation)
    {
        Transform t = pickup.transform;
        Bounds bounds = VehicleBounds(pickup);

        Vector3[] directions =
        {
            -t.right,
            t.right,
            -t.forward,
            (-t.right - t.forward).normalized,
            (t.right - t.forward).normalized,
            (-t.right + t.forward).normalized,
            (t.right + t.forward).normalized,
            t.forward
        };

        foreach (Vector3 dir in directions)
        {
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 0.001f) continue;
            flat.Normalize();

            // How far the bodywork reaches along this direction, so a step to the side lands
            // beside the cab rather than a truck length away from it.
            float reach = Mathf.Abs(flat.x) * bounds.extents.x + Mathf.Abs(flat.z) * bounds.extents.z;

            if (!TryGroundAt(bounds.center + flat * (reach + exitClearance), out Vector3 feet)) continue;

            rotation = Quaternion.LookRotation(flat, Vector3.up);
            return feet;
        }

        // Every side was blocked, so drop onto the roof rather than into the geometry.
        rotation = t.rotation;
        return bounds.center + Vector3.up * (bounds.extents.y + 0.5f);
    }

    private bool TryGroundAt(Vector3 probe, out Vector3 feet)
    {
        feet = probe;

        int mask = ~PlayerLayerMask();
        Vector3 origin = probe + Vector3.up * groundProbeHeight;

        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                groundProbeHeight * 2f, mask, QueryTriggerInteraction.Ignore))
            return false;

        Vector3 candidate = hit.point + Vector3.up * 0.05f;

        // The capsule has to fit, otherwise we would spawn the player inside a wall or box.
        const float radius = 0.3f;
        const float height = 2f;
        Vector3 bottom = candidate + Vector3.up * radius;
        Vector3 top = candidate + Vector3.up * (height - radius);

        if (Physics.CheckCapsule(bottom, top, radius * 0.95f, mask, QueryTriggerInteraction.Ignore))
            return false;

        feet = candidate;
        return true;
    }

    private static int PlayerLayerMask()
    {
        int playerLayer = LayerMask.NameToLayer("Player");
        return playerLayer >= 0 ? 1 << playerLayer : 0;
    }

    private static Bounds VehicleBounds(GameObject pickup)
    {
        bool found = false;
        Bounds bounds = new(pickup.transform.position, Vector3.one);

        foreach (Collider col in pickup.GetComponentsInChildren<Collider>())
        {
            if (col.isTrigger) continue;
            if (col is WheelCollider) continue;
            // A seated avatar is parented under the truck and is not part of its shape.
            if (col.GetComponentInParent<ToyController>() != null) continue;

            if (!found)
            {
                bounds = col.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(col.bounds);
            }
        }

        return bounds;
    }

    #endregion

    #region Falling off the map

    private void CheckFellOffMap()
    {
        if (deathCooldown > 0f || spawnedPlayer == null) return;
        // The rear passenger's avatar waits below the world until the truck exists.
        if (!everSeated && isBehindPlayer) return;

        Collider dead = GameSceneController.DeadZone;
        if (dead == null) return;

        Vector3 pos = spawnedPlayer.transform.position;
        Bounds body = new(pos + Vector3.up, new Vector3(0.6f, 2f, 0.6f));
        if (!dead.bounds.Intersects(body)) return;

        GameObject pickup = FindPickup();
        if (pickup == null) return;

        ToyController tc = spawnedPlayer.GetComponent<ToyController>();
        if (tc == null) return;

        Vector3 respawn = FindGroundExitPoint(pickup, out Quaternion rot);
        tc.TeleportTo(respawn, rot);
        deathCooldown = 2f;
    }

    #endregion

    #region Riding state replication

    /// <summary>
    /// Seating is published as a player property so it reaches late joiners for free, and
    /// reconciled on a timer so it does not matter whether the avatar or the property
    /// arrives first.
    /// </summary>
    private IEnumerator ReconcileRidersLoop()
    {
        var wait = new WaitForSeconds(0.5f);

        while (true)
        {
            yield return wait;

            GameObject pickup = FindPickup();
            if (pickup == null) continue;

            Transform seat = SeatOf(pickup);

            foreach (ToyController tc in FindObjectsByType<ToyController>(FindObjectsSortMode.None))
            {
                PhotonView pv = tc.photonView;
                if (pv == null || pv.Owner == null || pv.IsMine) continue;

                bool shouldRide = IsRiding(pv.Owner);
                if (shouldRide == tc.IsRidingVehicle) continue;

                if (shouldRide)
                    tc.AttachToVehicle(seat);
                else
                    tc.DetachFromVehicle();
            }
        }
    }

    /// <summary>
    /// Only the rear passenger ever owns an avatar while seated. Gating on the role keeps a
    /// driver's freshly spawned avatar from being seated by a client that has not yet
    /// received their updated property.
    /// </summary>
    private static bool IsRiding(Photon.Realtime.Player player)
    {
        if (!IsBehindActor(player.ActorNumber)) return false;
        if (player.CustomProperties.TryGetValue(RidingKey, out object val) && val is bool riding)
            return riding;
        return false;
    }

    private static bool IsBehindActor(int actorNumber)
    {
        if (!PhotonNetwork.InRoom) return false;
        if (!PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(BehindKey, out object val)) return false;
        return val != null && (int)val == actorNumber;
    }

    private void SetRidingProperty(bool riding)
    {
        if (!PhotonNetwork.InRoom) return;
        PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { RidingKey, riding } });
    }

    #endregion

    #region Controls

    /// <summary>
    /// A player who steps out gives up their key so nobody is driving from outside, and
    /// gets the same key back when they climb in again.
    /// </summary>
    private void SaveAndClearMyControls()
    {
        savedControls.Clear();
        if (!PhotonNetwork.InRoom) return;

        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;

        Hashtable clear = new();
        foreach (string key in DriveKeys)
        {
            if (props.TryGetValue(key, out object val) && val != null && (int)val == myActor)
            {
                savedControls.Add(key);
                clear[key] = -1;
            }
        }

        if (clear.Count > 0)
            PhotonNetwork.CurrentRoom.SetCustomProperties(clear);
    }

    private void RestoreMyControls()
    {
        if (savedControls.Count == 0) return;
        if (!PhotonNetwork.InRoom)
        {
            savedControls.Clear();
            return;
        }

        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;

        Hashtable restore = new();
        foreach (string key in savedControls)
        {
            // Never snatch a key that someone else picked up while we were out.
            bool taken = props.TryGetValue(key, out object val) && val != null && (int)val != -1 && (int)val != myActor;
            if (!taken)
                restore[key] = myActor;
        }

        savedControls.Clear();

        if (restore.Count > 0)
            PhotonNetwork.CurrentRoom.SetCustomProperties(restore);
    }

    private bool IsBehindRole() => IsBehindActor(PhotonNetwork.LocalPlayer.ActorNumber);

    #endregion

    #region Lookups

    private static Transform SeatOf(GameObject pickup)
    {
        Transform seat = pickup.transform.Find("PlayerCarSpawn");
        return seat != null ? seat : pickup.transform;
    }

    // CarCamera detaches itself from the truck in Start(), so it must be looked up
    // scene-wide instead of through the pickup hierarchy.
    private static void SetCarCameraActive(bool active)
    {
        foreach (CarCamera cam in FindObjectsByType<CarCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            cam.enabled = active;
            cam.gameObject.SetActive(active);
        }
    }

    private static GameObject FindCursorUI()
    {
        foreach (Canvas c in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Transform t = c.transform.Find("Cursor");
            if (t != null) return t.gameObject;
        }
        return null;
    }

    private static GameObject FindPickup()
    {
        foreach (PhotonView pv in FindObjectsByType<PhotonView>(FindObjectsSortMode.None))
        {
            if (pv.GetComponent<CarControl>() != null)
                return pv.gameObject;
        }
        return null;
    }

    #endregion
}
