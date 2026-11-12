using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

// Meccha > Measure Play Areas: how much walkable floor each room has, and how much of it lies
// within a square of each size around the room's spawns. Written to Resources/PlayAreas.json,
// which RoomSetup reads to fence or scale every room to the same playable area. Run it again
// after a room changes; batch: -executeMethod MeasurePlayAreas.Run.
//
// Walkable floor is the bots' own nav mesh (agent radius 2.5, height 12, climb 4, slope 45), baked
// over the colliders the game gives a room at runtime.
public static class MeasurePlayAreas
{
    private const string OutputPath = "Assets/Resources/PlayAreas.json";
    private const int SideStep = 10;
    private const int MaxSide = 2400;

    [MenuItem("Meccha/Measure Play Areas")]
    public static void Measure() => Write(MeasureAll());

    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Write(MeasureAll());
        EditorApplication.Exit(0);
    }

    private static void Write(PlayAreaData data)
    {
        File.WriteAllText(OutputPath, JsonUtility.ToJson(data, true));
        AssetDatabase.ImportAsset(OutputPath);
        foreach (PlayAreaData.Room r in data.rooms)
            Debug.Log($"[PlayAreas] {r.room}: walkable {r.walkable:F0}, spawns centre ({r.centreX:F0}, {r.centreZ:F0}), smallest fence {r.minSide:F0}");
    }

    private static PlayAreaData MeasureAll()
    {
        var rooms = new List<PlayAreaData.Room>();
        foreach (GameObject prefab in Resources.LoadAll<GameObject>("Rooms").OrderBy(p => p.name))
        {
            GameObject room = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
            room.name = prefab.name;
            try
            {
                rooms.Add(MeasureRoom(room));
            }
            finally
            {
                Object.DestroyImmediate(room);
                NavMesh.RemoveAllNavMeshData();
            }
        }

        return new PlayAreaData { rooms = rooms.ToArray() };
    }

    private static PlayAreaData.Room MeasureRoom(GameObject room)
    {
        RoomSetup.EnsureColliders(room);
        Physics.SyncTransforms();

        Bounds bb = new Bounds(room.transform.position, Vector3.zero);
        bool first = true;
        foreach (Renderer r in room.GetComponentsInChildren<Renderer>(true))
        {
            if (first) { bb = r.bounds; first = false; }
            else bb.Encapsulate(r.bounds);
        }
        bb.Expand(12f);

        NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = 2.5f;
        settings.agentHeight = 12f;
        settings.agentClimb = 4f;
        settings.agentSlope = 45f;
        var sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(room.transform, -1, NavMeshCollectGeometry.PhysicsColliders, 0, new List<NavMeshBuildMarkup>(), sources);
        NavMeshDataInstance instance = NavMesh.AddNavMeshData(NavMeshBuilder.BuildNavMeshData(settings, sources, bb, Vector3.zero, Quaternion.identity));
        NavMeshTriangulation mesh = NavMesh.CalculateTriangulation();
        instance.Remove();

        var triangles = new List<(Vector3 Centre, float Area)>();
        for (int i = 0; i < mesh.indices.Length; i += 3)
        {
            Vector3 a = mesh.vertices[mesh.indices[i]], b = mesh.vertices[mesh.indices[i + 1]], c = mesh.vertices[mesh.indices[i + 2]];
            triangles.Add(((a + b + c) / 3f, Vector3.Cross(b - a, c - a).magnitude * 0.5f));
        }

        List<Transform> markers = room.GetComponentsInChildren<Transform>(true)
            .Where(t => t.name.StartsWith("HunterSpawn") || t.name.StartsWith("HiderSpawn")).ToList();
        Vector3 centre = Vector3.zero;
        foreach (Transform m in markers) centre += m.position;
        if (markers.Count > 0) centre /= markers.Count;
        float spread = markers.Count == 0 ? 0f : markers.Max(m => Mathf.Max(Mathf.Abs(m.position.x - centre.x), Mathf.Abs(m.position.z - centre.z)));

        var sides = new List<float>();
        var areas = new List<float>();
        for (int side = SideStep; side <= MaxSide; side += SideStep)
        {
            float half = side / 2f, inside = 0f;
            foreach ((Vector3 c, float area) in triangles)
                if (Mathf.Abs(c.x - centre.x) <= half && Mathf.Abs(c.z - centre.z) <= half) inside += area;
            sides.Add(side);
            areas.Add(inside);
        }

        return new PlayAreaData.Room
        {
            room = room.name,
            walkable = triangles.Sum(t => t.Area),
            centreX = centre.x,
            centreZ = centre.z,
            minSide = 2f * spread + RoomSetup.FenceMarginAroundSpawns,
            sides = sides.ToArray(),
            areas = areas.ToArray(),
        };
    }
}
