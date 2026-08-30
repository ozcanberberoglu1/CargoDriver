using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Drop this on any UI button. While the mouse is over it, an assigned image turns on and the
/// button eases into a rotation (on the axis you set); when the mouse leaves, the image turns off
/// and the rotation eases back. A small scale pop is included for extra life.
///
/// Attach manually to whichever buttons you want, assign the hover image, and set the rotation.
/// </summary>
[DisallowMultipleComponent]
public class ButtonHoverEffect : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Header("Hover image (toggled on / off)")]
    [SerializeField] private GameObject hoverImage;

    [Header("Rotation")]
    [Tooltip("What rotates. Leave empty to rotate this object itself.")]
    [SerializeField] private Transform rotateTarget;
    [Tooltip("Degrees added while hovered, e.g. (0,0,8) for a Z-axis tilt.")]
    [SerializeField] private Vector3 hoverEulerAngles = new Vector3(0f, 0f, 8f);

    [Header("Feel")]
    [SerializeField] private float rotateSpeed = 12f;
    [Tooltip("Extra scale while hovered (1 = none).")]
    [SerializeField] private float hoverScale = 1.05f;
    [SerializeField] private float scaleSpeed = 12f;

    [Header("Hover sound (plays once each time the mouse enters)")]
    [SerializeField] private AudioClip hoverSound;
    [Range(0f, 1f)] [SerializeField] private float hoverVolume = 1f;

    private Quaternion baseRotation;
    private Vector3 baseScale;
    private bool hovered;
    private AudioSource audioSource;

    private void Awake()
    {
        if (rotateTarget == null) rotateTarget = transform;
        baseRotation = rotateTarget.localRotation;
        baseScale = rotateTarget.localScale;
        if (hoverImage != null) hoverImage.SetActive(false);

        if (hoverSound != null)
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f; // 2D UI sound
        }
    }

    private void OnDisable()
    {
        // If the button is turned off (or its panel closes) mid-hover, snap back so it isn't
        // left rotated/scaled the next time it appears.
        hovered = false;
        if (rotateTarget != null)
        {
            rotateTarget.localRotation = baseRotation;
            rotateTarget.localScale = baseScale;
        }
        if (hoverImage != null) hoverImage.SetActive(false);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        hovered = true;
        if (hoverImage != null) hoverImage.SetActive(true);
        if (hoverSound != null && audioSource != null) audioSource.PlayOneShot(hoverSound, hoverVolume);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        hovered = false;
        if (hoverImage != null) hoverImage.SetActive(false);
    }

    private void Update()
    {
        if (rotateTarget == null) return;

        // Unscaled time so it still animates while the game is paused (menus run at timeScale 0).
        float dt = Time.unscaledDeltaTime;

        Quaternion targetRot = hovered ? baseRotation * Quaternion.Euler(hoverEulerAngles) : baseRotation;
        rotateTarget.localRotation = Quaternion.Slerp(rotateTarget.localRotation, targetRot, dt * rotateSpeed);

        Vector3 targetScale = hovered ? baseScale * hoverScale : baseScale;
        rotateTarget.localScale = Vector3.Lerp(rotateTarget.localScale, targetScale, dt * scaleSpeed);
    }
}
