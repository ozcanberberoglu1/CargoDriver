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

    [Header("Camera")]
    [SerializeField] private Camera playerCamera;
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private float cameraDistance = 5f;
    [SerializeField] private float cameraHeight = 3f;
    [SerializeField] private float minPitch = -10f;
    [SerializeField] private float maxPitch = 60f;

    private CharacterController controller;
    private Animator animator;
    private Vector3 velocity;
    private float yaw;
    private float pitch = 20f;

    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");

    public bool IsGrounded { get; private set; }
    public float CurrentSpeed { get; private set; }

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

        DisableSceneCameras();
        SetPlayerName();
    }

    private void OnEnable()
    {
        SetPlayerName();
    }

    private void Update()
    {
        if (!photonView.IsMine) return;

        HandleCamera();
        HandleMovement();
        UpdateAnimator();
    }

    private void HandleCamera()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        Vector2 delta = mouse.delta.ReadValue();
        yaw += delta.x * mouseSensitivity * 0.1f;
        pitch -= delta.y * mouseSensitivity * 0.1f;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    private void LateUpdate()
    {
        if (!photonView.IsMine || playerCamera == null) return;

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 offset = rotation * new Vector3(0f, 0f, -cameraDistance);
        Vector3 target = transform.position + Vector3.up * cameraHeight;

        playerCamera.transform.position = target + offset;
        playerCamera.transform.LookAt(target);
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
