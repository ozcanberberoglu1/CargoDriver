using System.Collections;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
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

    private IEnumerator Start()
    {
        closeRoomButton.onClick.AddListener(OnCloseRoomClicked);
        closeRoomButton.gameObject.SetActive(PhotonNetwork.IsMasterClient);

        while (!PhotonNetwork.InRoom ||
               !PhotonNetwork.CurrentRoom.CustomProperties.ContainsKey("roomName"))
        {
            yield return null;
        }

        UpdateRoomInfo();
        UpdatePlayerCount();
        SpawnPlayer();
    }

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

        // No child spawn points: distribute in a circle
        float angle = (PhotonNetwork.LocalPlayer.ActorNumber - 1) * 60f;
        float radius = 2f;
        Vector3 center = spawnPoints != null ? spawnPoints.position : Vector3.zero;
        center.x += Mathf.Cos(angle * Mathf.Deg2Rad) * radius;
        center.z += Mathf.Sin(angle * Mathf.Deg2Rad) * radius;
        return center;
    }

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
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        UpdatePlayerCount();
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

    #endregion
}
