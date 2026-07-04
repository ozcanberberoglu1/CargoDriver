using UnityEngine;
using UnityEngine.InputSystem;

public class CarCamera : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;

    [Header("Mouse")]
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private float minPitch = -10f;
    [SerializeField] private float maxPitch = 60f;

    [Header("Camera Angles (Offset from target)")]
    [SerializeField] private Vector3 angle1 = new(0f, 4f, -8f);
    [SerializeField] private Vector3 angle2 = new(0f, 8f, -12f);
    [SerializeField] private Vector3 angle3 = new(0f, 2f, -4f);

    [Header("Smoothing")]
    [SerializeField] private float positionSmoothTime = 0.1f;
    [SerializeField] private float lookHeight = 1.5f;

    private int currentAngle;
    private float yaw;
    private float pitch = 15f;
    private Vector3[] angles;
    private Vector3 smoothVelocity;

    private void Start()
    {
        angles = new[] { angle1, angle2, angle3 };

        if (target == null)
            target = transform.parent;

        // Unparent camera so it moves independently
        transform.SetParent(null, true);

        if (target != null)
            yaw = target.eulerAngles.y;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.cKey.wasPressedThisFrame)
            currentAngle = (currentAngle + 1) % angles.Length;

        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * mouseSensitivity * 0.1f;
            pitch -= delta.y * mouseSensitivity * 0.1f;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }
    }

    private void FixedUpdate()
    {
        if (target == null) return;

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 desiredPos = target.position + rotation * angles[currentAngle];

        transform.position = Vector3.SmoothDamp(
            transform.position, desiredPos, ref smoothVelocity, positionSmoothTime);

        Vector3 lookTarget = target.position + Vector3.up * lookHeight;
        transform.rotation = Quaternion.LookRotation(lookTarget - transform.position);
    }
}
