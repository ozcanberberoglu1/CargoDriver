using Photon.Pun;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class ToyController : MonoBehaviourPun
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float rotationSpeed = 10f;
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float gravity = -20f;

    [Header("TPS Camera")]
    [SerializeField] private Camera playerCamera;
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private float cameraDistance = 5f;
    [SerializeField] private float cameraHeight = 3f;
    [SerializeField] private float minPitch = -10f;
    [SerializeField] private float maxPitch = 60f;

    [Header("FPS Camera")]
    [SerializeField] private Vector3 fpsOffset = new(0f, 1.6f, 0.2f);
    [SerializeField] private float fpsMinPitch = -80f;
    [SerializeField] private float fpsMaxPitch = 80f;

    [Header("Crosshair")]
    [SerializeField] private GameObject cursorUI;

    private CharacterController controller;
    private Animator animator;
    private CharacterRagdoll ragdoll;

    [Header("Ragdoll camera shake")]
    [SerializeField] private float ragdollShakeAmp = 0.06f;
    [SerializeField] private float ragdollShakeDuration = 0.22f;
    [SerializeField] private float ragdollShakeFrequency = 18f;
    private float ragdollShakeTimer;
    private bool wasRagdolled;
    private Vector3 velocity;
    private float yaw;
    private float pitch = 20f;
    private bool isFPS;
    private bool isPaused;
    private bool movementLocked;
    private bool physicsGhost;
    private Transform ridingSeat;

    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");

    public bool IsGrounded { get; private set; }
    public bool IsRidingVehicle { get; private set; }
    public float CurrentSpeed { get; private set; }
    public bool IsFPS => isFPS;
    public Camera PlayerCamera => playerCamera;
    public bool IsPaused => isPaused;
    public float Yaw => yaw;
    public float Pitch => pitch;

    public float MouseSensitivity
    {
        get => mouseSensitivity;
        set => mouseSensitivity = value;
    }

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();
        ragdoll = GetComponent<CharacterRagdoll>();

        if (playerCamera == null)
            playerCamera = GetComponentInChildren<Camera>();
    }

    private void Start()
    {
        SetPlayerLayer();

        if (!photonView.IsMine)
        {
            if (playerCamera != null)
                playerCamera.gameObject.SetActive(false);

            controller.enabled = false;

            foreach (Collider col in GetComponentsInChildren<Collider>())
            {
                if (col is CharacterController) continue;
                col.enabled = false;
            }

            return;
        }

        if (playerCamera != null)
            playerCamera.gameObject.SetActive(true);

        yaw = transform.eulerAngles.y;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (cursorUI == null)
        {
            Canvas[] allCanvas = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (Canvas c in allCanvas)
            {
                Transform t = c.transform.Find("Cursor");
                if (t != null)
                {
                    cursorUI = t.gameObject;
                    break;
                }
            }
        }

        isFPS = true;
        if (cursorUI != null)
            cursorUI.SetActive(true);
        SetOwnMeshVisibility(false);

        DisableSceneCameras();
        SetPlayerName();
    }

    private void OnEnable()
    {
        SetPlayerName();
    }

    /// <summary>
    /// Update() stops driving the animator while movement is locked, so the locomotion
    /// parameters are settled here. Without this a player who boards the truck at a run
    /// keeps the last frame's run cycle looping, jogging on the spot in the truck bed.
    /// </summary>
    public void SetMovementLocked(bool locked)
    {
        movementLocked = locked;

        if (!locked) return;

        CurrentSpeed = 0f;
        // A locked character is at rest by definition; boarding mid-jump would otherwise
        // leave it falling forever. The jump check that reads this only runs while unlocked.
        IsGrounded = true;
        UpdateAnimator();
    }

    #region Vehicle riding

    /// <summary>
    /// Seats the avatar in the truck. The collider goes away with it: a rider must not take
    /// part in the simulation, otherwise it fights the bodywork it is sitting inside.
    ///
    /// The avatar follows the seat rather than being parented to it. Re-parenting would
    /// make the synced local position jump between vehicle space and world space, and
    /// PhotonTransformView turns that jump into a velocity it extrapolates from, which
    /// flings the remote copy across the map on the frame someone steps out.
    /// </summary>
    public void AttachToVehicle(Transform seat)
    {
        if (seat == null) return;

        ridingSeat = seat;
        IsRidingVehicle = true;

        if (controller != null) controller.enabled = false;
        SetBodyCollidersEnabled(false);
        SetMovementLocked(true);

        velocity = Vector3.zero;
        SetPlayerLayer();
    }

    /// <summary>Puts the avatar back on its own feet, keeping its current world pose.</summary>
    public void DetachFromVehicle()
    {
        ridingSeat = null;
        IsRidingVehicle = false;

        SetMovementLocked(false);
        velocity = Vector3.zero;
        yaw = transform.eulerAngles.y;

        // Remote copies are display only and had their colliders stripped at spawn.
        if (photonView.IsMine)
        {
            SetBodyCollidersEnabled(true);
            if (controller != null) controller.enabled = true;
            ApplyPhysicsGhost();
        }
    }

    /// <summary>
    /// Makes the character one way solid: it is still stopped by everything it walks into,
    /// but nothing collides with its capsule. A CharacterController is a kinematic body, so
    /// without this it has infinite mass and standing on the truck would shove the truck
    /// around and driving into a player would stop a 1000 kg vehicle dead.
    /// </summary>
    public void SetPhysicsGhost(bool ghost)
    {
        physicsGhost = ghost;
        ApplyPhysicsGhost();
    }

    private void ApplyPhysicsGhost()
    {
        if (controller != null)
            controller.detectCollisions = !physicsGhost;
    }

    /// <summary>A CharacterController ignores transform writes, so it is cycled around them.</summary>
    public void TeleportTo(Vector3 position, Quaternion rotation)
    {
        bool hadController = controller != null && controller.enabled;
        if (hadController) controller.enabled = false;

        transform.SetPositionAndRotation(position, rotation);

        if (hadController)
        {
            controller.enabled = true;
            ApplyPhysicsGhost();
        }

        velocity = Vector3.zero;
        yaw = rotation.eulerAngles.y;
    }

    private void SetBodyCollidersEnabled(bool enabled)
    {
        foreach (Collider col in GetComponentsInChildren<Collider>(true))
        {
            if (col is CharacterController) continue;
            col.enabled = enabled;
        }
    }

    #endregion

    /// <summary>Set by CharacterRagdoll while the body is a ragdoll — freezes normal locomotion.</summary>
    public bool Ragdolled { get; set; }

    private void Update()
    {
        if (!photonView.IsMine) return;
        if (Ragdolled)
        {
            HandleCamera(); // still let the player look around while knocked down
            return;
        }

        HandlePauseToggle();

        if (isPaused) return;

        HandleCameraToggle();

        CargoPickup cp = GetComponent<CargoPickup>();
        if (cp == null || !cp.IsRotating)
            HandleCamera();

        if (!movementLocked)
        {
            HandleMovement();
            UpdateAnimator();
        }
        else
        {
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }

    public void SetPaused(bool paused)
    {
        isPaused = paused;

        if (paused)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            CurrentSpeed = 0f;
            if (animator != null)
                animator.SetFloat(SpeedHash, 0f);
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void HandlePauseToggle()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb.escapeKey.wasPressedThisFrame)
        {
            var lobby = FindAnyObjectByType<LobbyController>();
            if (lobby != null)
                lobby.TogglePause();
        }
    }

    private void HandleCameraToggle()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb.cKey.wasPressedThisFrame)
        {
            isFPS = !isFPS;

            if (cursorUI != null)
                cursorUI.SetActive(isFPS);

            if (isFPS)
                pitch = Mathf.Clamp(pitch, fpsMinPitch, fpsMaxPitch);

            SetOwnMeshVisibility(!isFPS);
        }
    }

    private void HandleCamera()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        Vector2 delta = mouse.delta.ReadValue();
        yaw += delta.x * mouseSensitivity * 0.1f;
        pitch -= delta.y * mouseSensitivity * 0.1f;

        if (isFPS)
            pitch = Mathf.Clamp(pitch, fpsMinPitch, fpsMaxPitch);
        else
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    private void LateUpdate()
    {
        // Runs on every client, and after the truck has been interpolated for this frame so
        // the rider sits still in the cab instead of shivering against it. Rotation is left
        // alone: the local player aims it with the mouse, remotes get it off the stream.
        if (ridingSeat != null)
            transform.position = ridingSeat.position;

        if (!photonView.IsMine || playerCamera == null) return;

        // While ragdolled the root doesn't move (the bones do), so drive the camera off the bones.
        if (Ragdolled && ragdoll != null)
        {
            if (!wasRagdolled) ragdollShakeTimer = ragdollShakeDuration; // jolt on impact
            wasRagdolled = true;

            Vector3 shake = Vector3.zero;
            if (ragdollShakeTimer > 0f)
            {
                ragdollShakeTimer -= Time.deltaTime;
                // Smooth (Perlin) rumble that eases out, instead of harsh per-frame jitter.
                float fade = ragdollShakeTimer / ragdollShakeDuration;
                float amp = ragdollShakeAmp * fade * fade;
                float n = Time.time * ragdollShakeFrequency;
                shake = new Vector3(Mathf.PerlinNoise(n, 0f) - 0.5f, Mathf.PerlinNoise(0f, n) - 0.5f, 0f) * (2f * amp);
            }

            if (isFPS && ragdoll.Head != null)
            {
                // Stay first-person from the head — you don't see yourself/your name, like normal FPS.
                playerCamera.transform.position = ragdoll.Head.position + shake;
                playerCamera.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }
            else if (ragdoll.Hips != null)
            {
                // Third-person orbit of the hips.
                Quaternion ragRot = Quaternion.Euler(pitch, yaw, 0f);
                Vector3 ragTarget = ragdoll.Hips.position + Vector3.up * 0.3f;
                playerCamera.transform.position = ragTarget + ragRot * new Vector3(0f, 0f, -cameraDistance) + shake;
                playerCamera.transform.LookAt(ragTarget);
            }
            return;
        }
        wasRagdolled = false;

        if (isFPS)
        {
            Vector3 headPos = transform.position + transform.TransformVector(fpsOffset);
            playerCamera.transform.position = headPos;
            playerCamera.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }
        else
        {
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 offset = rotation * new Vector3(0f, 0f, -cameraDistance);
            Vector3 target = transform.position + Vector3.up * cameraHeight;

            playerCamera.transform.position = target + offset;
            playerCamera.transform.LookAt(target);
        }
    }

    private void HandleMovement()
    {
        IsGrounded = controller.isGrounded;
        if (IsGrounded && velocity.y < 0f)
            velocity.y = -2f;

        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        float h = 0f, v = 0f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  h -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) h += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  v -= 1f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    v += 1f;

        Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;

        Vector3 move = (forward * v + right * h);
        if (move.sqrMagnitude > 1f)
            move.Normalize();

        CurrentSpeed = move.magnitude * moveSpeed;
        controller.Move(move * moveSpeed * Time.deltaTime);

        if (move.sqrMagnitude > 0.01f)
        {
            Quaternion target = Quaternion.LookRotation(move);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, rotationSpeed * Time.deltaTime);
        }

        if (kb.spaceKey.wasPressedThisFrame && IsGrounded)
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);
    }

    private void UpdateAnimator()
    {
        if (animator == null) return;

        animator.SetFloat(SpeedHash, CurrentSpeed);
        animator.SetBool(IsGroundedHash, IsGrounded);
    }

    private void DisableSceneCameras()
    {
        foreach (Camera cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            if (cam == playerCamera) continue;
            if (cam.GetComponentInParent<ToyController>() != null) continue;
            cam.gameObject.SetActive(false);
        }
    }

    private void SetPlayerLayer()
    {
        int playerLayer = LayerMask.NameToLayer("Player");
        if (playerLayer < 0) return;

        gameObject.layer = playerLayer;
        foreach (Transform child in GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = playerLayer;
    }

    private void SetOwnMeshVisibility(bool visible)
    {
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            if (r is SkinnedMeshRenderer || r is MeshRenderer)
                r.shadowCastingMode = visible
                    ? UnityEngine.Rendering.ShadowCastingMode.On
                    : UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }
    }

    private void SetPlayerName()
    {
        if (photonView == null || photonView.Owner == null) return;

        TextMeshProUGUI nameText = GetComponentInChildren<TextMeshProUGUI>();
        if (nameText != null)
        {
            string nick = photonView.Owner.NickName;
            nameText.text = string.IsNullOrEmpty(nick)
                ? $"Player{photonView.Owner.ActorNumber}"
                : nick;
        }
    }
}
