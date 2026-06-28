using System.Collections;
using System.Globalization;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

public class GameSceneController : MonoBehaviourPunCallbacks
{
    [Header("Spawn")]
    [SerializeField] private Transform carSpawnArea;
    [SerializeField] private string pickupPrefabName = "Pickup";

    [Header("Cargo Box Prefab")]
    [SerializeField] private GameObject cargoBoxPrefab;

    private GameObject spawnedPickup;

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
    }

    private IEnumerator WaitAndSpawnCargo()
    {
        yield return new WaitForSeconds(2f);

        GameObject pickup = FindPickupInScene();
        if (pickup == null) yield break;

        spawnedPickup = pickup;
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
            ResetCar();
    }

    private void ResetCar()
    {
        Vector3 spawnPos = carSpawnArea != null ? carSpawnArea.position : Vector3.zero;
        Quaternion spawnRot = carSpawnArea != null ? carSpawnArea.rotation : Quaternion.identity;

        Rigidbody rb = spawnedPickup.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        spawnedPickup.transform.position = spawnPos;
        spawnedPickup.transform.rotation = spawnRot;

        StartCoroutine(EnableCarPhysics(spawnedPickup));
    }

    private IEnumerator EnableCarPhysics(GameObject pickup)
    {
        yield return new WaitForSeconds(1f);

        Rigidbody rb = pickup.GetComponent<Rigidbody>();
        if (rb != null)
            rb.isKinematic = false;

        CarControl cc = pickup.GetComponent<CarControl>();
        if (cc != null)
            cc.enabled = true;
    }

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

        // First part: pickupPos|pickupRot|pickupScale
        string[] header = parts[0].Split('|');
        string[] posStr = header[0].Split(',');
        string[] rotStr = header[1].Split(',');

        Vector3 pickupOrigPos = ParseVec3(posStr[0], posStr[1], posStr[2]);
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

            Rigidbody rb = box.GetComponent<Rigidbody>();
            if (rb == null) rb = box.AddComponent<Rigidbody>();
            rb.isKinematic = true;

            if (cargoParent != null)
                box.transform.SetParent(cargoParent, true);
        }
    }

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
}
