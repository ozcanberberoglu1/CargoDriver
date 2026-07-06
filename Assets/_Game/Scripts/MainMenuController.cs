using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MainMenuController : MonoBehaviourPunCallbacks
{
    [Header("Audio")]
    [SerializeField] private AudioClip mainMenuMusic;
    [SerializeField] [Range(0f, 1f)] private float musicVolume = 0.5f;

    [Header("Panels")]
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject roomsPanel;
    [SerializeField] private GameObject roomsView;
    [SerializeField] private GameObject createRoomView;
    [SerializeField] private GameObject settingsPanel;

    [Header("Main Menu Buttons")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button settingsButton;
    [SerializeField] private Button exitButton;

    [Header("Rooms View")]
    [SerializeField] private Transform roomListContent;
    [SerializeField] private GameObject roomContentPrefab;
    [SerializeField] private Button createRoomNavButton;
    [SerializeField] private Button findRoomButton;
    [SerializeField] private TMP_InputField findInputField;
    [SerializeField] private TMP_InputField nameInputField;

    [Header("Rooms View Buttons")]
    [SerializeField] private Button roomsBackButton;

    [Header("Create Room")]
    [SerializeField] private TMP_InputField roomNameInput;
    [SerializeField] private TMP_InputField passwordInput;
    [SerializeField] private Button[] playerCountButtons;
    [SerializeField] private Button createRoomButton;
    [SerializeField] private Button createRoomBackButton;

    [Header("Settings")]
    [SerializeField] private Button settingsBackButton;

    private const string PlayerNameKey = "PlayerName";

    private int selectedPlayerCount = 2;
    private readonly Dictionary<string, RoomInfo> cachedRoomList = new();
    private string filterText = "";

    private readonly Color selectedColor = new(0.282f, 1f, 0f, 1f); // #48FF00
    private readonly Color unselectedColor = new(1f, 1f, 1f, 50f / 255f); // white, alpha 50

    private void Start()
    {
        if (mainMenuMusic != null)
        {
            AudioSource music = gameObject.AddComponent<AudioSource>();
            music.clip = mainMenuMusic;
            music.loop = true;
            music.volume = musicVolume;
            music.Play();
        }

        playButton.onClick.AddListener(OnPlayClicked);
        settingsButton.onClick.AddListener(OnSettingsClicked);
        exitButton.onClick.AddListener(OnExitClicked);
        createRoomNavButton.onClick.AddListener(OnCreateRoomNavClicked);
        findRoomButton.onClick.AddListener(OnFindRoomClicked);
        roomsBackButton.onClick.AddListener(OnRoomsBackClicked);
        createRoomButton.onClick.AddListener(OnCreateRoomClicked);
        createRoomBackButton.onClick.AddListener(OnCreateRoomBackClicked);
        settingsBackButton.onClick.AddListener(OnSettingsBackClicked);

        for (int i = 0; i < playerCountButtons.Length; i++)
        {
            int count = i + 2;
            playerCountButtons[i].onClick.AddListener(() => SelectPlayerCount(count));
        }

        roomNameInput.characterLimit = 15;
        passwordInput.characterLimit = 15;
        nameInputField.characterLimit = 10;

        LoadPlayerName();
        nameInputField.onValueChanged.AddListener(OnPlayerNameChanged);

        ShowMainMenu();
        UpdatePlayerCountButtons();
    }

    private void LoadPlayerName()
    {
        string saved = PlayerPrefs.GetString(PlayerNameKey, "");
        nameInputField.text = saved;
        ApplyPlayerName(saved);
    }

    private void OnPlayerNameChanged(string value)
    {
        ApplyPlayerName(value);
        PlayerPrefs.SetString(PlayerNameKey, value);
        PlayerPrefs.Save();
    }

    private void ApplyPlayerName(string name)
    {
        PhotonNetwork.NickName = string.IsNullOrWhiteSpace(name)
            ? ""
            : name.Trim();
    }

    #region UI Navigation

    private void ShowMainMenu()
    {
        mainMenuPanel.SetActive(true);
        roomsPanel.SetActive(false);
        settingsPanel.SetActive(false);
    }

    private void ShowRooms()
    {
        mainMenuPanel.SetActive(false);
        roomsPanel.SetActive(true);
        roomsView.SetActive(true);
        createRoomView.SetActive(false);
        filterText = "";
        findInputField.text = "";
        UpdateRoomListUI();
    }

    private void ShowCreateRoom()
    {
        roomsView.SetActive(false);
        createRoomView.SetActive(true);
        roomNameInput.text = "";
        passwordInput.text = "";
        selectedPlayerCount = 2;
        UpdatePlayerCountButtons();
    }

    private void ShowSettings()
    {
        mainMenuPanel.SetActive(false);
        roomsPanel.SetActive(false);
        settingsPanel.SetActive(true);
    }

    #endregion

    #region Button Callbacks

    private void OnPlayClicked() => ShowRooms();

    private void OnExitClicked()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    private void OnSettingsClicked() => ShowSettings();

    private void OnCreateRoomNavClicked() => ShowCreateRoom();

    private void OnRoomsBackClicked() => ShowMainMenu();

    private void OnCreateRoomBackClicked() => ShowRooms();

    private void OnSettingsBackClicked() => ShowMainMenu();

    private void OnFindRoomClicked()
    {
        filterText = findInputField.text.Trim();
        UpdateRoomListUI();
    }

    private void OnCreateRoomClicked()
    {
        string roomName = roomNameInput.text.Trim();
        if (string.IsNullOrEmpty(roomName))
            return;

        if (!PhotonNetwork.IsConnectedAndReady)
        {
            Debug.LogWarning("[MainMenu] Not connected to Photon yet.");
            return;
        }

        string password = passwordInput.text.Trim();
        string roomId = GenerateUniqueRoomId();

        var props = new ExitGames.Client.Photon.Hashtable
        {
            { "roomId", roomId },
            { "password", password },
            { "roomName", roomName }
        };

        RoomOptions options = new()
        {
            MaxPlayers = (byte)selectedPlayerCount,
            CustomRoomProperties = props,
            CustomRoomPropertiesForLobby = new[] { "roomId", "password", "roomName" }
        };

        PhotonNetwork.CreateRoom(roomId, options);
    }

    #endregion

    #region Player Count Selection

    private void SelectPlayerCount(int count)
    {
        selectedPlayerCount = count;
        UpdatePlayerCountButtons();
    }

    private void UpdatePlayerCountButtons()
    {
        for (int i = 0; i < playerCountButtons.Length; i++)
        {
            Image img = playerCountButtons[i].GetComponent<Image>();
            if (img != null)
                img.color = (i + 2 == selectedPlayerCount) ? selectedColor : unselectedColor;
        }
    }

    #endregion

    #region Room List

    public override void OnRoomListUpdate(List<RoomInfo> roomList)
    {
        foreach (RoomInfo info in roomList)
        {
            if (info.RemovedFromList)
                cachedRoomList.Remove(info.Name);
            else
                cachedRoomList[info.Name] = info;
        }

        UpdateRoomListUI();
    }

    private void UpdateRoomListUI()
    {
        foreach (Transform child in roomListContent)
            Destroy(child.gameObject);

        foreach (var kvp in cachedRoomList)
        {
            RoomInfo info = kvp.Value;

            if (!string.IsNullOrEmpty(filterText))
            {
                string roomId = info.CustomProperties.ContainsKey("roomId")
                    ? info.CustomProperties["roomId"].ToString()
                    : "";

                if (!roomId.Contains(filterText))
                    continue;
            }

            GameObject entry = Instantiate(roomContentPrefab, roomListContent);
            RoomListItem item = entry.GetComponent<RoomListItem>();
            if (item == null)
                item = entry.AddComponent<RoomListItem>();

            item.Setup(info);
        }
    }

    #endregion

    #region Helpers

    private string GenerateUniqueRoomId()
    {
        string id;
        int attempts = 0;

        do
        {
            id = Random.Range(1000, 10000).ToString();
            attempts++;
        }
        while (cachedRoomList.ContainsKey(id) && attempts < 100);

        return id;
    }

    #endregion

    #region Photon Callbacks

    public override void OnJoinedRoom()
    {
        Debug.Log($"[MainMenu] Joined room: {PhotonNetwork.CurrentRoom.Name}");
        if (PhotonNetwork.IsMasterClient)
            PhotonNetwork.LoadLevel("LobbyScene");
    }

    public override void OnCreateRoomFailed(short returnCode, string message)
    {
        Debug.LogError($"[MainMenu] Create room failed: {message}");
    }

    public override void OnJoinRoomFailed(short returnCode, string message)
    {
        Debug.LogError($"[MainMenu] Join room failed: {message}");
    }

    #endregion
}
