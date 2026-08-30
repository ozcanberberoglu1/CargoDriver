using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RoomListItem : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI roomNameText;
    [SerializeField] private TextMeshProUGUI roomCountText;
    [SerializeField] private TextMeshProUGUI pingCountText;
    [SerializeField] private Button joinButton;

    [Header("Status (Public / Full / Private)")]
    [SerializeField] private Image statusIcon;
    [SerializeField] private TextMeshProUGUI statusCountText;
    [SerializeField] private Sprite publicIcon;   // Spr_GameAtlas_9
    [SerializeField] private Sprite fullIcon;      // Spr_GameAtlas_8
    [SerializeField] private Sprite privateIcon;   // Spr_GameAtlas_10

    private RoomInfo roomInfo;

    private static readonly Color PublicColor = Hex("DFBC03");
    private static readonly Color FullColor = Hex("FC5F12");
    private static readonly Color PrivateColor = Hex("D7D7D9");

    private static Color Hex(string h)
    {
        ColorUtility.TryParseHtmlString("#" + h, out Color c);
        return c;
    }

    public void Setup(RoomInfo info)
    {
        roomInfo = info;

        if (roomNameText == null) roomNameText = transform.Find("RoomNameText")?.GetComponent<TextMeshProUGUI>();
        if (roomCountText == null) roomCountText = transform.Find("RoomCountText")?.GetComponent<TextMeshProUGUI>();
        if (joinButton == null) joinButton = transform.Find("JoinButton")?.GetComponent<Button>();

        string displayName = info.CustomProperties.ContainsKey("roomName")
            ? info.CustomProperties["roomName"].ToString()
            : info.Name;

        if (roomNameText != null) roomNameText.text = displayName;
        if (roomCountText != null) roomCountText.text = $"{info.PlayerCount}/{info.MaxPlayers}";

        bool isFull = info.PlayerCount >= info.MaxPlayers;
        bool isPrivate = info.CustomProperties.TryGetValue("password", out object pw)
                         && !string.IsNullOrEmpty(pw as string);

        // Host's ping — the host publishes this into a room property every so often.
        if (pingCountText != null)
        {
            string ms = info.CustomProperties.TryGetValue("hostPing", out object p) ? p.ToString() : "-";
            pingCountText.text = $"{ms} ms";
        }

        // Public / Full / Private state (private wins — a locked room is private whether full or not).
        if (statusIcon != null && statusCountText != null)
        {
            if (isPrivate)
            {
                statusIcon.sprite = privateIcon;
                statusCountText.text = "PRIVATE";
                statusCountText.color = PrivateColor;
            }
            else if (isFull)
            {
                statusIcon.sprite = fullIcon;
                statusCountText.text = "FULL";
                statusCountText.color = FullColor;
            }
            else
            {
                statusIcon.sprite = publicIcon;
                statusCountText.text = "PUBLIC";
                statusCountText.color = PublicColor;
            }
        }

        if (joinButton != null)
        {
            joinButton.onClick.RemoveAllListeners();
            joinButton.interactable = !isFull;
            if (!isFull) joinButton.onClick.AddListener(OnJoinClicked);

            Image btnImage = joinButton.GetComponent<Image>();
            if (btnImage != null)
            {
                Color c = btnImage.color;
                c.a = isFull ? 0.2f : 1f; // 20% when full, 100% when joinable
                btnImage.color = c;
            }
        }
    }

    private void OnJoinClicked()
    {
        if (roomInfo == null) return;
        if (roomInfo.PlayerCount >= roomInfo.MaxPlayers) return;

        bool isPrivate = roomInfo.CustomProperties.TryGetValue("password", out object pw)
                         && !string.IsNullOrEmpty(pw as string);

        if (isPrivate)
        {
            // Locked room: hand off to the password prompt instead of joining directly.
            var panel = FindAnyObjectByType<PrivateRoomJoinPanel>(FindObjectsInactive.Include);
            if (panel != null)
            {
                panel.Open(roomInfo);
                return;
            }
        }

        LoadingScreen.Instance?.Begin();
        PhotonNetwork.JoinRoom(roomInfo.Name);
    }
}
