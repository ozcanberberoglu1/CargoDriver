using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using Hashtable = ExitGames.Client.Photon.Hashtable;

public class VehicleInteraction : MonoBehaviour
{
    [SerializeField] private float enterRange = 8f;
    [SerializeField] private string playerPrefabName = "Toy1";

    private GameObject spawnedPlayer;
    private GameObject cursorUI;
    private bool isInCar = true;
    private bool isBehindPlayer;
    private Transform behindAnchor;

    private System.Collections.IEnumerator Start()
    {
        Canvas[] allCanvas = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Canvas c in allCanvas)
        {
            Transform t = c.transform.Find("Cursor");
            if (t != null)
            {
                cursorUI = t.gameObject;
                break;
            }
        }
        if (cursorUI != null)
            cursorUI.SetActive(false);

        if (IsBehindRole())
        {
            isBehindPlayer = true;
            SpawnBehindVehicle();

            while (FindPickup() == null)
                yield return null;

            yield return null;
            AttachToPickup(FindPickup());
        }
    }

    private bool IsBehindRole()
    {
        if (!PhotonNetwork.InRoom) return false;
        object val;
        PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue("ctrl_Behind", out val);
        return val != null && (int)val == PhotonNetwork.LocalPlayer.ActorNumber;
    }

    private void SpawnBehindVehicle()
    {
        spawnedPlayer = PhotonNetwork.Instantiate(playerPrefabName, Vector3.zero, Quaternion.identity);

        ToyController tc = spawnedPlayer.GetComponent<ToyController>();
        if (tc != null)
            tc.SetMovementLocked(true);

        CharacterController cc = spawnedPlayer.GetComponent<CharacterController>();
        if (cc != null)
            cc.enabled = false;

        CargoPickup cp = spawnedPlayer.GetComponent<CargoPickup>();
        if (cp != null)
        {
            cp.grabDistance = 1.5f;
            cp.detectRange = 5f;
        }

        isInCar = false;
    }

    private void AttachToPickup(GameObject pickup)
    {
        if (spawnedPlayer == null || pickup == null) return;

        CarCamera carCam = pickup.GetComponentInChildren<CarCamera>(true);
        if (carCam != null)
            carCam.gameObject.SetActive(false);

        Transform spawnPoint = pickup.transform.Find("PlayerCarSpawn");
        behindAnchor = spawnPoint != null ? spawnPoint : pickup.transform;
        spawnedPlayer.transform.position = behindAnchor.position;
    }

    private void LateUpdate()
    {
        if (!isBehindPlayer || spawnedPlayer == null || behindAnchor == null) return;

        spawnedPlayer.transform.position = behindAnchor.position;
    }

    private void Update()
    {
        if (isBehindPlayer) return;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (!kb.fKey.wasPressedThisFrame) return;

        if (isInCar)
            ExitCar();
        else
            TryEnterCar();
    }

    private void ExitCar()
    {
        GameObject pickup = FindPickup();
        if (pickup == null) return;

        Transform exitPoint = pickup.transform.Find("PlayerCarSpawn");
        Vector3 spawnPos = exitPoint != null
            ? exitPoint.position
            : pickup.transform.position + pickup.transform.right * 3f + Vector3.up * 0.5f;

        spawnedPlayer = PhotonNetwork.Instantiate(playerPrefabName, spawnPos, Quaternion.identity);

        ClearMyControls();

        CarCamera carCam = pickup.GetComponentInChildren<CarCamera>();
        if (carCam != null)
            carCam.gameObject.SetActive(false);

        isInCar = false;
    }

    private void TryEnterCar()
    {
        if (spawnedPlayer == null) return;

        GameObject pickup = FindPickup();
        if (pickup == null) return;

        float dist = Vector3.Distance(spawnedPlayer.transform.position, pickup.transform.position);
        if (dist > enterRange) return;

        CarCamera carCam = pickup.GetComponentInChildren<CarCamera>(true);
        if (carCam != null)
            carCam.gameObject.SetActive(true);

        if (cursorUI != null)
            cursorUI.SetActive(false);

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        PhotonNetwork.Destroy(spawnedPlayer);
        spawnedPlayer = null;

        isInCar = true;
    }

    private void ClearMyControls()
    {
        if (!PhotonNetwork.InRoom) return;

        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;
        string[] keys = { "ctrl_W", "ctrl_A", "ctrl_S", "ctrl_D", "ctrl_Space" };

        Hashtable clear = new();
        foreach (string key in keys)
        {
            object val;
            props.TryGetValue(key, out val);
            if (val != null && (int)val == myActor)
                clear[key] = -1;
        }

        if (clear.Count > 0)
            PhotonNetwork.CurrentRoom.SetCustomProperties(clear);
    }

    private GameObject FindPickup()
    {
        foreach (var pv in FindObjectsByType<PhotonView>(FindObjectsSortMode.None))
        {
            if (pv.GetComponent<CarControl>() != null)
                return pv.gameObject;
        }
        return null;
    }
}
