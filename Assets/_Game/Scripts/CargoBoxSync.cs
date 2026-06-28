using Photon.Pun;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(PhotonView))]
public class CargoBoxSync : MonoBehaviourPun, IPunObservable
{
    private Rigidbody rb;
    private bool isGrabbed;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
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
            isGrabbed = (bool)stream.ReceiveNext();
        }
    }
}
