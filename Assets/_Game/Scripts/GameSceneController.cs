using System.Collections;
using System.Globalization;
using Photon.Pun;
using UnityEngine;

public class GameSceneController : MonoBehaviourPunCallbacks
{
    [Header("Spawn")]
    [SerializeField] private Transform carSpawnArea;
    [SerializeField] private string pickupPrefabName = "Pickup";

    [Header("Cargo Box Prefab")]
    [SerializeField] private GameObject cargoBoxPrefab;

    private IEnumerator Start()
    {
        Debug.Log($"[GameScene] Start. InRoom={PhotonNetwork.InRoom} IsMaster={PhotonNetwork.IsMasterClient}");

        while (!PhotonNetwork.InRoom)
            yield return null;

        yield return new WaitForSeconds(1f);

        Debug.Log($"[GameScene] Room ready. IsMaster={PhotonNetwork.IsMasterClient} Props count={PhotonNetwork.CurrentRoom.CustomProperties.Count}");

        if (PhotonNetwork.IsMasterClient)
            SpawnPickupWithCargo();
    }

    private void SpawnPickupWithCargo()
    {
        Vector3 spawnPos = carSpawnArea != null ? carSpawnArea.position : Vector3.zero;

        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        if (!props.ContainsKey("cargoData"))
        {
            Debug.LogError("[GameScene] cargoData NOT FOUND in room properties!");
            return;
        }

        Debug.Log($"[GameScene] cargoData found, spawning pickup at {spawnPos}");

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

        CarControl cc = pickup.GetComponent<CarControl>();
        if (cc != null)
            cc.enabled = true;

        Rigidbody prb = pickup.GetComponent<Rigidbody>();
        if (prb != null)
            prb.isKinematic = false;

        PhotonView pv = pickup.GetComponent<PhotonView>();
        if (pv != null && cc != null)
        {
            if (pv.ObservedComponents == null)
                pv.ObservedComponents = new System.Collections.Generic.List<Component>();
            if (!pv.ObservedComponents.Contains(cc))
                pv.ObservedComponents.Add(cc);
        }

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
