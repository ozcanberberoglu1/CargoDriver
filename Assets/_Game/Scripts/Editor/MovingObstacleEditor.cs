using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MovingObstacle))]
public class MovingObstacleEditor : Editor
{
    private bool isPlaying;
    private float previewTime;
    private double lastEditorTime;
    private Vector3 savedPosition;
    private bool hasSavedPosition;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Preview Controls", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        if (!isPlaying)
        {
            if (GUILayout.Button("▶ Play", GUILayout.Height(30)))
                StartPreview();
        }
        else
        {
            if (GUILayout.Button("■ Stop", GUILayout.Height(30)))
                StopPreview();
        }

        if (GUILayout.Button("↺ Restart", GUILayout.Height(30)))
        {
            StopPreview();
            StartPreview();
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(5);

        if (GUILayout.Button("▶ Play All Obstacles", GUILayout.Height(25)))
        {
            PlayAllObstacles();
        }

        if (GUILayout.Button("■ Stop All Obstacles", GUILayout.Height(25)))
        {
            StopAllObstacles();
        }
    }

    private void StartPreview()
    {
        var obstacle = (MovingObstacle)target;

        if (!hasSavedPosition)
        {
            savedPosition = obstacle.transform.position;
            hasSavedPosition = true;
        }

        obstacle.transform.position = savedPosition;
        previewTime = 0f;
        lastEditorTime = EditorApplication.timeSinceStartup;
        isPlaying = true;

        EditorApplication.update += PreviewUpdate;
    }

    private void StopPreview()
    {
        isPlaying = false;
        EditorApplication.update -= PreviewUpdate;

        if (hasSavedPosition)
        {
            var obstacle = (MovingObstacle)target;
            obstacle.transform.position = savedPosition;
        }
    }

    private void PreviewUpdate()
    {
        if (!isPlaying) return;

        var obstacle = (MovingObstacle)target;
        if (obstacle == null)
        {
            StopPreview();
            return;
        }

        double now = EditorApplication.timeSinceStartup;
        float dt = (float)(now - lastEditorTime);
        lastEditorTime = now;
        previewTime += dt;

        var targetOffset = serializedObject.FindProperty("targetOffset").vector3Value;
        var speed = serializedObject.FindProperty("speed").floatValue;
        var pingPong = serializedObject.FindProperty("pingPong").boolValue;
        var waitAtStart = serializedObject.FindProperty("waitAtStart").floatValue;
        var waitAtEnd = serializedObject.FindProperty("waitAtEnd").floatValue;
        var curve = serializedObject.FindProperty("moveCurve").animationCurveValue;

        Vector3 startPos = savedPosition;
        Vector3 endPos = startPos + targetOffset;
        float distance = Vector3.Distance(startPos, endPos);
        if (distance < 0.001f) return;

        float moveDuration = distance / speed;
        float totalCycle;

        if (pingPong)
            totalCycle = moveDuration + waitAtEnd + moveDuration + waitAtStart;
        else
            totalCycle = moveDuration + waitAtEnd;

        float cycleTime = previewTime % totalCycle;

        Vector3 pos;

        if (cycleTime < moveDuration)
        {
            float t = curve.Evaluate(cycleTime / moveDuration);
            pos = Vector3.Lerp(startPos, endPos, t);
        }
        else if (cycleTime < moveDuration + waitAtEnd)
        {
            pos = endPos;
        }
        else if (pingPong && cycleTime < moveDuration + waitAtEnd + moveDuration)
        {
            float returnTime = cycleTime - moveDuration - waitAtEnd;
            float t = curve.Evaluate(returnTime / moveDuration);
            pos = Vector3.Lerp(endPos, startPos, t);
        }
        else
        {
            pos = startPos;
        }

        obstacle.transform.position = pos;
        SceneView.RepaintAll();
    }

    private void PlayAllObstacles()
    {
        StopAllObstacles();

        allPreviews.Clear();
        var all = Object.FindObjectsByType<MovingObstacle>(FindObjectsSortMode.None);
        double now = EditorApplication.timeSinceStartup;

        foreach (var obs in all)
        {
            var data = new AllPreviewData
            {
                obstacle = obs,
                savedPos = obs.transform.position,
                startTime = now
            };
            allPreviews.Add(data);
        }

        allPlaying = true;
        EditorApplication.update += AllPreviewUpdate;
    }

    private void StopAllObstacles()
    {
        allPlaying = false;
        EditorApplication.update -= AllPreviewUpdate;

        foreach (var data in allPreviews)
        {
            if (data.obstacle != null)
                data.obstacle.transform.position = data.savedPos;
        }
        allPreviews.Clear();
    }

    private static bool allPlaying;
    private static System.Collections.Generic.List<AllPreviewData> allPreviews = new();
    private static double allLastTime;

    private class AllPreviewData
    {
        public MovingObstacle obstacle;
        public Vector3 savedPos;
        public double startTime;
    }

    private static void AllPreviewUpdate()
    {
        if (!allPlaying) return;

        double now = EditorApplication.timeSinceStartup;

        foreach (var data in allPreviews)
        {
            if (data.obstacle == null) continue;

            float elapsed = (float)(now - data.startTime);
            var so = new SerializedObject(data.obstacle);

            var targetOffset = so.FindProperty("targetOffset").vector3Value;
            var speed = so.FindProperty("speed").floatValue;
            var pingPong = so.FindProperty("pingPong").boolValue;
            var waitAtStart = so.FindProperty("waitAtStart").floatValue;
            var waitAtEnd = so.FindProperty("waitAtEnd").floatValue;
            var curve = so.FindProperty("moveCurve").animationCurveValue;

            Vector3 startPos = data.savedPos;
            Vector3 endPos = startPos + targetOffset;
            float distance = Vector3.Distance(startPos, endPos);
            if (distance < 0.001f) continue;

            float moveDuration = distance / speed;
            float totalCycle = pingPong
                ? moveDuration + waitAtEnd + moveDuration + waitAtStart
                : moveDuration + waitAtEnd;

            float cycleTime = elapsed % totalCycle;
            Vector3 pos;

            if (cycleTime < moveDuration)
            {
                float t = curve.Evaluate(cycleTime / moveDuration);
                pos = Vector3.Lerp(startPos, endPos, t);
            }
            else if (cycleTime < moveDuration + waitAtEnd)
            {
                pos = endPos;
            }
            else if (pingPong && cycleTime < moveDuration + waitAtEnd + moveDuration)
            {
                float returnTime = cycleTime - moveDuration - waitAtEnd;
                float t = curve.Evaluate(returnTime / moveDuration);
                pos = Vector3.Lerp(endPos, startPos, t);
            }
            else
            {
                pos = startPos;
            }

            data.obstacle.transform.position = pos;
        }

        SceneView.RepaintAll();
    }

    private void OnDisable()
    {
        if (isPlaying)
            StopPreview();
    }
}
