using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Hashtable = ExitGames.Client.Photon.Hashtable;

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

    private Rigidbody rb;
    private float currentTurnAngle;

    private Transform[] cargoBoxTransforms;

    // Interpolation buffer
    private Vector3 posFrom, posTo;
    private Quaternion rotFrom, rotTo;
    private Vector3 velocity;
    private Vector3[][] cargoFromPos, cargoToPos;
    private Quaternion[][] cargoFromRot, cargoToRot;
    private float interpTime;
    private float interpDuration = 0.033f;
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
            posFrom = posTo = rb.position;
            rotFrom = rotTo = rb.rotation;
        }

        PhotonNetwork.SendRate = 60;
        PhotonNetwork.SerializationRate = 60;

        if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient)
        {
            rb.isKinematic = true;
            foreach (WheelCollider wc in GetComponentsInChildren<WheelCollider>())
                wc.enabled = false;
        }

        StartCoroutine(FindCargoBoxes());
    }

    private System.Collections.IEnumerator FindCargoBoxes()
    {
        yield return new WaitForSeconds(3f);

        var list = new List<Transform>();
        foreach (Transform child in transform)
            FindCargoRecursive(child, list);

        cargoBoxTransforms = list.ToArray();
        int n = cargoBoxTransforms.Length;

        cargoFromPos = new Vector3[][] { new Vector3[n], new Vector3[n] };
        cargoToPos = new Vector3[][] { new Vector3[n], new Vector3[n] };
        cargoFromRot = new Quaternion[][] { new Quaternion[n], new Quaternion[n] };
        cargoToRot = new Quaternion[][] { new Quaternion[n], new Quaternion[n] };

        for (int i = 0; i < n; i++)
        {
            if (cargoBoxTransforms[i] == null) continue;
            Vector3 p = cargoBoxTransforms[i].position;
            Quaternion r = cargoBoxTransforms[i].rotation;
            cargoFromPos[0][i] = cargoToPos[0][i] = p;
            cargoFromRot[0][i] = cargoToRot[0][i] = r;
        }

        if (!PhotonNetwork.IsMasterClient)
        {
            foreach (var t in cargoBoxTransforms)
            {
                if (t == null) continue;
                t.SetParent(null, true);
            }
        }
    }

    private void FindCargoRecursive(Transform t, List<Transform> list)
    {
        if (t.name.StartsWith("CargoBox"))
            list.Add(t);
        foreach (Transform child in t)
            FindCargoRecursive(child, list);
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
        }
    }

    void Update()
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) return;

        interpTime += Time.deltaTime;
        float t = interpDuration > 0f ? Mathf.Clamp01(interpTime / interpDuration) : 1f;

        transform.position = Vector3.Lerp(posFrom, posTo, t);
        transform.rotation = Quaternion.Slerp(rotFrom, rotTo, t);

        if (steeringWheel != null)
            steeringWheel.transform.localEulerAngles = new Vector3(-64, 0, currentTurnAngle * 3);

        float speed = (posTo - posFrom).magnitude / Mathf.Max(interpDuration, 0.01f);
        wheelSpin += speed * Time.deltaTime * 200f;

        for (int i = 0; i < wheelMeshes.Length && i < wheels.Length; i++)
        {
            wheelMeshes[i].position = wheels[i].position;

            Quaternion steer = i < 2
                ? Quaternion.Euler(0f, currentTurnAngle, 0f)
                : Quaternion.identity;
            Quaternion spin = Quaternion.Euler(wheelSpin, 0f, 0f);

            wheelMeshes[i].rotation = transform.rotation * steer * spin;
        }

        if (cargoBoxTransforms != null && cargoFromPos != null)
        {
            int count = Mathf.Min(cargoBoxTransforms.Length,
                cargoFromPos[0] != null ? cargoFromPos[0].Length : 0);
            for (int i = 0; i < count; i++)
            {
                if (cargoBoxTransforms[i] == null) continue;
                if (CargoPickup.heldByPickup.Contains(cargoBoxTransforms[i])) continue;
                cargoBoxTransforms[i].position = Vector3.Lerp(cargoFromPos[0][i], cargoToPos[0][i], t);
                cargoBoxTransforms[i].rotation = Quaternion.Slerp(cargoFromRot[0][i], cargoToRot[0][i], t);
            }
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

            int boxCount = cargoBoxTransforms != null ? cargoBoxTransforms.Length : 0;
            stream.SendNext(boxCount);
            for (int i = 0; i < boxCount; i++)
            {
                if (cargoBoxTransforms[i] != null)
                {
                    stream.SendNext(cargoBoxTransforms[i].position);
                    stream.SendNext(cargoBoxTransforms[i].rotation);
                }
                else
                {
                    stream.SendNext(Vector3.zero);
                    stream.SendNext(Quaternion.identity);
                }
            }
        }
        else
        {
            Vector3 prevTo = posTo;
            posFrom = transform.position;
            rotFrom = transform.rotation;
            posTo = (Vector3)stream.ReceiveNext();
            rotTo = (Quaternion)stream.ReceiveNext();
            currentTurnAngle = (float)stream.ReceiveNext();

            velocity = (posTo - prevTo) / interpDuration;
            interpDuration = 1f / PhotonNetwork.SerializationRate;
            interpTime = 0f;

            int boxCount = (int)stream.ReceiveNext();
            if (cargoFromPos == null || cargoFromPos[0].Length != boxCount)
            {
                cargoFromPos = new Vector3[][] { new Vector3[boxCount] };
                cargoToPos = new Vector3[][] { new Vector3[boxCount] };
                cargoFromRot = new Quaternion[][] { new Quaternion[boxCount] };
                cargoToRot = new Quaternion[][] { new Quaternion[boxCount] };
            }

            for (int i = 0; i < boxCount; i++)
            {
                if (cargoBoxTransforms != null && i < cargoBoxTransforms.Length && cargoBoxTransforms[i] != null)
                {
                    cargoFromPos[0][i] = cargoBoxTransforms[i].position;
                    cargoFromRot[0][i] = cargoBoxTransforms[i].rotation;
                }

                cargoToPos[0][i] = (Vector3)stream.ReceiveNext();
                cargoToRot[0][i] = (Quaternion)stream.ReceiveNext();
            }
        }
    }

    #endregion
}
