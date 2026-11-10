using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Meccha > Measure Pose Hit Boxes: the boxes shots are tested against (BodyHitBoxes on the server
// and for the crosshair), measured from the bodies the game shows. Each body is posed through its
// own Animator for every server pose id (0 = none, 1-9 = the pose picker's slots,
// PoseSelectorUI.AnimatorPoseFor), its skinned meshes are baked over the whole animation, and the
// box around every vertex is written down in the body's own axes, from its feet, at full size.
//
// Run it again if the poses or the bodies change, and paste the arrays into BodyHitBoxes.cs.
//
// Batch mode (Unity closed):
//   Unity.exe -batchmode -projectPath <project> -executeMethod PoseHitBoxMeasurer.MeasureBatch
// writes Logs/PoseHitBoxes.txt (Temp/ is deleted when Unity quits).
public static class PoseHitBoxMeasurer
{
    private const string MenuPath = "Meccha/Measure Pose Hit Boxes";

    // In model id order (CharacterSelect's). The Bot prefab is the first body under another name.
    private static readonly string[] Models =
    {
        "Assets/Resources/Players/Player.prefab",
        "Assets/Resources/Players/Player2.prefab",
    };

    private const int PoseIds = 10;              // server pose ids 0..9
    private const float SettleSeconds = 1.5f;    // long enough for any transition into a pose
    private const float SampleSeconds = 3f;      // a whole idle and run cycle
    private const float Step = 1f / 30f;
    private static readonly int PoseHash = Animator.StringToHash("Pose");
    private static readonly int SpeedHash = Animator.StringToHash("Speed");

    public static string OutputPath => Path.Combine(Directory.GetCurrentDirectory(), "Logs", "PoseHitBoxes.txt");

    [MenuItem(MenuPath)]
    public static void MeasureMenu()
    {
        string report = Measure();
        Debug.Log(report);
        EditorUtility.DisplayDialog("Pose hit boxes", "Written to " + OutputPath, "OK");
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
        var sb = new StringBuilder();
        foreach (string path in Models)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new FileNotFoundException("Body prefab not found", path);

            sb.AppendLine("// " + Path.GetFileNameWithoutExtension(path));
            sb.AppendLine("new float[]");
            sb.AppendLine("{");
            Bounds[] boxes = MeasureBody(prefab);
            for (int id = 0; id < PoseIds; id++)
            {
                Vector3 min = boxes[id].min, max = boxes[id].max;
                sb.AppendLine($"    {Down(min.x)}, {Up(max.x)},   {Down(min.y)}, {Up(max.y)},   {Down(min.z)}, {Up(max.z)},   // pose {id}");
            }
            sb.AppendLine("},");
        }

        string report = sb.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
        File.WriteAllText(OutputPath, report);
        return report;
    }

    // Rounded outwards, so that every vertex stays inside.
    private static string Down(float v) => (Mathf.Floor(v * 100f) / 100f).ToString("0.00", CultureInfo.InvariantCulture) + "f";
    private static string Up(float v) => (Mathf.Ceil(v * 100f) / 100f).ToString("0.00", CultureInfo.InvariantCulture) + "f";

    private static Bounds[] MeasureBody(GameObject prefab)
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
            float feetY = cc != null ? cc.center.y - cc.height * 0.5f : 0f;

            var boxes = new Bounds[PoseIds];
            for (int id = 0; id < PoseIds; id++)
            {
                bool any = false;
                Play(anim, id, 0f, body, feetY, ref boxes[id], ref any);

                // Standing still and running are one pose: the body may be doing either.
                if (id == 0) Play(anim, id, 1f, body, feetY, ref boxes[id], ref any);
                if (!any) throw new System.InvalidOperationException("The body measured empty; is the mesh readable?");
            }

            return boxes;
        }
        finally
        {
            Object.DestroyImmediate(body);
        }
    }

    private static void Play(Animator anim, int id, float speed, GameObject body, float feetY, ref Bounds box, ref bool any)
    {
        anim.Rebind();
        anim.Update(0f);
        anim.SetFloat(SpeedHash, speed);
        anim.SetInteger(PoseHash, PoseSelectorUI.AnimatorPoseFor(id));

        for (float t = 0f; t < SettleSeconds; t += Step) anim.Update(Step);
        for (float t = 0f; t < SampleSeconds; t += Step)
        {
            anim.Update(Step);
            Grow(body, feetY, ref box, ref any);
        }
    }

    // Every vertex of the posed meshes, from the feet (the point the server tracks) in the body's axes.
    private static void Grow(GameObject body, float feetY, ref Bounds box, ref bool any)
    {
        var baked = new Mesh();
        Vector3 root = body.transform.position;

        foreach (SkinnedMeshRenderer smr in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!smr.enabled || smr.sharedMesh == null || !smr.gameObject.activeInHierarchy) continue;
            smr.BakeMesh(baked, true);
            Matrix4x4 toWorld = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
            foreach (Vector3 v in baked.vertices)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(v) - root;
                w.y -= feetY;
                if (!any) { box = new Bounds(w, Vector3.zero); any = true; } else box.Encapsulate(w);
            }
        }

        Object.DestroyImmediate(baked);
    }
}
