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

    [Header("Angle 1 (Offset from target)")]
    [SerializeField] private bool useAngle1 = true;
    [SerializeField] private Vector3 angle1 = new(0f, 4f, -8f);

    [Header("Angle 2 (Offset from target)")]
    [SerializeField] private bool useAngle2;
    [SerializeField] private Vector3 angle2 = new(0f, 8f, -12f);

    [Header("Camera Positions (Transform)")]
    [SerializeField] private Transform[] cameraPositions;
    [SerializeField] private float interiorMinYaw = -90f;
    [SerializeField] private float interiorMaxYaw = 90f;
    [SerializeField] private float interiorMinPitch = -30f;
    [SerializeField] private float interiorMaxPitch = 40f;

    [Header("Smoothing")]
    [SerializeField] private float positionSmoothTime = 0.1f;
    [SerializeField] private float lookHeight = 1.5f;

    private int currentAngle;
    private float yaw;
    private float pitch = 15f;
    private float interiorYaw;
    private float interiorPitch;
    private readonly System.Collections.Generic.List<object> allAngles = new();
    private Vector3 smoothVelocity;
    private Vector3 smoothTarget;

    private void Start()
    {
        if (useAngle1) allAngles.Add(angle1);
        if (useAngle2) allAngles.Add(angle2);

        if (cameraPositions != null)
        {
            foreach (var t in cameraPositions)
            {
                if (t != null) allAngles.Add(t);
            }
        }

        if (allAngles.Count == 0) allAngles.Add(new Vector3(0f, 4f, -8f));

        if (target == null)
            target = transform.parent;

        transform.SetParent(null, true);

        if (target != null)
        {
            yaw = target.eulerAngles.y;
            smoothTarget = target.position;
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.cKey.wasPressedThisFrame && allAngles.Count > 0)
        {
            currentAngle = (currentAngle + 1) % allAngles.Count;
            interiorYaw = 0f;
            interiorPitch = 0f;
        }

        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            Vector2 delta = mouse.delta.ReadValue();

            if (IsInteriorMode())
            {
                interiorYaw += delta.x * mouseSensitivity * 0.1f;
                interiorPitch -= delta.y * mouseSensitivity * 0.1f;
                interiorYaw = Mathf.Clamp(interiorYaw, interiorMinYaw, interiorMaxYaw);
                interiorPitch = Mathf.Clamp(interiorPitch, interiorMinPitch, interiorMaxPitch);
            }
            else
            {
                yaw += delta.x * mouseSensitivity * 0.1f;
                pitch -= delta.y * mouseSensitivity * 0.1f;
                pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
            }
        }
    }

    private void LateUpdate()
    {
        if (target == null || allAngles.Count == 0) return;

        smoothTarget = Vector3.Lerp(smoothTarget, target.position, Time.deltaTime * 12f);

        object current = allAngles[currentAngle];

        if (current is Vector3 offset)
        {
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 desiredPos = smoothTarget + rotation * offset;

            transform.position = Vector3.SmoothDamp(
                transform.position, desiredPos, ref smoothVelocity, positionSmoothTime);

            Vector3 lookTarget = smoothTarget + Vector3.up * lookHeight;
            transform.rotation = Quaternion.LookRotation(lookTarget - transform.position);
        }
        else if (current is Transform camPos)
        {
            transform.position = camPos.position;
            Quaternion baseRot = camPos.rotation;
            Quaternion lookOffset = Quaternion.Euler(interiorPitch, interiorYaw, 0f);
            transform.rotation = baseRot * lookOffset;
        }
    }

    private bool IsInteriorMode()
    {
        if (allAngles.Count == 0) return false;
        return allAngles[currentAngle] is Transform;
    }
}
