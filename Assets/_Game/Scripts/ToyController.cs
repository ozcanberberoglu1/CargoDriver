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
    private Vector3 velocity;
    private float yaw;
    private float pitch = 20f;
    private bool isFPS;
    private bool isPaused;
    private bool movementLocked;

    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");

    public bool IsGrounded { get; private set; }
    public float CurrentSpeed { get; private set; }
    public bool IsFPS => isFPS;
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

        if (playerCamera == null)
            playerCamera = GetComponentInChildren<Camera>();
    }

    private void Start()
    {
        if (!photonView.IsMine)
        {
            if (playerCamera != null)
                playerCamera.gameObject.SetActive(false);

            controller.enabled = false;
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

    public void SetMovementLocked(bool locked)
    {
        movementLocked = locked;
    }

    private void Update()
    {
        if (!photonView.IsMine) return;

        HandlePauseToggle();

        if (isPaused) return;

        HandleCameraToggle();
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
        if (!photonView.IsMine || playerCamera == null) return;

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
