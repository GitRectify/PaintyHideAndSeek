using System.Collections.Generic;
using UnityEngine;

// The free camera's walls (2026-11-23): the objects tagged "CameraBounds" in the map loaded (Room
// has planes all round the inside of the house, floors and ceilings too). They are for the free
// camera alone, so the moment the map loads (RoomSetup.Prepare) each one's shape is written down
// here and the object is switched off: no collider for players, bots, shots, the bots' navigation
// or the stair ramps, and nothing drawn. CameraController moves the free camera through Move.
//
// A flat wall (a Plane or a Quad) stops the camera coming at it from its front, the side its face
// looks to (in Room, the inside of the house), and the camera slides along it. A camera that is
// somehow behind one is not trapped there: coming up to it from behind, it is let back in. A wall
// with depth (a cube) stops the camera from every side. Cushion slows the camera over the last few
// units before a wall, so it comes to rest against it rather than hitting it.
//
// A map without such walls keeps the free camera's older limits (CameraController.ClampFree).
public static class CameraBounds
{
    public const string Tag = "CameraBounds";

    private struct Wall
    {
        public Vector3 Centre;
        public Vector3 U, V, N;          // unit axes: two along the face, one across it (N: the front)
        public float HalfU, HalfV, HalfN;
        public bool Flat;
    }

    private const float FlatBelow = 0.05f;   // world units: thinner than this is a face, not a box

    private static readonly List<Wall> walls = new List<Wall>();

    /// <summary>Whether the map loaded has camera walls.</summary>
    public static bool Any => walls.Count > 0;

    /// <summary>Whether one of them faces up (a floor) / down (a ceiling).</summary>
    public static bool HasFloor { get; private set; }
    public static bool HasCeiling { get; private set; }

    /// <summary>Reads the map's camera walls and switches the objects off (RoomSetup.Prepare).</summary>
    public static void Collect(GameObject room)
    {
        walls.Clear();
        HasFloor = HasCeiling = false;
        if (room == null || !SafeTags.Defined(Tag)) return;

        var found = new List<GameObject>();
        foreach (Transform t in room.GetComponentsInChildren<Transform>(true))
        {
            if (!t.CompareTag(Tag)) continue;
            found.Add(t.gameObject);
            if (!TryShape(t, out Wall wall)) continue;

            walls.Add(wall);
            if (wall.Flat && wall.N.y > 0.7f) HasFloor = true;
            if (wall.Flat && wall.N.y < -0.7f) HasCeiling = true;
        }

        // Out of physics, the bots' navigation, the ramp scan and the picture.
        foreach (GameObject go in found) go.SetActive(false);

        if (found.Count > 0)
            Debug.Log($"[CameraBounds] {room.name}: {walls.Count} free-camera walls (floor {HasFloor}, ceiling {HasCeiling}), switched off for everything else");
    }

    /// <summary>Whether this object is one of the free camera's walls (left out of the map's size).</summary>
    public static bool IsWall(Component c) => SafeTags.Has(c, Tag);

    // The wall's box in the world: from its BoxCollider, else its mesh.
    private static bool TryShape(Transform t, out Wall wall)
    {
        wall = default;
        Bounds local;
        Mesh mesh = null;
        if (t.TryGetComponent(out BoxCollider box)) local = new Bounds(box.center, box.size);
        else if (t.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null) { mesh = mf.sharedMesh; local = mesh.bounds; }
        else if (t.TryGetComponent(out MeshCollider mc) && mc.sharedMesh != null) { mesh = mc.sharedMesh; local = mesh.bounds; }
        else return false;

        Vector3 e = local.extents;
        Vector3[] along =
        {
            t.TransformVector(new Vector3(e.x, 0f, 0f)),
            t.TransformVector(new Vector3(0f, e.y, 0f)),
            t.TransformVector(new Vector3(0f, 0f, e.z)),
        };
        float[] half = { along[0].magnitude, along[1].magnitude, along[2].magnitude };

        int thin = half[0] <= half[1] && half[0] <= half[2] ? 0 : half[1] <= half[2] ? 1 : 2;
        int a = (thin + 1) % 3, b = (thin + 2) % 3;
        if (half[a] < 0.001f || half[b] < 0.001f) return false;   // a line, not a wall

        Vector3 u = along[a] / half[a], v = along[b] / half[b];
        Vector3 n = half[thin] > 0.0001f ? along[thin] / half[thin] : Vector3.Cross(u, v).normalized;

        // The front of a face is where its mesh's normals point (a Plane's is its up).
        if (half[thin] < FlatBelow)
        {
            Vector3 face = FaceNormal(mesh, thin);
            Vector3 world = t.worldToLocalMatrix.transpose.MultiplyVector(face);
            if (Vector3.Dot(world, n) < 0f) n = -n;
        }

        wall = new Wall
        {
            Centre = t.TransformPoint(local.center),
            U = u, V = v, N = n,
            HalfU = half[a], HalfV = half[b], HalfN = half[thin],
            Flat = half[thin] < FlatBelow,
        };
        return true;
    }

    private static Vector3 FaceNormal(Mesh mesh, int thinAxis)
    {
        if (mesh != null && mesh.isReadable)
        {
            Vector3[] normals = mesh.normals;
            if (normals != null && normals.Length > 0) return normals[0];
        }
        return thinAxis == 0 ? Vector3.right : thinAxis == 1 ? Vector3.up : Vector3.forward;
    }

    /// <summary>
    /// Where a camera of this radius going from <paramref name="from"/> to <paramref name="to"/>
    /// ends up: in steps of half its radius, so nothing thin is jumped, kept clear of every wall.
    /// </summary>
    public static Vector3 Move(Vector3 from, Vector3 to, float radius)
    {
        if (walls.Count == 0) return to;

        Vector3 delta = to - from;
        int steps = Mathf.Clamp(Mathf.CeilToInt(delta.magnitude / Mathf.Max(0.05f, radius * 0.5f)), 1, 512);
        Vector3 step = delta / steps;
        Vector3 p = from;
        for (int i = 0; i < steps; i++) p = PushOut(p + step, radius);
        return p;
    }

    /// <summary>
    /// The camera's velocity, slowed toward a wall over the last stretch before it: the part going
    /// into the wall may be no faster than rate times the gap left, so it eases to a stop. Along
    /// the wall it is unchanged.
    /// </summary>
    public static Vector3 Cushion(Vector3 p, Vector3 velocity, float radius, float rate)
    {
        foreach (Wall w in walls)
        {
            if (!Facing(w, p, radius, out Vector3 normal, out float gap)) continue;
            float into = Vector3.Dot(velocity, normal);
            if (into >= 0f) continue;
            float most = Mathf.Max(0f, gap) * rate;
            if (-into > most) velocity -= normal * (into + most);
        }
        return velocity;
    }

    // The way out of a wall the camera faces from close by, and how far it is from touching it.
    private static bool Facing(Wall w, Vector3 p, float radius, out Vector3 normal, out float gap)
    {
        normal = w.N;
        gap = 0f;
        Vector3 d = p - w.Centre;
        float du = Vector3.Dot(d, w.U), dv = Vector3.Dot(d, w.V), dn = Vector3.Dot(d, w.N);
        if (Mathf.Abs(du) > w.HalfU || Mathf.Abs(dv) > w.HalfV) return false;

        if (w.Flat)
        {
            if (dn < 0f) return false;            // behind it
            gap = dn - radius;
            return gap < 40f;
        }

        float out1 = Mathf.Abs(dn) - w.HalfN;
        if (out1 < 0f) return false;
        normal = dn >= 0f ? w.N : -w.N;
        gap = out1 - radius;
        return gap < 40f;
    }

    // The camera kept clear of every wall: pushed out of the ones it is in, a few times for corners.
    private static Vector3 PushOut(Vector3 p, float radius)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            bool moved = false;
            foreach (Wall w in walls)
            {
                Vector3 q = w.Flat ? PushFace(w, p, radius) : PushBox(w, p, radius);
                if ((q - p).sqrMagnitude > 1e-8f) { p = q; moved = true; }
            }
            if (!moved) break;
        }
        return p;
    }

    private static Vector3 PushFace(Wall w, Vector3 p, float radius)
    {
        Vector3 d = p - w.Centre;
        float du = Vector3.Dot(d, w.U), dv = Vector3.Dot(d, w.V), dn = Vector3.Dot(d, w.N);
        if (dn >= radius || dn <= -radius) return p;

        if (Mathf.Abs(du) <= w.HalfU && Mathf.Abs(dv) <= w.HalfV)
            return p + w.N * (radius - dn);          // over the face: out in front of it

        float cu = Mathf.Clamp(du, -w.HalfU, w.HalfU), cv = Mathf.Clamp(dv, -w.HalfV, w.HalfV);

        // Past its edge: round the edge, from the front only.
        if (dn < 0f) return p;
        Vector3 edge = w.Centre + w.U * cu + w.V * cv;
        Vector3 away = p - edge;
        float dist = away.magnitude;
        if (dist >= radius || dist < 1e-5f) return p;
        return edge + away * (radius / dist);
    }

    private static Vector3 PushBox(Wall w, Vector3 p, float radius)
    {
        Vector3 d = p - w.Centre;
        float du = Vector3.Dot(d, w.U), dv = Vector3.Dot(d, w.V), dn = Vector3.Dot(d, w.N);
        float cu = Mathf.Clamp(du, -w.HalfU, w.HalfU), cv = Mathf.Clamp(dv, -w.HalfV, w.HalfV), cn = Mathf.Clamp(dn, -w.HalfN, w.HalfN);
        Vector3 closest = w.Centre + w.U * cu + w.V * cv + w.N * cn;
        Vector3 away = p - closest;
        float dist = away.magnitude;
        if (dist >= radius) return p;
        if (dist > 1e-5f) return closest + away * (radius / dist);

        // Inside the box: out through the nearest side.
        float pu = w.HalfU - Mathf.Abs(du), pv = w.HalfV - Mathf.Abs(dv), pn = w.HalfN - Mathf.Abs(dn);
        if (pu <= pv && pu <= pn) return p + w.U * (Mathf.Sign(du) * (pu + radius));
        if (pv <= pn) return p + w.V * (Mathf.Sign(dv) * (pv + radius));
        return p + w.N * (Mathf.Sign(dn) * (pn + radius));
    }
}
