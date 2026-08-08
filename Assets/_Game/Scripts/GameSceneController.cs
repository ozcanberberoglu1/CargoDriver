using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using Hashtable = ExitGames.Client.Photon.Hashtable;

public class GameSceneController : MonoBehaviourPunCallbacks
{
    [Header("Spawn")]
    [SerializeField] private Transform carSpawnArea;
    [SerializeField] private string pickupPrefabName = "Pickup";

    [Header("Cargo Box Prefabs")]
    [SerializeField] private List<GameObject> cargoBoxPrefabs;
    [SerializeField] private float cargoMass = 2f;

    [Header("Death")]
    [SerializeField] private Collider deadCollider;

    [Header("Checkpoints")]
    [SerializeField] private CheckpointData[] checkpoints;

    private GameObject spawnedPickup;
    private int currentCheckpointIndex;
    private readonly List<CargoSnapshot> savedCargoSnapshots = new();
    private bool isDead;

    [Serializable]
    public class CheckpointData
    {
        public string levelName;
        public Collider checkpointTrigger;
        public Transform spawnPoint;
    }

    private class CargoSnapshot
    {
        public NetworkedCargoBody body;
        public Vector3 localPos;
        public Quaternion localRot;
    }

    private void Awake()
    {
        // The truck moves here, so cargo contacts must be resolved on a single machine.
        NetworkedCargoBody.Policy = CargoAuthorityPolicy.HostAuthority;
        NetworkedCargoBody.ReferenceFrame = null;
        NetworkedCargoBody.PreventSleep = true;
    }

    private IEnumerator Start()
    {
        while (!PhotonNetwork.InRoom)
            yield return null;

        yield return new WaitForSeconds(1f);

        if (PhotonNetwork.IsMasterClient)
        {
            spawnedPickup = SpawnPickupWithCargo();
            if (spawnedPickup != null)
            {
                NetworkedCargoBody.ReferenceFrame = spawnedPickup.transform;
                StartCoroutine(EnableCarPhysics(spawnedPickup));
            }
        }
        else
        {
            StartCoroutine(WaitForPickup());
        }

        GameObject go = new GameObject("VehicleInteraction_Local");
        go.AddComponent<VehicleInteraction>();

        // Lego groups resolve their parent links over the next few physics steps.
        yield return new WaitForSeconds(0.5f);
        SaveCargoSnapshot();
    }

    /// <summary>
    /// Non-masters only wait for the networked truck; cargo arrives through PUN
    /// instantiation, so there is nothing to build locally.
    /// </summary>
    private IEnumerator WaitForPickup()
    {
        GameObject pickup = null;
        float waited = 0f;
        while (pickup == null && waited < 10f)
        {
            yield return new WaitForSeconds(0.3f);
            waited += 0.3f;
            pickup = FindPickupInScene();
        }
        if (pickup == null) yield break;

        spawnedPickup = pickup;
        NetworkedCargoBody.ReferenceFrame = pickup.transform;

        SetupCollisionLayers();
        SetLayerRecursive(pickup, LayerMask.NameToLayer("Vehicle"));
        StripCargoParentCollider(pickup);
    }

    private GameObject FindPickupInScene()
    {
        foreach (var pv in FindObjectsByType<PhotonView>(FindObjectsSortMode.None))
        {
            if (pv.GetComponent<CarControl>() != null)
                return pv.gameObject;
        }
        return GameObject.Find("Pickup(Clone)");
    }

    private static void StripCargoParentCollider(GameObject pickup)
    {
        Transform cargoBoxes = pickup.transform.Find("CargoBoxes");
        if (cargoBoxes == null) return;

        BoxCollider bc = cargoBoxes.GetComponent<BoxCollider>();
        if (bc != null)
            Destroy(bc);
    }

    private void Update()
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (spawnedPickup == null) return;

        Keyboard kb = Keyboard.current;
        if (kb != null && kb.rKey.wasPressedThisFrame)
            RespawnAtCheckpoint();

        CheckCheckpoints();
        CheckDeath();
    }

    #region Checkpoints

    private void CheckCheckpoints()
    {
        if (checkpoints == null || spawnedPickup == null) return;

        for (int i = currentCheckpointIndex; i < checkpoints.Length; i++)
        {
            var cp = checkpoints[i];
            if (cp.checkpointTrigger == null) continue;

            Collider carCol = spawnedPickup.GetComponent<Collider>();
            if (carCol == null) continue;

            if (cp.checkpointTrigger.bounds.Intersects(carCol.bounds))
            {
                if (i > currentCheckpointIndex || (i == 0 && savedCargoSnapshots.Count == 0))
                {
                    currentCheckpointIndex = i;
                    SaveCargoSnapshot();

                    PhotonNetwork.CurrentRoom.SetCustomProperties(
                        new Hashtable { { "checkpoint", currentCheckpointIndex } });
                }
            }
        }
    }

    private void CheckDeath()
    {
        if (deadCollider == null || spawnedPickup == null || isDead) return;

        Collider carCol = spawnedPickup.GetComponent<Collider>();
        if (carCol == null) return;

        if (deadCollider.bounds.Intersects(carCol.bounds))
        {
            isDead = true;
            RespawnAtCheckpoint();
        }
    }

    /// <summary>
    /// Records each box relative to the truck. Stowed boxes are skipped because they are
    /// lego children and travel with their root.
    /// </summary>
    private void SaveCargoSnapshot()
    {
        savedCargoSnapshots.Clear();
        if (spawnedPickup == null) return;

        Transform truck = spawnedPickup.transform;
        Quaternion invTruckRot = Quaternion.Inverse(truck.rotation);

        foreach (NetworkedCargoBody body in NetworkedCargoBody.All)
        {
            if (body == null) continue;
            if (body.State == CargoState.Stowed) continue;

            savedCargoSnapshots.Add(new CargoSnapshot
            {
                body = body,
                localPos = truck.InverseTransformPoint(body.transform.position),
                localRot = invTruckRot * body.transform.rotation
            });
        }
    }

    private void RespawnAtCheckpoint()
    {
        if (spawnedPickup == null) return;

        Transform spawnPoint = null;

        if (checkpoints != null && currentCheckpointIndex < checkpoints.Length)
            spawnPoint = checkpoints[currentCheckpointIndex].spawnPoint;

        if (spawnPoint == null)
            spawnPoint = carSpawnArea;

        Vector3 spawnPos = spawnPoint != null ? spawnPoint.position : Vector3.zero;
        Quaternion spawnRot = spawnPoint != null ? spawnPoint.rotation : Quaternion.identity;

        Rigidbody rb = spawnedPickup.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }

        spawnedPickup.transform.position = spawnPos;
        spawnedPickup.transform.rotation = spawnRot;

        RestoreCargoSnapshot();
        StartCoroutine(EnableCarAfterRespawn());
    }

    private void RestoreCargoSnapshot()
    {
        if (spawnedPickup == null) return;

        Transform truck = spawnedPickup.transform;

        foreach (CargoSnapshot snap in savedCargoSnapshots)
        {
            if (snap.body == null) continue;
            snap.body.AuthorityTeleport(
                truck.TransformPoint(snap.localPos),
                truck.rotation * snap.localRot);
        }
    }

    private IEnumerator EnableCarAfterRespawn()
    {
        yield return new WaitForSeconds(1f);

        Rigidbody rb = spawnedPickup.GetComponent<Rigidbody>();
        if (rb != null)
            rb.isKinematic = false;

        NetworkedCargoBody.WakeAll();
        isDead = false;
    }

    #endregion

    #region Car Physics

    public static void SetupCollisionLayers()
    {
        int playerLayer = LayerMask.NameToLayer("Player");
        int vehicleLayer = LayerMask.NameToLayer("Vehicle");
        if (playerLayer >= 0 && vehicleLayer >= 0)
            Physics.IgnoreLayerCollision(playerLayer, vehicleLayer, true);
    }

    public static void SetLayerRecursive(GameObject obj, int layer, bool skipCargoBoxes = true)
    {
        if (layer < 0) return;
        if (skipCargoBoxes && obj.name.StartsWith("CargoBox")) return;
        if (skipCargoBoxes && obj.CompareTag("CargoBox")) return;

        obj.layer = layer;
        foreach (Transform child in obj.transform)
            SetLayerRecursive(child.gameObject, layer, skipCargoBoxes);
    }

    private IEnumerator EnableCarPhysics(GameObject pickup)
    {
        SetupCollisionLayers();
        SetLayerRecursive(pickup, LayerMask.NameToLayer("Vehicle"));

        yield return new WaitForSeconds(1f);

        Rigidbody rb = pickup.GetComponent<Rigidbody>();
        if (rb != null)
            rb.isKinematic = false;

        CarControl cc = pickup.GetComponent<CarControl>();
        if (cc != null)
            cc.enabled = true;

        StripCargoParentCollider(pickup);

        // The truck drops onto its suspension the moment it goes dynamic, so the cargo has
        // to be awake to follow the bed down instead of being left hanging above it.
        NetworkedCargoBody.WakeAll();
        yield return new WaitForSeconds(0.5f);
        NetworkedCargoBody.WakeAll();
    }

    #endregion

    #region Pickup Spawn

    private GameObject SpawnPickupWithCargo()
    {
        Vector3 spawnPos = carSpawnArea != null ? carSpawnArea.position : Vector3.zero;

        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        if (!props.ContainsKey("cargoData"))
        {
            Debug.LogError("[GameScene] cargoData NOT FOUND in room properties!");
            return null;
        }

        string data = props["cargoData"].ToString();
        string[] parts = data.Split(';');

        string[] header = parts[0].Split('|');
        string[] rotStr = header[1].Split(',');

        Quaternion pickupOrigRot = ParseQuat(rotStr[0], rotStr[1], rotStr[2], rotStr[3]);

        GameObject pickup = PhotonNetwork.InstantiateRoomObject(
            pickupPrefabName, spawnPos, pickupOrigRot);

        Rigidbody prb = pickup.GetComponent<Rigidbody>();
        if (prb != null)
            prb.isKinematic = true;

        CarControl cc = pickup.GetComponent<CarControl>();
        if (cc != null)
            cc.enabled = false;

        PhotonView pv = pickup.GetComponent<PhotonView>();
        if (pv != null && cc != null)
        {
            if (pv.ObservedComponents == null)
                pv.ObservedComponents = new List<Component>();
            if (!pv.ObservedComponents.Contains(cc))
                pv.ObservedComponents.Add(cc);
        }

        SpawnCargoOnPickup(pickup, parts);

        return pickup;
    }

    /// <summary>
    /// Master-only. Each box is created as a room object so it carries a real ViewID on
    /// every client, including anyone who joins mid-match.
    /// </summary>
    private void SpawnCargoOnPickup(GameObject pickup, string[] parts)
    {
        var spawnedBoxes = new List<GameObject>();

        for (int i = 1; i < parts.Length; i++)
        {
            string[] c = parts[i].Split(',');
            if (c.Length < 10) continue;

            Vector3 localPos = ParseVec3(c[0], c[1], c[2]);
            Quaternion localRot = ParseQuat(c[3], c[4], c[5], c[6]);
            Vector3 scale = ParseVec3(c[7], c[8], c[9]);
            string prefabName = c.Length > 10 ? c[10] : "";
            int parentIdx = c.Length > 11 ? int.Parse(c[11]) : -1;

            Vector3 worldPos = pickup.transform.TransformPoint(localPos);
            Quaternion worldRot = pickup.transform.rotation * localRot;

            // The layout writer emits parents before their children, so a lego parent is
            // always already spawned and has a ViewID we can reference.
            int legoParentViewId = -1;
            if (parentIdx >= 0 && parentIdx < spawnedBoxes.Count)
            {
                PhotonView parentView = spawnedBoxes[parentIdx].GetComponent<PhotonView>();
                if (parentView != null) legoParentViewId = parentView.ViewID;
            }

            object[] instantiationData =
            {
                scale.x, scale.y, scale.z, legoParentViewId
            };

            GameObject box = PhotonNetwork.InstantiateRoomObject(
                ResolveCargoPrefabName(prefabName), worldPos, worldRot, 0, instantiationData);

            if (box == null) continue;

            box.transform.localScale = scale;

            // Goes through the body so the mass survives the rigidbody being dropped and
            // rebuilt when the box is welded into a lego structure and later detached.
            NetworkedCargoBody body = box.GetComponent<NetworkedCargoBody>();
            if (body != null)
                body.SetMass(cargoMass);

            spawnedBoxes.Add(box);
        }
    }

    #endregion

    private string ResolveCargoPrefabName(string prefabName)
    {
        if (cargoBoxPrefabs != null && !string.IsNullOrEmpty(prefabName))
        {
            foreach (var p in cargoBoxPrefabs)
            {
                if (p != null && p.name == prefabName)
                    return p.name;
            }
        }

        if (cargoBoxPrefabs != null && cargoBoxPrefabs.Count > 0 && cargoBoxPrefabs[0] != null)
            return cargoBoxPrefabs[0].name;

        return "CargoBox";
    }

    #region Parsing

    private Vector3 ParseVec3(string x, string y, string z)
    {
        return new Vector3(
            float.Parse(x, CultureInfo.InvariantCulture),
            float.Parse(y, CultureInfo.InvariantCulture),
            float.Parse(z, CultureInfo.InvariantCulture));
    }

    private Quaternion ParseQuat(string x, string y, string z, string w)
    {
        return new Quaternion(
            float.Parse(x, CultureInfo.InvariantCulture),
            float.Parse(y, CultureInfo.InvariantCulture),
            float.Parse(z, CultureInfo.InvariantCulture),
            float.Parse(w, CultureInfo.InvariantCulture));
    }

    #endregion
}
