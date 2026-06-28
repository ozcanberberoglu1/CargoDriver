using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Hashtable = ExitGames.Client.Photon.Hashtable;

public class CarControl : MonoBehaviourPun, IPunObservable
{
    public float enginePower = 2000.0f;
    public float brakePower = 3000.0f;
    public float turnSpeed = 25.0f;
    public float turnSmoothness = 5.0f;
    public Transform[] wheels;
    public Transform[] wheelMeshes;
    public Transform centerOfMass;
    public GameObject steeringWheel;

    private Rigidbody rb;
    private float currentTurnAngle;

    private float netVertical;
    private float netHorizontal;
    private bool netBrake;

    private Vector3 syncPos;
    private Quaternion syncRot;
    private Vector3 syncVel;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (centerOfMass != null)
            rb.centerOfMass = centerOfMass.localPosition;

        syncPos = rb.position;
        syncRot = rb.rotation;
    }

    void FixedUpdate()
    {
        if (!PhotonNetwork.InRoom)
        {
            RunPhysics(GetLocalVertical(), GetLocalHorizontal(), GetLocalBrake());
            return;
        }

        if (PhotonNetwork.IsMasterClient)
        {
            float v = 0f, h = 0f;
            bool brake = false;
            GatherDistributedInput(ref v, ref h, ref brake);
            RunPhysics(v, h, brake);
        }
        else
        {
            rb.position = Vector3.Lerp(rb.position, syncPos, Time.fixedDeltaTime * 10f);
            rb.rotation = Quaternion.Lerp(rb.rotation, syncRot, Time.fixedDeltaTime * 10f);
            rb.linearVelocity = syncVel;
            UpdateWheelMeshes();
        }
    }

    private void GatherDistributedInput(ref float vertical, ref float horizontal, ref bool brake)
    {
        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        Keyboard kb = Keyboard.current;
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;

        vertical += GetKeyInput(props, "ctrl_W", myActor, kb, kb?.wKey, 1f);
        vertical += GetKeyInput(props, "ctrl_S", myActor, kb, kb?.sKey, -1f);
        horizontal += GetKeyInput(props, "ctrl_A", myActor, kb, kb?.leftArrowKey, -1f);
        if (horizontal == 0f)
            horizontal += GetKeyInput(props, "ctrl_A", myActor, kb, kb?.aKey, -1f);
        horizontal += GetKeyInput(props, "ctrl_D", myActor, kb, kb?.dKey, 1f);

        object spaceVal;
        props.TryGetValue("ctrl_Space", out spaceVal);
        int spaceOwner = spaceVal != null ? (int)spaceVal : -1;
        if (spaceOwner == myActor && kb != null && kb.spaceKey.isPressed)
            brake = true;

        vertical += netVertical;
        horizontal += netHorizontal;
        if (netBrake) brake = true;

        vertical = Mathf.Clamp(vertical, -1f, 1f);
        horizontal = Mathf.Clamp(horizontal, -1f, 1f);
    }

    private float GetKeyInput(Hashtable props, string ctrlKey, int myActor, Keyboard kb, KeyControl key, float value)
    {
        if (kb == null || key == null) return 0f;

        object val;
        props.TryGetValue(ctrlKey, out val);
        int owner = val != null ? (int)val : -1;

        if (owner == myActor && key.isPressed)
            return value;
        return 0f;
    }

    private float GetLocalVertical()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return 0f;
        float v = 0f;
        if (kb.wKey.isPressed) v += 1f;
        if (kb.sKey.isPressed) v -= 1f;
        return v;
    }

    private float GetLocalHorizontal()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return 0f;
        float h = 0f;
        if (kb.aKey.isPressed) h -= 1f;
        if (kb.dKey.isPressed) h += 1f;
        return h;
    }

    private bool GetLocalBrake()
    {
        Keyboard kb = Keyboard.current;
        return kb != null && kb.spaceKey.isPressed;
    }

    private void RunPhysics(float verticalInput, float horizontalInput, bool brake)
    {
        float targetTurnAngle = horizontalInput * turnSpeed;
        currentTurnAngle = Mathf.Lerp(currentTurnAngle, targetTurnAngle, Time.deltaTime * turnSmoothness);

        if (steeringWheel != null)
            steeringWheel.transform.localEulerAngles = new Vector3(-64, 0, currentTurnAngle * 3);

        for (int i = 0; i < wheels.Length; i++)
        {
            WheelCollider wc = wheels[i].GetComponent<WheelCollider>();

            if (i < 2)
                wc.steerAngle = currentTurnAngle;
            else
                wc.steerAngle = 0f;

            if (brake)
            {
                wc.motorTorque = 0f;
                wc.brakeTorque = brakePower;
            }
            else
            {
                wc.brakeTorque = 0f;
                wc.motorTorque = verticalInput * enginePower;
            }
        }

        UpdateWheelMeshes();
    }

    private void UpdateWheelMeshes()
    {
        for (int i = 0; i < wheels.Length && i < wheelMeshes.Length; i++)
        {
            WheelCollider wc = wheels[i].GetComponent<WheelCollider>();
            wc.GetWorldPose(out Vector3 pos, out Quaternion rot);
            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(rb.position);
            stream.SendNext(rb.rotation);
            stream.SendNext(rb.linearVelocity);
            stream.SendNext(currentTurnAngle);
        }
        else
        {
            syncPos = (Vector3)stream.ReceiveNext();
            syncRot = (Quaternion)stream.ReceiveNext();
            syncVel = (Vector3)stream.ReceiveNext();
            currentTurnAngle = (float)stream.ReceiveNext();
        }
    }

    public void ReceiveRemoteInput(float v, float h, bool brake)
    {
        netVertical = v;
        netHorizontal = h;
        netBrake = brake;
    }
}
