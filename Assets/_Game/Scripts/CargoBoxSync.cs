using Photon.Pun;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(PhotonView))]
public class CargoBoxSync : MonoBehaviourPun, IPunObservable
{
    private Rigidbody rb;
    private bool syncGrabbed;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void Update()
    {
        if (photonView.IsMine) return;

        rb.isKinematic = true;
    }

    public void SetGrabbed(bool grabbed)
    {
        syncGrabbed = grabbed;

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
            stream.SendNext(syncGrabbed);
        }
        else
        {
            syncGrabbed = (bool)stream.ReceiveNext();
        }
    }

    private void OnEnable()
    {
        if (photonView != null && !photonView.IsMine)
            rb.isKinematic = true;
    }
}
