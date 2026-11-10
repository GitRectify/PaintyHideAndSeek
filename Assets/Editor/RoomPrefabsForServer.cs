using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

// Meccha > Prepare Room Prefabs For Server: what a room prefab needs to be played online. Run it on
// a new room, and add the room's name to the server's Maps.
//   1. The floor gets "Floor" in its name, which is how shots know not to stop at it
//      (Bullet.BlocksShot). The floor is the collider the game's own StairRampBuilder.FindFloor
//      picks: the largest flat one below the middle of the room.
//   2. HunterSpawn / HiderSpawn markers under a "ServerSpawns" child, placed where the offline game
//      starts a round: around LevelManager's player spawn (x 0, z 0 in every room), hunters in a
//      tight group and hiders on a ring of radius 10, each snapped onto the bots' NavMesh.
// Running it again keeps the floor name and replaces the markers.
public static class RoomPrefabsForServer
{
    private const string MenuPath = "Meccha/Prepare Room Prefabs For Server";
    private const string RoomFolder = "Assets/Resources/Rooms";
    private const string MarkerRoot = "ServerSpawns";

    // LevelManager's playerSpawnPos for every level, on the floor plane.
    private static readonly Vector2 SpawnCentre = Vector2.zero;
    private const int HunterCount = 4;
    private const float HunterRadius = 4f;
    private const int HiderCount = 8;
    private const float HiderRadius = 10f;

    [MenuItem(MenuPath)]
    public static void PrepareAll()
    {
        var report = new List<string>();

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RoomFolder }))
        {
            report.Add(Prepare(AssetDatabase.GUIDToAssetPath(guid)));
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[RoomPrefabs] " + string.Join("\n", report));
        EditorUtility.DisplayDialog("Room prefabs prepared",
            string.Join("\n", report) + "\n\nAdd any new room's name to Maps in the server's server.json.", "OK");
    }

    private static string Prepare(string path)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

        // Measure the room as played: at the origin (LevelManager), with the bots' colliders and NavMesh.
        GameObject room = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        NavMeshDataInstance navMesh = default;
        List<int> floorPath;
        string floorName;
        float floorTop;
        var hunters = new List<Vector3>();
        var hiders = new List<Vector3>();
        int unsnapped = 0;

        try
        {
            PrepareLikeBots(room);
            navMesh = NavMesh.AddNavMeshData(BakeLikeBots(room));

            Collider floor = FindFloor(room, out Bounds roomBounds);
            if (roomBounds.size == Vector3.zero) return $"{prefab.name}: no renderers; left unchanged.";

            if (floor != null)
            {
                floorPath = IndexPath(room.transform, floor.transform);
                floorName = floor.name;
                floorTop = floor.bounds.max.y;
            }
            else
            {
                // StairRampBuilder's own fallback when a room has no floor collider.
                floorPath = null;
                floorName = "";
                floorTop = roomBounds.min.y + 1f;
            }

            unsnapped += Ring(HunterCount, HunterRadius, floorTop, hunters);
            unsnapped += Ring(HiderCount, HiderRadius, floorTop, hiders);
        }
        finally
        {
            if (navMesh.valid) NavMesh.RemoveNavMeshData(navMesh);
            Object.DestroyImmediate(room);
        }

        GameObject contents = PrefabUtility.LoadPrefabContents(path);

        try
        {
            Transform floorInPrefab = floorPath != null ? Follow(contents.transform, floorPath) : null;
            string renamed = "";
            if (floorInPrefab != null && !floorInPrefab.name.Contains("Floor"))
            {
                floorInPrefab.name += "_Floor";
                renamed = $" (renamed from {floorName})";
            }

            Transform old = contents.transform.Find(MarkerRoot);
            if (old != null) Object.DestroyImmediate(old.gameObject);

            // The positions are for the room at the origin; keep them relative to the root wherever
            // the prefab itself was saved.
            Transform root = contents.transform;
            root.GetPositionAndRotation(out Vector3 savedPosition, out Quaternion savedRotation);
            root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var markers = new GameObject(MarkerRoot).transform;
            markers.SetParent(root, false);
            AddMarkers(markers, "HunterSpawn", hunters);
            AddMarkers(markers, "HiderSpawn", hiders);

            root.SetPositionAndRotation(savedPosition, savedRotation);
            PrefabUtility.SaveAsPrefabAsset(contents, path);

            string floorText = floorInPrefab != null ? $"floor '{floorInPrefab.name}'{renamed} at {floorTop:F1}"
                             : $"no floor collider, spawns placed from {floorTop:F1}";
            string snapText = unsnapped > 0 ? $", {unsnapped} marker(s) off the NavMesh" : "";
            return $"{prefab.name}: {floorText}, {hunters.Count} hunter + {hiders.Count} hider spawns{snapText}";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    // Same rule as StairRampBuilder.FindFloor (largest collider no more than 6 tall whose centre is
    // not above the middle of the room's renderers), leaving out the ramps it generates.
    private static Collider FindFloor(GameObject room, out Bounds roomBounds)
    {
        roomBounds = default;
        Renderer[] renderers = room.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return null;

        roomBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) roomBounds.Encapsulate(renderers[i].bounds);

        Collider best = null;
        float bestArea = 0f;

        foreach (Collider c in room.GetComponentsInChildren<Collider>())
        {
            if (c.transform.parent != null && c.transform.parent.name == "AutoStairRamps") continue;

            Bounds cb = c.bounds;
            if (cb.size.y > 6f || cb.center.y > roomBounds.center.y) continue;

            float area = cb.size.x * cb.size.z;
            if (area > bestArea)
            {
                bestArea = area;
                best = c;
            }
        }

        return best;
    }

    // Points on a ring around the offline spawn, moved onto the NavMesh and lifted 1 unit so the
    // server drops them onto the floor. Returns how many found no NavMesh nearby.
    private static int Ring(int count, float radius, float floorTop, List<Vector3> points)
    {
        int unsnapped = 0;

        for (int i = 0; i < count; i++)
        {
            float angle = i * Mathf.PI * 2f / count;
            var probe = new Vector3(SpawnCentre.x + Mathf.Cos(angle) * radius, floorTop + 2f,
                                    SpawnCentre.y + Mathf.Sin(angle) * radius);

            if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 30f, NavMesh.AllAreas))
            {
                points.Add(hit.position + Vector3.up);
            }
            else
            {
                points.Add(new Vector3(probe.x, floorTop + 1f, probe.z));
                unsnapped++;
            }
        }

        return unsnapped;
    }

    private static void AddMarkers(Transform parent, string prefix, List<Vector3> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            var marker = new GameObject($"{prefix}_{i + 1}").transform;
            marker.position = points[i];
            marker.SetParent(parent, true);
        }
    }

    // Sibling indices from the root down to t, so the same object can be found in the prefab's
    // contents (names repeat in these rooms).
    private static List<int> IndexPath(Transform root, Transform t)
    {
        var path = new List<int>();
        for (; t != root; t = t.parent) path.Insert(0, t.GetSiblingIndex());
        return path;
    }

    private static Transform Follow(Transform root, List<int> path)
    {
        Transform t = root;
        foreach (int index in path)
        {
            if (index >= t.childCount) return null;
            t = t.GetChild(index);
        }
        return t;
    }

    // Same as BotHideController.EnsureRoomColliders / BotSeekController: readable room meshes
    // without a collider get a MeshCollider, then the stair ramps.
    private static void PrepareLikeBots(GameObject room)
    {
        foreach (MeshRenderer mr in room.GetComponentsInChildren<MeshRenderer>())
        {
            if (mr.GetComponent<Collider>() != null) continue;
            MeshFilter mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable || mf.sharedMesh.vertexCount <= 0) continue;
            mr.gameObject.AddComponent<MeshCollider>();
        }

        // Edit mode does not sync transforms on its own; the ramp builder raycasts against them.
        Physics.SyncTransforms();
        StairRampBuilder.EnsureRampsForRoom(room);
        Physics.SyncTransforms();
    }

    // Same as BotHideController.BakeNavMesh / BotSeekController (agent r 2.5, h 12, climb 4,
    // slope 45; colliders only; renderer bounds expanded by 12).
    private static NavMeshData BakeLikeBots(GameObject room)
    {
        Bounds bb = new Bounds(room.transform.position, Vector3.one);
        Renderer[] rends = room.GetComponentsInChildren<Renderer>();
        for (int i = 0; i < rends.Length; i++)
        {
            if (i > 0) bb.Encapsulate(rends[i].bounds);
            else bb = rends[i].bounds;
        }
        bb.Expand(12f);

        NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = 2.5f;
        settings.agentHeight = 12f;
        settings.agentClimb = 4f;
        settings.agentSlope = 45f;

        var markups = new List<NavMeshBuildMarkup>();
        var sources = new List<NavMeshBuildSource>();
        UnityEngine.AI.NavMeshBuilder.CollectSources(room.transform, -1, NavMeshCollectGeometry.PhysicsColliders, 0, markups, sources);

        return UnityEngine.AI.NavMeshBuilder.BuildNavMeshData(settings, sources, bb, Vector3.zero, Quaternion.identity);
    }
}
