using UnityEngine;

// NOTE: Resolves a previously never-reversed class from the original handoff notes - only the
// call surface (DisableCasting) was known before. Plain static utility, no fields, no instance
// members.
//
// RE-VERIFICATION PASS: found one real bug — see FIXED note below. Unlike most of this
// codebase's "crash on null" convention, this particular function actually DOES guard against a
// null roomRoot in the raw code (returns silently instead of crashing); the draft had dropped
// that guard.
public static class RoomShadows
{
    public static void DisableCasting(GameObject roomRoot)
    {
        // FIXED: raw explicitly checks roomRoot != null and returns silently (no crash) when it
        // is - the draft had removed this guard, which would throw a NullReferenceException
        // instead. This matters in practice: the string overload below passes GameObject.Find's
        // result straight through, which is legitimately null when the named room doesn't exist,
        // and the original clearly intends that to be a silent no-op, not a crash.
        if (roomRoot == null)
        {
            return;
        }

        Renderer[] renderers = roomRoot.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r != null && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }
    }

    public static void DisableCasting(string roomName)
    {
        if (string.IsNullOrEmpty(roomName))
        {
            return;
        }

        GameObject roomRoot = GameObject.Find(roomName);
        DisableCasting(roomRoot);
    }
}