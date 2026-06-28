using Photon.Pun;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(PhotonView))]
public class CargoBoxSync : MonoBehaviourPun, IPunObservable, IPunOwnershipCallbacks
{
    private Rigidbody rb;
    private bool isGrabbed;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Start()
    {
        PhotonNetwork.AddCallbackTarget(this);
        Debug.Log($"[CargoBoxSync] {gameObject.name} ViewID={photonView.ViewID} Owner={photonView.Owner?.NickName ?? "null"} IsMine={photonView.IsMine} OwnershipTransfer={photonView.OwnershipTransfer}");
    }

    private void OnDestroy()
    {
        PhotonNetwork.RemoveCallbackTarget(this);
    }

    private void Update()
    {
        if (photonView.IsMine) return;

        if (isGrabbed)
        {
            rb.isKinematic = true;
        }
        else
        {
            rb.isKinematic = false;
            rb.useGravity = true;
        }
    }

    public void SetGrabbed(bool grabbed)
    {
        isGrabbed = grabbed;
        Debug.Log($"[CargoBoxSync] SetGrabbed({grabbed}) on {gameObject.name} ViewID={photonView.ViewID} IsMine={photonView.IsMine} Owner={photonView.Owner?.NickName ?? "null"}");

        if (photonView.IsMine)
        {
            rb.isKinematic = false;
            rb.useGravity = !grabbed;
            rb.linearDamping = grabbed ? 12f : 0f;
            rb.angularDamping = grabbed ? 8f : 0.05f;
        }
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(isGrabbed);
        }
        else
        {
            bool prev = isGrabbed;
            isGrabbed = (bool)stream.ReceiveNext();
            if (prev != isGrabbed)
                Debug.Log($"[CargoBoxSync] RECV grabbed={isGrabbed} on {gameObject.name} from {info.Sender?.NickName}");
        }
    }

    public void OnOwnershipRequest(PhotonView targetView, Photon.Realtime.Player requestingPlayer)
    {
        if (targetView != photonView) return;
        Debug.Log($"[CargoBoxSync] OwnershipRequest on {gameObject.name} from {requestingPlayer.NickName}");
    }

    public void OnOwnershipTransfered(PhotonView targetView, Photon.Realtime.Player previousOwner)
    {
        if (targetView != photonView) return;
        Debug.Log($"[CargoBoxSync] OwnershipTransferred on {gameObject.name} from {previousOwner?.NickName} to {targetView.Owner?.NickName} IsMine={targetView.IsMine}");
    }

    public void OnOwnershipTransferFailed(PhotonView targetView, Photon.Realtime.Player senderOfFailedRequest)
    {
        if (targetView != photonView) return;
        Debug.LogError($"[CargoBoxSync] OwnershipTransferFAILED on {gameObject.name} sender={senderOfFailedRequest?.NickName}");
    }
}
