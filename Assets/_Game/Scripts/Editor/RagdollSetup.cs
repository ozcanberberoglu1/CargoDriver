using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click ragdoll builder for the Mixamo-rigged character. Select the character root (Toy1),
/// then Tools ▸ CargoDriver ▸ Setup Ragdoll — it adds a kinematic Rigidbody + a disabled collider
/// + a CharacterJoint to each ragdoll bone and drops a CharacterRagdoll controller on the root.
/// CharacterRagdoll flips the bodies/colliders on only while the character is knocked down.
///
/// Collider sizes are estimated from the bone lengths; tweak them in the inspector if a limb looks
/// too fat/thin. Run it on the Toy1 prefab (open the prefab, select the root, run the tool).
/// </summary>
public static class RagdollSetup
{
    private const float TotalMass = 20f;

    [MenuItem("Tools/CargoDriver/Setup Ragdoll on Selected")]
    private static void Setup()
    {
        GameObject go = Selection.activeGameObject;
        if (go == null)
        {
            EditorUtility.DisplayDialog("Ragdoll", "Select the character root (Toy1) first.", "OK");
            return;
        }

        Transform root = go.transform;
        Undo.RegisterFullObjectHierarchyUndo(go, "Setup Ragdoll");

        Transform hips = Find(root, "Hips"), spine = Find(root, "Spine");
        Transform neck = Find(root, "Neck"), head = Find(root, "Head"), headTop = Find(root, "HeadTop");
        Transform lUpLeg = Find(root, "LeftUpLeg"), lLeg = Find(root, "LeftLeg"), lFoot = Find(root, "LeftFoot");
        Transform rUpLeg = Find(root, "RightUpLeg"), rLeg = Find(root, "RightLeg"), rFoot = Find(root, "RightFoot");
        Transform lArm = Find(root, "LeftArm"), lFore = Find(root, "LeftForeArm"), lHand = Find(root, "LeftHand");
        Transform rArm = Find(root, "RightArm"), rFore = Find(root, "RightForeArm"), rHand = Find(root, "RightHand");

        if (hips == null || spine == null || head == null)
        {
            EditorUtility.DisplayDialog("Ragdoll",
                "Couldn't find mixamorig:Hips / Spine / Head under the selection. Is this the character root?", "OK");
            return;
        }

        Rigidbody rbHips = MakeBody(hips, TotalMass * 0.30f);
        Capsule(hips, spine, 0.28f);

        Rigidbody rbSpine = MakeBody(spine, TotalMass * 0.25f);
        Capsule(spine, neck != null ? neck : head, 0.28f);
        Joint(spine, rbHips);

        Rigidbody rbHead = MakeBody(head, TotalMass * 0.06f);
        Sphere(head, headTop, 0.5f);
        Joint(head, rbSpine);

        Limb(lUpLeg, lLeg, rbHips, 0.22f, TotalMass * 0.10f);
        Limb(lLeg, lFoot, GetBody(lUpLeg), 0.20f, TotalMass * 0.06f);
        Limb(rUpLeg, rLeg, rbHips, 0.22f, TotalMass * 0.10f);
        Limb(rLeg, rFoot, GetBody(rUpLeg), 0.20f, TotalMass * 0.06f);

        Limb(lArm, lFore, rbSpine, 0.18f, TotalMass * 0.04f);
        Limb(lFore, lHand, GetBody(lArm), 0.16f, TotalMass * 0.03f);
        Limb(rArm, rFore, rbSpine, 0.18f, TotalMass * 0.04f);
        Limb(rFore, rHand, GetBody(rArm), 0.16f, TotalMass * 0.03f);

        CharacterRagdoll ragdoll = go.GetComponent<CharacterRagdoll>();
        if (ragdoll == null) ragdoll = Undo.AddComponent<CharacterRagdoll>(go);
        var so = new SerializedObject(ragdoll);
        so.FindProperty("hips").objectReferenceValue = hips;
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(go);
        Debug.Log($"[Ragdoll] Setup complete on {go.name}. Fine-tune collider sizes if needed.");
    }

    private static void Limb(Transform bone, Transform child, Rigidbody parent, float radiusRatio, float mass)
    {
        if (bone == null || child == null) return;
        MakeBody(bone, mass);
        Capsule(bone, child, radiusRatio);
        if (parent != null) Joint(bone, parent);
    }

    private static Rigidbody GetBody(Transform t) => t != null ? t.GetComponent<Rigidbody>() : null;

    private static Rigidbody MakeBody(Transform bone, float mass)
    {
        Rigidbody rb = bone.GetComponent<Rigidbody>();
        if (rb == null) rb = Undo.AddComponent<Rigidbody>(bone.gameObject);
        rb.mass = mass;
        rb.linearDamping = 0.05f;
        rb.angularDamping = 0.05f;
        rb.isKinematic = true; // CharacterRagdoll turns physics on only while knocked down
        return rb;
    }

    private static void Capsule(Transform bone, Transform child, float radiusRatio)
    {
        if (bone == null || child == null) return;
        CapsuleCollider cap = bone.GetComponent<CapsuleCollider>();
        if (cap == null) cap = Undo.AddComponent<CapsuleCollider>(bone.gameObject);

        Vector3 childLocal = bone.InverseTransformPoint(child.position);
        float length = childLocal.magnitude;

        float ax = Mathf.Abs(childLocal.x), ay = Mathf.Abs(childLocal.y), az = Mathf.Abs(childLocal.z);
        int axis = (ax >= ay && ax >= az) ? 0 : (az >= ax && az >= ay) ? 2 : 1;

        cap.direction = axis;
        cap.height = length;
        cap.radius = Mathf.Max(0.01f, length * radiusRatio);
        cap.center = childLocal * 0.5f;
        cap.enabled = false;
    }

    private static void Sphere(Transform bone, Transform top, float ratio)
    {
        if (bone == null) return;
        SphereCollider s = bone.GetComponent<SphereCollider>();
        if (s == null) s = Undo.AddComponent<SphereCollider>(bone.gameObject);
        s.radius = top != null ? Vector3.Distance(bone.position, top.position) * ratio : 0.1f;
        s.center = Vector3.zero;
        s.enabled = false;
    }

    private static void Joint(Transform bone, Rigidbody parent)
    {
        if (bone == null || parent == null) return;
        CharacterJoint j = bone.GetComponent<CharacterJoint>();
        if (j == null) j = Undo.AddComponent<CharacterJoint>(bone.gameObject);
        j.connectedBody = parent;
        j.enableProjection = true;

        SoftJointLimit low = j.lowTwistLimit; low.limit = -20f; j.lowTwistLimit = low;
        SoftJointLimit high = j.highTwistLimit; high.limit = 20f; j.highTwistLimit = high;
        SoftJointLimit s1 = j.swing1Limit; s1.limit = 30f; j.swing1Limit = s1;
        SoftJointLimit s2 = j.swing2Limit; s2.limit = 30f; j.swing2Limit = s2;
    }

    private static Transform Find(Transform root, string suffix)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "mixamorig:" + suffix || t.name.EndsWith(":" + suffix) || t.name == suffix)
                return t;
        }
        return null;
    }
}
