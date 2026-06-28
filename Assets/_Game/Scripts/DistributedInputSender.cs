using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using Hashtable = ExitGames.Client.Photon.Hashtable;

public class DistributedInputSender : MonoBehaviourPun
{
    private CarControl carControl;

    private void Start()
    {
        carControl = FindAnyObjectByType<CarControl>();
    }

    private void FixedUpdate()
    {
        if (carControl == null)
            carControl = FindAnyObjectByType<CarControl>();

        if (carControl == null || !PhotonNetwork.InRoom) return;
        if (PhotonNetwork.IsMasterClient) return;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;

        float v = 0f, h = 0f;
        bool brake = false;

        if (HasControl(props, "ctrl_W", myActor) && kb.wKey.isPressed) v += 1f;
        if (HasControl(props, "ctrl_S", myActor) && kb.sKey.isPressed) v -= 1f;
        if (HasControl(props, "ctrl_A", myActor) && kb.aKey.isPressed) h -= 1f;
        if (HasControl(props, "ctrl_D", myActor) && kb.dKey.isPressed) h += 1f;
        if (HasControl(props, "ctrl_Space", myActor) && kb.spaceKey.isPressed) brake = true;

        if (Mathf.Abs(v) > 0.01f || Mathf.Abs(h) > 0.01f || brake)
        {
            photonView.RPC(nameof(RPC_SendInput), RpcTarget.MasterClient, v, h, brake);
        }
        else
        {
            photonView.RPC(nameof(RPC_SendInput), RpcTarget.MasterClient, 0f, 0f, false);
        }
    }

    private bool HasControl(Hashtable props, string key, int actor)
    {
        object val;
        props.TryGetValue(key, out val);
        return val != null && (int)val == actor;
    }

    [PunRPC]
    private void RPC_SendInput(float v, float h, bool brake)
    {
        if (carControl == null) return;
        carControl.ReceiveRemoteInput(v, h, brake);
    }
}
