using System;
using System.Collections.Generic;
using Meccha.Shared.Game;
using UnityEngine;

/// <summary>
/// What every room gets the moment it is loaded, in every mode (LevelManager.ApplyActiveRoom):
///
/// 1. Floors and furniture to stand on. The room meshes come without colliders; before, only the
///    bot controllers and the paint tool added them, so an online player whose game ran no bots
///    loaded the match's room with no floor and fell through it. Every room is made solid here.
///
/// 2. The same playable area as every other room. The rooms as built hold from about 78 000 to
///    860 000 square units of walkable floor, which gave the hiders far more room on some maps
///    than others. A room with more than the playable area is fenced to a square around its
///    spawns holding that much (invisible walls on the Ignore Raycast layer, so the camera,
///    climbing and aiming pay them no attention, with a faint glow where they stand); a room with
///    less is scaled up to it, by at most GameTuning.MaxMapUpscale. The area is the server's
///    (MatchFound, ServerConfig.PlayableArea); Single Play uses the last one the server gave, or
///    the standard one. Every client in a match computes the same fence from the same data
///    (Resources/PlayAreas.json, from Meccha > Measure Play Areas), so everyone plays on the same
///    ground.
///
/// 3. A floor under everyone: FallGuard puts a player who drops below the room back on a spawn.
/// </summary>
public static class RoomSetup
{
    /// <summary>How far around the spawns a fence always stands, so no spawn is ever outside it.</summary>
    public const float FenceMarginAroundSpawns = 60f;

    private const string AreaPrefsKey = "PlayableArea";
    private const float FenceThickness = 6f;
    private const float FenceGlowHeight = 18f;

    private static Dictionary<string, PlayAreaData.Room> table;

    /// <summary>The room now loaded (the last one prepared).</summary>
    public static GameObject Room { get; private set; }

    /// <summary>The fenced square (world x/z) of the room now loaded; null when it is played whole.</summary>
    public static Rect? Fence { get; private set; }

    /// <summary>How much the room now loaded was scaled up (1 = as built).</summary>
    public static float Scale { get; private set; } = 1f;

    /// <summary>Below this a player has fallen out of the room (FallGuard).</summary>
    public static float FallFloorY { get; private set; } = float.NegativeInfinity;

    /// <summary>The playable area for the next room: the server's last word, or the standard one.</summary>
    public static int PlayableArea
    {
        get
        {
            try { return PlayerPrefs.GetInt(AreaPrefsKey, GameTuning.PlayableArea); }
            catch (Exception) { return GameTuning.PlayableArea; }
        }
    }

    /// <summary>Remembers the server's playable area (MatchFound) for this and later rooms, Single Play's too.</summary>
    public static void NoteServerArea(int area)
    {
        if (area < 0) return;
        try
        {
            PlayerPrefs.SetInt(AreaPrefsKey, area);
            PlayerPrefs.Save();
        }
        catch (Exception)
        {
            // Applied for this match anyway; remembered next time.
        }
    }

    /// <summary>A point inside the playable area (always true for a room played whole).</summary>
    public static bool Inside(Vector3 point) => Fence == null || Fence.Value.Contains(new Vector2(point.x, point.z));

    /// <summary>Narrows a bake or search box to the playable area.</summary>
    public static Bounds Clamp(Bounds bounds)
    {
        if (Fence == null) return bounds;
        Rect f = Fence.Value;
        Vector3 min = bounds.min, max = bounds.max;
        min.x = Mathf.Max(min.x, f.xMin); max.x = Mathf.Min(max.x, f.xMax);
        min.z = Mathf.Max(min.z, f.yMin); max.z = Mathf.Min(max.z, f.yMax);
        if (max.x <= min.x || max.z <= min.z) return bounds;
        var clamped = new Bounds();
        clamped.SetMinMax(min, max);
        return clamped;
    }

    /// <summary>Everything a freshly loaded room needs before anyone stands in it.</summary>
    public static void Prepare(GameObject room, int playableArea)
    {
        Room = room;
        Fence = null;
        Scale = 1f;
        if (room == null) return;

        PlayAreaData.Room data = DataFor(room.name);
        float scale = 1f;
        float side = 0f;
        if (playableArea > 0 && data != null && data.walkable > 0f)
        {
            if (playableArea >= data.walkable) scale = Mathf.Min(Mathf.Sqrt(playableArea / data.walkable), GameTuning.MaxMapUpscale);
            else side = Mathf.Max(SideFor(data, playableArea), data.minSide);
        }

        if (scale > 1.0001f)
        {
            // On top of the prefab's own scale (Room and Room2 are built at 17, Room3 at 45, Room4
            // at 10, Room5 at 8): the room is a fresh instance here (LevelManager.ApplyActiveRoom).
            room.transform.localScale *= scale;
            Scale = scale;
        }

        EnsureColliders(room);
        Physics.SyncTransforms();
        StairRampBuilder.EnsureRampsForRoom(room);

        Bounds bounds = RendererBounds(room);
        FallFloorY = bounds.min.y - 30f;

        if (side > 0f)
        {
            var centre = new Vector2(data.centreX * Scale, data.centreZ * Scale);
            Fence = new Rect(centre - Vector2.one * (side / 2f), Vector2.one * side);
            BuildFence(room, Fence.Value, bounds);
        }

        Debug.Log($"[RoomSetup] {room.name}: playable area {playableArea}, " +
                  (Fence != null ? $"fenced to {side:F0} x {side:F0}" : Scale > 1.0001f ? $"scaled x{Scale:F2}" : "played whole"));
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

    // ================= Inside =================

    private static PlayAreaData.Room DataFor(string roomName)
    {
        if (table == null)
        {
            table = new Dictionary<string, PlayAreaData.Room>(StringComparer.Ordinal);
            TextAsset json = Resources.Load<TextAsset>("PlayAreas");
            if (json != null)
            {
                PlayAreaData data = JsonUtility.FromJson<PlayAreaData>(json.text);
                if (data?.rooms != null)
                    foreach (PlayAreaData.Room r in data.rooms) table[r.room] = r;
            }
            else Debug.LogWarning("[RoomSetup] Resources/PlayAreas.json is missing: rooms are played whole (Meccha > Measure Play Areas).");
        }

        return roomName != null && table.TryGetValue(roomName, out PlayAreaData.Room room) ? room : null;
    }

    // The smallest square side holding the area, between the measured sizes.
    private static float SideFor(PlayAreaData.Room data, float area)
    {
        for (int i = 0; i < data.sides.Length; i++)
        {
            if (data.areas[i] < area) continue;
            if (i == 0) return data.sides[0];
            float t = Mathf.InverseLerp(data.areas[i - 1], data.areas[i], area);
            return Mathf.Lerp(data.sides[i - 1], data.sides[i], t);
        }

        return data.sides.Length > 0 ? data.sides[data.sides.Length - 1] : 0f;
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

    private static Material fenceMaterial;

    // Four walls round the square, tall enough that nobody climbs over, on Ignore Raycast so the
    // camera, the climber and aiming never meet them; a faint band shows where they stand.
    private static void BuildFence(GameObject room, Rect fence, Bounds roomBounds)
    {
        var root = new GameObject("PlayAreaFence");
        root.transform.SetParent(room.transform, true);
        root.transform.localScale = new Vector3(1f / room.transform.lossyScale.x, 1f / room.transform.lossyScale.y, 1f / room.transform.lossyScale.z);
        root.layer = 2;

        float bottom = roomBounds.min.y - 20f;
        float top = roomBounds.max.y + 120f;
        float floor = FloorUnder(new Vector3(fence.center.x, roomBounds.max.y + 5f, fence.center.y), roomBounds.min.y);

        Wall(root, "North", new Vector3(fence.center.x, 0f, fence.yMax), new Vector2(fence.width + 2 * FenceThickness, FenceThickness), bottom, top, floor);
        Wall(root, "South", new Vector3(fence.center.x, 0f, fence.yMin), new Vector2(fence.width + 2 * FenceThickness, FenceThickness), bottom, top, floor);
        Wall(root, "East", new Vector3(fence.xMax, 0f, fence.center.y), new Vector2(FenceThickness, fence.height), bottom, top, floor);
        Wall(root, "West", new Vector3(fence.xMin, 0f, fence.center.y), new Vector2(FenceThickness, fence.height), bottom, top, floor);
    }

    private static void Wall(GameObject root, string name, Vector3 at, Vector2 size, float bottom, float top, float floor)
    {
        var wall = new GameObject("Fence" + name) { layer = 2 };
        wall.transform.SetParent(root.transform, false);
        wall.transform.position = new Vector3(at.x, (bottom + top) / 2f, at.z);

        BoxCollider box = wall.AddComponent<BoxCollider>();
        Vector3 lossy = wall.transform.lossyScale;
        box.size = new Vector3(size.x / lossy.x, (top - bottom) / lossy.y, size.y / lossy.z);

        // The glow: a thin translucent band from the floor up, so the edge is seen before it is met.
        Material glow = FenceMaterial();
        if (glow == null) return;

        GameObject band = GameObject.CreatePrimitive(PrimitiveType.Cube);
        band.name = "Glow";
        band.layer = 2;
        UnityEngine.Object.Destroy(band.GetComponent<Collider>());
        band.transform.SetParent(wall.transform, false);
        band.transform.position = new Vector3(at.x, floor + FenceGlowHeight / 2f, at.z);
        const float thin = 0.4f;    // the band is a sheet on the wall's inner line, not the wall's thickness
        band.transform.localScale = new Vector3((size.x > size.y ? size.x : thin) / lossy.x,
                                                FenceGlowHeight / lossy.y,
                                                (size.y > size.x ? size.y : thin) / lossy.z);
        Renderer r = band.GetComponent<Renderer>();
        r.sharedMaterial = glow;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    private static float FloorUnder(Vector3 from, float fallback) =>
        Physics.Raycast(from, Vector3.down, out RaycastHit hit, 2000f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y : fallback;

    // URP's unlit shader, transparent; the sprite shader where that is not in the build.
    private static Material FenceMaterial()
    {
        if (fenceMaterial != null) return fenceMaterial;

        var tint = new Color(0.35f, 0.85f, 1f, 0.16f);
        Shader urp = Shader.Find("Universal Render Pipeline/Unlit");
        if (urp != null)
        {
            fenceMaterial = new Material(urp) { name = "PlayAreaFence" };
            fenceMaterial.SetFloat("_Surface", 1f);
            fenceMaterial.SetFloat("_Blend", 0f);
            fenceMaterial.SetFloat("_ZWrite", 0f);
            fenceMaterial.SetFloat("_Cull", 0f);
            fenceMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            fenceMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            fenceMaterial.SetOverrideTag("RenderType", "Transparent");
            fenceMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            fenceMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            fenceMaterial.SetColor("_BaseColor", tint);
            return fenceMaterial;
        }

        Shader sprite = Shader.Find("Sprites/Default");
        if (sprite == null) return null;
        fenceMaterial = new Material(sprite) { name = "PlayAreaFence", color = tint };
        return fenceMaterial;
    }
}

/// <summary>Resources/PlayAreas.json: each room's walkable floor, measured (Meccha > Measure Play Areas).</summary>
[Serializable]
public class PlayAreaData
{
    [Serializable]
    public class Room
    {
        public string room;
        public float walkable;          // square units of nav-mesh floor in the whole room
        public float centreX, centreZ;  // the middle of its spawn markers
        public float minSide;           // the smallest fence that keeps every spawn inside
        public float[] sides;           // square sides, and the walkable floor inside each
        public float[] areas;
    }

    public Room[] rooms;
}
