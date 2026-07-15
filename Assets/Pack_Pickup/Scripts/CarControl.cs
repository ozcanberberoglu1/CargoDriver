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

    [Header("Cargo Tuning")]
    [SerializeField] private float cargoMass = 8f;
    [SerializeField] private float cargoStaticFriction = 0.6f;
    [SerializeField] private float cargoDynamicFriction = 0.45f;
    [SerializeField] private float cargoAngularDamping = 0.25f;
    [SerializeField] private float bedFloorThickness = 0.9f;

    private Rigidbody rb;
    private float currentTurnAngle;

    private Transform[] cargoBoxTransforms;

    private Vector3 targetPos;
    private Quaternion targetRot;
    private Vector3 carSmoothVel;
    private Vector3[] cargoTargetLocalPos;
    private Quaternion[] cargoTargetLocalRot;
    private float wheelSpin;

    private GameObject cargoBedProxy;
    private BoxCollider cargoBedCollider;
    private bool cargoInitialized;
    private PhysicsMaterial cargoPhysMat;

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

        PhotonNetwork.SendRate = 30;
        PhotonNetwork.SerializationRate = 30;

        bool isMaster = !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;
        if (rb != null && isMaster)
        {
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient)
        {
            rb.isKinematic = true;
            foreach (WheelCollider wc in GetComponentsInChildren<WheelCollider>())
                wc.enabled = false;
        }

        StartCoroutine(FindCargoBoxes());
    }

    #region Cargo Init

    private System.Collections.IEnumerator FindCargoBoxes()
    {
        if (cargoInitialized) yield break;

        var list = new List<Transform>();
        float timeout = 10f;
        while (timeout > 0f)
        {
            if (cargoInitialized) yield break;
            list.Clear();
            foreach (Transform child in transform)
                FindCargoRecursive(child, list);
            if (list.Count > 0) break;
            timeout -= 0.2f;
            yield return new WaitForSeconds(0.2f);
        }

        if (list.Count == 0) yield break;
        InitializeCargo(list);
    }

    public void InitializeCargoImmediately()
    {
        var list = new List<Transform>();
        foreach (Transform child in transform)
            FindCargoRecursive(child, list);
        if (list.Count > 0)
            InitializeCargo(list);
    }

    private void InitializeCargo(List<Transform> list)
    {
        cargoInitialized = true;

        var roots = new List<Transform>();
        foreach (Transform t in list)
        {
            if (t == null) continue;
            LegoSnap snap = t.GetComponent<LegoSnap>();
            if (snap != null && snap.HasParent) continue;
            roots.Add(t);
        }

        cargoBoxTransforms = roots.ToArray();
        int n = cargoBoxTransforms.Length;
        cargoTargetLocalPos = new Vector3[n];
        cargoTargetLocalRot = new Quaternion[n];

        for (int i = 0; i < n; i++)
        {
            if (cargoBoxTransforms[i] == null) continue;
            cargoTargetLocalPos[i] = transform.InverseTransformPoint(cargoBoxTransforms[i].position);
            cargoTargetLocalRot[i] = Quaternion.Inverse(transform.rotation) * cargoBoxTransforms[i].rotation;
        }

        CreateCargoBedProxy();

        bool isMaster = !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;
        foreach (var t in cargoBoxTransforms)
        {
            if (t == null) continue;
            SetupCargoBox(t, isMaster);
        }
    }

    private void CreateCargoBedProxy()
    {
        if (cargoBedProxy != null) return;

        cargoPhysMat = new PhysicsMaterial("CargoBed")
        {
            staticFriction = cargoStaticFriction,
            dynamicFriction = cargoDynamicFriction,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Average,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };

        cargoBedProxy = new GameObject("CargoBedProxy");
        cargoBedProxy.transform.SetPositionAndRotation(transform.position, transform.rotation);

        Rigidbody bedRb = cargoBedProxy.AddComponent<Rigidbody>();
        bedRb.isKinematic = true;
        bedRb.useGravity = false;
        bedRb.interpolation = RigidbodyInterpolation.Interpolate;
        bedRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        // Aracın kasa collider'larını (zemin, sol duvar, sağ duvar, arka duvar)
        // bağımsız kinematic proxy'ye kopyala ve orijinalleri kapat.
        // Kutular SADECE proxy collider'larla çarpışır → aracı sektirmez.
        var truckBoxCols = new List<BoxCollider>();
        foreach (BoxCollider bc in GetComponents<BoxCollider>())
        {
            if (bc.isTrigger || !bc.enabled) continue;
            truckBoxCols.Add(bc);
        }

        BoxCollider floorSource = null;
        var wallSources = new List<BoxCollider>();

        foreach (BoxCollider bc in truckBoxCols)
        {
            // Zemin: geniş, ince
            if (bc.size.y < 0.6f && bc.size.z > 4f)
                floorSource = bc;
            // Yan duvarlar: dar X, uzun Z
            else if (bc.size.x < 1f && bc.size.z > 3f)
                wallSources.Add(bc);
            // Arka kabin duvarı: geniş X, yüksek Y, kısa Z
            else if (bc.size.x > 3f && bc.size.z < 2.5f && bc.center.z > -1f)
                wallSources.Add(bc);
        }

        // Zemin proxy — kalınlaştırılmış
        BoxCollider bed = cargoBedProxy.AddComponent<BoxCollider>();
        cargoBedCollider = bed;
        bed.material = cargoPhysMat;
        if (floorSource != null)
        {
            float top = floorSource.center.y + floorSource.size.y * 0.5f;
            bed.size = new Vector3(floorSource.size.x, bedFloorThickness, floorSource.size.z);
            bed.center = new Vector3(floorSource.center.x, top - bedFloorThickness * 0.5f, floorSource.center.z);
            floorSource.enabled = false;
        }
        else
        {
            bed.center = new Vector3(0f, 1.5f - bedFloorThickness * 0.5f, -3.25f);
            bed.size = new Vector3(4.15f, bedFloorThickness, 4.85f);
        }

        // Duvar proxy'leri — aynı boyut/pozisyon
        foreach (BoxCollider wallSource in wallSources)
        {
            BoxCollider wallProxy = cargoBedProxy.AddComponent<BoxCollider>();
            wallProxy.center = wallSource.center;
            wallProxy.size = wallSource.size;
            wallProxy.material = cargoPhysMat;
            wallSource.enabled = false;
        }

        // Proxy'nin tüm collider'ları ile aracın kalan collider'ları arasında ignore
        foreach (Collider proxyCol in cargoBedProxy.GetComponents<Collider>())
        {
            foreach (Collider truckCol in GetComponentsInChildren<Collider>(true))
            {
                if (truckCol.transform.CompareTag("CargoBox")) continue;
                if (truckCol.GetComponentInParent<LegoSnap>() != null) continue;
                Physics.IgnoreCollision(proxyCol, truckCol, true);
            }
        }
    }

    private void SetupCargoBox(Transform box, bool simulate)
    {
        box.SetParent(null, true);

        PhotonTransformView ptv = box.GetComponent<PhotonTransformView>();
        if (ptv != null) ptv.enabled = false;
        CargoBoxSync cbs = box.GetComponent<CargoBoxSync>();
        if (cbs != null) cbs.enabled = false;

        HashSet<Collider> proxyColliders = new HashSet<Collider>();
        if (cargoBedProxy != null)
        {
            foreach (Collider pc in cargoBedProxy.GetComponents<Collider>())
                proxyColliders.Add(pc);
        }

        foreach (Collider boxCol in box.GetComponentsInChildren<Collider>(true))
        {
            if (boxCol.isTrigger) continue;
            boxCol.material = cargoPhysMat;

            foreach (Collider truckCol in GetComponentsInChildren<Collider>(true))
            {
                if (truckCol == boxCol) continue;
                if (truckCol.transform.CompareTag("CargoBox")) continue;
                if (truckCol.GetComponentInParent<LegoSnap>() != null) continue;
                Physics.IgnoreCollision(boxCol, truckCol, true);
            }
            // Proxy collider'lar araçtan ayrı — kutular bunlarla ÇARPIŞMALI
            // (yukarıdaki döngü bunları kapsamaz çünkü proxy child değil)
        }

        Rigidbody boxRb = box.GetComponent<Rigidbody>();
        if (boxRb == null) return;

        boxRb.mass = cargoMass;
        boxRb.interpolation = RigidbodyInterpolation.Interpolate;
        boxRb.linearDamping = 0.02f;
        boxRb.angularDamping = cargoAngularDamping;
        boxRb.solverIterations = 16;
        boxRb.solverVelocityIterations = 8;
        boxRb.maxDepenetrationVelocity = 2f;
        boxRb.sleepThreshold = 0.005f;

        if (simulate)
        {
            boxRb.isKinematic = false;
            boxRb.useGravity = true;
            boxRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            if (rb != null && !rb.isKinematic)
                boxRb.linearVelocity = rb.GetPointVelocity(boxRb.worldCenterOfMass);
        }
        else
        {
            boxRb.isKinematic = true;
            boxRb.useGravity = false;
        }
    }

    public void SnapCargoBedProxyToTruck()
    {
        CreateCargoBedProxy();
    }

    public void ReleaseCargoToBed(Transform box)
    {
        if (box == null) return;
        CreateCargoBedProxy();
        bool simulate = !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;
        SetupCargoBox(box, simulate);

        if (cargoBoxTransforms != null && cargoTargetLocalPos != null)
        {
            for (int i = 0; i < cargoBoxTransforms.Length; i++)
            {
                if (cargoBoxTransforms[i] == box)
                {
                    cargoTargetLocalPos[i] = transform.InverseTransformPoint(box.position);
                    cargoTargetLocalRot[i] = Quaternion.Inverse(transform.rotation) * box.rotation;
                    break;
                }
            }
        }
    }

    private void FindCargoRecursive(Transform t, List<Transform> list)
    {
        if (t.CompareTag("CargoBox"))
            list.Add(t);
        foreach (Transform child in t)
            FindCargoRecursive(child, list);
    }

    #endregion

    #region Master Switch

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
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
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
                SetupCargoBox(box, true);
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
                SetupCargoBox(box, false);
            }
        }
    }

    #endregion

    #region FixedUpdate / Update

    void FixedUpdate()
    {
        if (rb == null) return;

        SyncBedProxy();

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
        else
        {
            SyncRemoteCargo();
        }
    }

    private void SyncBedProxy()
    {
        if (cargoBedProxy == null) return;
        Rigidbody bedRb = cargoBedProxy.GetComponent<Rigidbody>();
        if (bedRb == null) return;
        bedRb.MovePosition(rb.position);
        bedRb.MoveRotation(rb.rotation);
    }

    private void SyncRemoteCargo()
    {
        if (cargoBoxTransforms == null || cargoTargetLocalPos == null) return;

        int count = Mathf.Min(cargoBoxTransforms.Length, cargoTargetLocalPos.Length);
        for (int i = 0; i < count; i++)
        {
            Transform box = cargoBoxTransforms[i];
            if (box == null) continue;
            if (IsInSetOrChildOf(box, CargoPickup.heldByPickup)) continue;

            Rigidbody boxRb = box.GetComponent<Rigidbody>();
            if (boxRb == null) continue;
            if (!boxRb.isKinematic)
            {
                boxRb.isKinematic = true;
                boxRb.interpolation = RigidbodyInterpolation.Interpolate;
            }

            Vector3 worldPos = transform.TransformPoint(cargoTargetLocalPos[i]);
            Quaternion worldRot = transform.rotation * cargoTargetLocalRot[i];
            boxRb.MovePosition(worldPos);
            boxRb.MoveRotation(worldRot);
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
            Quaternion steer = i < 2 ? Quaternion.Euler(0f, currentTurnAngle, 0f) : Quaternion.identity;
            Quaternion spin = Quaternion.Euler(wheelSpin, 0f, 0f);
            wheelMeshes[i].rotation = transform.rotation * steer * spin;
        }
    }

    #endregion

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

        PhotonNetwork.RaiseEvent(INPUT_EVENT,
            new float[] { v, h, brake ? 1f : 0f },
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
        remoteInputs[photonEvent.Sender] = (float[])photonEvent.CustomData;
    }

    private bool HasCtrl(Hashtable props, string key, int actor)
    {
        props.TryGetValue(key, out object val);
        return val != null && (int)val == actor;
    }

    #endregion

    #region Vehicle Physics

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

        for (int i = 0; i < wheels.Length && i < wheelMeshes.Length; i++)
        {
            WheelCollider wc = wheels[i].GetComponent<WheelCollider>();
            wc.GetWorldPose(out Vector3 pos, out Quaternion rot);
            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }

    public static bool IsInSetOrChildOf(Transform t, HashSet<Transform> set)
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

            int n = cargoBoxTransforms != null ? cargoBoxTransforms.Length : 0;
            stream.SendNext(n);
            for (int i = 0; i < n; i++)
            {
                if (cargoBoxTransforms[i] != null)
                {
                    stream.SendNext(transform.InverseTransformPoint(cargoBoxTransforms[i].position));
                    stream.SendNext(Quaternion.Inverse(transform.rotation) * cargoBoxTransforms[i].rotation);
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

            int n = (int)stream.ReceiveNext();
            if (cargoTargetLocalPos == null || cargoTargetLocalPos.Length != n)
            {
                cargoTargetLocalPos = new Vector3[n];
                cargoTargetLocalRot = new Quaternion[n];
            }
            for (int i = 0; i < n; i++)
            {
                cargoTargetLocalPos[i] = (Vector3)stream.ReceiveNext();
                cargoTargetLocalRot[i] = (Quaternion)stream.ReceiveNext();
            }
        }
    }

    #endregion

    private void OnDestroy()
    {
        if (cargoBedProxy != null)
            Destroy(cargoBedProxy);
    }
}
