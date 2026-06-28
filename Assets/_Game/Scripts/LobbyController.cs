using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Hashtable = ExitGames.Client.Photon.Hashtable;

public class LobbyController : MonoBehaviourPunCallbacks
{
    [Header("Room Info")]
    [SerializeField] private TextMeshProUGUI roomNameText;
    [SerializeField] private TextMeshProUGUI roomNumberText;
    [SerializeField] private TextMeshProUGUI playersText;

    [Header("Buttons")]
    [SerializeField] private Button closeRoomButton;

    [Header("Spawn")]
    [SerializeField] private Transform spawnPoints;
    [SerializeField] private string playerPrefabName = "Toy1";

    [Header("Players Panel")]
    [SerializeField] private GameObject playersPanel;
    [SerializeField] private Transform playerListContent;
    [SerializeField] private GameObject playerListPrefab;

    [Header("Cargo")]
    [SerializeField] private List<GameObject> cargoBoxes;
    [SerializeField] private Collider truckTrigger;
    [SerializeField] private TextMeshProUGUI successCargoText;
    [SerializeField] private float successDisplayTime = 3f;
    [SerializeField] private float successFadeTime = 1f;

    private readonly Dictionary<int, GameObject> playerListEntries = new();
    private bool cargoCompleted;

    private IEnumerator Start()
    {
        closeRoomButton.onClick.AddListener(OnCloseRoomClicked);
        closeRoomButton.gameObject.SetActive(PhotonNetwork.IsMasterClient);
        playersPanel.SetActive(false);

        if (successCargoText != null)
            successCargoText.gameObject.SetActive(false);

        while (!PhotonNetwork.InRoom ||
               !PhotonNetwork.CurrentRoom.CustomProperties.ContainsKey("roomName"))
        {
            yield return null;
        }

        UpdateRoomInfo();
        UpdatePlayerCount();
        SpawnPlayer();
        RebuildPlayerList();
        SetupPhysicsIgnore();
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        playersPanel.SetActive(kb.tabKey.isPressed);

        if (playersPanel.activeSelf)
            UpdateAllPings();

        if (!cargoCompleted)
            CheckAllCargoLoaded();
    }

    #region Physics Ignore

    private void SetupPhysicsIgnore()
    {
        GameObject pickup = GameObject.Find("Pickup");
        if (pickup == null) return;

        Rigidbody vehicleRb = pickup.GetComponent<Rigidbody>();
        if (vehicleRb == null) vehicleRb = pickup.GetComponentInChildren<Rigidbody>();
        if (vehicleRb != null)
            vehicleRb.isKinematic = true;
    }

    #endregion

    #region Cargo Check

    private void CheckAllCargoLoaded()
    {
        if (truckTrigger == null || cargoBoxes == null || cargoBoxes.Count == 0) return;

        foreach (GameObject box in cargoBoxes)
        {
            if (box == null) return;

            Collider boxCol = box.GetComponent<Collider>();
            if (boxCol == null) return;

            if (!truckTrigger.bounds.Intersects(boxCol.bounds))
                return;
        }

        cargoCompleted = true;
        StartCoroutine(ShowSuccessMessage());
    }

    private IEnumerator ShowSuccessMessage()
    {
        if (successCargoText == null) yield break;

        successCargoText.gameObject.SetActive(true);

        Color c = successCargoText.color;
        c.a = 1f;
        successCargoText.color = c;

        yield return new WaitForSeconds(successDisplayTime);

        float elapsed = 0f;
        while (elapsed < successFadeTime)
        {
            elapsed += Time.deltaTime;
            c.a = 1f - (elapsed / successFadeTime);
            successCargoText.color = c;
            yield return null;
        }

        c.a = 0f;
        successCargoText.color = c;
        successCargoText.gameObject.SetActive(false);
    }

    #endregion

    #region Player List

    private void RebuildPlayerList()
    {
        foreach (var kvp in playerListEntries)
            Destroy(kvp.Value);
        playerListEntries.Clear();

        foreach (Player player in PhotonNetwork.PlayerList)
            AddPlayerEntry(player);
    }

    private void AddPlayerEntry(Player player)
    {
        if (playerListEntries.ContainsKey(player.ActorNumber)) return;

        GameObject entry = Instantiate(playerListPrefab, playerListContent);
        playerListEntries[player.ActorNumber] = entry;

        TextMeshProUGUI nameText = entry.transform.Find("PlayerNameText")?.GetComponent<TextMeshProUGUI>();
        if (nameText != null)
        {
            string nick = player.NickName;
            nameText.text = string.IsNullOrEmpty(nick)
                ? $"Player{player.ActorNumber}"
                : nick;
        }
    }

    private void RemovePlayerEntry(Player player)
    {
        if (playerListEntries.TryGetValue(player.ActorNumber, out GameObject entry))
        {
            Destroy(entry);
            playerListEntries.Remove(player.ActorNumber);
        }
    }

    private void UpdateAllPings()
    {
        foreach (var kvp in playerListEntries)
        {
            int actorNumber = kvp.Key;
            GameObject entry = kvp.Value;
            if (entry == null) continue;

            TextMeshProUGUI msText = entry.transform.Find("msText")?.GetComponent<TextMeshProUGUI>();
            if (msText == null) continue;

            Player player = null;
            foreach (Player p in PhotonNetwork.PlayerList)
            {
                if (p.ActorNumber == actorNumber)
                {
                    player = p;
                    break;
                }
            }

            if (player != null && player.IsLocal)
            {
                msText.text = $"{PhotonNetwork.GetPing()} ms";
            }
            else if (player != null && player.CustomProperties.TryGetValue("ping", out object ping))
            {
                msText.text = $"{ping} ms";
            }
            else
            {
                msText.text = "- ms";
            }
        }
    }

    #endregion

    #region Ping Sync

    private float pingUpdateTimer;

    private void LateUpdate()
    {
        if (!PhotonNetwork.InRoom) return;

        pingUpdateTimer += Time.deltaTime;
        if (pingUpdateTimer >= 2f)
        {
            pingUpdateTimer = 0f;
            PhotonNetwork.LocalPlayer.SetCustomProperties(
                new Hashtable { { "ping", PhotonNetwork.GetPing() } }
            );
        }
    }

    #endregion

    #region Spawn

    private void SpawnPlayer()
    {
        Vector3 pos = GetSpawnPosition();
        PhotonNetwork.Instantiate(playerPrefabName, pos, Quaternion.identity);
    }

    private Vector3 GetSpawnPosition()
    {
        if (spawnPoints != null && spawnPoints.childCount > 0)
        {
            int index = (PhotonNetwork.LocalPlayer.ActorNumber - 1) % spawnPoints.childCount;
            return spawnPoints.GetChild(index).position;
        }

        float angle = (PhotonNetwork.LocalPlayer.ActorNumber - 1) * 60f;
        float radius = 2f;
        Vector3 center = spawnPoints != null ? spawnPoints.position : Vector3.zero;
        center.x += Mathf.Cos(angle * Mathf.Deg2Rad) * radius;
        center.z += Mathf.Sin(angle * Mathf.Deg2Rad) * radius;
        return center;
    }

    #endregion

    #region Room Info

    private void UpdateRoomInfo()
    {
        Room room = PhotonNetwork.CurrentRoom;

        if (room.CustomProperties.TryGetValue("roomName", out object roomName))
            roomNameText.text = roomName.ToString();

        if (room.CustomProperties.TryGetValue("roomId", out object roomId))
            roomNumberText.text = roomId.ToString();
    }

    private void UpdatePlayerCount()
    {
        if (!PhotonNetwork.InRoom) return;

        Room room = PhotonNetwork.CurrentRoom;
        playersText.text = $"{room.PlayerCount}/{room.MaxPlayers}";
    }

    private void OnCloseRoomClicked()
    {
        if (!PhotonNetwork.IsMasterClient) return;

        PhotonNetwork.CurrentRoom.IsOpen = false;
        PhotonNetwork.CurrentRoom.IsVisible = false;

        PhotonNetwork.CurrentRoom.SetCustomProperties(
            new Hashtable { { "closed", true } }
        );
    }

    #endregion

    #region Photon Callbacks

    public override void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
    {
        if (propertiesThatChanged.TryGetValue("closed", out object closed) && (bool)closed)
        {
            PhotonNetwork.LeaveRoom();
            return;
        }

        UpdateRoomInfo();
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        UpdatePlayerCount();
        AddPlayerEntry(newPlayer);
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        UpdatePlayerCount();
        RemovePlayerEntry(otherPlayer);
        closeRoomButton.gameObject.SetActive(PhotonNetwork.IsMasterClient);
    }

    public override void OnLeftRoom()
    {
        SceneManager.LoadScene("SampleScene");
    }

    public override void OnMasterClientSwitched(Player newMasterClient)
    {
        closeRoomButton.gameObject.SetActive(PhotonNetwork.IsMasterClient);
    }

    public override void OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps)
    {
        if (!playersPanel.activeSelf) return;

        if (changedProps.ContainsKey("ping") &&
            playerListEntries.TryGetValue(targetPlayer.ActorNumber, out GameObject entry))
        {
            TextMeshProUGUI msText = entry.transform.Find("msText")?.GetComponent<TextMeshProUGUI>();
            if (msText != null)
                msText.text = $"{changedProps["ping"]} ms";
        }
    }

    #endregion
}
