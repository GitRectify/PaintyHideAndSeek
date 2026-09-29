using System.Collections.Generic;
using UnityEngine;

// NOTE: New class this session — not previously seen in any earlier handoff. Only the four
// methods below have been decompiled so far; there may be more members on this class that
// simply haven't come up in a call site yet.
//
// Purpose (inferred from the full body, not just call-surface): given a "room" GameObject,
// scans its floor on a grid via downward raycasts, looks for places where the floor height
// changes abruptly across a short, consistent run (a "step"), and auto-generates a ramp
// GameObject + BoxCollider bridging the two levels wherever the step is climbable-sized.
// It tags itself idempotent by creating a marker child called "AutoStairRamps" and bailing
// out immediately if that child already exists on the room.
//
// VERIFICATION NOTE: this is the densest file reversed this session (raw pointer/offset math,
// a packed x*100000+z integer encoding, and a three-way boolean comparison collapse in
// FindFloor). Every non-obvious translation call flagged below was independently re-derived
// against the raw decompile — the FindFloor boolean collapse was verified case-by-case
// (not-flat / less / equal / greater), the GradientRadius "dead code" claim was confirmed by
// tracing modf(2.0, ...) (always returns fractional part 0.0, so the half-to-even branch is
// unreachable), and the flood-fill's 8-neighbor loop and (x,z) packing were traced through the
// raw offset arithmetic. No discrepancies found.
public static class StairRampBuilder
{
    // ---- tuning constants pulled out of the decompiled literals ----
    private const float CellSize = 2.5f;              // world units per scan-grid cell
    private const float ScanPadding = 8f;              // margin added around the floor footprint for the scan grid
    private const float ScanRayHeight = 32f;           // ray origin height above the detected floor Y
    private const float RayDistance = 102f;            // SampleSurface's downward raycast length

    // NOTE: the decompiled code computes this via a generic-looking Math.Round(...) call
    // (full half-to-even rounding logic, including a modf branch). Traced it through: the
    // value being rounded is already a compile-time constant by the time it reaches that
    // call, so every branch in the rounding logic is dead and the result is always 2 for
    // any grid built here. Recorded as a plain constant rather than reproducing the dead
    // rounding machinery.
    private const int GradientRadius = 2;

    private const float FlatnessAngleThreshold = 18f;  // deg; surface normal within this of "up" counts as walkable
    private const float StepSlopeMin = 0.40402624f;    // NOTE: literal from the decompile (~22 deg grade)
    private const float StepSlopeMax = 1.3270448f;     // NOTE: literal from the decompile (~53 deg grade)
    private const int MinComponentCells = 8;           // original check was "> 7"
    private const float MinStepHeight = 4f;
    private const float MinRunLength = 4f;
    private const float MaxClimbAngleDeg = 48f;
    private const float BandFraction = 0.2f;           // top/bottom height band used to find the two ramp-end centroids
    private const float DirectionSimilarity = 0.5f;    // dot-product threshold (~60 deg) for joining a step region

    public static void EnsureRampsForRoom(GameObject room)
    {
        if (room == null) return;

        Transform roomTransform = room.transform;
        if (roomTransform.Find("AutoStairRamps") != null) return; // already built for this room

        if (!RoomRendererBounds(room, out Bounds roomBounds)) return;

        bool foundFloor = FindFloor(room, roomBounds, out float floorY, out Bounds floorBounds);
        if (!foundFloor)
        {
            // No floor collider found — fall back to the bottom of the room's render bounds.
            floorBounds = roomBounds;
            floorY = (roomBounds.center.y - roomBounds.extents.y) + 1f;
        }

        float floorMaxX = floorBounds.center.x + floorBounds.extents.x;
        float floorMinZ = floorBounds.center.z - floorBounds.extents.z;
        float floorMaxZ = floorBounds.center.z + floorBounds.extents.z;
        float scanMinX = (floorBounds.center.x - floorBounds.extents.x) - ScanPadding;
        float scanMinZ = floorMinZ - ScanPadding;
        float scanTop = floorY + ScanRayHeight;

        int gridSizeX = Mathf.Max(2, (int)(((floorMaxX + ScanPadding) - scanMinX) / CellSize) + 1);
        int gridSizeZ = Mathf.Max(2, (int)(((floorMaxZ + ScanPadding) - scanMinZ) / CellSize) + 1);
        // NOTE: the decompile also special-cased the division result being +Infinity (forcing
        // the size back to 2). Can't happen here since ScanPadding/CellSize are finite
        // compile-time constants, so that branch is omitted.

        var heightMap = new float[gridSizeX, gridSizeZ];
        var hitMask = new bool[gridSizeX, gridSizeZ];
        var walkableMask = new bool[gridSizeX, gridSizeZ];

        for (int x = 0; x < gridSizeX; x++)
        {
            for (int z = 0; z < gridSizeZ; z++)
            {
                Vector3 samplePoint = new Vector3(scanMinX + x * CellSize, scanTop, scanMinZ + z * CellSize);
                bool hit = SampleSurface(roomTransform, samplePoint, out float sampleY, out float sampleNormalAngle, out bool sampleIsRamp);
                if (hit)
                {
                    heightMap[x, z] = sampleY;
                    hitMask[x, z] = true;
                    walkableMask[x, z] = sampleNormalAngle < FlatnessAngleThreshold && !sampleIsRamp;
                }
            }
        }

        // --- Step-edge detection: central-difference height gradient at every interior walkable cell ---
        var edgeMask = new bool[gridSizeX, gridSizeZ];
        var gradX = new float[gridSizeX, gridSizeZ];
        var gradZ = new float[gridSizeX, gridSizeZ];

        if (GradientRadius < gridSizeX - GradientRadius && GradientRadius < gridSizeZ - GradientRadius)
        {
            float gradientSpan = (GradientRadius + GradientRadius) * CellSize;
            for (int x = GradientRadius; x < gridSizeX - GradientRadius; x++)
            {
                for (int z = GradientRadius; z < gridSizeZ - GradientRadius; z++)
                {
                    if (!walkableMask[x, z]) continue;
                    if (!hitMask[x + GradientRadius, z] || !hitMask[x - GradientRadius, z] ||
                        !hitMask[x, z + GradientRadius] || !hitMask[x, z - GradientRadius])
                        continue;

                    float gx = (heightMap[x + GradientRadius, z] - heightMap[x - GradientRadius, z]) / gradientSpan;
                    float gz = (heightMap[x, z + GradientRadius] - heightMap[x, z - GradientRadius]) / gradientSpan;
                    float slope = Mathf.Sqrt(gx * gx + gz * gz);
                    if (slope >= StepSlopeMin && slope <= StepSlopeMax)
                    {
                        edgeMask[x, z] = true;
                        gradX[x, z] = gx;
                        gradZ[x, z] = gz;
                    }
                }
            }
        }

        // --- Group connected step cells (flood fill) and build a ramp for each qualifying group ---
        var rampsParent = new GameObject("AutoStairRamps");
        Transform rampsParentTransform = rampsParent.transform;
        rampsParentTransform.SetParent(roomTransform, false);

        var visited = new bool[gridSizeX, gridSizeZ];
        var stack = new Stack<int>();
        var component = new List<int>();
        int rampCount = 0;

        for (int x = 0; x < gridSizeX; x++)
        {
            for (int z = 0; z < gridSizeZ; z++)
            {
                if (!edgeMask[x, z] || visited[x, z]) continue;

                float seedDirX = gradX[x, z];
                float seedDirZ = gradZ[x, z];
                float seedMag = Mathf.Sqrt(seedDirX * seedDirX + seedDirZ * seedDirZ);
                if (seedMag <= 1e-5f) { seedDirX = 0f; seedDirZ = 0f; }
                else { seedDirX /= seedMag; seedDirZ /= seedMag; }

                stack.Clear();
                component.Clear();
                visited[x, z] = true;
                stack.Push(x * 100000 + z); // NOTE: decompile packs (x,z) into one int as x*100000+z; preserved as-is

                // Flood-fill outward through connected edge cells whose gradient direction stays
                // close to the SEED cell's direction. Confirmed via trace: this compares every
                // candidate against the original seed's direction for the whole pass, not a
                // running/updated reference direction — kept faithful even though it means a
                // region can fragment slightly along a curving step.
                while (stack.Count > 0)
                {
                    int code = stack.Pop();
                    component.Add(code);
                    int cx = code / 100000;
                    int cz = code % 100000;

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int nx = cx + dx;
                            int nz = cz + dz;
                            if (nx < 0 || nz < 0 || nx >= gridSizeX || nz >= gridSizeZ) continue;
                            if (!edgeMask[nx, nz] || visited[nx, nz]) continue;

                            float gx = gradX[nx, nz];
                            float gz = gradZ[nx, nz];
                            float gmag = Mathf.Sqrt(gx * gx + gz * gz);
                            if (gmag <= 1e-5f) { gx = 0f; gz = 0f; }
                            else { gx /= gmag; gz /= gmag; }

                            if (seedDirX * gx + seedDirZ * gz >= DirectionSimilarity)
                            {
                                visited[nx, nz] = true;
                                stack.Push(nx * 100000 + nz);
                            }
                        }
                    }
                }

                if (component.Count < MinComponentCells) continue;

                float maxHeight = float.MinValue;
                float minHeight = float.MaxValue;
                foreach (int code in component)
                {
                    float h = heightMap[code / 100000, code % 100000];
                    if (h > maxHeight) maxHeight = h;
                    if (h < minHeight) minHeight = h;
                }
                if (maxHeight - minHeight < MinStepHeight) continue;

                float lowBand = minHeight + (maxHeight - minHeight) * BandFraction;
                float highBand = maxHeight - (maxHeight - minHeight) * BandFraction;

                int lowCount = 0, highCount = 0;
                float sumLowX = 0f, sumLowY = 0f, sumLowZ = 0f;
                float sumHighX = 0f, sumHighY = 0f, sumHighZ = 0f;
                foreach (int code in component)
                {
                    int cx = code / 100000;
                    int cz = code % 100000;
                    float h = heightMap[cx, cz];
                    float wx = scanMinX + cx * CellSize;
                    float wz = scanMinZ + cz * CellSize;
                    if (h <= lowBand) { sumLowX += wx; sumLowZ += wz; sumLowY += h; lowCount++; }
                    if (h >= highBand) { sumHighX += wx; sumHighZ += wz; sumHighY += h; highCount++; }
                }
                if (lowCount == 0 || highCount == 0) continue;

                Vector3 lowCentroid = new Vector3(sumLowX / lowCount, sumLowY / lowCount, sumLowZ / lowCount);
                Vector3 highCentroid = new Vector3(sumHighX / highCount, sumHighY / highCount, sumHighZ / highCount);

                float dx3 = highCentroid.x - lowCentroid.x;
                float dz3 = highCentroid.z - lowCentroid.z;
                float horizDist = Mathf.Sqrt(dx3 * dx3 + dz3 * dz3);
                if (horizDist < MinRunLength) continue;

                float dy3 = highCentroid.y - lowCentroid.y;
                float verticalAngleDeg = Mathf.Atan2(Mathf.Abs(dy3), horizDist) * Mathf.Rad2Deg;
                if (verticalAngleDeg > MaxClimbAngleDeg) continue;

                // Width of the step region, measured perpendicular to the travel direction.
                float perpX = -dz3 / horizDist;
                float perpZ = dx3 / horizDist;
                float minProj = float.MaxValue, maxProj = float.MinValue;
                foreach (int code in component)
                {
                    int cx = code / 100000;
                    int cz = code % 100000;
                    float wx = scanMinX + cx * CellSize;
                    float wz = scanMinZ + cz * CellSize;
                    float proj = perpX * wx + perpZ * wz;
                    if (proj < minProj) minProj = proj;
                    if (proj > maxProj) maxProj = proj;
                }
                float rampWidth = (maxProj - minProj) + 5f;

                // NOTE: this is the FULL 3D distance between the two centroids (includes the
                // vertical rise), not the horizDist computed above — confirmed by tracing the
                // exact order the decompiled float register gets reassigned in.
                Vector3 diff = highCentroid - lowCentroid;
                float slopeLength = diff.magnitude;
                Vector3 forward = diff / slopeLength;
                Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
                Vector3 rotatedUp = rotation * Vector3.up;

                rampCount++;
                var ramp = new GameObject("StairRamp_Auto" + rampCount);
                ramp.layer = 0;
                Transform rampTransform = ramp.transform;

                Vector3 midpoint = (highCentroid + lowCentroid) * 0.5f;
                rampTransform.position = midpoint - rotatedUp * 0.6f;
                rampTransform.rotation = rotation;

                BoxCollider box = ramp.AddComponent<BoxCollider>();
                box.size = new Vector3(rampWidth, 2f, slopeLength + 6f);

                rampTransform.SetParent(rampsParentTransform, true);
            }
        }

        Debug.Log("[StairRamp] Auto-generated " + rampCount + " ramp(s) for room '" + room.name +
                  "' (floorY=" + floorY.ToString("F1") + ", scanTop=" + scanTop.ToString("F1") + ").");
    }

    private static bool RoomRendererBounds(GameObject room, out Bounds bounds)
    {
        bounds = default;
        Renderer[] renderers = room.GetComponentsInChildren<Renderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            if (i == 0) bounds = renderers[i].bounds;
            else bounds.Encapsulate(renderers[i].bounds);
        }
        // NOTE: decompiled as manual component-wise min/max + recomputed center/extents;
        // confirmed equivalent to Bounds.Encapsulate, used here for clarity.
        return renderers.Length > 0;
    }

    private static bool FindFloor(GameObject room, Bounds roomBounds, out float floorY, out Bounds floorBounds)
    {
        floorY = 0f;
        floorBounds = default;

        // NOTE: decompiled as (roomBounds.center.y - roomBounds.extents.y) + roomBounds.extents.y,
        // which algebraically simplifies to roomBounds.center.y — verified via trace, not a guess.
        float roomMidY = roomBounds.center.y;

        Collider[] colliders = room.GetComponentsInChildren<Collider>();
        Collider best = null;
        float bestArea = 0f;

        for (int i = 0; i < colliders.Length; i++)
        {
            Bounds cb = colliders[i].bounds;
            bool isFlat = cb.extents.y * 2f <= 6f;
            // NOTE: original branches on '<' vs '==' vs '>' against roomMidY separately, but
            // both '<' and '==' fall through to the same area comparison/selection below, and
            // '>' (and non-flat) never do — collapsed to a single '<=' here. Independently
            // re-verified case-by-case (not-flat / less / equal / greater) against the raw
            // three-way boolean logic; all four cases match exactly.
            if (isFlat && cb.center.y <= roomMidY)
            {
                float area = (cb.extents.x * 2f) * (cb.extents.z * 2f);
                // NOTE: this prefers the LARGEST footprint among flat/low candidates (bestArea
                // starts at 0 and only grows) — i.e. "biggest flat thing below room-middle wins",
                // not smallest. Confirmed via trace, this looked backwards at first glance.
                if (area > bestArea)
                {
                    bestArea = area;
                    best = colliders[i];
                    floorBounds = cb;
                }
            }
        }

        if (best != null)
        {
            floorY = floorBounds.center.y + floorBounds.extents.y;
        }
        return best != null;
    }

    private static bool SampleSurface(Transform roomTf, Vector3 origin, out float y, out float normalAngle, out bool isExistingRamp)
    {
        y = 0f;
        normalAngle = 0f;
        isExistingRamp = false;

        // direction confirmed via the standing Vector3 static-table offset (0x24 = down)
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, RayDistance, -1, QueryTriggerInteraction.Ignore);

        bool found = false;
        float bestY = float.MinValue;
        RaycastHit best = default;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null) continue;
            if (!hit.collider.transform.IsChildOf(roomTf)) continue; // only consider hits belonging to this room
            if (hit.point.y > bestY)
            {
                bestY = hit.point.y;
                best = hit;
                found = true;
            }
        }

        if (found)
        {
            y = best.point.y;
            // NOTE: decompiled as a manual dot/acos with a 1e-15 epsilon guard on near-zero
            // vectors; confirmed equivalent to Vector3.Angle.
            normalAngle = Vector3.Angle(best.normal, Vector3.up);
            isExistingRamp = best.collider != null && best.collider.name.StartsWith("StairRamp");
        }

        return found;
    }
}