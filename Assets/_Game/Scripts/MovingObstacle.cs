using System.Collections;
using UnityEngine;

public class MovingObstacle : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private Vector3 targetOffset = new(5f, 0f, 0f);
    [SerializeField] private float speed = 2f;
    [SerializeField] private float waitAtStart = 0f;
    [SerializeField] private float waitAtEnd = 0f;

    [Header("Options")]
    [SerializeField] private bool loop = true;
    [SerializeField] private bool pingPong = true;
    [SerializeField] private bool startAutomatically = true;
    [SerializeField] private AnimationCurve moveCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Rotation (optional)")]
    [SerializeField] private Vector3 rotationSpeed = Vector3.zero;

    [Header("Editor Preview")]
    [SerializeField] private bool previewInEditor;

    private Vector3 startPos;
    private Vector3 endPos;

    private void Start()
    {
        startPos = transform.position;
        endPos = startPos + targetOffset;

        if (startAutomatically)
            StartCoroutine(MovementRoutine());
    }

    private void Update()
    {
        if (rotationSpeed != Vector3.zero)
            transform.Rotate(rotationSpeed * Time.deltaTime);
    }

    private IEnumerator MovementRoutine()
    {
        do
        {
            yield return StartCoroutine(MoveToward(startPos, endPos));

            if (waitAtEnd > 0f)
                yield return new WaitForSeconds(waitAtEnd);

            if (pingPong)
            {
                yield return StartCoroutine(MoveToward(endPos, startPos));

                if (waitAtStart > 0f)
                    yield return new WaitForSeconds(waitAtStart);
            }
        }
        while (loop);
    }

    private IEnumerator MoveToward(Vector3 from, Vector3 to)
    {
        float distance = Vector3.Distance(from, to);
        if (distance < 0.001f) yield break;

        float duration = distance / speed;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = moveCurve.Evaluate(elapsed / duration);
            transform.position = Vector3.Lerp(from, to, t);
            yield return null;
        }

        transform.position = to;
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 start = Application.isPlaying ? startPos : transform.position;
        Vector3 end = start + targetOffset;

        Gizmos.color = Color.green;
        Gizmos.DrawSphere(start, 0.2f);
        Gizmos.color = Color.red;
        Gizmos.DrawSphere(end, 0.2f);
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(start, end);
    }
}
