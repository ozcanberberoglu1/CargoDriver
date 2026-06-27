using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

public class NetworkManager : MonoBehaviourPunCallbacks
{
    public static NetworkManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        PhotonNetwork.AutomaticallySyncScene = true;

        if (!PhotonNetwork.IsConnected)
            PhotonNetwork.ConnectUsingSettings();
    }

    public override void OnConnectedToMaster()
    {
        Debug.Log("[NetworkManager] Connected to Master Server");
        PhotonNetwork.JoinLobby();
    }

    public override void OnJoinedLobby()
    {
        Debug.Log("[NetworkManager] Joined Lobby");
    }

    public override void OnDisconnected(DisconnectCause cause)
    {
        Debug.LogWarning($"[NetworkManager] Disconnected: {cause}");
    }
}
