using UnityEngine;
using UnityEngine.InputSystem;

public class CarCamera : MonoBehaviour
{
    [Header("Follow")]
    [SerializeField] private Transform target;
    [SerializeField] private float followSpeed = 8f;
    [SerializeField] private float rotationSpeed = 5f;

    [Header("Mouse")]
    [SerializeField] private float mouseSensitivity = 2f;

    [Header("Camera Angles (Local Offset)")]
    [SerializeField] private Vector3 angle1 = new(0f, 4f, -8f);
    [SerializeField] private Vector3 angle2 = new(0f, 8f, -12f);
    [SerializeField] private Vector3 angle3 = new(0f, 2f, -4f);

    private int currentAngle;
    private float yaw;
    private float pitch = 15f;
    private Vector3[] angles;

    private void Start()
    {
        angles = new[] { angle1, angle2, angle3 };

        if (target == null)
            target = transform.parent;

        if (target != null)
            yaw = target.eulerAngles.y;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.cKey.wasPressedThisFrame)
        {
            currentAngle = (currentAngle + 1) % angles.Length;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * mouseSensitivity * 0.1f;
            pitch -= delta.y * mouseSensitivity * 0.1f;
            pitch = Mathf.Clamp(pitch, -10f, 60f);
        }
    }

    private void LateUpdate()
    {
        if (target == null) return;

        Vector3 offset = angles[currentAngle];

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 desiredPos = target.position + rotation * offset;

        transform.position = Vector3.Lerp(transform.position, desiredPos, Time.deltaTime * followSpeed);

        Vector3 lookTarget = target.position + Vector3.up * 1.5f;
        Quaternion desiredRot = Quaternion.LookRotation(lookTarget - transform.position);
        transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, Time.deltaTime * rotationSpeed);
    }
}
