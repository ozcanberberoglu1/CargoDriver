using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;
using Hashtable = ExitGames.Client.Photon.Hashtable;

/// <summary>
/// Co-op vehicle control. Every player owns a single key; their input is relayed to the
/// master, which is the only client that simulates the truck. Non-masters move a kinematic
/// copy toward the replicated pose.
///
/// Cargo is deliberately not part of this stream: each box carries its own PhotonView and
/// is replicated by <see cref="NetworkedCargoBody"/>.
/// </summary>
public class CarControl : MonoBehaviourPun, IPunObservable, IOnEventCallback
{
    public float enginePower = 2000.0f;
    public float brakePower = 3000.0f;
    public float turnSpeed = 25.0f;
    public float turnSmoothness = 5.0f;
    public Transform[] wheels;
    public Transform[] wheelMeshes;
    public Transform centerOfMass;
    public GameObject steeringWheel;

    [Header("Remote Smoothing")]
    [SerializeField] private float positionSmoothTime = 0.06f;
    [SerializeField] private float rotationLerpSpeed = 20f;
    [SerializeField] private float teleportDistance = 10f;

    private Rigidbody rb;
    private float currentTurnAngle;

    private Vector3 targetPos;
    private Quaternion targetRot;
    private Vector3 targetVelocity;
    private Vector3 carSmoothVel;
    private float wheelSpin;

    private const byte INPUT_EVENT = 42;
    private readonly Dictionary<int, float[]> remoteInputs = new();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void Start()
    {
        if (centerOfMass != null && rb != null)
            rb.centerOfMass = centerOfMass.localPosition;

        if (rb != null)
        {
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            targetPos = rb.position;
            targetRot = rb.rotation;
        }

        if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient && rb != null)
        {
            rb.isKinematic = true;
            foreach (WheelCollider wc in GetComponentsInChildren<WheelCollider>())
                wc.enabled = false;
        }
    }

    void OnEnable() => PhotonNetwork.AddCallbackTarget(this);
    void OnDisable() => PhotonNetwork.RemoveCallbackTarget(this);

    void FixedUpdate()
    {
        if (rb == null) return;

        if (!PhotonNetwork.InRoom)
        {
            RunPhysics(GetLocalVertical(), GetLocalHorizontal(), GetLocalBrake());
            return;
        }

        SendMyInput();

        if (PhotonNetwork.IsMasterClient)
        {
            float v = 0f, h = 0f;
            bool brake = false;
            GatherAllInput(ref v, ref h, ref brake);
            RunPhysics(v, h, brake);
            return;
        }

        ApplyRemotePose();
    }

    /// <summary>
    /// Moves the kinematic copy through the physics engine rather than writing the
    /// transform directly. Physics.autoSyncTransforms is off in this project, so a raw
    /// transform write would leave the truck's colliders (and anything resting on them)
    /// a frame behind.
    /// </summary>
    private void ApplyRemotePose()
    {
        if (Vector3.Distance(rb.position, targetPos) > teleportDistance)
        {
            carSmoothVel = Vector3.zero;
            rb.position = targetPos;
            rb.rotation = targetRot;
            return;
        }

        Vector3 next = Vector3.SmoothDamp(rb.position, targetPos, ref carSmoothVel,
            positionSmoothTime, Mathf.Infinity, Time.fixedDeltaTime);

        rb.MovePosition(next);
        rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, Time.fixedDeltaTime * rotationLerpSpeed));
    }

    void Update()
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) return;

        if (steeringWheel != null)
            steeringWheel.transform.localEulerAngles = new Vector3(-64, 0, currentTurnAngle * 3);

        wheelSpin += targetVelocity.magnitude * Time.deltaTime * 60f;

        for (int i = 0; i < wheelMeshes.Length && i < wheels.Length; i++)
        {
            wheelMeshes[i].position = wheels[i].position;

            Quaternion steer = i < 2
                ? Quaternion.Euler(0f, currentTurnAngle, 0f)
                : Quaternion.identity;
            Quaternion spin = Quaternion.Euler(wheelSpin, 0f, 0f);

            wheelMeshes[i].rotation = transform.rotation * steer * spin;
        }
    }

    #region Input

    private void SendMyInput()
    {
        if (PhotonNetwork.IsMasterClient) return;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;

        float v = 0f, h = 0f;
        bool brake = false;

        if (HasCtrl(props, "ctrl_W", myActor) && kb.wKey.isPressed) v += 1f;
        if (HasCtrl(props, "ctrl_S", myActor) && kb.sKey.isPressed) v -= 1f;
        if (HasCtrl(props, "ctrl_A", myActor) && kb.aKey.isPressed) h -= 1f;
        if (HasCtrl(props, "ctrl_D", myActor) && kb.dKey.isPressed) h += 1f;
        if (HasCtrl(props, "ctrl_Space", myActor) && kb.spaceKey.isPressed) brake = true;

        float[] data = { v, h, brake ? 1f : 0f };

        PhotonNetwork.RaiseEvent(INPUT_EVENT, data,
            new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient },
            new SendOptions { DeliveryMode = DeliveryMode.Unreliable });
    }

    private void GatherAllInput(ref float vertical, ref float horizontal, ref bool brake)
    {
        Keyboard kb = Keyboard.current;
        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        int myActor = PhotonNetwork.LocalPlayer.ActorNumber;

        if (kb != null)
        {
            if (HasCtrl(props, "ctrl_W", myActor) && kb.wKey.isPressed) vertical += 1f;
            if (HasCtrl(props, "ctrl_S", myActor) && kb.sKey.isPressed) vertical -= 1f;
            if (HasCtrl(props, "ctrl_A", myActor) && kb.aKey.isPressed) horizontal -= 1f;
            if (HasCtrl(props, "ctrl_D", myActor) && kb.dKey.isPressed) horizontal += 1f;
            if (HasCtrl(props, "ctrl_Space", myActor) && kb.spaceKey.isPressed) brake = true;
        }

        foreach (var kvp in remoteInputs)
        {
            float[] inp = kvp.Value;
            vertical += inp[0];
            horizontal += inp[1];
            if (inp[2] > 0.5f) brake = true;
        }

        vertical = Mathf.Clamp(vertical, -1f, 1f);
        horizontal = Mathf.Clamp(horizontal, -1f, 1f);
    }

    public void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != INPUT_EVENT) return;
        float[] data = (float[])photonEvent.CustomData;
        remoteInputs[photonEvent.Sender] = data;
    }

    private bool HasCtrl(Hashtable props, string key, int actor)
    {
        object val;
        props.TryGetValue(key, out val);
        return val != null && (int)val == actor;
    }

    #endregion

    #region Physics

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

            if (i < 2) wc.steerAngle = currentTurnAngle;
            else wc.steerAngle = 0f;

            if (brake) { wc.motorTorque = 0f; wc.brakeTorque = brakePower; }
            else { wc.brakeTorque = 0f; wc.motorTorque = verticalInput * enginePower; }
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

    #endregion

    #region Network Sync

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (rb == null) return;

        if (stream.IsWriting)
        {
            stream.SendNext(rb.position);
            stream.SendNext(rb.rotation);
            stream.SendNext(currentTurnAngle);
            stream.SendNext(rb.linearVelocity);
        }
        else
        {
            targetPos = (Vector3)stream.ReceiveNext();
            targetRot = (Quaternion)stream.ReceiveNext();
            currentTurnAngle = (float)stream.ReceiveNext();
            targetVelocity = (Vector3)stream.ReceiveNext();
        }
    }

    #endregion
}
