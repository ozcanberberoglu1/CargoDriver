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

    private void Start()
    {
        closeRoomButton.onClick.AddListener(OnCloseRoomClicked);

        if (PhotonNetwork.InRoom)
        {
            UpdateRoomInfo();
            UpdatePlayerCount();
        }

        closeRoomButton.gameObject.SetActive(PhotonNetwork.IsMasterClient);
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
        }
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
