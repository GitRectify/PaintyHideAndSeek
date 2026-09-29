using UnityEngine;

public class BotLook : MonoBehaviour
{
    public float turnSpeed = 6f;
    public float neckShare = 0.45f;
    public float maxYaw = 68f;
    public float maxPitch = 38f;
    public float looseAimFactor = 0.55f;
    public float looseAimMaxAngle = 45f;
    public float aimMaxAngle = 80f;
    public float aimSpeed = 12f;
    public float looseAimSpeed = 5f;

    private Transform root;
    private Transform head;
    private Transform neck;
    private Transform rightArm;
    private Transform gun;

    public bool _hasTarget;
    public Vector3 _lookPoint;

    public bool _aiming;
    public Vector3 _aimPoint;

    public bool _looseAim;
    public Vector3 _loosePoint;

    private float _yaw;
    private float _pitch;

    private Quaternion _armAim = Quaternion.identity;

    private void Awake()
    {
        root = transform;

        // Bone names confirmed exact via string literals: "mixamorig:Head", "mixamorig:Neck",
        // "mixamorig:RightArm" — matches a standard Mixamo rig.
        var allTransforms = GetComponentsInChildren<Transform>(true);
        foreach (var t in allTransforms)
        {
            if (t == null) continue;

            if (head == null && t.name == "mixamorig:Head")
            {
                head = t;
            }
            else if (neck == null && t.name == "mixamorig:Neck")
            {
                neck = t;
            }
            else if (rightArm == null && t.name == "mixamorig:RightArm")
            {
                rightArm = t;
            }
        }
    }

    public void LookAt(Vector3 worldPoint)
    {
        _lookPoint = worldPoint;
        _hasTarget = true;
    }

    public void LookForward()
    {
        _hasTarget = false;
        _aiming = false;
        _looseAim = false;
    }

    public void AimAt(Vector3 worldPoint)
    {
        _aimPoint = worldPoint;
        _aiming = true;
        _looseAim = false;
    }

    public void AimLoose(Vector3 worldPoint)
    {
        _loosePoint = worldPoint;
        _aiming = false;
        _looseAim = true;
    }

    public void AimRelax()
    {
        _aiming = false;
        _looseAim = false;
    }

    private void LateUpdate()
    {
        if (head == null || root == null) return;

        Vector3 rootFwdFlat = new Vector3(root.forward.x, 0f, root.forward.z);
        Vector3 baseDir = rootFwdFlat.sqrMagnitude >= 0.001f ? rootFwdFlat.normalized : Vector3.forward;

        float targetYaw = 0f;
        float targetPitch = 0f;

        if (_hasTarget)
        {
            Vector3 toTarget = _lookPoint - head.position;

            if (toTarget.sqrMagnitude > 0.04f)
            {
                Vector3 dir = toTarget.normalized;

                // Yaw/pitch computed relative to the root's flat forward/right basis.
                // (Verified term-by-term against the raw decompile — exact match.)
                float x = baseDir.z * dir.x + 0f * dir.y - baseDir.x * dir.z;
                float z = baseDir.z * dir.z + baseDir.x * dir.x + 0f /* baseDir.y */ * dir.y;
                float yaw = Mathf.Atan2(x, z) * Mathf.Rad2Deg;

                float horizDist = Mathf.Max(0.01f, Mathf.Sqrt(x * x + z * z));
                float pitch = Mathf.Atan2(dir.y, horizDist) * Mathf.Rad2Deg;

                targetYaw = Mathf.Clamp(yaw, -maxYaw, maxYaw);
                targetPitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);
            }
        }

        float t = 1f - Mathf.Exp(-turnSpeed * Time.deltaTime);
        t = Mathf.Clamp01(t);

        _yaw += (targetYaw - _yaw) * t;
        _pitch += (targetPitch - _pitch) * t;

        Vector3 right = new Vector3(baseDir.z, 0f, -baseDir.x);

        if (neck != null)
        {
            ApplyLook(neck, _yaw * neckShare, _pitch * neckShare, right);
        }

        float headShare = 1f - neckShare;
        ApplyLook(head, _yaw * headShare, _pitch * headShare, right);

        AimArm();
    }

    private static void ApplyLook(Transform bone, float yawDeg, float pitchDeg, Vector3 right)
    {
        if (bone == null) return;

        Quaternion yawRot = Quaternion.AngleAxis(yawDeg, Vector3.up);
        Quaternion pitchRot = Quaternion.AngleAxis(-pitchDeg, right);

        // Combine yaw then pitch, then apply on top of the bone's current local rotation.
        // (Verified the "w" component algebraically against the raw decompile — matches this
        // multiplication order exactly.)
        Quaternion combined = yawRot * pitchRot;
        bone.rotation = combined * bone.rotation;
    }

    private void AimArm()
    {
        if (rightArm == null) return;

        // CORRECTION: the gun-search name is confirmed as "BotGun" via its string literal — the
        // earlier draft guessed "Cards" (visually similar to something in this codebase, but not
        // the actual confirmed string).
        if (gun == null)
        {
            var children = GetComponentsInChildren<Transform>(true);
            foreach (var t in children)
            {
                if (t == null) continue;
                if (t.name == "BotGun")
                {
                    gun = t;
                    break;
                }
            }
        }

        bool aiming = _aiming;

        Vector3 targetPoint = aiming ? _aimPoint : _loosePoint;
        float maxAngle = aiming ? aimMaxAngle : looseAimMaxAngle;
        float speed = aiming ? aimSpeed : looseAimSpeed;
        float blend = aiming ? 1f : Mathf.Clamp01(looseAimFactor);

        Quaternion desiredArmAim = Quaternion.identity;

        if ((aiming || _looseAim) && gun != null && blend > 0.001f)
        {
            Vector3 toTarget = targetPoint - gun.position;

            if (toTarget.sqrMagnitude > 0.04f)
            {
                Vector3 dir = toTarget.normalized;

                // Reject aiming if the target is behind the bot's own facing by more than
                // ~110 degrees (avoids twisting the arm backward).
                if (root != null)
                {
                    float angleFromForward = Vector3.Angle(dir, root.forward);
                    if (angleFromForward <= 110f)
                    {
                        Quaternion fromTo = Quaternion.FromToRotation(gun.forward, dir);
                        fromTo.ToAngleAxis(out float angleDeg, out Vector3 axis);

                        // CORRECTION: the earlier draft omitted this guard entirely. Confirmed via
                        // trace — FromToRotation's resulting axis can come out NaN/Infinity when
                        // the two input directions are exactly opposite, and the real decompile
                        // only proceeds with building desiredArmAim if the axis is finite;
                        // otherwise it silently keeps desiredArmAim at its default identity.
                        if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
                        {
                            if (angleDeg > 180f) angleDeg -= 360f;

                            float clampedAngle = Mathf.Clamp(blend * angleDeg, -maxAngle, maxAngle);
                            desiredArmAim = Quaternion.AngleAxis(clampedAngle, axis);
                        }
                    }
                }
            }
        }

        float armT = 1f - Mathf.Exp(-speed * Time.deltaTime);
        _armAim = Quaternion.Slerp(_armAim, desiredArmAim, armT);

        // Skip applying the rotation if it's a negligible change (< ~0.15 degrees) from identity.
        float dot = Mathf.Min(Mathf.Abs(Quaternion.Dot(_armAim, Quaternion.identity)), 1f);
        if (dot <= 0.999999f)
        {
            float angleFromIdentity = Mathf.Acos(dot) * 2f * Mathf.Rad2Deg;
            if (angleFromIdentity > 0.15f)
            {
                rightArm.rotation = _armAim * rightArm.rotation;
            }
        }
    }
}