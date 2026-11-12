using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What every room gets the moment it is loaded, in every mode (LevelManager.ApplyActiveRoom):
///
/// 1. Floors and furniture to stand on. The room meshes come without colliders; before, only the
///    bot controllers and the paint tool added them, so an online player whose game ran no bots
///    loaded the match's room with no floor and fell through it. Every room is made solid here.
///
/// 2. Spawns on the floor: TrySpawn and PivotForFeet put a character's feet on the floor under a
///    spawn marker, not its middle.
///
/// 3. A floor under everyone: FallGuard puts a player who drops below the room back on a spawn.
///
/// Rooms are played at the size they are built.
/// </summary>
public static class RoomSetup
{
    /// <summary>The room now loaded (the last one prepared).</summary>
    public static GameObject Room { get; private set; }

    /// <summary>Below this a player has fallen out of the room (FallGuard).</summary>
    public static float FallFloorY { get; private set; } = float.NegativeInfinity;

    /// <summary>Everything a freshly loaded room needs before anyone stands in it.</summary>
    public static void Prepare(GameObject room)
    {
        Room = room;
        if (room == null) return;

        int added = EnsureColliders(room);
        Physics.SyncTransforms();
        StairRampBuilder.EnsureRampsForRoom(room);

        FallFloorY = RendererBounds(room).min.y - 30f;

        Debug.Log($"[RoomSetup] {room.name}: {added} colliders added, played as built");
    }

    /// <summary>A collider on every readable mesh that has none: floors, walls and furniture.</summary>
    public static int EnsureColliders(GameObject room)
    {
        int added = 0;
        foreach (MeshRenderer mr in room.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (mr.GetComponent<Collider>() != null) continue;
            MeshFilter mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable || mf.sharedMesh.vertexCount <= 0) continue;

            MeshCollider mc = mr.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            added++;
        }

        return added;
    }

    /// <summary>
    /// Where a character's transform goes for its feet to be at <paramref name="feet"/>. The players'
    /// CharacterController reaches about 7 units below the transform (center -0.24, height 14), so
    /// a transform put at floor height starts with half the capsule inside the floor — and the
    /// player drops through it as soon as they can move. NetEntityView places online bodies the
    /// same way (FeetPosition).
    /// </summary>
    public static Vector3 PivotForFeet(Transform character, Vector3 feet)
    {
        CharacterController cc = character != null ? character.GetComponent<CharacterController>() : null;
        if (cc == null) return feet;
        float offset = (cc.center.y - cc.height * 0.5f) * character.lossyScale.y;
        return feet - Vector3.up * offset;
    }

    /// <summary>
    /// Where feet stand on this side's spawn markers in the room now loaded: the marker's own spot,
    /// lowered onto the floor under it. False when the room has no such marker.
    /// </summary>
    public static bool TrySpawn(GameObject room, bool seeker, int index, out Vector3 feet, out Quaternion facing)
    {
        feet = Vector3.zero;
        facing = Quaternion.identity;
        if (room == null) return false;

        string prefix = seeker ? "HunterSpawn" : "HiderSpawn";
        var markers = new List<Transform>();
        foreach (Transform t in room.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith(prefix, StringComparison.Ordinal)) markers.Add(t);
        if (markers.Count == 0) return false;

        markers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        Transform marker = markers[Mathf.Abs(index) % markers.Count];

        feet = marker.position;
        if (Physics.Raycast(feet + Vector3.up * 2f, Vector3.down, out RaycastHit ground, 40f, ~0, QueryTriggerInteraction.Ignore))
            feet = ground.point + Vector3.up * 0.2f;

        facing = Quaternion.Euler(0f, marker.eulerAngles.y, 0f);
        return true;
    }

    private static Bounds RendererBounds(GameObject room)
    {
        Bounds bounds = new Bounds(room.transform.position, Vector3.zero);
        bool first = true;
        foreach (Renderer r in room.GetComponentsInChildren<Renderer>(true))
        {
            if (first) { bounds = r.bounds; first = false; }
            else bounds.Encapsulate(r.bounds);
        }
        return bounds;
    }
}
