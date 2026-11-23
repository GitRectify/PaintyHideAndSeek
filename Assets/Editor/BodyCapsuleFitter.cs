using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Meccha > Fit Body Capsules (2026-11-23): fits each character's CharacterController to its body.
// Each body is posed through its own Animator (standing and running, lying down, every struck
// pose), its skinned meshes are baked over the animation, and the highest vertex above the
// capsule's bottom (the feet) is written into the prefab's BodyCapsule, which keeps the capsule
// that tall in play. The standing height also becomes the controller's own height, with the bottom
// left where it is: the feet are what placement, the server and the shot boxes go by.
//
// Run it again when a character's mesh or animations change.
//
// Batch mode (Unity closed):
//   Unity.exe -batchmode -projectPath <project> -executeMethod BodyCapsuleFitter.FitBatch
// writes Logs/BodyCapsules.txt.
public static class BodyCapsuleFitter
{
    private const string MenuPath = "Meccha/Fit Body Capsules";

    private static readonly string[] Bodies =
    {
        "Assets/Resources/Players/Player.prefab",
        "Assets/Resources/Players/Player2.prefab",
        "Assets/Resources/GameObjects/Bot.prefab",       // the bots and other players online
    };

    private const int PoseValues = 10;           // the Animator's Pose values 0..9
    private const float SettleSeconds = 1.5f;    // long enough for any transition into a pose
    private const float SampleSeconds = 3f;      // a whole idle and run cycle
    private const float Step = 1f / 30f;
    private static readonly int PoseHash = Animator.StringToHash("Pose");
    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int NamHash = Animator.StringToHash("Nam");

    public static string OutputPath => Path.Combine(Directory.GetCurrentDirectory(), "Logs", "BodyCapsules.txt");

    [MenuItem(MenuPath)]
    public static void FitMenu()
    {
        string report = Fit();
        Debug.Log(report);
        EditorUtility.DisplayDialog("Body capsules", report, "OK");
    }

    public static void FitBatch()
    {
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Debug.Log(Fit());
            EditorApplication.Exit(0);
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    public static string Fit()
    {
        var sb = new StringBuilder();
        foreach (string path in Bodies)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new FileNotFoundException("Body prefab not found", path);

            Measure(prefab, out float standing, out float lying, out float[] poses);

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                CharacterController cc = root.GetComponent<CharacterController>();
                if (cc == null) throw new MissingComponentException(path + " has no CharacterController");

                float bottom = cc.center.y - cc.height * 0.5f;
                float before = cc.height;
                cc.height = standing;
                cc.center = new Vector3(cc.center.x, bottom + standing * 0.5f, cc.center.z);

                BodyCapsule capsule = root.GetComponent<BodyCapsule>();
                if (capsule == null) capsule = root.AddComponent<BodyCapsule>();
                capsule.standingHeight = standing;
                capsule.lyingHeight = lying;
                capsule.poseHeights = poses;

                PrefabUtility.SaveAsPrefabAsset(root, path);

                sb.AppendLine($"{Path.GetFileNameWithoutExtension(path)}: standing {F(standing)} (was {F(before)}), radius {F(cc.radius)}, lying {F(lying)}, poses {string.Join(" ", System.Array.ConvertAll(poses, F))}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        string report = sb.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
        File.WriteAllText(OutputPath, report);
        return report;
    }

    private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // Rounded up, so that every vertex is under the top.
    private static float Up(float v) => Mathf.Ceil(v * 100f) / 100f;

    private static void Measure(GameObject prefab, out float standing, out float lying, out float[] poses)
    {
        GameObject body = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        body.SetActive(true);
        try
        {
            // Only the rig and its meshes matter: the game's scripts would move the body.
            foreach (MonoBehaviour m in body.GetComponentsInChildren<MonoBehaviour>(true)) m.enabled = false;
            foreach (Rigidbody rb in body.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;

            Animator anim = body.GetComponentInChildren<Animator>(true);
            if (anim == null) throw new MissingComponentException("The body prefab has no Animator");
            anim.enabled = true;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.applyRootMotion = false;

            CharacterController cc = body.GetComponent<CharacterController>();
            float scale = Mathf.Max(0.0001f, body.transform.lossyScale.y);
            float feet = (cc.center.y - cc.height * 0.5f) * scale;

            standing = Mathf.Max(Top(anim, body, feet, 0, false, 0f), Top(anim, body, feet, 0, false, 1f)) / scale;
            lying = Mathf.Max(Top(anim, body, feet, 0, true, 0f), Top(anim, body, feet, 0, true, 1f)) / scale;
            poses = new float[PoseValues];
            for (int pose = 1; pose < PoseValues; pose++) poses[pose] = Up(Top(anim, body, feet, pose, false, 0f) / scale);

            standing = Up(standing);
            lying = Up(lying);
        }
        finally
        {
            Object.DestroyImmediate(body);
        }
    }

    // The highest vertex above the feet while the body plays this.
    private static float Top(Animator anim, GameObject body, float feet, int pose, bool lyingDown, float speed)
    {
        anim.Rebind();
        anim.Update(0f);
        anim.SetFloat(SpeedHash, speed);
        anim.SetInteger(PoseHash, pose);
        anim.SetBool(NamHash, lyingDown);
        for (float t = 0f; t < SettleSeconds; t += Step) anim.Update(Step);

        float top = float.MinValue;
        var baked = new Mesh();
        for (float t = 0f; t < SampleSeconds; t += Step)
        {
            anim.Update(Step);
            float rootY = body.transform.position.y;
            foreach (SkinnedMeshRenderer smr in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!smr.enabled || smr.sharedMesh == null || !smr.gameObject.activeInHierarchy) continue;
                smr.BakeMesh(baked, true);
                Matrix4x4 toWorld = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                foreach (Vector3 v in baked.vertices) top = Mathf.Max(top, toWorld.MultiplyPoint3x4(v).y - rootY - feet);
            }
        }
        Object.DestroyImmediate(baked);

        if (top == float.MinValue) throw new System.InvalidOperationException("The body measured empty; is the mesh readable?");
        return top;
    }
}
