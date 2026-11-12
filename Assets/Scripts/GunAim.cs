using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A seeker's gun is held, not aimed on its own. Each frame, after the animation:
///
/// 1. The arm aims: the right arm turns at the shoulder so the forearm points where the shots go
///    (the body's facing tilted by the aim pitch). The whole arm turns as one, so nothing
///    stretches, and the hand moves with it.
/// 2. The gun lies along the arm: from its place in the hand as the character was built with it,
///    it is turned about the palm until the barrel is parallel to the forearm. The handle stays in
///    the hand; when the arm moves, the gun moves with it.
///
/// So the barrel, the arm and the line of fire are one, standing or lying down, on every screen.
/// Before, the gun was turned onto the line of fire by itself and could cross the arm.
/// </summary>
public static class GunAim
{
    private const string UpperArmName = "mixamorig:RightArm";
    private const string ForeArmName = "mixamorig:RightForeArm";
    private const string HandName = "mixamorig:RightHand";
    private const string PalmName = "mixamorig:RightHandMiddle1";

    /// <summary>The bones of a body's right arm.</summary>
    public sealed class Arm
    {
        public Transform Upper, Fore, Hand, Palm;

        /// <summary>Where the forearm points: elbow to wrist.</summary>
        public Vector3 Direction => (Hand.position - Fore.position).normalized;

        /// <summary>Where the gun is held: the middle of the hand.</summary>
        public Vector3 Grip => Palm != null ? (Hand.position + Palm.position) * 0.5f : Hand.position;
    }

    // Each gun's place in the hand as built, and its barrel in its own space.
    private static readonly Dictionary<Transform, (Vector3 Position, Quaternion Rotation)> rests = new Dictionary<Transform, (Vector3, Quaternion)>();
    private static readonly Dictionary<Transform, Vector3> barrels = new Dictionary<Transform, Vector3>();

    /// <summary>The right arm under <paramref name="body"/>, or null when it has no such bones.</summary>
    public static Arm FindArm(Transform body)
    {
        if (body == null) return null;
        var arm = new Arm();
        foreach (Transform t in body.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == UpperArmName) arm.Upper = t;
            else if (t.name == ForeArmName) arm.Fore = t;
            else if (t.name == HandName) arm.Hand = t;
            else if (t.name == PalmName) arm.Palm = t;
        }
        return arm.Upper != null && arm.Fore != null && arm.Hand != null ? arm : null;
    }

    /// <summary>A body's aim: its level facing, tilted up by <paramref name="pitchDegrees"/> (up is positive).</summary>
    public static Vector3 Direction(Transform body, float pitchDegrees)
    {
        Vector3 forward = body.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude < 1e-6f ? Vector3.forward : forward.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        return Quaternion.AngleAxis(-pitchDegrees, right) * forward;
    }

    /// <summary>Turns the arm at the shoulder so the forearm points along <paramref name="aim"/>.</summary>
    public static void PointArm(Arm arm, Vector3 aim)
    {
        if (arm == null || aim.sqrMagnitude < 1e-6f) return;
        Vector3 forearm = arm.Direction;
        if (forearm.sqrMagnitude < 1e-6f) return;
        arm.Upper.rotation = Quaternion.FromToRotation(forearm, aim.normalized) * arm.Upper.rotation;
    }

    /// <summary>
    /// Puts <paramref name="gun"/> back where the hand holds it, then turns it about the palm until
    /// the barrel is parallel to the forearm.
    /// </summary>
    public static void AlongArm(Transform gun, Arm arm)
    {
        if (gun == null || arm == null) return;

        if (!rests.TryGetValue(gun, out var rest))
        {
            if (rests.Count > 64) rests.Clear();       // bodies come and go; the live guns are measured again
            rest = (gun.localPosition, gun.localRotation);
            rests[gun] = rest;
        }
        gun.localPosition = rest.Position;
        gun.localRotation = rest.Rotation;

        Vector3 barrel = gun.TransformDirection(BarrelAxis(gun));
        Vector3 along = arm.Direction;
        if (barrel.sqrMagnitude < 1e-6f || along.sqrMagnitude < 1e-6f) return;

        Quaternion turn = Quaternion.FromToRotation(barrel, along);
        Vector3 grip = arm.Grip;
        gun.SetPositionAndRotation(grip + turn * (gun.position - grip), turn * gun.rotation);
    }

    /// <summary>How far, in degrees, the gun's barrel is from <paramref name="direction"/> (the play test reads it).</summary>
    public static float AngleOff(Transform gun, Vector3 direction) =>
        gun == null ? 180f : Vector3.Angle(gun.TransformDirection(BarrelAxis(gun)), direction);

    /// <summary>The barrel in the gun's own space, unit length: its longest side, towards the end away from the hand.</summary>
    public static Vector3 BarrelAxis(Transform gun)
    {
        if (barrels.TryGetValue(gun, out Vector3 known)) return known;

        Vector3 axis = Vector3.forward;
        if (TryLocalBounds(gun, out Bounds b))
        {
            Vector3 size = b.size;
            axis = size.x >= size.y && size.x >= size.z ? Vector3.right
                 : size.y >= size.z ? Vector3.up
                 : Vector3.forward;

            // Towards the end away from the hand (the parent), as the muzzle is placed.
            Transform hand = gun.parent != null ? gun.parent : gun;
            Vector3 plus = b.center + Vector3.Scale(axis, b.extents);
            Vector3 minus = b.center - Vector3.Scale(axis, b.extents);
            if (Vector3.Distance(gun.TransformPoint(minus), hand.position) > Vector3.Distance(gun.TransformPoint(plus), hand.position))
                axis = -axis;

            // A muzzle already on the gun says which end outright.
            Transform muzzle = gun.Find("MuzzlePoint");
            if (muzzle != null && Vector3.Dot(muzzle.localPosition - b.center, axis) < 0f) axis = -axis;
        }

        if (barrels.Count > 64) barrels.Clear();
        barrels[gun] = axis;
        return axis;
    }

    // Every mesh's own bounds, corner by corner, in the gun's space: exact however the gun is
    // turned when first measured (world-aligned boxes would grow with the turn).
    private static bool TryLocalBounds(Transform gun, out Bounds local)
    {
        local = default;
        bool any = false;
        Matrix4x4 toGun = gun.worldToLocalMatrix;

        foreach (MeshFilter mf in gun.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            AddCorners(mf.sharedMesh.bounds, toGun * mf.transform.localToWorldMatrix, ref local, ref any);
        }

        foreach (SkinnedMeshRenderer smr in gun.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Transform root = smr.rootBone != null ? smr.rootBone : smr.transform;
            AddCorners(smr.localBounds, toGun * root.localToWorldMatrix, ref local, ref any);
        }

        return any;
    }

    private static void AddCorners(Bounds b, Matrix4x4 toGun, ref Bounds local, ref bool any)
    {
        Vector3 c = b.center, e = b.extents;
        for (int k = 0; k < 8; k++)
        {
            Vector3 corner = c + new Vector3((k & 1) != 0 ? e.x : -e.x, (k & 2) != 0 ? e.y : -e.y, (k & 4) != 0 ? e.z : -e.z);
            Vector3 p = toGun.MultiplyPoint3x4(corner);
            if (any) local.Encapsulate(p);
            else { local = new Bounds(p, Vector3.zero); any = true; }
        }
    }
}
