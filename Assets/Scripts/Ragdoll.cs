using System.Collections.Generic;
using UnityEngine;

// NOTE: Resolves the previously-open "Ragdoll.IsActive property unconfirmed" caveat from the
// original handoff - the property is actually named IsRagdolling, and this session fully
// reverses the class. Builds a physics ragdoll at runtime from a fixed 12-bone Mixamo skeleton
// table (decoded below from the .cctor's Def[] array construction), with CharacterJoints linking
// each bone to its parent and auto-sized colliders (capsule/box/sphere) spanning bone-to-tip.
// Confirmed the Vector3 static-table offset layout while decoding this file's raw offset reads,
// cross-checked against the already-confirmed right=0x3c/forward=0x48 from JoystickPlayerExample:
// zero=0x00, one=0x0c, up=0x18, down=0x24, left=0x30, right=0x3c, forward=0x48, back=0x54.
//
// RE-VERIFICATION PASS: traced every branch of Dom(), MakeCollider(), Build(), Activate(),
// Deactivate(), and Teardown() against the raw pseudocode by hand - all confirmed correct,
// including some genuinely convoluted register-reuse logic (Teardown's goto-into-a-different-
// loop's-middle structure, and Activate's swap-then-copy-back "closest part" tracking) that both
// reduce exactly to what's already implemented here. See the one added NOTE inside Build() below
// for the single spot that remains genuinely ambiguous rather than fully resolved.
public class Ragdoll : MonoBehaviour
{
    public enum RagdollShape
    {
        Capsule = 0,
        Box = 1,
        Sphere = 2
    }

    private struct Def
    {
        public string bone;
        public string parent;
        public string tip;
        public RagdollShape shape;
        public float mass;
    }

    private class Part
    {
        public Transform tf;
        public Rigidbody rb;
        public Collider col;
        public Vector3 lp;
        public Quaternion lr;
    }

    [SerializeField] private float autoDespawnSeconds = 3f;
    [SerializeField] private float hitImpulse = 70f;
    [SerializeField] private float spin = 7f;
    [SerializeField] private float launchSpeed = 11f;
    [SerializeField] private float upBoost = 4.5f;
    [SerializeField] private float twistLimit = 25f;
    [SerializeField] private float swingLimit = 48f;

    private Animator anim;
    private CharacterController cc;
    private SkinnedMeshRenderer smr;
    private Dictionary<string, Transform> map;
    private bool built;
    private readonly List<Part> parts = new List<Part>();

    private bool ccWasEnabled;
    private bool despawnArmed;
    private float despawnTimer;

    public bool IsRagdolling { get; private set; }

    // NOTE: (bone, parent, tip, shape, mass) for each of the 12 ragdoll bones, decoded directly
    // from the .cctor's sequence of D(...) calls populating a compiler-embedded array. "D" and
    // "B" (the bone-lookup helper below) are the actual short names used in the binary - kept
    // as-is rather than renamed, per the convention of preserving real identifiers.
    private static readonly Def[] defs =
    {
        D("Hips", null, "Spine1", 1, 2.5f),
        D("Spine1", "Hips", "Spine2", 1, 1.8f),
        D("Spine2", "Spine1", "Neck", 1, 1.8f),
        D("Head", "Spine2", "HeadTop_End", 2, 1.0f),
        D("LeftArm", "Spine2", "LeftForeArm", 0, 0.9f),
        D("LeftForeArm", "LeftArm", "LeftHand", 0, 0.7f),
        D("RightArm", "Spine2", "RightForeArm", 0, 0.9f),
        D("RightForeArm", "RightArm", "RightHand", 0, 0.7f),
        D("LeftUpLeg", "Hips", "LeftLeg", 0, 1.5f),
        D("LeftLeg", "LeftUpLeg", "LeftFoot", 0, 1.2f),
        D("RightUpLeg", "Hips", "RightLeg", 0, 1.5f),
        D("RightLeg", "RightUpLeg", "RightFoot", 0, 1.2f),
    };

    private static Def D(string b, string p, string t, int s, float m)
    {
        return new Def { bone = b, parent = p, tip = t, shape = (RagdollShape)s, mass = m };
    }

    // NOTE: strips the "mixamorig:" bone-name prefix (confirmed literal string) from every
    // child transform and indexes the result by short name.
    private void Resolve()
    {
        anim = GetComponentInChildren<Animator>(true);
        cc = GetComponent<CharacterController>();
        smr = GetComponentInChildren<SkinnedMeshRenderer>(true);

        map = new Dictionary<string, Transform>();

        Transform[] all = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            string n = all[i].name;
            if (n != null && n.StartsWith("mixamorig:"))
            {
                map[n.Substring("mixamorig:".Length)] = all[i];
            }
        }
    }

    private Transform B(string n)
    {
        if (n == null)
        {
            return null;
        }
        Transform t;
        return map.TryGetValue(n, out t) ? t : null;
    }

    // First pass: give every bone a Rigidbody + auto-sized Collider (colliders start disabled).
    // Second pass: link every non-root bone to its parent's Rigidbody via a CharacterJoint, with
    // twist/swing limits from the serialized fields and a joint axis pointing toward the bone's
    // "tip" bone (falling back to Vector3.right if the tip is missing or too close).
    public void Build()
    {
        if (built)
        {
            return;
        }

        if (map == null)
        {
            Resolve();
        }

        Dictionary<string, Rigidbody> rbMap = new Dictionary<string, Rigidbody>();

        foreach (Def def in defs)
        {
            Transform boneT = B(def.bone);
            if (boneT == null)
            {
                continue;
            }

            Rigidbody rb = boneT.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = boneT.gameObject.AddComponent<Rigidbody>();
            }

            rb.mass = def.mass;
            rb.useGravity = true;
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.angularDamping = 0.05f;
            rb.linearDamping = 0.02f;
            rb.maxAngularVelocity = 30f;

            rbMap[def.bone] = rb;

            Collider col = MakeCollider(boneT, def);
            if (col != null)
            {
                col.enabled = false;
            }

            boneT.gameObject.layer = 2;

            Part part = new Part
            {
                tf = boneT,
                rb = rb,
                col = col,
                lp = boneT.localPosition,
                lr = boneT.localRotation
            };
            parts.Add(part);
        }

        foreach (Def def in defs)
        {
            if (def.parent == null)
            {
                continue;
            }

            Transform boneT = B(def.bone);
            if (boneT == null)
            {
                continue;
            }
            Rigidbody parentRb;
            if (!rbMap.TryGetValue(def.parent, out parentRb))
            {
                continue;
            }

            CharacterJoint joint = boneT.GetComponent<CharacterJoint>();
            if (joint == null)
            {
                joint = boneT.gameObject.AddComponent<CharacterJoint>();
            }

            joint.connectedBody = parentRb;
            joint.anchor = Vector3.zero;
            joint.enablePreprocessing = false;

            Transform tipT = B(def.tip);
            Vector3 axis;
            if (tipT != null)
            {
                Vector3 toTip = boneT.InverseTransformPoint(tipT.position);
                axis = (toTip.sqrMagnitude > 1e-10f) ? toTip.normalized : Vector3.zero;
            }
            else
            {
                axis = Vector3.right;
            }
            if (axis.sqrMagnitude < 0.0001f)
            {
                axis = Vector3.right;
            }
            joint.axis = axis;
            joint.swingAxis = (Mathf.Abs(axis.y) >= 0.9f) ? Vector3.forward : Vector3.up;

            // NOTE: raw has clear, unambiguous direct field writes here (m_Limit = -twistLimit,
            // = twistLimit, = swingLimit, = swingLimit for these four in order), which is what's
            // used below. Each is immediately followed in the decompile by a SoftJointLimit
            // set_limit(...) call whose argument is a visibly stale/reused register from an
            // unrelated earlier computation (the swingAxis threshold check a few lines up) - this
            // reads as decompiler noise from how ARM64 passes/copies value-type structs rather
            // than a second, real write, but it's the one piece of this file I can't fully
            // resolve with certainty. Flagging rather than silently treating it as settled.
            joint.lowTwistLimit = new SoftJointLimit { limit = -twistLimit };
            joint.highTwistLimit = new SoftJointLimit { limit = twistLimit };
            joint.swing1Limit = new SoftJointLimit { limit = swingLimit };
            joint.swing2Limit = new SoftJointLimit { limit = swingLimit };
        }

        built = true;
    }

    // Sizes/shapes a collider spanning from `bt` toward its "tip" bone (falls back to
    // Vector3.up if the tip is missing), picking Box/Sphere/Capsule per the Def's shape.
    private Collider MakeCollider(Transform bt, Def d)
    {
        Transform tipT = B(d.tip);
        Vector3 localOffset = (tipT != null) ? bt.InverseTransformPoint(tipT.position) : Vector3.up;
        float length = Mathf.Max(0.15f, localOffset.magnitude);

        if (d.shape == RagdollShape.Box)
        {
            BoxCollider box = bt.GetComponent<BoxCollider>();
            if (box == null)
            {
                box = bt.gameObject.AddComponent<BoxCollider>();
            }

            int dominant = Dom(localOffset);
            Vector3 size = Vector3.one * (length * 0.9f);
            if (dominant == 0) size.x = length;
            else if (dominant == 2) size.z = length;
            else size.y = length;

            box.size = size;
            box.center = localOffset * 0.5f;
            return box;
        }

        if (d.shape == RagdollShape.Sphere)
        {
            SphereCollider sphere = bt.GetComponent<SphereCollider>();
            if (sphere == null)
            {
                sphere = bt.gameObject.AddComponent<SphereCollider>();
            }

            sphere.radius = length * 0.5f;
            sphere.center = localOffset * 0.5f;
            return sphere;
        }

        CapsuleCollider capsule = bt.GetComponent<CapsuleCollider>();
        if (capsule == null)
        {
            capsule = bt.gameObject.AddComponent<CapsuleCollider>();
        }

        capsule.direction = Dom(localOffset);
        capsule.height = length;
        capsule.radius = Mathf.Max(0.05f, length * 0.22f);
        capsule.center = localOffset * 0.5f;
        return capsule;
    }

    // NOTE: dominant-axis helper. Decompiled uses the same NaN-aware three-way comparison
    // pattern seen in Mode1FirstPersonGun.ComputeBarrelTipLocal; reconstructed here with plain
    // >= comparisons (semantically equivalent for non-NaN input, which this always receives -
    // confirmed by hand-tracing every tie case: x wins ties with y, x wins ties with z, y wins
    // ties with z, matching this method's >= ordering exactly). Not called from anywhere else
    // in this paste, despite being a natural fit for that use.
    private static int Dom(Vector3 v)
    {
        float ax = Mathf.Abs(v.x);
        float ay = Mathf.Abs(v.y);
        float az = Mathf.Abs(v.z);

        if (ax >= ay && ax >= az) return 0;
        if (ay >= az) return 1;
        return 2;
    }

    // Launches every ragdoll part outward from worldHitPoint with some randomized spin, and
    // applies an extra impulse to whichever part is closest to the hit point. Also disables the
    // character's own Animator/CharacterController and ignores collisions against any other
    // CharacterController in the scene (so the ragdoll doesn't get stuck on other players).
    public bool Activate(Vector3 worldHitPoint, Vector3 worldDir, float forceScale, bool autoDespawn)
    {
        if (IsRagdolling)
        {
            return true;
        }

        Build();
        if (parts.Count == 0)
        {
            return false;
        }

        IsRagdolling = true;
        despawnArmed = autoDespawn;
        despawnTimer = autoDespawnSeconds;

        if (anim != null)
        {
            anim.enabled = false;
        }
        if (cc != null)
        {
            ccWasEnabled = cc.enabled;
            cc.enabled = false;
        }
        if (smr != null)
        {
            smr.updateWhenOffscreen = true;
        }

        CharacterController[] otherCCs = FindObjectsByType<CharacterController>(FindObjectsSortMode.None);

        Vector3 dir = (worldDir.sqrMagnitude <= 0.0001f) ? transform.forward : worldDir.normalized;
        Vector3 launchVel = (dir * launchSpeed + Vector3.up * upBoost) * forceScale;

        float closestDistSq = float.MaxValue;
        Part closestPart = null;

        foreach (Part part in parts)
        {
            if (part.col != null)
            {
                part.col.enabled = true;
                if (otherCCs != null)
                {
                    foreach (CharacterController otherCC in otherCCs)
                    {
                        if (otherCC != null && otherCC != cc)
                        {
                            Physics.IgnoreCollision(part.col, otherCC);
                        }
                    }
                }
            }

            part.rb.isKinematic = false;
            part.rb.linearVelocity = launchVel;
            part.rb.angularVelocity = new Vector3(
                Random.Range(-spin, spin),
                Random.Range(-spin, spin),
                Random.Range(-spin, spin));

            float distSq = (part.tf.position - worldHitPoint).sqrMagnitude;
            if (distSq < closestDistSq)
            {
                closestDistSq = distSq;
                closestPart = part;
            }
        }

        if (closestPart == null)
        {
            return true;
        }

        if (closestPart.rb != null)
        {
            Vector3 force = dir * hitImpulse * forceScale;
            closestPart.rb.AddForceAtPosition(force, worldHitPoint, ForceMode.Impulse);
        }

        return true;
    }

    private void Update()
    {
        if (!despawnArmed || !IsRagdolling)
        {
            return;
        }

        despawnTimer -= Time.deltaTime;
        if (despawnTimer <= 0f)
        {
            despawnArmed = false;
            gameObject.SetActive(false);
        }
    }

    public void Deactivate()
    {
        IsRagdolling = false;
        despawnArmed = false;

        if (built)
        {
            foreach (Part part in parts)
            {
                if (part.rb != null)
                {
                    part.rb.linearVelocity = Vector3.zero;
                    part.rb.angularVelocity = Vector3.zero;
                    part.rb.isKinematic = true;
                }
                if (part.col != null)
                {
                    part.col.enabled = false;
                }
                if (part.tf != null)
                {
                    part.tf.localPosition = part.lp;
                    part.tf.localRotation = part.lr;
                }
            }
        }

        if (smr != null)
        {
            smr.updateWhenOffscreen = false;
        }
        if (cc != null)
        {
            cc.enabled = ccWasEnabled;
        }
        if (anim != null)
        {
            anim.enabled = true;
        }
    }

    public void Teardown()
    {
        Deactivate();

        foreach (Part part in parts)
        {
            if (part.tf != null)
            {
                CharacterJoint joint = part.tf.GetComponent<CharacterJoint>();
                if (joint != null)
                {
                    Destroy(joint);
                }
            }
        }

        foreach (Part part in parts)
        {
            if (part.col != null)
            {
                Destroy(part.col);
            }
            if (part.rb != null)
            {
                Destroy(part.rb);
            }
        }

        parts.Clear();
        built = false;
        Destroy(this);
    }

    public static Ragdoll Trigger(Transform character, Vector3 hitPoint, Vector3 dir, float forceScale, bool autoDespawn)
    {
        if (character == null)
        {
            return null;
        }

        Ragdoll ragdoll = character.GetComponent<Ragdoll>();
        if (ragdoll == null)
        {
            ragdoll = character.gameObject.AddComponent<Ragdoll>();
        }

        return ragdoll.Activate(hitPoint, dir, forceScale, autoDespawn) ? ragdoll : null;
    }
}