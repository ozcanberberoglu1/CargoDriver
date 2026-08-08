using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

public enum CargoAuthorityPolicy
{
    /// <summary>Master simulates every body. Grabbing is a request; the holder predicts locally.</summary>
    HostAuthority,

    /// <summary>The grabber takes PhotonView ownership and simulates the body it carries.</summary>
    DistributedOwnership
}

public enum CargoState
{
    /// <summary>Dynamic rigidbody driven by the writer, kinematic puppet on everyone else.</summary>
    Free = 0,

    /// <summary>Carried by a player. Writer depends on the active authority policy.</summary>
    Held = 1,

    /// <summary>Kinematic and parented to a carrier. No independent simulation.</summary>
    Stowed = 2
}

/// <summary>
/// Single source of truth for a cargo box's networked physics state.
///
/// The whole cargo system rests on one invariant: every body has exactly one writer
/// at any moment. The writer runs real PhysX; everybody else is a kinematic puppet fed
/// by PhotonTransformView. Nothing outside this component may touch isKinematic,
/// useGravity or the transform parent of a cargo box.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(PhotonView))]
public class NetworkedCargoBody : MonoBehaviourPunCallbacks, IPunInstantiateMagicCallback
{
    public static CargoAuthorityPolicy Policy = CargoAuthorityPolicy.HostAuthority;

    [Header("Carry Servo")]
    [SerializeField] private float carryStiffness = 12f;
    [SerializeField] private float carryRotStiffness = 12f;

    [Header("Client Prediction")]
    [SerializeField] private float predictionSmooth = 0.05f;
    [SerializeField] private float maxPredictionError = 1.5f;

    private static readonly List<NetworkedCargoBody> all = new();
    private static readonly Dictionary<int, NetworkedCargoBody> heldByActor = new();

    public static IReadOnlyList<NetworkedCargoBody> All => all;

    public static NetworkedCargoBody HeldBy(int actorNumber)
        => heldByActor.TryGetValue(actorNumber, out NetworkedCargoBody body) ? body : null;

    private Rigidbody rb;
    private PhotonTransformView transformView;
    private LegoSnap legoSnap;

    private CargoState state = CargoState.Free;
    private int holderActor = -1;
    private Transform carrier;

    private Vector3 holdTargetPos;
    private Quaternion holdTargetRot = Quaternion.identity;
    private bool hasHoldTarget;

    private Vector3 predictionVel;
    private bool bodyModeIsWriter;
    private bool bodyModeInitialized;

    private int pendingCarrierViewId = -1;
    private float pendingCarrierTimeout;

    public CargoState State => state;
    public int HolderActor => holderActor;
    public bool IsHeld => state == CargoState.Held;

    /// <summary>True on the single client responsible for simulating this body.</summary>
    public bool IsWriter => photonView.IsMine;

    /// <summary>True when the local player is the one carrying this box.</summary>
    public bool IsHeldByLocalPlayer
        => state == CargoState.Held && holderActor == PhotonNetwork.LocalPlayer.ActorNumber;

    #region Lifecycle

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        transformView = GetComponent<PhotonTransformView>();
        legoSnap = GetComponent<LegoSnap>();

        rb.interpolation = RigidbodyInterpolation.Interpolate;
    }

    public override void OnEnable()
    {
        base.OnEnable();
        if (!all.Contains(this))
            all.Add(this);
    }

    public override void OnDisable()
    {
        base.OnDisable();
        all.Remove(this);
        if (holderActor >= 0 && heldByActor.TryGetValue(holderActor, out NetworkedCargoBody held) && held == this)
            heldByActor.Remove(holderActor);
    }

    private void Start()
    {
        // Scene-placed boxes (lobby) never receive instantiation data; Free is the correct default.
        ApplyBodyMode(force: true);
    }

    public void OnPhotonInstantiate(PhotonMessageInfo info)
    {
        object[] data = info.photonView.InstantiationData;
        if (data == null || data.Length < 4) return;

        transform.localScale = new Vector3((float)data[0], (float)data[1], (float)data[2]);

        int legoParentViewId = (int)data[3];
        if (legoParentViewId > 0)
        {
            pendingCarrierViewId = legoParentViewId;
            pendingCarrierTimeout = 10f;
        }
    }

    #endregion

    #region State machine

    private void ApplyState(CargoState newState, int newHolderActor, int carrierViewId,
        Vector3 localPos, Quaternion localRot, bool hasPose)
    {
        carrier = ResolveView(carrierViewId);

        // A newcomer can be told about a stow before the carrier's view is registered
        // locally. Defer rather than silently dropping the box out of the structure.
        if (newState == CargoState.Stowed && carrier == null && carrierViewId > 0)
        {
            pendingCarrierViewId = carrierViewId;
            pendingCarrierTimeout = 10f;
            return;
        }

        if (holderActor >= 0 && heldByActor.TryGetValue(holderActor, out NetworkedCargoBody prev) && prev == this)
            heldByActor.Remove(holderActor);

        pendingCarrierViewId = -1;
        state = newState;
        holderActor = newHolderActor;
        hasHoldTarget = false;
        predictionVel = Vector3.zero;

        switch (state)
        {
            case CargoState.Stowed:
                ZeroVelocities();
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.isKinematic = true;
                rb.useGravity = false;
                transform.SetParent(carrier, !hasPose);
                if (hasPose)
                {
                    transform.localPosition = localPos;
                    transform.localRotation = localRot;
                }
                LinkLegoParent(true);
                break;

            case CargoState.Held:
                transform.SetParent(null, true);
                LinkLegoParent(false);
                if (holderActor >= 0)
                    heldByActor[holderActor] = this;
                break;

            case CargoState.Free:
                transform.SetParent(null, true);
                LinkLegoParent(false);
                break;
        }

        ApplyBodyMode(force: true);
        ResetTransformView();
    }

    /// <summary>
    /// Aligns the rigidbody with our role for the current state. The writer runs real
    /// physics; everyone else is a puppet so the two never fight over the same body.
    /// </summary>
    private void ApplyBodyMode(bool force = false)
    {
        if (state == CargoState.Stowed)
        {
            bodyModeIsWriter = false;
            bodyModeInitialized = true;
            return;
        }

        bool writer = IsWriter;
        if (!force && bodyModeInitialized && writer == bodyModeIsWriter) return;

        bodyModeIsWriter = writer;
        bodyModeInitialized = true;

        if (writer)
        {
            rb.isKinematic = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.useGravity = true;
            rb.linearDamping = state == CargoState.Held ? 12f : 0f;
            rb.angularDamping = state == CargoState.Held ? 8f : 0.05f;
        }
        else
        {
            ZeroVelocities();
            // ContinuousDynamic is rejected on kinematic bodies; speculative works for both.
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.isKinematic = true;
            rb.useGravity = false;
        }
    }

    private void LinkLegoParent(bool link)
    {
        if (legoSnap == null) return;

        if (link && carrier != null)
        {
            LegoSnap parentSnap = carrier.GetComponent<LegoSnap>();
            if (parentSnap != null)
                legoSnap.AttachToParent(parentSnap);
        }
        else
        {
            legoSnap.ClearParentLink();
        }
    }

    /// <summary>
    /// Forces PhotonTransformView to re-seed from the next packet. Its cached network
    /// position is expressed in the old parent space and would otherwise drag the box
    /// across the level after a re-parent.
    /// </summary>
    private void ResetTransformView()
    {
        if (transformView == null || !transformView.enabled) return;
        transformView.enabled = false;
        transformView.enabled = true;
    }

    private void ZeroVelocities()
    {
        if (rb.isKinematic) return;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    private static Transform ResolveView(int viewId)
    {
        if (viewId <= 0) return null;
        PhotonView view = PhotonView.Find(viewId);
        return view != null ? view.transform : null;
    }

    private int ViewIdOf(Transform t)
    {
        if (t == null) return -1;
        PhotonView view = t.GetComponent<PhotonView>();
        return view != null ? view.ViewID : -1;
    }

    #endregion

    #region Authority commands

    /// <summary>Master/owner puts this box into the carrier's local frame as a kinematic child.</summary>
    public void AuthorityStow(Transform newCarrier, Vector3 localPos, Quaternion localRot)
    {
        int carrierViewId = ViewIdOf(newCarrier);
        if (carrierViewId <= 0) return;

        photonView.RPC(nameof(RpcApplyState), RpcTarget.All,
            (int)CargoState.Stowed, -1, carrierViewId, localPos, localRot, true);
    }

    /// <summary>Master/owner releases this box back into free physics simulation.</summary>
    public void AuthorityFree()
    {
        photonView.RPC(nameof(RpcApplyState), RpcTarget.All,
            (int)CargoState.Free, -1, -1, Vector3.zero, Quaternion.identity, false);
    }

    /// <summary>Master snaps the box to a world pose (checkpoint respawn).</summary>
    public void AuthorityTeleport(Vector3 worldPos, Quaternion worldRot)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        photonView.RPC(nameof(RpcTeleport), RpcTarget.All, worldPos, worldRot);
    }

    #endregion

    #region Grab requests

    /// <summary>Called by the local player's CargoPickup when it wants to carry this box.</summary>
    public bool RequestGrab()
    {
        if (state == CargoState.Held) return false;

        int me = PhotonNetwork.LocalPlayer.ActorNumber;

        if (Policy == CargoAuthorityPolicy.DistributedOwnership)
        {
            // The grabber becomes the writer, so it can announce the transition itself.
            if (!photonView.IsMine)
                photonView.TransferOwnership(PhotonNetwork.LocalPlayer);

            photonView.RPC(nameof(RpcApplyState), RpcTarget.All,
                (int)CargoState.Held, me, -1, Vector3.zero, Quaternion.identity, false);
            return true;
        }

        // Host authority: the master owns the simulation, so a grab is just an input.
        if (PhotonNetwork.IsMasterClient)
            MasterGrant(me);
        else
            photonView.RPC(nameof(RpcRequestGrab), RpcTarget.MasterClient, me);

        return true;
    }

    public void RequestRelease()
    {
        if (state != CargoState.Held) return;
        if (holderActor != PhotonNetwork.LocalPlayer.ActorNumber) return;

        if (Policy == CargoAuthorityPolicy.DistributedOwnership)
        {
            photonView.RPC(nameof(RpcApplyState), RpcTarget.All,
                (int)CargoState.Free, -1, -1, Vector3.zero, Quaternion.identity, false);

            // Hand the body back so the master owns settled cargo again.
            if (photonView.IsMine && PhotonNetwork.MasterClient != null)
                photonView.TransferOwnership(PhotonNetwork.MasterClient);
            return;
        }

        if (PhotonNetwork.IsMasterClient)
            AuthorityFree();
        else
            photonView.RPC(nameof(RpcRequestRelease), RpcTarget.MasterClient);
    }

    private void MasterGrant(int actorNumber)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (state == CargoState.Held) return;

        photonView.RPC(nameof(RpcApplyState), RpcTarget.All,
            (int)CargoState.Held, actorNumber, -1, Vector3.zero, Quaternion.identity, false);
    }

    /// <summary>Holder feeds the desired hand pose; the writer servos the body toward it.</summary>
    public void SetHoldTarget(Vector3 worldPos, Quaternion worldRot)
    {
        holdTargetPos = worldPos;
        holdTargetRot = worldRot;
        hasHoldTarget = true;
    }

    #endregion

    #region Simulation

    private void FixedUpdate()
    {
        if (pendingCarrierViewId > 0)
            ResolvePendingCarrier();

        ApplyBodyMode();

        if (state == CargoState.Held && IsWriter && hasHoldTarget)
            DriveHeldBody();
    }

    /// <summary>
    /// The carried box stays a real rigidbody driven by a velocity servo, so it still
    /// collides with the world instead of tunnelling through it.
    /// </summary>
    private void DriveHeldBody()
    {
        Vector3 diff = holdTargetPos - rb.position;
        rb.linearVelocity = diff * carryStiffness;

        Quaternion delta = holdTargetRot * Quaternion.Inverse(rb.rotation);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;

        if (Mathf.Abs(angle) > 0.05f && !float.IsInfinity(axis.x) && axis.sqrMagnitude > 0.0001f)
            rb.angularVelocity = axis.normalized * (angle * Mathf.Deg2Rad * carryRotStiffness);
        else
            rb.angularVelocity = Vector3.zero;
    }

    /// <summary>
    /// Host-authority grabs cost a round trip. The holder renders the box at its own hand
    /// and blends toward the authoritative pose, clamped so it can never drift far enough
    /// to disagree with what everyone else sees.
    /// </summary>
    private void LateUpdate()
    {
        if (state != CargoState.Held) return;
        if (IsWriter || !IsHeldByLocalPlayer || !hasHoldTarget) return;

        Vector3 authoritative = transform.position;
        Vector3 predicted = holdTargetPos;

        if (Vector3.Distance(predicted, authoritative) > maxPredictionError)
            predicted = authoritative + Vector3.ClampMagnitude(predicted - authoritative, maxPredictionError);

        transform.position = Vector3.SmoothDamp(authoritative, predicted, ref predictionVel, predictionSmooth);
    }

    private void ResolvePendingCarrier()
    {
        pendingCarrierTimeout -= Time.fixedDeltaTime;

        Transform resolved = ResolveView(pendingCarrierViewId);
        if (resolved != null)
        {
            int carrierViewId = pendingCarrierViewId;
            pendingCarrierViewId = -1;
            ApplyState(CargoState.Stowed, -1, carrierViewId, transform.localPosition, transform.localRotation, false);
            return;
        }

        if (pendingCarrierTimeout <= 0f)
            pendingCarrierViewId = -1;
    }

    #endregion

    #region RPCs

    [PunRPC]
    private void RpcRequestGrab(int actorNumber)
    {
        MasterGrant(actorNumber);
    }

    [PunRPC]
    private void RpcRequestRelease(PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (state != CargoState.Held) return;
        if (info.Sender == null || info.Sender.ActorNumber != holderActor) return;
        AuthorityFree();
    }

    [PunRPC]
    private void RpcApplyState(int newState, int newHolderActor, int carrierViewId,
        Vector3 localPos, Quaternion localRot, bool hasPose)
    {
        ApplyState((CargoState)newState, newHolderActor, carrierViewId, localPos, localRot, hasPose);
    }

    [PunRPC]
    private void RpcTeleport(Vector3 worldPos, Quaternion worldRot)
    {
        ZeroVelocities();
        transform.position = worldPos;
        transform.rotation = worldRot;
        ResetTransformView();
    }

    #endregion

    #region Photon callbacks

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        // Cargo state lives in unbuffered RPCs, so the writer re-asserts it for newcomers
        // instead of letting the server accumulate a buffer that grows all match long.
        if (!IsWriter) return;
        StartCoroutine(ReassertStateFor(newPlayer));
    }

    private IEnumerator ReassertStateFor(Player target)
    {
        // An RPC addressed to a view the newcomer has not instantiated yet is dropped, so
        // wait for the room's cached instantiation events to land first.
        yield return new WaitForSeconds(1f);

        if (!IsWriter || target == null) yield break;
        if (PhotonNetwork.CurrentRoom == null) yield break;
        if (!PhotonNetwork.CurrentRoom.Players.ContainsKey(target.ActorNumber)) yield break;

        bool stowed = state == CargoState.Stowed;
        Vector3 localPos = stowed ? transform.localPosition : Vector3.zero;
        Quaternion localRot = stowed ? transform.localRotation : Quaternion.identity;

        photonView.RPC(nameof(RpcApplyState), target,
            (int)state, holderActor, ViewIdOf(carrier), localPos, localRot, stowed);
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (state != CargoState.Held || holderActor != otherPlayer.ActorNumber) return;
        AuthorityFree();
    }

    public override void OnMasterClientSwitched(Player newMasterClient)
    {
        ApplyBodyMode(force: true);
    }

    #endregion
}
