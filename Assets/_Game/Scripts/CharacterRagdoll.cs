using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Turns the character into a physics ragdoll when hit hard by a lego, then stands it back up.
///
/// Multiplayer model (see CLAUDE.md §8):
/// - The hit is detected by the machine simulating the swung lego (its writer) and broadcast with
///   an RPC on the victim's PhotonView, so every client ragdolls the same character with the same
///   knockback. Ragdoll physics diverge a little per client during the fall (it's short and
///   chaotic), which is fine — the parts that must agree are synced: the ragdoll START (RPC) and
///   the STAND-UP position, which the victim's OWNER decides and broadcasts.
/// - While ragdolled, the character's PhotonTransformView / PhotonAnimatorView are turned off so
///   the streamed animated pose doesn't fight the local ragdoll; they come back on when it stands.
/// </summary>
[RequireComponent(typeof(PhotonView))]
public class CharacterRagdoll : MonoBehaviourPun, IPunObservable
{
    [Tooltip("The hips bone — the ragdoll's root, where the knockback impulse is applied.")]
    [SerializeField] private Transform hips;
    [Tooltip("How long the character stays down before standing back up.")]
    [SerializeField] private float ragdollDuration = 3f;
    [Tooltip("Seconds to smoothly blend from the fallen pose into standing (0 = instant snap).")]
    [SerializeField] private float getUpBlend = 0.45f;
    [SerializeField] private LayerMask groundMask = ~0;
    [Tooltip("How high above the hips the name tag rides while the character is down.")]
    [SerializeField] private float nameLabelUp = 0.6f;

    [Header("Fall sound (3D — quieter from far away)")]
    [SerializeField] private AudioClip ragdollSound;
    [Range(0f, 1f)] [SerializeField] private float ragdollVolume = 1f;
    [Tooltip("Within this range the sound is at full volume.")]
    [SerializeField] private float soundMinDistance = 2f;
    [Tooltip("Past this range the sound fades to nothing.")]
    [SerializeField] private float soundMaxDistance = 20f;

    public Transform Hips => hips;
    public Transform Head => headBone;

    [Header("Test")]
    [Tooltip("Press this key to ragdoll yourself, as if hit (leave as None to disable).")]
    [SerializeField] private Key testKey = Key.P;
    [SerializeField] private float testKnockback = 8f;

    private ToyController toy;
    private Animator animator;
    private CharacterController controller;
    private PhotonTransformView transformView;
    private PhotonAnimatorView animatorView;

    private Rigidbody[] bones;
    private Collider[] boneColliders;
    private Rigidbody hipsBody;

    private bool ragdolled;
    private float timer;

    private Quaternion[] ragdollPose;   // bone rotations captured at get-up, to blend out of
    private float getUpTimer;

    private Transform nameLabel;        // the name tag, so it can ride the falling body
    private Vector3 nameLabelLocalPos;
    private Transform headBone;         // for the first-person camera while ragdolled
    private AudioSource fallAudio;

    private Vector3 netHipsPos;          // owner's hips pose, so remotes place the body identically
    private Quaternion netHipsRot = Quaternion.identity;
    private bool hasNetHips;

    public bool IsRagdolled => ragdolled;

    /// <summary>Every live character, so a swung lego can find them without relying on colliders.</summary>
    public static readonly List<CharacterRagdoll> All = new List<CharacterRagdoll>();

    /// <summary>Roughly the middle of the torso (the root sits at the feet).</summary>
    public Vector3 BodyCenter => transform.position + Vector3.up * 1f;

    private void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    private void OnDisable() { All.Remove(this); }

    private void Awake()
    {
        toy = GetComponent<ToyController>();
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
        transformView = GetComponent<PhotonTransformView>();
        animatorView = GetComponent<PhotonAnimatorView>();

        bones = GetComponentsInChildren<Rigidbody>(true);
        var cols = new List<Collider>();
        foreach (Rigidbody rb in bones)
        {
            Collider c = rb.GetComponent<Collider>();
            if (c != null) cols.Add(c);
        }
        boneColliders = cols.ToArray();
        hipsBody = hips != null ? hips.GetComponent<Rigidbody>() : (bones.Length > 0 ? bones[0] : null);

        BillboardUI billboard = GetComponentInChildren<BillboardUI>(true);
        if (billboard != null)
        {
            nameLabel = billboard.transform;
            nameLabelLocalPos = nameLabel.localPosition;
        }

        foreach (Transform t in GetComponentsInChildren<Transform>(true))
            if (t.name == "mixamorig:Head") { headBone = t; break; }

        fallAudio = gameObject.AddComponent<AudioSource>();
        fallAudio.playOnAwake = false;
        fallAudio.spatialBlend = 1f;                       // full 3D so distance matters
        fallAudio.rolloffMode = AudioRolloffMode.Linear;
        fallAudio.dopplerLevel = 0f;
        fallAudio.minDistance = soundMinDistance;
        fallAudio.maxDistance = soundMaxDistance;

        // Observe this component so the hips pose can be streamed while ragdolled.
        if (photonView.ObservedComponents == null)
            photonView.ObservedComponents = new List<Component>();
        if (!photonView.ObservedComponents.Contains(this))
            photonView.ObservedComponents.Add(this);

        SetRagdollActive(false); // start animated
    }

    private void Update()
    {
        // Keep the name tag over the actual body while it's down, not hanging where it fell from.
        if (ragdolled && nameLabel != null && hips != null)
            nameLabel.position = hips.position + Vector3.up * nameLabelUp;

        // Self-test: knock yourself down as if a lego hit you, so it can be tried solo.
        if (photonView.IsMine && !ragdolled && testKey != Key.None &&
            Keyboard.current != null && Keyboard.current[testKey].wasPressedThisFrame)
        {
            Vector3 point = hips != null ? hips.position : transform.position + Vector3.up;
            Vector3 force = (-transform.forward + Vector3.up * 0.6f).normalized * testKnockback;
            ApplyHit(force, point);
        }

        // Only the owner runs the stand-up timer and decides where the character ends up.
        if (!ragdolled || !photonView.IsMine) return;

        timer -= Time.deltaTime;
        if (timer <= 0f)
            photonView.RPC(nameof(RpcGetUp), RpcTarget.All, GroundedRootPosition(), UprightRotation());
    }

    [PunRPC]
    private void RpcRagdoll(Vector3 force, Vector3 point)
    {
        if (ragdolled) return;
        SetRagdollActive(true);
        if (hipsBody != null)
            hipsBody.AddForceAtPosition(force, point, ForceMode.Impulse);
        timer = ragdollDuration;

        // Plays on every client at this character's position, so far-off players hear it faint.
        if (ragdollSound != null && fallAudio != null)
            fallAudio.PlayOneShot(ragdollSound, ragdollVolume);
    }

    [PunRPC]
    private void RpcGetUp(Vector3 pos, Quaternion rot)
    {
        // Remember the fallen pose, then let the animator take over and blend out of it so the
        // character eases up to standing instead of snapping.
        CaptureRagdollPose();
        SetRagdollActive(false);
        transform.SetPositionAndRotation(pos, rot);
        getUpTimer = getUpBlend;

        if (nameLabel != null) nameLabel.localPosition = nameLabelLocalPos; // back above the head
    }

    private void CaptureRagdollPose()
    {
        if (ragdollPose == null || ragdollPose.Length != bones.Length)
            ragdollPose = new Quaternion[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            ragdollPose[i] = bones[i].transform.localRotation;
    }

    // Runs after the Animator has written the standing pose; eases the bones out of the fallen pose.
    private void LateUpdate()
    {
        if (getUpTimer <= 0f || ragdollPose == null) return;

        getUpTimer -= Time.deltaTime;
        float t = getUpBlend > 0f ? Mathf.Clamp01(1f - getUpTimer / getUpBlend) : 1f;

        for (int i = 0; i < bones.Length; i++)
            bones[i].transform.localRotation = Quaternion.Slerp(ragdollPose[i], bones[i].transform.localRotation, t);
    }

    private void SetRagdollActive(bool active)
    {
        ragdolled = active;
        if (active) hasNetHips = false;

        bool owner = photonView.IsMine;
        foreach (Rigidbody rb in bones)
        {
            // On the owner every bone is full physics. On remotes the hips is kinematic and driven
            // by the streamed pose so the body sits in the same place everywhere; the other bones
            // still dangle locally, which is only cosmetic.
            bool kinematic = !active || (!owner && rb == hipsBody);
            rb.isKinematic = kinematic;
            rb.interpolation = active ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            if (active && !kinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
        foreach (Collider c in boneColliders)
            c.enabled = active; // solid only while ragdolled, so they don't fight the CharacterController

        if (animator != null) animator.enabled = !active;
        if (controller != null) controller.enabled = !active;
        if (toy != null) toy.Ragdolled = active;

        // The ragdoll runs locally on each client; don't let the streamed animated pose fight it.
        if (transformView != null) transformView.enabled = !active;
        if (animatorView != null) animatorView.enabled = !active;
    }

    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            bool sending = ragdolled && hips != null;
            stream.SendNext(sending);
            stream.SendNext(sending ? hips.position : Vector3.zero);
            stream.SendNext(sending ? hips.rotation : Quaternion.identity);
        }
        else
        {
            bool remoteRag = (bool)stream.ReceiveNext();
            Vector3 hp = (Vector3)stream.ReceiveNext();
            Quaternion hr = (Quaternion)stream.ReceiveNext();

            if (remoteRag)
            {
                netHipsPos = hp;
                netHipsRot = hr;
                hasNetHips = true;
            }
        }
    }

    // Remotes drive the (kinematic) hips to the owner's streamed pose so the body matches everywhere.
    private void FixedUpdate()
    {
        if (photonView.IsMine || !ragdolled || !hasNetHips || hipsBody == null) return;
        hipsBody.MovePosition(netHipsPos);
        hipsBody.MoveRotation(netHipsRot);
    }

    /// <summary>Ground point under the hips, so the character stands where it actually fell.</summary>
    private Vector3 GroundedRootPosition()
    {
        Vector3 p = hips != null ? hips.position : transform.position;
        if (Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 5f, groundMask, QueryTriggerInteraction.Ignore))
            p = hit.point;
        else
            p.y = transform.position.y;

        return p; // stand up exactly where the ragdoll came to rest (the car no longer launches on overlap)
    }

    private Quaternion UprightRotation()
    {
        // Keep the character's original heading (the root never rotated during the ragdoll).
        return Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
    }

    /// <summary>Called by the attacker's lego (via RPC) — routed here from NetworkedCargoBody.</summary>
    public void ApplyHit(Vector3 force, Vector3 point)
    {
        photonView.RPC(nameof(RpcRagdoll), RpcTarget.All, force, point);
    }
}
