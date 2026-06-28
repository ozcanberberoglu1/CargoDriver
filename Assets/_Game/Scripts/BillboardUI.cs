using UnityEngine;

public class BillboardUI : MonoBehaviour
{
    private Camera cam;

    private void LateUpdate()
    {
        if (cam == null || !cam.isActiveAndEnabled)
            cam = Camera.main ?? FindAnyObjectByType<Camera>();

        if (cam == null) return;

        transform.forward = cam.transform.forward;
    }
}
