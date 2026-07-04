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

    [Header("Cargo Box Prefab")]
    [SerializeField] private GameObject cargoBoxPrefab;

    [Header("Death")]
    [SerializeField] private Collider deadCollider;

    [Header("Checkpoints")]
    [SerializeField] private CheckpointData[] checkpoints;

    private GameObject spawnedPickup;
    private int currentCheckpointIndex;
    private List<CargoSnapshot> savedCargoSnapshots = new();
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
        public Vector3 localPos;
        public Quaternion localRot;
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
                StartCoroutine(EnableCarPhysics(spawnedPickup));
        }
        else
        {
            StartCoroutine(WaitAndSpawnCargo());
        }

        GameObject go = new GameObject("VehicleInteraction_Local");
        go.AddComponent<VehicleInteraction>();

        SaveCargoSnapshot();
    }

    private IEnumerator WaitAndSpawnCargo()
    {
        yield return new WaitForSeconds(2f);

        GameObject pickup = FindPickupInScene();
        if (pickup == null) yield break;

        spawnedPickup = pickup;

        SetupCollisionLayers();
        SetLayerRecursive(pickup, LayerMask.NameToLayer("Vehicle"));

        SpawnCargoOnPickup(pickup);
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

        Transform cargoParent = spawnedPickup.transform.Find("CargoBoxes");
        if (cargoParent == null) return;

        foreach (Transform child in cargoParent)
        {
            if (!child.name.StartsWith("CargoBox")) continue;
            savedCargoSnapshots.Add(new CargoSnapshot
            {
                localPos = child.localPosition,
                localRot = child.localRotation
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

        Transform cargoParent = spawnedPickup.transform.Find("CargoBoxes");
        if (cargoParent == null) return;

        List<Transform> boxes = new();
        foreach (Transform child in cargoParent)
        {
            if (child.name.StartsWith("CargoBox"))
                boxes.Add(child);
        }

        for (int i = 0; i < boxes.Count && i < savedCargoSnapshots.Count; i++)
        {
            Rigidbody boxRb = boxes[i].GetComponent<Rigidbody>();
            if (boxRb != null)
            {
                boxRb.isKinematic = true;
                boxRb.linearVelocity = Vector3.zero;
                boxRb.angularVelocity = Vector3.zero;
            }

            boxes[i].localPosition = savedCargoSnapshots[i].localPos;
            boxes[i].localRotation = savedCargoSnapshots[i].localRot;
        }
    }

    private IEnumerator EnableCarAfterRespawn()
    {
        yield return new WaitForSeconds(1f);

        Rigidbody rb = spawnedPickup.GetComponent<Rigidbody>();
        if (rb != null)
            rb.isKinematic = false;

        Transform cargoParent = spawnedPickup.transform.Find("CargoBoxes");
        if (cargoParent != null)
        {
            foreach (Transform child in cargoParent)
            {
                if (!child.name.StartsWith("CargoBox")) continue;
                Rigidbody boxRb = child.GetComponent<Rigidbody>();
                if (boxRb != null)
                {
                    boxRb.isKinematic = false;
                    boxRb.useGravity = true;
                }
            }
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

    public static void SetLayerRecursive(GameObject obj, int layer)
    {
        if (layer < 0) return;
        obj.layer = layer;
        foreach (Transform child in obj.transform)
            SetLayerRecursive(child.gameObject, layer);
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

        SpawnCargoOnPickup(pickup);

        return pickup;
    }

    private void SpawnCargoOnPickup(GameObject pickup)
    {
        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        if (!props.ContainsKey("cargoData")) return;

        string data = props["cargoData"].ToString();
        string[] parts = data.Split(';');

        Transform cargoParent = pickup.transform.Find("CargoBoxes");

        for (int i = 1; i < parts.Length; i++)
        {
            string[] c = parts[i].Split(',');
            if (c.Length < 10) continue;

            Vector3 localPos = ParseVec3(c[0], c[1], c[2]);
            Quaternion localRot = ParseQuat(c[3], c[4], c[5], c[6]);
            Vector3 scale = ParseVec3(c[7], c[8], c[9]);

            Vector3 worldPos = pickup.transform.TransformPoint(localPos);
            Quaternion worldRot = pickup.transform.rotation * localRot;

            GameObject box;
            if (cargoBoxPrefab != null)
            {
                box = Instantiate(cargoBoxPrefab, worldPos, worldRot);
            }
            else
            {
                box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.transform.position = worldPos;
                box.transform.rotation = worldRot;
            }

            box.transform.localScale = scale;
            box.name = $"CargoBox_{i}";
            box.tag = "CargoBox";

            if (cargoParent != null)
                box.transform.SetParent(cargoParent, true);

            Rigidbody rb = box.GetComponent<Rigidbody>();
            if (rb == null) rb = box.AddComponent<Rigidbody>();

            Collider col = box.GetComponent<Collider>();
            if (col == null) box.AddComponent<BoxCollider>();

            if (PhotonNetwork.IsMasterClient)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.mass = 2f;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
            else
            {
                rb.isKinematic = true;
            }
        }
    }

    #endregion

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
