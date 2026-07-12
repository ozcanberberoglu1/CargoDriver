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

    [Header("Death")]
    [SerializeField] private Collider deadCollider;

    [Header("Checkpoints")]
    [SerializeField] private CheckpointData[] checkpoints;

    private GameObject spawnedPickup;
    private int currentCheckpointIndex;
    private List<CargoSnapshot> savedCargoSnapshots = new();
    private Vector3 savedPickupPos;
    private Quaternion savedPickupRot;
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
        public Vector3 worldPos;
        public Quaternion worldRot;
        public Transform transform;
        public Transform originalParent;
    }

    private IEnumerator Start()
    {
        CargoPickup.heldByPickup.Clear();

        while (!PhotonNetwork.InRoom)
            yield return null;

        yield return new WaitForSeconds(1f);

        if (PhotonNetwork.IsMasterClient)
        {
            spawnedPickup = SpawnPickupWithCargo();
            if (spawnedPickup != null)
                StartCoroutine(EnableCarPhysics(spawnedPickup));
        }
        else
        {
            yield return StartCoroutine(WaitForPickup());
        }

        // Cargo boxes are real networked objects (spawned by the master), exactly
        // like LobbyScene. Every client sets up naming/scale/snap; each box syncs
        // through its own PhotonTransformView. Boxes are NOT parented to the truck
        // so they stay free physics objects (rest by friction, slosh, fall off).
        if (spawnedPickup != null)
            StartCoroutine(SetupNetworkedCargoRoutine());

        GameObject go = new GameObject("VehicleInteraction_Local");
        go.AddComponent<VehicleInteraction>();
    }

    private IEnumerator WaitForPickup()
    {
        GameObject pickup = null;
        float waited = 0f;
        while (pickup == null && waited < 15f)
        {
            yield return new WaitForSeconds(0.3f);
            waited += 0.3f;
            pickup = FindPickupInScene();
        }
        if (pickup == null) yield break;

        spawnedPickup = pickup;

        SetupCollisionLayers();
        SetLayerRecursive(pickup, LayerMask.NameToLayer("Vehicle"));

        Transform cargoBoxes = pickup.transform.Find("CargoBoxes");
        if (cargoBoxes != null)
        {
            BoxCollider bc = cargoBoxes.GetComponent<BoxCollider>();
            if (bc != null)
                Destroy(bc);
        }
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

    private void SaveCargoSnapshot()
    {
        savedCargoSnapshots.Clear();

        if (spawnedPickup == null) return;

        savedPickupPos = spawnedPickup.transform.position;
        savedPickupRot = spawnedPickup.transform.rotation;

        var allBoxes = new List<Transform>();
        foreach (var go in GameObject.FindGameObjectsWithTag("CargoBox"))
            allBoxes.Add(go.transform);

        foreach (Transform box in allBoxes)
        {
            savedCargoSnapshots.Add(new CargoSnapshot
            {
                worldPos = box.position,
                worldRot = box.rotation,
                transform = box,
                originalParent = box.parent
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
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        spawnedPickup.transform.position = spawnPos;
        spawnedPickup.transform.rotation = spawnRot;

        RestoreCargoSnapshot();
        StartCoroutine(EnableCarAfterRespawn());
    }

    private void RestoreCargoSnapshot()
    {
        if (spawnedPickup == null) return;

        CargoPickup.heldByPickup.Clear();

        Vector3 posDelta = spawnedPickup.transform.position - savedPickupPos;
        Quaternion rotDelta = spawnedPickup.transform.rotation * Quaternion.Inverse(savedPickupRot);

        for (int i = 0; i < savedCargoSnapshots.Count; i++)
        {
            var snap = savedCargoSnapshots[i];
            if (snap.transform == null) continue;

            snap.transform.SetParent(snap.originalParent, true);

            Rigidbody boxRb = snap.transform.GetComponent<Rigidbody>();
            if (boxRb != null)
            {
                boxRb.isKinematic = true;
                boxRb.linearVelocity = Vector3.zero;
                boxRb.angularVelocity = Vector3.zero;
            }

            Vector3 newPos = rotDelta * (snap.worldPos - savedPickupPos) + spawnedPickup.transform.position;
            snap.transform.position = newPos;
            snap.transform.rotation = rotDelta * snap.worldRot;
        }
    }

    private IEnumerator EnableCarAfterRespawn()
    {
        yield return new WaitForSeconds(1f);

        Rigidbody rb = spawnedPickup.GetComponent<Rigidbody>();
        if (rb != null)
            rb.isKinematic = false;

        // Boxes are free networked objects (not parented to the truck), so
        // re-enable their physics by tag. Snapped children have no Rigidbody.
        foreach (var go in GameObject.FindGameObjectsWithTag("CargoBox"))
        {
            Rigidbody boxRb = go.GetComponent<Rigidbody>();
            if (boxRb == null) continue;
            boxRb.isKinematic = false;
            boxRb.useGravity = true;
            boxRb.linearVelocity = Vector3.zero;
            boxRb.angularVelocity = Vector3.zero;
        }

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

        Transform cargoBoxes = pickup.transform.Find("CargoBoxes");
        if (cargoBoxes != null)
        {
            BoxCollider bc = cargoBoxes.GetComponent<BoxCollider>();
            if (bc != null)
                Destroy(bc);
        }
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
        string[] posStr = header[0].Split(',');
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
                pv.ObservedComponents = new System.Collections.Generic.List<Component>();
            if (!pv.ObservedComponents.Contains(cc))
                pv.ObservedComponents.Add(cc);
        }

        MasterSpawnNetworkedCargo(pickup);

        return pickup;
    }

    // Master spawns each cargo box as a real networked object (PhotonView +
    // PhotonTransformView), identical to how LobbyScene boxes work. They appear on
    // every client automatically and sync through their own PhotonTransformView.
    // scale / box index / parent index are passed via instantiation data.
    private void MasterSpawnNetworkedCargo(GameObject pickup)
    {
        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        if (!props.ContainsKey("cargoData")) return;

        string data = props["cargoData"].ToString();
        string[] parts = data.Split(';');

        for (int i = 1; i < parts.Length; i++)
        {
            string[] c = parts[i].Split(',');
            if (c.Length < 10) continue;

            Vector3 localPos = ParseVec3(c[0], c[1], c[2]);
            Quaternion localRot = ParseQuat(c[3], c[4], c[5], c[6]);
            Vector3 scale = ParseVec3(c[7], c[8], c[9]);
            string prefabName = (c.Length > 10 && !string.IsNullOrEmpty(c[10])) ? c[10] : "CargoBox";
            int parentIdx = c.Length > 11 ? int.Parse(c[11]) : -1;

            Vector3 worldPos = pickup.transform.TransformPoint(localPos);
            Quaternion worldRot = pickup.transform.rotation * localRot;

            // Use 0-based index (i-1) to match JoinGameController's allBoxes list
            // where parentIdx references are also 0-based.
            int zeroBasedIndex = i - 1;
            object[] initData = { scale.x, scale.y, scale.z, zeroBasedIndex, parentIdx };
            PhotonNetwork.InstantiateRoomObject(prefabName, worldPos, worldRot, 0, initData);
        }
    }

    // Runs on every client. Waits for the networked boxes to arrive, then applies
    // name/scale/tag from instantiation data and rebuilds the snap groups. Boxes
    // are left as free physics objects (NOT parented to the truck) - just like in
    // LobbyScene - so the master simulates them and each box's PhotonTransformView
    // keeps everyone in sync.
    private IEnumerator SetupNetworkedCargoRoutine()
    {
        int expected = CountCargoEntries();

        float t = 0f;
        while (t < 15f)
        {
            if (expected > 0 && FindNetworkedCargoBoxes().Count >= expected) break;
            t += 0.25f;
            yield return new WaitForSeconds(0.25f);
        }
        yield return new WaitForSeconds(0.25f);

        var boxes = FindNetworkedCargoBoxes();
        Debug.Log($"[GameScene] SetupNetworkedCargo: found {boxes.Count} boxes, expected {expected}, IsMaster={PhotonNetwork.IsMasterClient}");
        var byIndex = new Dictionary<int, GameObject>();
        var parentOf = new Dictionary<int, int>();

        foreach (var pv in boxes)
        {
            object[] d = pv.InstantiationData;
            if (d == null || d.Length < 5) continue;

            int index = Convert.ToInt32(d[3]);
            int pIdx = Convert.ToInt32(d[4]);
            GameObject box = pv.gameObject;
            Debug.Log($"[GameScene] Box ViewID={pv.ViewID} index={index} parentIdx={pIdx} pos={box.transform.position}");

            box.name = $"CargoBox_{index}";
            box.tag = "CargoBox";
            box.transform.localScale = new Vector3(
                Convert.ToSingle(d[0]), Convert.ToSingle(d[1]), Convert.ToSingle(d[2]));

            // Master is the physics authority (boxes have Fixed ownership); other
            // clients keep the box kinematic and mirror it via PhotonTransformView,
            // so it stays smooth (no local physics fighting the network updates).
            Rigidbody boxRb = box.GetComponent<Rigidbody>();
            if (boxRb != null)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    boxRb.isKinematic = false;
                    boxRb.useGravity = true;
                }
                else
                {
                    boxRb.isKinematic = true;
                    boxRb.useGravity = false;
                }
            }

            byIndex[index] = box;
            parentOf[index] = pIdx;
        }

        // Rebuild snap groups: parent each snapped child under its parent lego.
        // This mirrors what lobby does when LegoSnap.TrySnap() connects two boxes:
        // destroy child Rigidbody, SetParent to parent lego, disable child PTV.
        foreach (var kv in parentOf)
        {
            int idx = kv.Key, pidx = kv.Value;
            if (pidx < 0) continue;
            if (!byIndex.ContainsKey(idx) || !byIndex.ContainsKey(pidx)) continue;

            GameObject child = byIndex[idx];
            GameObject parent = byIndex[pidx];

            // Destroy Rigidbody before parenting (same as LegoSnap.TrySnap)
            Rigidbody childRb = child.GetComponent<Rigidbody>();
            if (childRb != null) Destroy(childRb);

            // Parent the child lego under the parent lego (worldPositionStays=true
            // preserves the child's world position so it doesn't jump)
            child.transform.SetParent(parent.transform, true);

            // Disable child's PhotonTransformView: the child is now parented and
            // moves with its parent. If PTV stays on it would fight the parenting.
            PhotonTransformView cptv = child.GetComponent<PhotonTransformView>();
            if (cptv != null) cptv.enabled = false;

            // Register the snap relationship
            LegoSnap childSnap = child.GetComponent<LegoSnap>();
            LegoSnap parentSnap = parent.GetComponent<LegoSnap>();
            if (childSnap != null && parentSnap != null)
            {
                childSnap.SetParentDirect(parentSnap);
                Debug.Log($"[GameScene] Snap: {child.name}(idx={idx}) -> {parent.name}(idx={pidx}) localPos={child.transform.localPosition}");
            }
            else
            {
                Debug.LogWarning($"[GameScene] Snap FAILED: child={child.name} LegoSnap={childSnap != null}, parent={parent.name} LegoSnap={parentSnap != null}");
            }
        }

        SaveCargoSnapshot();
    }

    private int CountCargoEntries()
    {
        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        if (!props.ContainsKey("cargoData")) return 0;
        string[] parts = props["cargoData"].ToString().Split(';');
        return Mathf.Max(0, parts.Length - 1);
    }

    private List<PhotonView> FindNetworkedCargoBoxes()
    {
        var result = new List<PhotonView>();
        foreach (var pv in FindObjectsByType<PhotonView>(FindObjectsSortMode.None))
        {
            if (pv.GetComponent<LegoSnap>() == null) continue;
            if (pv.InstantiationData == null || pv.InstantiationData.Length < 5) continue;
            result.Add(pv);
        }
        return result;
    }

    #endregion

    private GameObject FindCargoPrefab(string prefabName)
    {
        if (cargoBoxPrefabs != null && !string.IsNullOrEmpty(prefabName))
        {
            foreach (var p in cargoBoxPrefabs)
            {
                if (p != null && p.name == prefabName)
                    return p;
            }
        }

        if (cargoBoxPrefabs != null && cargoBoxPrefabs.Count > 0 && cargoBoxPrefabs[0] != null)
            return cargoBoxPrefabs[0];

        return null;
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
