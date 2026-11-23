using UnityEngine;

// A character's CharacterController capsule, fitted to the body it carries (2026-11-23):
//
//   * Standing, it is as tall as the character: from the feet to the top of the head, measured
//     from the character's own mesh (Meccha > Fit Body Capsules writes the heights below into the
//     Player, Player2 and Bot prefabs). The heights are in the body's own units, so the transform's
//     scale applies on top: hiders are half size in every mode, and so is their capsule.
//   * Lying down (the seeker's lie-down button) or holding a low pose (a hider's struck pose), it
//     is as tall as that pose, so the body fits under tables and beds where the pose does.
//   * Standing up, it grows back only as far as there is room above the head: a body under a
//     table stays low until it is out from under it. The seeker's own lie-down button asks first
//     (CanStand) and keeps them lying down when there is no room.
//
// The bottom of the capsule never moves: it is the feet, the point placement, the server and the
// shot boxes go by (RoomSetup.PivotForFeet, NetEntityView.FeetPosition, BodyHitBoxes). Only the
// top changes, eased over ResizeSeconds. A climbing body keeps its standing capsule (WallClimber
// works from it), and a body whose controller is switched off (other players online, a ragdoll,
// the Home showcase) is left alone.
[DisallowMultipleComponent]
public sealed class BodyCapsule : MonoBehaviour
{
    [Tooltip("Feet to the top of the head, standing or running, in the body's own units (the transform's scale applies).")]
    public float standingHeight;

    [Tooltip("Feet to the highest point of the body lying down (the seeker's lie-down button). 0 = as standing.")]
    public float lyingHeight;

    [Tooltip("Feet to the highest point of the body in each struck pose, by the Animator's Pose value (index 0 is no pose). 0 = as standing.")]
    public float[] poseHeights = new float[0];

    private const float ResizeSeconds = 0.2f;    // standing to lying down, or back
    private const float Clearance = 0.05f;       // kept free above the head, world units
    private const float MinAboveRadius = 0.1f;   // the capsule stays a capsule: at least 2 radii and this

    private CharacterController cc;
    private BodyAnimation body;
    private float bottom;                        // the capsule's bottom (the feet), local units
    private int nextBodyLook;
    private readonly RaycastHit[] hits = new RaycastHit[16];

    /// <summary>Whether this body, lying down or in a low pose, has room above it to stand up.</summary>
    public static bool CanStand(Transform character)
    {
        BodyCapsule capsule = character != null ? character.GetComponent<BodyCapsule>() : null;
        return capsule == null || capsule.RoomToStand();
    }

    public float StandingHeight => standingHeight > 0f ? standingHeight : (cc != null ? cc.height : 0f);

    private float MinHeight => cc.radius * 2f + MinAboveRadius;

    private void Awake()
    {
        cc = GetComponent<CharacterController>();
        if (cc == null)
        {
            enabled = false;
            return;
        }

        bottom = cc.center.y - cc.height * 0.5f;
    }

    private void LateUpdate()
    {
        if (cc == null || !cc.enabled) return;

        float now = cc.height;
        float target = TargetHeight();
        if (target > now + 0.001f) target = Mathf.Min(target, now + Headroom(now, target - now));
        if (Mathf.Abs(target - now) < 0.001f) return;

        float rate = Mathf.Max(0.01f, StandingHeight - MinHeight) / ResizeSeconds;
        SetHeight(Mathf.MoveTowards(now, target, rate * Time.deltaTime));
    }

    private bool RoomToStand()
    {
        if (cc == null) return true;
        float missing = StandingHeight - cc.height;
        return missing <= 0.001f || Headroom(cc.height, missing) >= missing - 0.001f;
    }

    // How tall the body is now: its pose's height, between a capsule's least and standing.
    private float TargetHeight()
    {
        float standing = StandingHeight;
        if (body == null && Time.frameCount >= nextBodyLook)
        {
            // BodyAnimation is added with the first thing said about the body.
            body = GetComponentInChildren<BodyAnimation>(true);
            nextBodyLook = Time.frameCount + 15;
        }
        if (body == null || body.Climbing) return standing;

        float h = 0f;
        if (body.Prone) h = lyingHeight;
        else
        {
            int pose = body.ShownPose;
            if (pose > 0 && poseHeights != null && pose < poseHeights.Length) h = poseHeights[pose];
        }

        return h > 0f ? Mathf.Clamp(h, Mathf.Min(MinHeight, standing), standing) : standing;
    }

    // How far (local units, up to wanted) the capsule can grow from height before its top meets
    // something that is not a character: the top sphere swept up along the body's axis.
    private float Headroom(float height, float wanted)
    {
        float scale = Mathf.Max(0.0001f, transform.lossyScale.y);
        float radius = cc.radius * Mathf.Max(transform.lossyScale.x, transform.lossyScale.z);
        Vector3 top = transform.TransformPoint(new Vector3(cc.center.x, bottom + height - cc.radius, cc.center.z));
        Vector3 up = transform.up;
        float reach = wanted * scale + Clearance;

        int n = Physics.SphereCastNonAlloc(top, radius * 0.95f, up, hits, reach, ~0, QueryTriggerInteraction.Ignore);
        float free = reach;
        for (int i = 0; i < n; i++)
        {
            Collider col = hits[i].collider;
            if (col == null || col.transform.IsChildOf(transform) || Bullet.IsCharacter(col)) continue;
            free = Mathf.Min(free, hits[i].distance);
        }

        return Mathf.Max(0f, (free - Clearance) / scale);
    }

    private void SetHeight(float height)
    {
        // The step first: a controller whose step is above its height stops working.
        float most = MaxStep(cc, height);
        if (cc.stepOffset > most) cc.stepOffset = most;

        Vector3 c = cc.center;
        cc.height = height;
        cc.center = new Vector3(c.x, bottom + height * 0.5f, c.z);
    }

    /// <summary>
    /// The highest step offset this controller may be given. Unity refuses a step offset above the
    /// controller's scaled height, and a controller refused when it is switched on stops working
    /// ("Move called on inactive controller") until it is switched on again with a valid one. A
    /// full-size body can be halved (made a hider) while it is still lying down or posing, so it
    /// keeps under half of that; standing, that is still well above the 4-unit stair step.
    /// </summary>
    public static float MaxStep(CharacterController cc) => MaxStep(cc, cc.height);

    private static float MaxStep(CharacterController cc, float height)
    {
        float scale = Mathf.Max(0.0001f, cc.transform.lossyScale.y);
        float scaledHeight = scale * height;
        float limit = scale > 0.75f ? scaledHeight * 0.5f : scaledHeight;
        return Mathf.Max(0.1f, limit - 0.5f);
    }
}
