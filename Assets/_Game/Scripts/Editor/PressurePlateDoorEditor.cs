using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds Play / Stop preview buttons to the PressurePlateDoor inspector so the door motion can be
/// tested quickly without placing any legos. The buttons drive the door locally and only work in
/// Play mode (the motion is animated in Update).
/// </summary>
[CustomEditor(typeof(PressurePlateDoor))]
public class PressurePlateDoorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Quick test", EditorStyles.boldLabel);

        var door = (PressurePlateDoor)target;

        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("▶ Play (open)", GUILayout.Height(28)))
                door.Play();
            if (GUILayout.Button("■ Stop (reset)", GUILayout.Height(28)))
                door.Stop();
            EditorGUILayout.EndHorizontal();
        }

        if (!Application.isPlaying)
            EditorGUILayout.HelpBox("Enter Play mode, then use Play / Stop to preview the door — no legos needed.",
                MessageType.Info);
    }
}
