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

    /// <summary>Welded into a carrier's compound collider. No rigidbody of its own.</summary>
    Stowed = 2,

    /// <summary>Pinned in the world by a player. Kinematic and solid on every client, no
    /// writer simulates it — a static obstacle that never moves and never follows the truck.</summary>
    Frozen = 3
}

/// <summary>
/// Single source of truth for a cargo box's networked physics state.
///
/// The whole cargo system rests on one invariant: every body has exactly one writer at
/// any moment. The writer runs real PhysX; everybody else is a puppet driven by this
/// component's own transform stream. Nothing outside this component may touch the
/// rigidbody, isKinematic, useGravity or the transform parent of a cargo box.
/// </summary>
[RequireComponent(typeof(PhotonView))]
public class NetworkedCargoBody : MonoBehaviourPunCallbacks, IPunInstantiateMagicCallback, IPunObservable
{
    public static CargoAuthorityPolicy Policy = CargoAuthorityPolicy.HostAuthority;

    [Header("Melee (swing a held lego into a character → ragdoll)")]
    [Tooltip("Minimum swing speed (m/s) to knock a character down. Raise it to need a harder swing.")]
    [SerializeField] private float meleeMinSpeed = 6f;
    [Tooltip("How close the swung lego must get to a character to count as a hit.")]
    [SerializeField] private float meleeRadius = 0.6f;
    [SerializeField] private float meleeKnockback = 8f;
    [SerializeField] private float meleeUp = 3f;
    private static readonly Collider[] meleeHits = new Collider[8];

    [Header("Carry Servo")]
    [SerializeField] private float carryStiffness = 12f;
    [SerializeField] private float carryRotStiffness = 12f;
    [SerializeField] private float maxThrowSpeed = 14f;

    [Header("Client Prediction")]
    [SerializeField] private float predictionSmooth = 0.05f;
    [SerializeField] private float maxPredictionError = 1.5f;

    [Header("Remote Smoothing")]
    [SerializeField] private float maxExtrapolation = 0.3f;
    [SerializeField] private float teleportDistance = 4f;

    /// <summary>
    /// Moving platform that cargo poses are expressed against, set to the truck in the
    /// game scene and left null in the lobby. Streaming world poses while the truck drives
    /// makes every box inherit the truck's own interpolation error, which is what the
    /// cargo shivering on the bed actually is.
    /// </summary>
    public static Transform ReferenceFrame;

    /// <summary>
    /// Set while cargo rides a vehicle. A settled box otherwise falls asleep on the bed,
    /// and a sleeping body ignores the bed moving out from under it, so it hangs in the
    /// air until something bumps it awake.
    /// </summary>
    public static bool PreventSleep;

    private static readonly List<NetworkedCargoBody> all = new();
    private static readonly Dictionary<int, NetworkedCargoBody> heldByActor = new();

    public static IReadOnlyList<NetworkedCargoBody> All => all;

    public static NetworkedCargoBody HeldBy(int actorNumber)
        => heldByActor.TryGetValue(actorNumber, out NetworkedCargoBody body) ? body : null;

    /// <summary>Null while Stowed: the box is then part of its carrier's compound collider.</summary>
    private Rigidbody rb;
    private LegoSnap legoSnap;

    private float bodyMass = 1f;

    private CargoState state = CargoState.Free;
    private int holderActor = -1;
    private Transform carrier;

    private static Transform frameBodyFor;
    private static Rigidbody frameBody;

    private Vector3 holdTargetPos;
    private Quaternion holdTargetRot = Quaternion.identity;
    private Vector3 holdTargetVel;
    private float holdTargetTime;
    private bool hasHoldTarget;

    private Vector3 predictionVel;
    private bool bodyModeIsWriter;
    private bool bodyModeInitialized;

    private Vector3 netPos;
    private Quaternion netRot = Quaternion.identity;
    private bool hasNetPose;
    private bool snapNextPose;

    private Vector3 smoothLocalPos;
    private Quaternion smoothLocalRot = Quaternion.identity;
    private bool hasSmoothPose;

    private int pendingCarrierViewId = -1;
    private float pendingCarrierTimeout;
    private float lastOwnershipClaim = -99f;

    private const float ownershipClaimCooldown = 0.5f;
    private const float defaultSleepThreshold = 0.005f;

    public CargoState State => state;
    public int HolderActor => holderActor;
    public bool IsHeld => state == CargoState.Held;
    public bool IsFrozen => state == CargoState.Frozen;

    /// <summary>Roughly at rest — used to reject legos that are still flying, not placed.</summary>
    public bool IsSettled => rb == null || rb.isKinematic || rb.linearVelocity.sqrMagnitude < 0.5f;

    /// <summary>True on the single client responsible for simulating this body.</summary>
    public bool IsWriter => photonView.IsMine;

    /// <summary>True when the local player is the one carrying this box.</summary>
    public bool IsHeldByLocalPlayer
        => state == CargoState.Held && holderActor == PhotonNetwork.LocalPlayer.ActorNumber;

    #region Lifecycle

    private void Awake()
    {
        legoSnap = GetComponent<LegoSnap>();

        rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            bodyMass = rb.mass;
            ConfigureBody(rb);
        }
    }

    /// <summary>
    /// Capping depenetration is what keeps cargo from launching the truck. A box that ends
    /// up overlapping something is otherwise pushed out at whatever speed it takes to clear
    /// the overlap in one step, and that impulse goes straight into whatever it is resting on.
    /// </summary>
    private void ConfigureBody(Rigidbody body)
    {
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.maxDepenetrationVelocity = 1f;
    }

    /// <summary>Rouses every simulated box, for when the ground under them just moved.</summary>
    public static void WakeAll()
    {
        foreach (NetworkedCargoBody body in all)
        {
            if (body != null && body.rb != null && !body.rb.isKinematic)
                body.rb.WakeUp();
        }
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

        // Runs on every client, so the lobby-authored tint shows up for everyone.
        if (data.Length >= 8)
            ApplyColor(new Color((float)data[4], (float)data[5], (float)data[6], (float)data[7]));
    }

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    /// <summary>Applies the serialized tint to this box's own material instance locally.</summary>
    private void ApplyColor(Color c)
    {
        Renderer r = GetComponent<Renderer>();
        if (r == null) r = GetComponentInChildren<Renderer>();
        if (r == null) return;

        Material m = r.material; // per-box instance; the box keeps it through stow/detach
        if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
        m.color = c;
    }

    /// <summary>Only meaningful on the writer, which is the machine that simulates this body.</summary>
    public void SetMass(float mass)
    {
        bodyMass = mass;
        if (rb != null) rb.mass = mass;
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
        holdTargetVel = Vector3.zero;
        hasNetPose = false;
        snapNextPose = true;
        predictionVel = Vector3.zero;

        switch (state)
        {
            case CargoState.Stowed:
                // The rigidbody has to go, not just turn kinematic: a child that keeps its
                // own body stays a separate solver island, so the deliberate stud overlap
                // becomes a penetration the solver fights every step and pushes into
                // whatever the structure is resting on. Without it the colliders fold into
                // the carrier's compound and the overlap costs nothing.
                RemoveRigidbody();
                transform.SetParent(carrier, true);
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
                EnsureRigidbody();
                if (holderActor >= 0)
                    heldByActor[holderActor] = this;
                break;

            case CargoState.Free:
                transform.SetParent(null, true);
                LinkLegoParent(false);
                EnsureRigidbody();
                break;

            case CargoState.Frozen:
                // Pinned in the world. Stays parentless so it never rides the truck; any
                // stowed children keep hanging off it, so a welded block freezes as one piece.
                transform.SetParent(null, true);
                LinkLegoParent(false);
                EnsureRigidbody();
                if (hasPose)
                    transform.SetPositionAndRotation(localPos, localRot); // localPos/Rot carry the world pose
                break;
        }

        ApplyBodyMode(force: true);
    }

    /// <summary>
    /// Aligns the rigidbody with our role for the current state. The writer runs real
    /// physics; everyone else is a puppet so the two never fight over the same body.
    /// </summary>
    private void ApplyBodyMode(bool force = false)
    {
        if (state == CargoState.Stowed || rb == null)
        {
            bodyModeIsWriter = false;
            bodyModeInitialized = true;
            return;
        }

        if (state == CargoState.Frozen)
        {
            // Kinematic and solid on every client — no one simulates it, everyone collides
            // with it. Dynamic bodies (the car, a carried box) get stopped; it never moves.
            bodyModeIsWriter = false;
            bodyModeInitialized = true;
            ZeroVelocities();
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
            return;
        }

        bool writer = IsWriter;
        if (!force && bodyModeInitialized && writer == bodyModeIsWriter) return;

        bodyModeIsWriter = writer;
        bodyModeInitialized = true;

        if (writer)
        {
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            // Set here rather than at construction because scene objects have no ordering
            // guarantee against the controller that decides the policy.
            rb.sleepThreshold = PreventSleep ? 0f : defaultSleepThreshold;
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

            // In a moving frame LateUpdate writes the transform every frame, so PhysX
            // interpolation would only be undone a moment later.
            rb.interpolation = ReferenceFrame != null
                ? RigidbodyInterpolation.None
                : RigidbodyInterpolation.Interpolate;
        }
    }

    private void EnsureRigidbody()
    {
        if (rb != null) return;

        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();

        rb.mass = bodyMass;
        ConfigureBody(rb);
        bodyModeInitialized = false;
    }

    private void RemoveRigidbody()
    {
        if (rb == null) return;

        ZeroVelocities();
        Destroy(rb);
        rb = null;
        bodyModeInitialized = false;
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

    private void ZeroVelocities()
    {
        if (rb == null || rb.isKinematic) return;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    private static Vector3 WorldToFramePoint(Vector3 world)
        => ReferenceFrame != null ? ReferenceFrame.InverseTransformPoint(world) : world;

    private static Vector3 FrameToWorldPoint(Vector3 local)
        => ReferenceFrame != null ? ReferenceFrame.TransformPoint(local) : local;

    private static Quaternion WorldToFrameRotation(Quaternion world)
        => ReferenceFrame != null ? Quaternion.Inverse(ReferenceFrame.rotation) * world : world;

    private static Quaternion FrameToWorldRotation(Quaternion local)
        => ReferenceFrame != null ? ReferenceFrame.rotation * local : local;

    /// <summary>Velocity relative to the reference frame, expressed in frame space.</summary>
    private Vector3 FrameRelativeVelocity()
    {
        if (rb == null || rb.isKinematic) return Vector3.zero;
        if (ReferenceFrame == null) return rb.linearVelocity;

        if (frameBodyFor != ReferenceFrame)
        {
            frameBodyFor = ReferenceFrame;
            frameBody = ReferenceFrame.GetComponent<Rigidbody>();
        }

        Vector3 frameVel = frameBody != null ? frameBody.GetPointVelocity(rb.position) : Vector3.zero;
        return ReferenceFrame.InverseTransformDirection(rb.linearVelocity - frameVel);
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

    /// <summary>Master/owner welds this box into the carrier's local frame.</summary>
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
        if (state == CargoState.Frozen) return false; // pinned bricks can't be grabbed/moved

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
            // Ownership deliberately stays with the thrower. Handing it back here would
            // make the box a puppet mid-flight and yank it to the previous owner's
            // lagging copy, which reads as the box falling, snapping back and falling again.
            photonView.RPC(nameof(RpcApplyState), RpcTarget.All,
                (int)CargoState.Free, -1, -1, Vector3.zero, Quaternion.identity, false);
            return;
        }

        if (PhotonNetwork.IsMasterClient)
            AuthorityFree();
        else
            photonView.RPC(nameof(RpcRequestRelease), RpcTarget.MasterClient);
    }

    /// <summary>Called by the holder to pin the box (and any welded block) where it is now.</summary>
    public void RequestFreeze()
    {
        if (state != CargoState.Held) return;
        if (holderActor != PhotonNetwork.LocalPlayer.ActorNumber) return;

        // Freeze at the pose the player actually sees (their predicted transform).
        Vector3 wp = transform.position;
        Quaternion wr = transform.rotation;

        if (Policy == CargoAuthorityPolicy.DistributedOwnership || PhotonNetwork.IsMasterClient)
            photonView.RPC(nameof(RpcApplyState), RpcTarget.All,
                (int)CargoState.Frozen, -1, -1, wp, wr, true);
        else
            photonView.RPC(nameof(RpcRequestFreeze), RpcTarget.MasterClient, wp, wr);
    }

    /// <summary>Called by any player to unpin a frozen box back into free physics.</summary>
    public void RequestUnfreeze()
    {
        if (state != CargoState.Frozen) return;

        photonView.RPC(nameof(RpcApplyState), RpcTarget.All,
            (int)CargoState.Free, -1, -1, Vector3.zero, Quaternion.identity, false);
    }

    /// <summary>Uniform-scales the held brick on every client. Only legos of equal scale snap.</summary>
    public void RequestScale(float uniformScale)
    {
        if (state != CargoState.Held) return;
        if (holderActor != PhotonNetwork.LocalPlayer.ActorNumber) return;

        photonView.RPC(nameof(RpcSetScale), RpcTarget.All, uniformScale);
    }

    /// <summary>Master sets the scale on every client (used by checkpoint restore to reset size).</summary>
    public void AuthorityScale(float uniformScale)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        photonView.RPC(nameof(RpcSetScale), RpcTarget.All, uniformScale);
    }

    [PunRPC]
    private void RpcSetScale(float s)
    {
        transform.localScale = new Vector3(s, s, s);
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
        // The hand's own speed is tracked so the servo can carry it as a feed forward term.
        // Without it the servo converges on the hand and the box leaves the player's grip
        // with almost no velocity, so it drops straight down instead of being thrown.
        if (hasHoldTarget)
        {
            float dt = Time.time - holdTargetTime;
            if (dt > 0.0001f)
                holdTargetVel = Vector3.Lerp(holdTargetVel, (worldPos - holdTargetPos) / dt, 0.5f);
        }

        holdTargetTime = Time.time;
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

        if (state == CargoState.Stowed || rb == null) return;

        if (IsWriter)
        {
            if (state == CargoState.Held && hasHoldTarget)
            {
                DriveHeldBody();
                TryMeleeHit();
            }
        }
        else if (hasNetPose)
        {
            DriveRemotePose();
        }
    }

    /// <summary>
    /// A held lego swung fast past a character knocks it into a ragdoll. Uses a proximity + speed
    /// check (not a collision) so it works even on the "ghost" walking players in the game scene,
    /// whose CharacterController lets rigidbodies pass through. Runs on the writer only, so the
    /// swing speed is authoritative; it then tells the victim to ragdoll on every client.
    /// </summary>
    private void TryMeleeHit()
    {
        if (rb == null) return;
        Vector3 vel = rb.linearVelocity;
        if (vel.magnitude < meleeMinSpeed) return;

        int n = Physics.OverlapSphereNonAlloc(transform.position, meleeRadius, meleeHits, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            CharacterRagdoll victim = meleeHits[i].GetComponentInParent<CharacterRagdoll>();
            if (victim == null || victim.IsRagdolled) continue;
            if (victim.photonView.OwnerActorNr == holderActor) continue; // not the one swinging it

            Vector3 dir = vel; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = victim.transform.position - transform.position;
            Vector3 force = dir.normalized * meleeKnockback + Vector3.up * meleeUp;

            victim.ApplyHit(force, transform.position);
            return; // one victim per swing
        }
    }

    /// <summary>
    /// The carried box stays a real rigidbody driven by a velocity servo, so it still
    /// collides with the world instead of tunnelling through it.
    /// </summary>
    private void DriveHeldBody()
    {
        Vector3 diff = holdTargetPos - rb.position;
        rb.linearVelocity = diff * carryStiffness + Vector3.ClampMagnitude(holdTargetVel, maxThrowSpeed);

        Quaternion delta = holdTargetRot * Quaternion.Inverse(rb.rotation);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;

        if (Mathf.Abs(angle) > 0.05f && !float.IsInfinity(axis.x) && axis.sqrMagnitude > 0.0001f)
            rb.angularVelocity = axis.normalized * (angle * Mathf.Deg2Rad * carryRotStiffness);
        else
            rb.angularVelocity = Vector3.zero;
    }

    /// <summary>
    /// Drives the puppet copy toward the writer's stream.
    ///
    /// Two details matter here. The smoothing runs in the reference frame, so a box riding
    /// the truck converges on a target that is standing still in truck space and then sits
    /// perfectly on the bed however fast the truck drives. And the move goes through the
    /// rigidbody rather than the transform, so the puppet sweeps and can shove dynamic
    /// bodies out of the way instead of teleporting into them.
    /// </summary>
    private void DriveRemotePose()
    {
        // The smoothed pose is kept here rather than read back off the transform, so the
        // holder's local prediction can move the box for rendering without feeding its
        // own guess back into the correction.
        bool snap = snapNextPose || !hasSmoothPose ||
                    Vector3.Distance(smoothLocalPos, netPos) > teleportDistance;

        if (snap)
        {
            snapNextPose = false;
            hasSmoothPose = true;
            smoothLocalPos = netPos;
            smoothLocalRot = netRot;
        }
        else
        {
            float step = Mathf.Clamp01(Time.fixedDeltaTime * PhotonNetwork.SerializationRate);
            smoothLocalPos = Vector3.Lerp(smoothLocalPos, netPos, step);
            smoothLocalRot = Quaternion.Slerp(smoothLocalRot, netRot, step);
        }

        // With a moving frame the placement is deferred to LateUpdate, where the truck has
        // already been interpolated for this frame. Reading it here instead would pin the
        // box to the truck's previous physics step, which at speed is a visible slide.
        if (ReferenceFrame != null) return;

        if (snap)
        {
            rb.position = smoothLocalPos;
            rb.rotation = smoothLocalRot;
            transform.SetPositionAndRotation(smoothLocalPos, smoothLocalRot);
            return;
        }

        rb.MovePosition(smoothLocalPos);
        rb.MoveRotation(smoothLocalRot);
    }

    /// <summary>
    /// Host-authority grabs cost a round trip. The holder renders the box at its own hand
    /// and blends toward the authoritative pose, clamped so it can never drift far enough
    /// to disagree with what everyone else sees.
    /// </summary>
    private void LateUpdate()
    {
        PlaceInMovingFrame();

        if (state != CargoState.Held) return;
        if (IsWriter || !IsHeldByLocalPlayer || !hasHoldTarget) return;

        Vector3 authoritative = transform.position;
        Vector3 predicted = holdTargetPos;

        if (Vector3.Distance(predicted, authoritative) > maxPredictionError)
            predicted = authoritative + Vector3.ClampMagnitude(predicted - authoritative, maxPredictionError);

        transform.position = Vector3.SmoothDamp(authoritative, predicted, ref predictionVel, predictionSmooth);
    }

    /// <summary>
    /// Pins a puppet onto the truck using the pose the truck is actually being rendered at
    /// this frame. That is what makes cargo sit still on a bed doing 20 m/s: the box and
    /// the truck now carry the exact same interpolation error instead of two different ones.
    /// </summary>
    private void PlaceInMovingFrame()
    {
        if (ReferenceFrame == null || rb == null) return;
        if (state == CargoState.Stowed || state == CargoState.Frozen || IsWriter || !hasSmoothPose) return;

        transform.SetPositionAndRotation(
            FrameToWorldPoint(smoothLocalPos),
            FrameToWorldRotation(smoothLocalRot));
    }

    /// <summary>
    /// Only the machine that owns a body simulates it, so every other box is a puppet here
    /// and a carried box would just scrape along them. Claiming a box the moment we touch
    /// it hands us its simulation, which is what makes cargo shove and topple cargo.
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        if (Policy != CargoAuthorityPolicy.DistributedOwnership) return;
        if (state != CargoState.Held || !IsWriter || !IsHeldByLocalPlayer) return;
        if (collision.rigidbody == null) return;

        NetworkedCargoBody other = collision.rigidbody.GetComponent<NetworkedCargoBody>();
        if (other == null || other == this) return;
        if (other.state != CargoState.Free || other.photonView.IsMine) return;
        if (Time.time - other.lastOwnershipClaim < ownershipClaimCooldown) return;

        other.lastOwnershipClaim = Time.time;
        other.photonView.TransferOwnership(PhotonNetwork.LocalPlayer);
    }

    private void ResolvePendingCarrier()
    {
        pendingCarrierTimeout -= Time.fixedDeltaTime;

        if (ResolveView(pendingCarrierViewId) != null)
        {
            int carrierViewId = pendingCarrierViewId;
            pendingCarrierViewId = -1;
            ApplyState(CargoState.Stowed, -1, carrierViewId, Vector3.zero, Quaternion.identity, false);
            return;
        }

        if (pendingCarrierTimeout <= 0f)
            pendingCarrierViewId = -1;
    }

    #endregion

    #region Network

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            // Stowed and Frozen boxes are static, so they send a constant and UnreliableOnChange
            // suppresses them entirely.
            bool sendPose = state != CargoState.Stowed && state != CargoState.Frozen;

            stream.SendNext((int)state);
            stream.SendNext(ReferenceFrame != null);
            stream.SendNext(sendPose ? WorldToFramePoint(transform.position) : Vector3.zero);
            stream.SendNext(sendPose ? WorldToFrameRotation(transform.rotation) : Quaternion.identity);
            stream.SendNext(sendPose ? FrameRelativeVelocity() : Vector3.zero);
        }
        else
        {
            int remoteState = (int)stream.ReceiveNext();
            bool remoteHasFrame = (bool)stream.ReceiveNext();
            Vector3 pos = (Vector3)stream.ReceiveNext();
            Quaternion rot = (Quaternion)stream.ReceiveNext();
            Vector3 vel = (Vector3)stream.ReceiveNext();

            if (IsWriter) return;

            // State travels by reliable RPC while poses are unreliable, so a packet from
            // before the last transition can still land. Its pose is meaningless now.
            if (remoteState != (int)state || state == CargoState.Stowed || state == CargoState.Frozen) return;

            // Same reasoning for the frame: a pose in truck space is nonsense until we
            // have resolved the truck ourselves.
            if (remoteHasFrame != (ReferenceFrame != null)) return;

            float lag = Mathf.Clamp((float)(PhotonNetwork.Time - info.SentServerTime), 0f, maxExtrapolation);
            netPos = pos + vel * lag;
            netRot = rot;
            hasNetPose = true;
        }
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
    private void RpcRequestFreeze(Vector3 worldPos, Quaternion worldRot, PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (state != CargoState.Held) return;
        if (info.Sender == null || info.Sender.ActorNumber != holderActor) return;

        photonView.RPC(nameof(RpcApplyState), RpcTarget.All,
            (int)CargoState.Frozen, -1, -1, worldPos, worldRot, true);
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
        transform.SetPositionAndRotation(worldPos, worldRot);
        if (rb != null && rb.isKinematic)
        {
            rb.position = worldPos;
            rb.rotation = worldRot;
        }

        netPos = WorldToFramePoint(worldPos);
        netRot = WorldToFrameRotation(worldRot);
        snapNextPose = true;
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
        bool frozen = state == CargoState.Frozen;

        // Stowed carries a carrier-local pose; Frozen carries its fixed world pose; the rest
        // carry nothing here and get their world pose from the teleport below.
        Vector3 localPos = stowed ? transform.localPosition : (frozen ? transform.position : Vector3.zero);
        Quaternion localRot = stowed ? transform.localRotation : (frozen ? transform.rotation : Quaternion.identity);

        photonView.RPC(nameof(RpcApplyState), target,
            (int)state, holderActor, ViewIdOf(carrier), localPos, localRot, stowed || frozen);

        // Runtime scale changes aren't in the instantiation data, so restate it for newcomers.
        photonView.RPC(nameof(RpcSetScale), target, transform.localScale.x);

        // The pose stream is UnreliableOnChange, so a box that already settled sends nothing and
        // the newcomer would keep it wherever it was instantiated. Stowed/Frozen already carried
        // their pose in the state RPC above.
        if (!stowed && !frozen)
            photonView.RPC(nameof(RpcTeleport), target, transform.position, transform.rotation);
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
