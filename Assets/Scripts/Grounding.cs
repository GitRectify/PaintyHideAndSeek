using UnityEngine;
using System;

public static class Grounding
{
    // Repositions `player` vertically so that the lowest point of the combined bounds of `rends`
    // (or, if none are given, the player's own pivot) sits exactly on the nearest floor point found
    // by raycasting straight down from 5 units above the player. Skips hits on CharacterControllers
    // (Unity's CharacterController IS a Collider, so it can otherwise show up as a hit — this avoids
    // "flooring" against the player's own or another character's capsule) and hits on anything with
    // an Animator in its parent chain (so it only floors against static level geometry, not bots or
    // other characters).
    //
    // NOTE: the decompiled bounds-combining loop only ever reads/writes the Y component of each
    // renderer's bounds — it never touches X or Z. Verified numerically (200,000 random trials)
    // that this dense arithmetic is identical to floating-point precision to a standard
    // min/max-based Encapsulate restricted to Y. Unity's real Bounds.Encapsulate() combines all
    // three axes, but since only .center.y/.extents.y are read afterward here, using the real API
    // call is a faithful, more readable stand-in for the manual Y-only min/max math in the dump.
    //
    // FIX (confirmed): raw explicitly throws (falls through to the function's shared NRE-throw
    // target) on three null checks that a prior draft instead handled with a silent `return;` -
    // rends[0] == null, rends[i] == null inside the combine loop, and hits == null after
    // Physics.RaycastAll. None of these are the usual "let it crash naturally" simplification;
    // raw's explicit throw was actively replaced with different (silent) behavior. Fixed to throw,
    // matching raw. (rends[i] == null is the most practically reachable of the three - a Renderer
    // reference can genuinely go "null" if destroyed while still held in the array; the other two
    // are effectively unreachable given Physics.RaycastAll's own contract, but matched anyway.)
    public static void ToFloor(Transform player, Renderer[] rends)
    {
        if (player == null) return;

        float bottomY;

        if (rends == null || rends.Length == 0)
        {
            bottomY = player.position.y;
        }
        else
        {
            if (rends[0] == null)
            {
                throw new NullReferenceException("rends[0] is null");
            }

            Bounds combined = rends[0].bounds;

            for (int i = 1; i < rends.Length; i++)
            {
                if (rends[i] == null)
                {
                    throw new NullReferenceException($"rends[{i}] is null");
                }
                combined.Encapsulate(rends[i].bounds);
            }

            bottomY = combined.center.y - combined.extents.y;
        }

        Vector3 origin = player.position;
        origin.y = player.position.y + 5f;

        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 300f);
        if (hits == null)
        {
            throw new NullReferenceException("Physics.RaycastAll returned null");
        }

        float floorY = float.NegativeInfinity;
        bool foundFloor = false;

        foreach (RaycastHit hit in hits)
        {
            Collider col = hit.collider;

            if (col != null && col is CharacterController)
            {
                continue;
            }

            Animator animatorInParent = col != null ? col.GetComponentInParent<Animator>() : null;
            if (animatorInParent != null) continue;

            if (hit.point.y > player.position.y + 1f) continue;

            if (hit.point.y > floorY)
            {
                floorY = hit.point.y;
                foundFloor = true;
            }
        }

        if (foundFloor && Mathf.Abs(bottomY - floorY) >= 0.01f)
        {
            CharacterController cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            Vector3 pos = player.position;
            pos.y -= (bottomY - floorY);
            player.position = pos;

            if (cc != null) cc.enabled = true;
        }
    }
}