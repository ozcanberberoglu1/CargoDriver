using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Hashtable = ExitGames.Client.Photon.Hashtable;

public class JoinGameController : MonoBehaviourPunCallbacks
{
    [Header("Panel")]
    [SerializeField] private GameObject joinGamePanel;
    [SerializeField] private Collider joinTrigger;

    [Header("Player List")]
    [SerializeField] private Transform playerListContent;
    [SerializeField] private GameObject joinPlayerPrefab;

    [Header("Control Buttons")]
    [SerializeField] private Button wButton;
    [SerializeField] private Button aButton;
    [SerializeField] private Button sButton;
    [SerializeField] private Button dButton;
    [SerializeField] private Button spaceButton;

    [Header("Behind Vehicle")]
    [SerializeField] private Button behindVehicleButton;

    [Header("Action Buttons")]
    [SerializeField] private Button readyButton;
    [SerializeField] private Button goButton;

    [Header("Countdown")]
    [SerializeField] private TextMeshProUGUI countdownText;

    private static readonly string[] ControlKeys = { "ctrl_W", "ctrl_A", "ctrl_S", "ctrl_D", "ctrl_Space" };
    private const string BehindKey = "ctrl_Behind";
    private Button[] controlButtons;
    private readonly Dictionary<int, GameObject> joinPlayerEntries = new();
    private bool panelActive;
    private bool localReady;

    private readonly Color lockedByMeColor = new(1f, 0f, 0f, 1f);        // FF0000
    private readonly Color lockedByOtherColor = new(0.388f, 0.388f, 0.388f, 1f); // 636363
    private readonly Color unlockedColor = Color.white;

    private void Start()
    {
        controlButtons = new[] { wButton, aButton, sButton, dButton, spaceButton };

        if (joinGamePanel != null)
            joinGamePanel.SetActive(false);

        for (int i = 0; i < controlButtons.Length; i++)
        {
            int idx = i;
            controlButtons[i].onClick.AddListener(() => OnControlButtonClicked(idx));
        }

        if (behindVehicleButton != null)
            behindVehicleButton.onClick.AddListener(OnBehindVehicleClicked);

        readyButton.onClick.AddListener(OnReadyClicked);

        if (goButton != null)
        {
            goButton.gameObject.SetActive(PhotonNetwork.IsMasterClient);
            goButton.onClick.AddListener(OnGoClicked);
        }

        InitRoomProperties();
    }

    private void InitRoomProperties()
    {
        if (!PhotonNetwork.IsMasterClient) return;

        Hashtable init = new();
        foreach (string key in ControlKeys)
        {
            if (!PhotonNetwork.CurrentRoom.CustomProperties.ContainsKey(key))
                init[key] = -1;
        }
        if (!PhotonNetwork.CurrentRoom.CustomProperties.ContainsKey(BehindKey))
            init[BehindKey] = -1;

        if (init.Count > 0)
            PhotonNetwork.CurrentRoom.SetCustomProperties(init);
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb.escapeKey.wasPressedThisFrame && IsPanelOpen())
            ClosePanel();
    }

    private bool IsPanelOpen()
    {
        if (joinGamePanel != null)
            return joinGamePanel.activeSelf;

        GameObject found = GameObject.Find("JoinGamePanel");
        return found != null && found.activeSelf;
    }

    public void ShowPanel()
    {
        if (panelActive) return;
        panelActive = true;

        if (joinGamePanel == null)
        {
            Canvas[] allCanvas = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (Canvas c in allCanvas)
            {
                Transform t = c.transform.Find("JoinGamePanel");
                if (t != null) { joinGamePanel = t.gameObject; break; }
            }
        }

        if (joinGamePanel != null)
            joinGamePanel.SetActive(true);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        ToyController local = FindAnyObjectByType<ToyController>();
        if (local != null && local.photonView.IsMine)
            local.SetPaused(true);

        RebuildJoinPlayerList();
        RefreshAllButtons();
    }

    #region Control Buttons

    private void OnControlButtonClicked(int idx)
    {
        string key = ControlKeys[idx];
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;

        object val;
        PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(key, out val);
        int current = val != null ? (int)val : -1;

        if (current == myActor)
        {
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { key, -1 } });
        }
        else if (current == -1)
        {
            Hashtable updates = new() { { key, myActor } };

            object behindVal;
            PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(BehindKey, out behindVal);
            if (behindVal != null && (int)behindVal == myActor)
                updates[BehindKey] = -1;

            PhotonNetwork.CurrentRoom.SetCustomProperties(updates);
        }
    }

    private void OnBehindVehicleClicked()
    {
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;

        object val;
        PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(BehindKey, out val);
        int current = val != null ? (int)val : -1;

        if (current == myActor)
        {
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { BehindKey, -1 } });
        }
        else if (current == -1)
        {
            Hashtable updates = new() { { BehindKey, myActor } };

            var props = PhotonNetwork.CurrentRoom.CustomProperties;
            foreach (string ctrlKey in ControlKeys)
            {
                object ctrlVal;
                props.TryGetValue(ctrlKey, out ctrlVal);
                if (ctrlVal != null && (int)ctrlVal == myActor)
                    updates[ctrlKey] = -1;
            }

            PhotonNetwork.CurrentRoom.SetCustomProperties(updates);
        }
    }

    private void RefreshAllButtons()
    {
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;
        var props = PhotonNetwork.CurrentRoom.CustomProperties;

        for (int i = 0; i < controlButtons.Length; i++)
        {
            string key = ControlKeys[i];
            object val;
            props.TryGetValue(key, out val);
            int owner = val != null ? (int)val : -1;

            Image img = controlButtons[i].GetComponent<Image>();
            Transform playerNameT = controlButtons[i].transform.Find("PlayerName");
            TextMeshProUGUI nameText = playerNameT != null
                ? playerNameT.GetComponent<TextMeshProUGUI>()
                : null;

            if (owner == -1)
            {
                if (img) img.color = unlockedColor;
                controlButtons[i].interactable = true;
                if (nameText) nameText.text = "Empty";
            }
            else if (owner == myActor)
            {
                if (img) img.color = lockedByMeColor;
                controlButtons[i].interactable = true;
                if (nameText) nameText.text = GetPlayerName(myActor);
            }
            else
            {
                if (img) img.color = lockedByOtherColor;
                controlButtons[i].interactable = false;
                if (nameText) nameText.text = GetPlayerName(owner);
            }
        }

        RefreshBehindButton(myActor, props);
    }

    private void RefreshBehindButton(int myActor, Hashtable props)
    {
        if (behindVehicleButton == null) return;

        object val;
        props.TryGetValue(BehindKey, out val);
        int owner = val != null ? (int)val : -1;

        Image img = behindVehicleButton.GetComponent<Image>();
        Transform playerNameT = behindVehicleButton.transform.Find("PlayerName");
        TextMeshProUGUI nameText = playerNameT != null
            ? playerNameT.GetComponent<TextMeshProUGUI>()
            : null;

        if (owner == -1)
        {
            if (img) img.color = unlockedColor;
            behindVehicleButton.interactable = true;
            if (nameText) nameText.text = "Empty";
        }
        else if (owner == myActor)
        {
            if (img) img.color = lockedByMeColor;
            behindVehicleButton.interactable = true;
            if (nameText) nameText.text = GetPlayerName(myActor);
        }
        else
        {
            if (img) img.color = lockedByOtherColor;
            behindVehicleButton.interactable = false;
            if (nameText) nameText.text = GetPlayerName(owner);
        }
    }

    #endregion

    #region Ready System

    private void OnReadyClicked()
    {
        localReady = !localReady;
        string readyKey = $"ready_{PhotonNetwork.LocalPlayer.ActorNumber}";
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { readyKey, localReady } });
    }

    private void RefreshReadyStates()
    {
        var props = PhotonNetwork.CurrentRoom.CustomProperties;

        foreach (var kvp in joinPlayerEntries)
        {
            int actorNum = kvp.Key;
            GameObject entry = kvp.Value;
            if (entry == null) continue;

            TextMeshProUGUI readyText = entry.transform.Find("PlayerReadyText")?.GetComponent<TextMeshProUGUI>();
            if (readyText == null) continue;

            string readyKey = $"ready_{actorNum}";
            object val;
            props.TryGetValue(readyKey, out val);
            bool ready = val != null && (bool)val;

            readyText.text = ready ? "Ready" : "Not Ready";
            readyText.color = ready ? Color.green : Color.red;
        }

        if (goButton != null && PhotonNetwork.IsMasterClient)
            goButton.interactable = IsEveryoneReady();
    }

    private bool IsEveryoneReady()
    {
        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        foreach (Player p in PhotonNetwork.PlayerList)
        {
            string key = $"ready_{p.ActorNumber}";
            object val;
            props.TryGetValue(key, out val);
            if (val == null || !(bool)val) return false;
        }
        return true;
    }

    #endregion

    #region Go Button

    private void OnGoClicked()
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (!IsEveryoneReady()) return;

        SaveCargoPositions();

        PhotonNetwork.CurrentRoom.SetCustomProperties(
            new Hashtable { { "countdown", 5 } });

        ClosePanel();
    }

    public void ClosePanel()
    {
        panelActive = false;

        if (joinGamePanel != null)
        {
            joinGamePanel.SetActive(false);
        }
        else
        {
            GameObject found = GameObject.Find("JoinGamePanel");
            if (found != null) found.SetActive(false);
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        ToyController local = FindAnyObjectByType<ToyController>();
        if (local != null && local.photonView.IsMine)
            local.SetPaused(false);
    }

    private void UpdateCountdown(Hashtable changedProps)
    {
        if (!changedProps.ContainsKey("countdown")) return;

        int value = (int)changedProps["countdown"];
        if (value <= 0) return;

        ClosePanel();

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(true);
            countdownText.text = value.ToString();
        }

        if (PhotonNetwork.IsMasterClient)
            StartCoroutine(CountdownTick(value));
    }

    private IEnumerator CountdownTick(int current)
    {
        yield return new WaitForSeconds(1f);

        int next = current - 1;

        if (next > 0)
        {
            PhotonNetwork.CurrentRoom.SetCustomProperties(
                new Hashtable { { "countdown", next } });
        }
        else
        {
            PhotonNetwork.CurrentRoom.SetCustomProperties(
                new Hashtable { { "countdown", 0 } });

            yield return new WaitForSeconds(0.3f);
            PhotonNetwork.LoadLevel("GameScene");
        }
    }

    private void SaveCargoPositions()
    {
        var lobby = FindAnyObjectByType<LobbyController>();
        if (lobby == null)
        {
            Debug.LogError("[JoinGame] LobbyController not found!");
            return;
        }

        List<GameObject> boxes = lobby.CargoBoxes;
        if (boxes == null || boxes.Count == 0)
        {
            Debug.LogError("[JoinGame] No cargo boxes in list!");
            return;
        }

        GameObject pickup = GameObject.Find("Pickup");
        if (pickup == null)
        {
            Debug.LogError("[JoinGame] Pickup not found!");
            return;
        }

        Vector3 pickupPos = pickup.transform.position;
        Quaternion pickupRot = pickup.transform.rotation;
        Vector3 pickupScale = pickup.transform.localScale;

        string data = $"{F(pickupPos.x)},{F(pickupPos.y)},{F(pickupPos.z)}|{F(pickupRot.x)},{F(pickupRot.y)},{F(pickupRot.z)},{F(pickupRot.w)}|{F(pickupScale.x)},{F(pickupScale.y)},{F(pickupScale.z)}";

        var allBoxes = new List<GameObject>();
        foreach (GameObject box in boxes)
        {
            if (box == null) continue;
            CollectAllLegos(box.transform, allBoxes);
        }

        for (int idx = 0; idx < allBoxes.Count; idx++)
        {
            GameObject box = allBoxes[idx];
            Vector3 localPos = pickup.transform.InverseTransformPoint(box.transform.position);
            Quaternion localRot = Quaternion.Inverse(pickup.transform.rotation) * box.transform.rotation;
            Vector3 scale = box.transform.lossyScale;
            string prefabName = GetPrefabName(box);

            int parentIdx = -1;
            LegoSnap snap = box.GetComponent<LegoSnap>();
            if (snap != null && snap.HasParent)
            {
                Transform p = box.transform.parent;
                if (p != null)
                {
                    LegoSnap pSnap = p.GetComponent<LegoSnap>();
                    if (pSnap != null)
                        parentIdx = allBoxes.IndexOf(p.gameObject);
                }
            }

            Color col = GetBoxColor(box);

            data += $";{F(localPos.x)},{F(localPos.y)},{F(localPos.z)},{F(localRot.x)},{F(localRot.y)},{F(localRot.z)},{F(localRot.w)},{F(scale.x)},{F(scale.y)},{F(scale.z)},{prefabName},{parentIdx},{F(col.r)},{F(col.g)},{F(col.b)},{F(col.a)}";
        }

        Debug.Log($"[JoinGame] Saving cargo data: {data}");
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { "cargoData", data } });
    }

    private string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    /// <summary>
    /// Reads the box's authored tint from its shared material so it can travel to the
    /// GameScene. The lobby boxes are scene instances with per-instance material overrides;
    /// GameScene re-instantiates from a single Resources prefab, so without this every box
    /// would spawn with the prefab's default color for every player.
    /// </summary>
    private Color GetBoxColor(GameObject box)
    {
        Renderer r = box.GetComponent<Renderer>();
        if (r == null) r = box.GetComponentInChildren<Renderer>();
        if (r == null || r.sharedMaterial == null) return Color.white;

        Material m = r.sharedMaterial;
        if (m.HasProperty(BaseColorId)) return m.GetColor(BaseColorId);
        return m.color;
    }

    private void CollectAllLegos(Transform t, List<GameObject> result)
    {
        if (result.Contains(t.gameObject)) return;
        result.Add(t.gameObject);
        foreach (Transform child in t)
        {
            if (child.GetComponent<LegoSnap>() != null)
                CollectAllLegos(child, result);
        }
    }

    private string GetPrefabName(GameObject obj)
    {
#if UNITY_EDITOR
        var prefab = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(obj);
        if (prefab != null) return prefab.name;
#endif
        string name = obj.name.Replace("(Clone)", "").Trim();
        int parenIdx = name.IndexOf(" (");
        if (parenIdx > 0) name = name.Substring(0, parenIdx);
        return name;
    }

    #endregion

    #region Player List

    private void RebuildJoinPlayerList()
    {
        foreach (var kvp in joinPlayerEntries)
            Destroy(kvp.Value);
        joinPlayerEntries.Clear();

        foreach (Player p in PhotonNetwork.PlayerList)
            AddJoinPlayerEntry(p);

        RefreshReadyStates();
    }

    private void AddJoinPlayerEntry(Player player)
    {
        if (joinPlayerEntries.ContainsKey(player.ActorNumber)) return;

        GameObject entry = Instantiate(joinPlayerPrefab, playerListContent);
        joinPlayerEntries[player.ActorNumber] = entry;

        TextMeshProUGUI nameText = entry.transform.Find("PlayerNameText")?.GetComponent<TextMeshProUGUI>();
        if (nameText != null)
            nameText.text = GetPlayerName(player.ActorNumber);
    }

    private void RemoveJoinPlayerEntry(Player player)
    {
        if (joinPlayerEntries.TryGetValue(player.ActorNumber, out GameObject entry))
        {
            Destroy(entry);
            joinPlayerEntries.Remove(player.ActorNumber);
        }
    }

    #endregion

    #region Photon Callbacks

    public override void OnRoomPropertiesUpdate(Hashtable changedProps)
    {
        UpdateCountdown(changedProps);

        if (!panelActive) return;

        bool controlChanged = false;
        bool readyChanged = false;

        foreach (var key in changedProps.Keys)
        {
            string k = key.ToString();
            if (k.StartsWith("ctrl_")) controlChanged = true;
            if (k.StartsWith("ready_")) readyChanged = true;
        }

        if (controlChanged) RefreshAllButtons();
        if (readyChanged) RefreshReadyStates();
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        if (panelActive)
        {
            AddJoinPlayerEntry(newPlayer);
            RefreshReadyStates();
        }
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        if (panelActive)
        {
            RemoveJoinPlayerEntry(otherPlayer);
            ClearPlayerLocks(otherPlayer.ActorNumber);
            RefreshAllButtons();
            RefreshReadyStates();
        }
    }

    private void ClearPlayerLocks(int actorNumber)
    {
        Hashtable clear = new();
        var props = PhotonNetwork.CurrentRoom.CustomProperties;

        foreach (string key in ControlKeys)
        {
            object val;
            props.TryGetValue(key, out val);
            if (val != null && (int)val == actorNumber)
                clear[key] = -1;
        }

        object behindVal;
        props.TryGetValue(BehindKey, out behindVal);
        if (behindVal != null && (int)behindVal == actorNumber)
            clear[BehindKey] = -1;

        string readyKey = $"ready_{actorNumber}";
        clear[readyKey] = false;

        if (clear.Count > 0)
            PhotonNetwork.CurrentRoom.SetCustomProperties(clear);
    }

    public override void OnMasterClientSwitched(Player newMasterClient)
    {
        if (goButton != null)
            goButton.gameObject.SetActive(PhotonNetwork.IsMasterClient);
    }

    #endregion

    #region Helpers

    private string GetPlayerName(int actorNumber)
    {
        foreach (Player p in PhotonNetwork.PlayerList)
        {
            if (p.ActorNumber == actorNumber)
                return string.IsNullOrEmpty(p.NickName) ? $"Player{p.ActorNumber}" : p.NickName;
        }
        return "Unknown";
    }

    #endregion
}
