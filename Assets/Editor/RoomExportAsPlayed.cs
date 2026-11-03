using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Meccha.Shared.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using Num = System.Numerics;
using Object = UnityEngine.Object;

// Meccha > Export Rooms For Server (as played): bakes the room prefabs into the server's .bin files
// from the room as the game builds it during a round, not from the bare prefab. The SDK's own
// Export Rooms For Server measures the bare prefab, which differs here in three ways:
//   1. LevelManager places every room at the origin; the SDK instantiates the prefab where it was
//      saved (Room5's root is at (-112, 28, 678)), so the server's map would be offset.
//   2. Before a round the bots add MeshColliders and stair ramps to the room
//      (BotHideController.EnsureRoomColliders); the server would not have them.
//   3. The bots bake their NavMesh at runtime (BotHideController.BakeNavMesh). The editor has none,
//      so the SDK's walkable grid comes out empty and the server's bots cannot move.
// The surfaces, floor, spawns and nav grid are still the SDK exporter's own steps, called through
// reflection; only the room they measure differs. Delete this once the SDK exporter can export a
// prepared room.
public static class RoomExportAsPlayed
{
    private const string MenuPath = "Meccha/Export Rooms For Server (as played)";
    private const string ExporterType = "Meccha.EditorTools.RoomDataExporter, Meccha.Client.Editor";

    [MenuItem(MenuPath)]
    public static void ExportAll()
    {
        Type exporter = Type.GetType(ExporterType);
        MethodInfo collectSurfaces = Step(exporter, "CollectSurfaces");
        MethodInfo findFloorY = Step(exporter, "FindFloorY");
        MethodInfo collectSpawns = Step(exporter, "CollectSpawns");
        MethodInfo bakeNav = Step(exporter, "BakeNav");

        if (collectSurfaces == null || findFloorY == null || collectSpawns == null || bakeNav == null)
        {
            EditorUtility.DisplayDialog("SDK exporter changed",
                "This tool calls steps of the Meccha SDK's RoomDataExporter that this SDK version no " +
                "longer has. Check whether the SDK exporter now handles rooms as played.", "OK");
            return;
        }

        // The server repository usually sits next to this project; its rooms go in data/rooms.
        string serverData = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Meccha_Server/data"));
        string outputDirectory = EditorUtility.SaveFolderPanel("Choose the server's data/rooms folder",
            Directory.Exists(serverData) ? serverData : Application.dataPath, "rooms");

        if (string.IsNullOrEmpty(outputDirectory)) return;

        // The folder LevelManager loads rooms from (prefabDir).
        GameObject[] roomPrefabs = Resources.LoadAll<GameObject>("Rooms");
        int exported = 0;

        try
        {
            for (int i = 0; i < roomPrefabs.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Exporting rooms", roomPrefabs[i].name, i / (float)roomPrefabs.Length);
                if (ExportRoom(roomPrefabs[i], outputDirectory, collectSurfaces, findFloorY, collectSpawns, bakeNav)) exported++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Debug.Log($"[RoomExport] Exported {exported}/{roomPrefabs.Length} room(s) to {outputDirectory}");
        EditorUtility.DisplayDialog("Export complete",
            $"Exported {exported} of {roomPrefabs.Length} room(s) to\n{outputDirectory}\n\nRestart the server to load them.", "OK");
    }

    private static MethodInfo Step(Type exporter, string name) =>
        exporter?.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

    private static bool ExportRoom(GameObject prefab, string outputDirectory, MethodInfo collectSurfaces,
                                   MethodInfo findFloorY, MethodInfo collectSpawns, MethodInfo bakeNav)
    {
        // Where and how LevelManager.ApplyActiveRoom places it.
        GameObject room = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        room.name = prefab.name;
        NavMeshDataInstance navMesh = default;

        try
        {
            int colliders = room.GetComponentsInChildren<Collider>(true).Length;
            PrepareLikeBots(room);
            int added = room.GetComponentsInChildren<Collider>(true).Length - colliders;
            navMesh = NavMesh.AddNavMeshData(BakeLikeBots(room));

            var surfaces = new List<Surface>();
            var bounds = (Bounds)collectSurfaces.Invoke(null, new object[] { room, surfaces });

            if (surfaces.Count == 0)
            {
                Debug.LogWarning($"[RoomExport] {prefab.name} has no colliders; skipped.");
                return false;
            }

            // The same RoomData the SDK exporter builds.
            var data = new RoomData
            {
                Name = prefab.name,
                BoundsMin = ToNumerics(bounds.min) - new Num.Vector3(4f, 4f, 4f),
                BoundsMax = ToNumerics(bounds.max) + new Num.Vector3(4f, 4f, 4f),
                FloorY = (float)findFloorY.Invoke(null, new object[] { surfaces, bounds }),
                Surfaces = surfaces.ToArray(),
                HiderSpawns = (Num.Vector3[])collectSpawns.Invoke(null, new object[] { room, "HiderSpawn", bounds, 8 }),
                HunterSpawns = (Num.Vector3[])collectSpawns.Invoke(null, new object[] { room, "HunterSpawn", bounds, 4 }),
            };

            data.Nav = (NavGrid)bakeNav.Invoke(null, new object[] { data });
            data.BuildAccelerators();

            Directory.CreateDirectory(outputDirectory);
            string path = Path.Combine(outputDirectory, prefab.name + ".bin");

            using (FileStream fs = File.Create(path))
            {
                data.Save(fs);
            }

            Debug.Log($"[RoomExport] {prefab.name}: {surfaces.Count} surfaces ({added} added at runtime), " +
                      $"{data.Nav.Width}x{data.Nav.Depth} nav cells, floor {data.FloorY:F1} -> {path}");
            return true;
        }
        finally
        {
            if (navMesh.valid) NavMesh.RemoveNavMeshData(navMesh);
            Object.DestroyImmediate(room);
        }
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

    private static Num.Vector3 ToNumerics(Vector3 v) => new Num.Vector3(v.x, v.y, v.z);
}
