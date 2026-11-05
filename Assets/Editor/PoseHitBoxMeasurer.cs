using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Meccha > Measure Pose Hit Boxes: the server's per-pose hit box (GameTuning.PoseHitHeightScale /
// PoseHitRadiusScale), measured from the body the game shows. For each server pose id (0 = none,
// 1-9 = the pose picker's slots, PoseSelectorUI.AnimatorPoseFor) the online body prefab is posed
// through its own Animator "Pose" parameter, its skinned meshes are baked, and the posed mesh's
// height above the standing feet (the body's pivot is at its centre, not its feet) and horizontal
// reach from the body's centre line are compared with standing.
// Single Play tests a hider's mesh bounds, which change with the pose; these ratios make the
// server's box do the same.
//
// Batch mode (Unity closed):
//   Unity.exe -batchmode -projectPath <project> -executeMethod PoseHitBoxMeasurer.MeasureBatch
// writes Logs/PoseHitBoxes.txt with the two C# arrays for the server's GameTuning (Temp/ is
// deleted when Unity quits).
public static class PoseHitBoxMeasurer
{
    private const string MenuPath = "Meccha/Measure Pose Hit Boxes";
    private const string BodyPrefab = "Assets/Resources/GameObjects/Bot.prefab";
    private const int PoseIds = 10;              // server pose ids 0..9
    private const float SettleSeconds = 3f;      // long enough for any transition into a pose
    private const float Step = 1f / 30f;
    private static readonly int PoseHash = Animator.StringToHash("Pose");
    private static readonly int SpeedHash = Animator.StringToHash("Speed");

    public static string OutputPath => Path.Combine(Directory.GetCurrentDirectory(), "Logs", "PoseHitBoxes.txt");

    [MenuItem(MenuPath)]
    public static void MeasureMenu()
    {
        string report = Measure();
        Debug.Log(report);
        EditorUtility.DisplayDialog("Pose hit boxes", "Written to " + OutputPath + "\n\n" + report, "OK");
    }

    public static void MeasureBatch()
    {
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Debug.Log(Measure());
            EditorApplication.Exit(0);
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    public static string Measure()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefab);
        if (prefab == null) throw new FileNotFoundException("Body prefab not found", BodyPrefab);

        GameObject body = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        body.SetActive(true);
        try
        {
            // Only the rig and its meshes matter: the AI and physics would move the body.
            foreach (MonoBehaviour m in body.GetComponentsInChildren<MonoBehaviour>(true)) m.enabled = false;
            foreach (Rigidbody rb in body.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;

            Animator anim = body.GetComponentInChildren<Animator>(true);
            if (anim == null) throw new MissingComponentException("The body prefab has no Animator");
            anim.enabled = true;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.applyRootMotion = false;

            var tops = new float[PoseIds];
            var reaches = new float[PoseIds];
            var bounds = new Bounds[PoseIds];

            for (int id = 0; id < PoseIds; id++)
            {
                int animatorPose = PoseSelectorUI.AnimatorPoseFor(id);
                anim.Rebind();
                anim.Update(0f);
                anim.SetFloat(SpeedHash, 0f);
                anim.SetInteger(PoseHash, animatorPose);
                for (float t = 0f; t < SettleSeconds; t += Step) anim.Update(Step);

                MeasureMesh(body, out bounds[id], out reaches[id]);
                tops[id] = bounds[id].max.y;
            }

            // The server's box stands on the player's position (the feet), so a pose's height is
            // its top above where the standing body's feet are.
            float feet = bounds[0].min.y;
            var heights = new float[PoseIds];
            for (int id = 0; id < PoseIds; id++) heights[id] = Mathf.Max(0f, tops[id] - feet);

            if (heights[0] <= 0.01f || reaches[0] <= 0.01f)
                throw new System.InvalidOperationException("The standing body measured empty; is the mesh readable?");

            var sb = new StringBuilder();
            sb.AppendLine("pose id  animator Pose  height  reach   posed mesh bounds (prefab units, from the body's pivot)");
            for (int id = 0; id < PoseIds; id++)
                sb.AppendLine($"{id,7}  {PoseSelectorUI.AnimatorPoseFor(id),13}  {heights[id],6:F2}  {reaches[id],5:F2}   min {bounds[id].min:F2} max {bounds[id].max:F2}");

            string Ratios(float[] v) =>
                "{ " + string.Join(", ", System.Array.ConvertAll(v, x => (x / v[0]).ToString("0.00", CultureInfo.InvariantCulture) + "f")) + " }";

            sb.AppendLine();
            sb.AppendLine("public static readonly float[] PoseHitHeightScale = " + Ratios(heights) + ";");
            sb.AppendLine("public static readonly float[] PoseHitRadiusScale = " + Ratios(reaches) + ";");

            string report = sb.ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllText(OutputPath, report);
            return report;
        }
        finally
        {
            Object.DestroyImmediate(body);
        }
    }

    // The posed mesh's bounds around the body's pivot, and its furthest horizontal reach from the
    // body's centre line.
    private static void MeasureMesh(GameObject body, out Bounds bounds, out float reach)
    {
        Vector3 root = body.transform.position;
        float far = 0f;
        bounds = new Bounds();
        bool any = false;
        var baked = new Mesh();

        foreach (SkinnedMeshRenderer smr in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!smr.enabled || smr.sharedMesh == null) continue;
            smr.BakeMesh(baked, true);
            Matrix4x4 toWorld = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
            foreach (Vector3 v in baked.vertices)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(v) - root;
                if (!any) { bounds = new Bounds(w, Vector3.zero); any = true; } else bounds.Encapsulate(w);
                float h = Mathf.Max(Mathf.Abs(w.x), Mathf.Abs(w.z));
                if (h > far) far = h;
            }
        }

        Object.DestroyImmediate(baked);
        reach = far;
    }
}
