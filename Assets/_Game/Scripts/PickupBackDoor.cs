using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Interactable rear door on the truck. When the local player looks at it, an outline turns on;
/// left-click toggles it open/closed with a rotation you configure. The collider sits on the door,
/// so it swings with it automatically.
///
/// Multiplayer:
///  • Authority = master. Anyone can click; non-masters send a request RPC, the master flips the
///    state and broadcasts it. One writer, no per-tick traffic.
///  • Each client animates the rotation locally from the synced bool (deterministic, no transform
///    streaming). The door is a child of the truck and uses LOCAL rotation, so a moving truck is fine.
///  • Late joiners get the current state via an RPC from the master in OnPlayerEnteredRoom.
///  • Outline is purely local — each player sees their own when they look.
///
/// Put this on PickupBackDoor and add a PhotonView to the same object (RPCs go through it).
/// </summary>
[RequireComponent(typeof(PhotonView))]
public class PickupBackDoor : MonoBehaviourPunCallbacks
{
    [Header("Rotation")]
    [Tooltip("Local rotation (degrees) added to the closed pose to reach the open pose, e.g. (0,120,0).")]
    [SerializeField] private Vector3 openRotationOffset = new Vector3(0f, 120f, 0f);
    [Tooltip("Local position (metres) added to the closed pose to reach the open pose. Use to nudge " +
             "the door to the exact spot rotation alone doesn't reach.")]
    [SerializeField] private Vector3 openPositionOffset = Vector3.zero;
    [Tooltip("Opening/closing speed in degrees per second.")]
    [SerializeField] private float rotateSpeed = 160f;

    [Header("Interaction")]
    [Tooltip("How close the player must be looking from (metres).")]
    [SerializeField] private float interactRange = 4f;
    [Tooltip("Outline visual to turn on while looked at. You build it; the script just toggles it.")]
    [SerializeField] private GameObject outlineObject;
    [Tooltip("Camera to look from. Leave empty to use Camera.main (the local player's active camera).")]
    [SerializeField] private Camera lookCamera;
    [SerializeField] private float toggleCooldown = 0.35f;
    [SerializeField] private bool debugLog = true;

    private Quaternion closedRot;
    private Quaternion openRot;
    private Vector3 closedPos;
    private Vector3 openPos;
    private float openAmount;   // 0 = closed, 1 = open
    private bool isOpen;
    private float nextToggleTime;
    private bool wasLooking;

    private void Awake()
    {
        closedRot = transform.localRotation;
        openRot = closedRot * Quaternion.Euler(openRotationOffset);
        closedPos = transform.localPosition;
        openPos = closedPos + openPositionOffset;
        SetOutline(false);

        if (debugLog) Debug.Log($"[BackDoor] Awake on '{gameObject.name}'.");

        if (photonView == null)
            Debug.LogError("[BackDoor] No PhotonView found — add one to THIS object.");
        else if (photonView.gameObject != gameObject)
            Debug.LogWarning($"[BackDoor] PhotonView is on '{photonView.gameObject.name}', not on the door " +
                             "object. Add a PhotonView to the door object itself, otherwise RPCs target the " +
                             "wrong view and the door won't sync.");
    }

    private void Update()
    {
        AnimateDoor();
        HandleLook();
    }

    private void AnimateDoor()
    {
        // Drive one 0..1 amount so rotation and position move together and finish at the same time.
        float goal = isOpen ? 1f : 0f;
        float totalAngle = Quaternion.Angle(closedRot, openRot);
        float rate = totalAngle > 1f ? rotateSpeed / totalAngle : rotateSpeed / 90f; // per-second
        openAmount = Mathf.MoveTowards(openAmount, goal, rate * Time.deltaTime);

        transform.localRotation = Quaternion.Slerp(closedRot, openRot, openAmount);
        transform.localPosition = Vector3.Lerp(closedPos, openPos, openAmount);
    }

    private Camera cachedCam;

    private Camera GetLookCamera()
    {
        if (lookCamera != null) return lookCamera;
        if (cachedCam != null && cachedCam.isActiveAndEnabled) return cachedCam;

        // The player controller disables the scene cameras, so Camera.main is null in-game.
        // Use the local player's own camera instead.
        foreach (var toy in FindObjectsByType<ToyController>(FindObjectsSortMode.None))
        {
            if (toy.photonView.IsMine && toy.PlayerCamera != null)
            {
                cachedCam = toy.PlayerCamera;
                return cachedCam;
            }
        }
        return Camera.main;
    }

    private void HandleLook()
    {
        Camera cam = GetLookCamera();
        bool looking = false;

        if (cam != null)
        {
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            if (Physics.Raycast(ray, out RaycastHit hit, interactRange, ~0, QueryTriggerInteraction.Ignore)
                && hit.collider.GetComponentInParent<PickupBackDoor>() == this)
            {
                looking = true;
            }
        }

        SetOutline(looking);

        if (debugLog && looking != wasLooking)
        {
            wasLooking = looking;
            Debug.Log($"[BackDoor] looking={looking}");
        }

        if (looking && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame
            && Time.time >= nextToggleTime)
        {
            nextToggleTime = Time.time + toggleCooldown;
            if (debugLog) Debug.Log("[BackDoor] toggle requested.");
            RequestToggle();
        }
    }

    private void SetOutline(bool on)
    {
        if (outlineObject != null && outlineObject.activeSelf != on)
            outlineObject.SetActive(on);
    }

    public override void OnDisable()
    {
        base.OnDisable();
        SetOutline(false);
    }

    // --- Networking: master is the authority for the open/closed state ---

    private void RequestToggle()
    {
        if (PhotonNetwork.IsMasterClient)
            Broadcast(!isOpen);
        else
            photonView.RPC(nameof(RpcRequestToggle), RpcTarget.MasterClient);
    }

    [PunRPC]
    private void RpcRequestToggle()
    {
        if (PhotonNetwork.IsMasterClient)
            Broadcast(!isOpen);
    }

    private void Broadcast(bool open)
    {
        photonView.RPC(nameof(RpcSetDoor), RpcTarget.All, open);
    }

    [PunRPC]
    private void RpcSetDoor(bool open)
    {
        isOpen = open;
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        // Bring the late joiner in sync with the current state.
        if (PhotonNetwork.IsMasterClient)
            photonView.RPC(nameof(RpcSetDoor), newPlayer, isOpen);
    }
}
