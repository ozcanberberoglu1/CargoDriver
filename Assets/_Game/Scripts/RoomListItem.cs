using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RoomListItem : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI roomNameText;
    [SerializeField] private TextMeshProUGUI roomCountText;
    [SerializeField] private Button joinButton;

    private RoomInfo roomInfo;

    public void Setup(RoomInfo info)
    {
        roomInfo = info;

        if (roomNameText == null)
            roomNameText = transform.Find("RoomNameText")?.GetComponent<TextMeshProUGUI>();
        if (roomCountText == null)
            roomCountText = transform.Find("RoomCountText")?.GetComponent<TextMeshProUGUI>();
        if (joinButton == null)
            joinButton = transform.Find("JoinButton")?.GetComponent<Button>();

        string displayName = info.CustomProperties.ContainsKey("roomName")
            ? info.CustomProperties["roomName"].ToString()
            : info.Name;

        if (roomNameText != null)
            roomNameText.text = displayName;

        if (roomCountText != null)
            roomCountText.text = $"{info.PlayerCount}/{info.MaxPlayers}";

        bool isFull = info.PlayerCount >= info.MaxPlayers;

        if (joinButton != null)
        {
            joinButton.onClick.RemoveAllListeners();

            if (isFull)
            {
                joinButton.interactable = false;
                Image btnImage = joinButton.GetComponent<Image>();
                if (btnImage != null)
                {
                    Color c = btnImage.color;
                    c.a = 0.3f;
                    btnImage.color = c;
                }
            }
            else
            {
                joinButton.interactable = true;
                joinButton.onClick.AddListener(OnJoinClicked);

                Image btnImage = joinButton.GetComponent<Image>();
                if (btnImage != null)
                {
                    Color c = btnImage.color;
                    c.a = 1f;
                    btnImage.color = c;
                }
            }
        }
    }

    private void OnJoinClicked()
    {
        if (roomInfo == null) return;
        if (roomInfo.PlayerCount >= roomInfo.MaxPlayers) return;

        PhotonNetwork.JoinRoom(roomInfo.Name);
    }
}
