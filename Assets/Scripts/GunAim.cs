using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Points a held gun where its shooter aims. A seeker's shot leaves the muzzle along the body's
/// facing tilted by the aim pitch (Mode1FirstPersonGun.ShotDir); the gun model itself only
/// followed the hand's animation, so lying down it pointed up at the ceiling, and on other players'
/// screens it never tilted at all. After the animation each frame the gun is turned in the hand so
/// its barrel lies exactly on that line: what the gun points at is where the bullet goes, standing,
/// crouching or lying down, on every screen.
/// </summary>
public static class GunAim
{
    // Each gun's barrel in its own space, found once: the longest side of its meshes, towards the
    // end away from the hand (where the muzzle is made, Mode1FirstPersonGun.EnsureMuzzle).
    private static readonly Dictionary<Transform, Vector3> barrels = new Dictionary<Transform, Vector3>();

    /// <summary>A body's aim: its level facing, tilted up by <paramref name="pitchDegrees"/> (up is positive).</summary>
    public static Vector3 Direction(Transform body, float pitchDegrees)
    {
        Vector3 forward = body.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude < 1e-6f ? Vector3.forward : forward.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        return Quaternion.AngleAxis(-pitchDegrees, right) * forward;
    }

    /// <summary>Turns <paramref name="gun"/> about its grip so its barrel points along <paramref name="direction"/>.</summary>
    public static void PointAlong(Transform gun, Vector3 direction)
    {
        if (gun == null || direction.sqrMagnitude < 1e-6f) return;

        Vector3 barrel = gun.TransformDirection(BarrelAxis(gun));
        if (barrel.sqrMagnitude < 1e-6f) return;

        gun.rotation = Quaternion.FromToRotation(barrel, direction.normalized) * gun.rotation;
    }

    /// <summary>How far, in degrees, the gun's barrel is from <paramref name="direction"/> (the play test reads it).</summary>
    public static float AngleOff(Transform gun, Vector3 direction) =>
        gun == null ? 180f : Vector3.Angle(gun.TransformDirection(BarrelAxis(gun)), direction);

    /// <summary>The barrel in the gun's own space, unit length.</summary>
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

        if (barrels.Count > 64) barrels.Clear();    // bodies come and go; the few live guns are found again
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
