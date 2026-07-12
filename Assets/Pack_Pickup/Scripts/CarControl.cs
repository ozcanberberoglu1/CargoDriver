using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Hashtable = ExitGames.Client.Photon.Hashtable;

public class CarControl : MonoBehaviourPunCallbacks, IPunObservable, IOnEventCallback
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

    private Vector3 targetPos;
    private Quaternion targetRot;
    private Vector3 carSmoothVel;
    private Vector3[] cargoTargetPos;
    private Quaternion[] cargoTargetRot;
    private Vector3[] cargoSmoothVel;
    private float wheelSpin;

    private GameObject cargoBedProxy;
    private Rigidbody cargoBedProxyRb;
    private Collider[] cargoBedColliders;

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

        StartCoroutine(FindCargoBoxes());
    }

    private System.Collections.IEnumerator FindCargoBoxes()
    {
        var list = new List<Transform>();
        int expectedCount = 1;
        if (PhotonNetwork.InRoom &&
            PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue("cargoData", out object cargoData))
        {
            expectedCount = Mathf.Max(1, cargoData.ToString().Split(';').Length - 1);
        }

        float timeout = 10f;
        while (timeout > 0f)
        {
            list.Clear();
            foreach (Transform child in transform)
                FindCargoRecursive(child, list);

            if (list.Count >= expectedCount)
                break;

            timeout -= 0.1f;
            yield return new WaitForSeconds(0.1f);
        }

        if (list.Count == 0)
        {
            Debug.LogError("[CarControl] Cargo boxes could not be initialized.");
            yield break;
        }

        cargoBoxTransforms = list.ToArray();
        int n = cargoBoxTransforms.Length;
        cargoTargetPos = new Vector3[n];
        cargoTargetRot = new Quaternion[n];
        cargoSmoothVel = new Vector3[n];

        for (int i = 0; i < n; i++)
        {
            if (cargoBoxTransforms[i] == null) continue;
            cargoTargetPos[i] = cargoBoxTransforms[i].position;
            cargoTargetRot[i] = cargoBoxTransforms[i].rotation;
        }

        CreateCargoBedProxy();

        foreach (var t in cargoBoxTransforms)
        {
            if (t == null) continue;
            LegoSnap snap = t.GetComponent<LegoSnap>();
            if (snap != null && snap.HasParent) continue;
            ConfigureCargoAuthority(t);
        }
    }

    private void CreateCargoBedProxy()
    {
        if (cargoBedProxy != null) return;

        cargoBedProxy = new GameObject("CargoBedPhysicsProxy");
        cargoBedProxy.transform.SetPositionAndRotation(transform.position, transform.rotation);

        cargoBedProxyRb = cargoBedProxy.AddComponent<Rigidbody>();
        cargoBedProxyRb.isKinematic = true;
        cargoBedProxyRb.useGravity = false;
        cargoBedProxyRb.interpolation = RigidbodyInterpolation.Interpolate;
        cargoBedProxyRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        PhysicsMaterial bedMaterial = new PhysicsMaterial("CargoBedFriction")
        {
            staticFriction = 0.8f,
            dynamicFriction = 0.55f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Maximum,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };

        var colliders = new List<Collider>
        {
            AddProxyCollider(new Vector3(0f, 1.55f, -3.25f), new Vector3(3.8f, 0.5f, 4.6f), bedMaterial),
            AddProxyCollider(new Vector3(-1.95f, 2.35f, -3.25f), new Vector3(0.25f, 1.8f, 4.6f), bedMaterial),
            AddProxyCollider(new Vector3(1.95f, 2.35f, -3.25f), new Vector3(0.25f, 1.8f, 4.6f), bedMaterial),
            AddProxyCollider(new Vector3(0f, 2.35f, -0.95f), new Vector3(3.8f, 1.8f, 0.25f), bedMaterial),
            AddProxyCollider(new Vector3(0f, 2.35f, -5.55f), new Vector3(3.8f, 1.8f, 0.25f), bedMaterial)
        };
        cargoBedColliders = colliders.ToArray();

        Collider[] truckColliders = GetComponentsInChildren<Collider>(true);
        foreach (Collider bedCollider in cargoBedColliders)
        {
            foreach (Collider truckCollider in truckColliders)
            {
                if (truckCollider != null && truckCollider != bedCollider)
                    Physics.IgnoreCollision(bedCollider, truckCollider, true);
            }
        }
    }

    private BoxCollider AddProxyCollider(
        Vector3 center, Vector3 size, PhysicsMaterial material)
    {
        BoxCollider collider = cargoBedProxy.AddComponent<BoxCollider>();
        collider.center = center;
        collider.size = size;
        collider.material = material;
        return collider;
    }

    private void ConfigureCargoAuthority(Transform box)
    {
        if (box == null) return;
        box.SetParent(null, true);
        IgnorePhysicalTruckCollisions(box);

        Rigidbody boxRb = box.GetComponent<Rigidbody>();
        if (boxRb != null)
        {
            bool simulate = !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;
            boxRb.isKinematic = !simulate;
            boxRb.useGravity = simulate;
            boxRb.collisionDetectionMode = simulate
                ? CollisionDetectionMode.ContinuousDynamic
                : CollisionDetectionMode.Discrete;
            boxRb.interpolation = RigidbodyInterpolation.Interpolate;

            foreach (Collider collider in box.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.isTrigger)
                    collider.material = cargoBedColliders?[0].material;
            }
        }
    }

    private void IgnorePhysicalTruckCollisions(Transform box)
    {
        Collider[] truckColliders = GetComponentsInChildren<Collider>(true);
        foreach (Collider boxCollider in box.GetComponentsInChildren<Collider>(true))
        {
            foreach (Collider truckCollider in truckColliders)
            {
                if (truckCollider == null) continue;
                if (truckCollider.transform.CompareTag("CargoBox")) continue;
                if (truckCollider.GetComponentInParent<LegoSnap>() != null) continue;
                Physics.IgnoreCollision(boxCollider, truckCollider, true);
            }
        }
    }

    public void ReleaseCargoToBed(Transform box)
    {
        if (box == null) return;
        box.SetParent(null, true);
        IgnorePhysicalTruckCollisions(box);

        Rigidbody boxRb = box.GetComponent<Rigidbody>();
        if (boxRb == null) return;

        bool simulate = !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;
        boxRb.isKinematic = !simulate;
        boxRb.useGravity = simulate;
        boxRb.collisionDetectionMode = simulate
            ? CollisionDetectionMode.ContinuousDynamic
            : CollisionDetectionMode.Discrete;
    }

    private void FindCargoRecursive(Transform t, List<Transform> list)
    {
        if (t.CompareTag("CargoBox"))
            list.Add(t);
        foreach (Transform child in t)
            FindCargoRecursive(child, list);
    }

    public override void OnMasterClientSwitched(Player newMasterClient)
    {
        if (PhotonNetwork.IsMasterClient)
            PromoteToMaster();
        else
            DemoteFromMaster();
    }

    private void PromoteToMaster()
    {
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.linearVelocity = carSmoothVel;
        }

        foreach (WheelCollider wc in GetComponentsInChildren<WheelCollider>(true))
            wc.enabled = true;

        if (cargoBoxTransforms != null)
        {
            foreach (Transform box in cargoBoxTransforms)
            {
                if (box == null) continue;
                if (CargoPickup.heldByPickup.Contains(box)) continue;

                LegoSnap snap = box.GetComponent<LegoSnap>();
                if (snap != null && snap.HasParent) continue;

                ConfigureCargoAuthority(box);
            }
        }

        remoteInputs.Clear();
    }

    private void DemoteFromMaster()
    {
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }

        foreach (WheelCollider wc in GetComponentsInChildren<WheelCollider>())
            wc.enabled = false;

        if (cargoBoxTransforms != null)
        {
            foreach (Transform box in cargoBoxTransforms)
            {
                if (box == null) continue;
                if (CargoPickup.heldByPickup.Contains(box)) continue;

                LegoSnap snap = box.GetComponent<LegoSnap>();
                if (snap != null && snap.HasParent) continue;

                ConfigureCargoAuthority(box);
            }
        }
    }

    void FixedUpdate()
    {
        if (rb == null) return;

        if (!PhotonNetwork.InRoom)
        {
            RunPhysics(GetLocalVertical(), GetLocalHorizontal(), GetLocalBrake());
            UpdateCargoBedProxy();
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
        UpdateCargoBedProxy();
    }

    private void UpdateCargoBedProxy()
    {
        if (cargoBedProxyRb == null || rb == null) return;
        cargoBedProxyRb.MovePosition(rb.position);
        cargoBedProxyRb.MoveRotation(rb.rotation);
    }

    private void OnDestroy()
    {
        if (cargoBedProxy != null)
            Destroy(cargoBedProxy);
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

        if (!PhotonNetwork.IsMasterClient && cargoBoxTransforms != null && cargoTargetPos != null)
        {
            int count = Mathf.Min(cargoBoxTransforms.Length, cargoTargetPos.Length);
            for (int i = 0; i < count; i++)
            {
                if (cargoBoxTransforms[i] == null) continue;
                if (IsInSetOrChildOf(cargoBoxTransforms[i], CargoPickup.heldByPickup)) continue;
                if (IsInSetOrChildOf(cargoBoxTransforms[i], CargoPickup.recentlyDroppedSet)) continue;
                cargoBoxTransforms[i].position = cargoTargetPos[i];
                cargoBoxTransforms[i].rotation = cargoTargetRot[i];
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

    private static bool IsInSetOrChildOf(Transform t, System.Collections.Generic.HashSet<Transform> set)
    {
        Transform current = t;
        while (current != null)
        {
            if (set.Contains(current)) return true;
            current = current.parent;
        }
        return false;
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
            if (cargoTargetPos == null || cargoTargetPos.Length != boxCount)
            {
                cargoTargetPos = new Vector3[boxCount];
                cargoTargetRot = new Quaternion[boxCount];
                cargoSmoothVel = new Vector3[boxCount];
            }

            for (int i = 0; i < boxCount; i++)
            {
                cargoTargetPos[i] = (Vector3)stream.ReceiveNext();
                cargoTargetRot[i] = (Quaternion)stream.ReceiveNext();
            }
        }
    }

    #endregion
}
