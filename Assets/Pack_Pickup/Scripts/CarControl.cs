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

    private readonly List<Transform> cargoBoxTransforms = new();
    private readonly Dictionary<string, Vector3> cargoTargetPos = new();
    private readonly Dictionary<string, Quaternion> cargoTargetRot = new();
    private readonly Dictionary<string, Vector3> cargoSmoothVel = new();
    private bool cargoReady;

    private Vector3 targetPos;
    private Quaternion targetRot;
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
            targetPos = rb.position;
            targetRot = rb.rotation;
        }

        PhotonNetwork.SendRate = 60;
        PhotonNetwork.SerializationRate = 60;

        if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient)
        {
            rb.isKinematic = true;
            foreach (WheelCollider wc in GetComponentsInChildren<WheelCollider>())
                wc.enabled = false;
        }

        // Cargo bulk-sync via CarControl is only used in GameScene. In LobbyScene
        // each box syncs itself through its own PhotonTransformView.
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "GameScene")
            StartCoroutine(FindCargoBoxes());
    }

    private System.Collections.IEnumerator FindCargoBoxes()
    {
        // Wait until cargo boxes are spawned by GameSceneController.
        float waited = 0f;
        while (waited < 8f)
        {
            RefreshCargoList();
            if (cargoBoxTransforms.Count > 0)
                break;
            waited += 0.25f;
            yield return new WaitForSeconds(0.25f);
        }

        RefreshCargoList();

        foreach (var t in cargoBoxTransforms)
        {
            cargoTargetPos[t.name] = t.position;
            cargoTargetRot[t.name] = t.rotation;
            cargoSmoothVel[t.name] = Vector3.zero;
        }

        cargoReady = true;
    }

    private void RefreshCargoList()
    {
        cargoBoxTransforms.Clear();

        var all = GameObject.FindGameObjectsWithTag("CargoBox");
        foreach (var go in all)
        {
            Transform t = go.transform;
            // Only sync root boxes. Snapped children follow their parent lego.
            LegoSnap snap = t.GetComponent<LegoSnap>();
            if (snap != null && snap.HasParent) continue;

            cargoBoxTransforms.Add(t);
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
        }
    }

    void Update()
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) return;

        float smooth = 0.04f;
        transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref carSmoothVel, smooth);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * 25f);

        if (steeringWheel != null)
            steeringWheel.transform.localEulerAngles = new Vector3(-64, 0, currentTurnAngle * 3);

        float speed = carSmoothVel.magnitude;
        wheelSpin += speed * Time.deltaTime * 60f;

        for (int i = 0; i < wheelMeshes.Length && i < wheels.Length; i++)
        {
            wheelMeshes[i].position = wheels[i].position;

            Quaternion steer = i < 2
                ? Quaternion.Euler(0f, currentTurnAngle, 0f)
                : Quaternion.identity;
            Quaternion spin = Quaternion.Euler(wheelSpin, 0f, 0f);

            wheelMeshes[i].rotation = transform.rotation * steer * spin;
        }

        if (!PhotonNetwork.IsMasterClient && cargoReady)
        {
            foreach (var t in cargoBoxTransforms)
            {
                if (t == null) continue;
                // Held boxes are driven by the grabber's CargoPickup stream.
                if (CargoPickup.heldByPickup.Contains(t)) continue;
                if (!cargoTargetPos.ContainsKey(t.name)) continue;

                Vector3 vel = cargoSmoothVel.TryGetValue(t.name, out var v) ? v : Vector3.zero;
                Vector3 targetP = cargoTargetPos[t.name];

                // Snap instantly on large deltas (respawn/teleport), else smooth.
                if (Vector3.Distance(t.position, targetP) > 5f)
                {
                    t.position = targetP;
                    vel = Vector3.zero;
                }
                else
                {
                    t.position = Vector3.SmoothDamp(t.position, targetP, ref vel, 0.05f);
                }
                cargoSmoothVel[t.name] = vel;

                t.rotation = Quaternion.Slerp(t.rotation, cargoTargetRot[t.name], Time.deltaTime * 20f);
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

            // Master writes only FREE (not held) root boxes, keyed by name.
            var toSend = new List<Transform>();
            foreach (var t in cargoBoxTransforms)
            {
                if (t == null) continue;
                if (CargoPickup.heldByPickup.Contains(t)) continue;
                toSend.Add(t);
            }

            stream.SendNext(toSend.Count);
            foreach (var t in toSend)
            {
                stream.SendNext(t.name);
                stream.SendNext(t.position);
                stream.SendNext(t.rotation);
            }
        }
        else
        {
            Vector3 newPos = (Vector3)stream.ReceiveNext();

            if (Vector3.Distance(targetPos, newPos) > 10f)
            {
                transform.position = newPos;
                carSmoothVel = Vector3.zero;
            }

            targetPos = newPos;
            targetRot = (Quaternion)stream.ReceiveNext();
            currentTurnAngle = (float)stream.ReceiveNext();

            int boxCount = (int)stream.ReceiveNext();
            for (int i = 0; i < boxCount; i++)
            {
                string name = (string)stream.ReceiveNext();
                Vector3 pos = (Vector3)stream.ReceiveNext();
                Quaternion rot = (Quaternion)stream.ReceiveNext();

                cargoTargetPos[name] = pos;
                cargoTargetRot[name] = rot;
            }
        }
    }

    #endregion
}
