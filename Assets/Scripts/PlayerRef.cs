using UnityEngine;

// NOTE: Resolves this session's long-standing "PlayerRef.Resolve(string) -> GameObject" caveat -
// previously only known from call sites (JoystickMover, Mode1FirstPersonGun, etc.), now fully
// reversed. Confirmed as a plain static class (no instance methods/fields at all - every method
// here decompiles with no __this parameter). Active is a real static property (explicit
// get_Active/set_Active), not a bare field.
public static class PlayerRef
{
    // FIX (confirmed): raw get_Active doesn't just return the stored field - it does Unity's
    // overloaded fake-null check (op_Inequality against null) on the stored transform and only
    // returns it if that check passes; otherwise it explicitly returns real null. A bare
    // `{ get; set; }` auto-property would instead hand back a destroyed-but-non-null ("fake
    // null") Transform reference as-is. Note this only matters for callers that bypass Unity's
    // `==`/`!=` overload (e.g. `is null`, `?.`, or boxing to `object`/`UnityEngine.Object` for
    // reference comparison) - callers using `!= null`/`== null` directly get the same answer
    // either way, since that overload does its own fake-null check at the comparison site.
    // Matched exactly anyway, since raw clearly does this deliberately.
    private static Transform activeBacking;

    public static Transform Active
    {
        get { return (activeBacking != null) ? activeBacking : null; }
        set { activeBacking = value; }
    }

    // Prefers the explicitly-set Active transform; otherwise looks up fallbackName via
    // GameObject.Find, then falls back further to a full scene scan if that also misses.
    public static GameObject Resolve(string fallbackName)
    {
        Transform active = Active;
        if (active != null)
        {
            return active.gameObject;
        }

        if (!string.IsNullOrEmpty(fallbackName))
        {
            GameObject found = GameObject.Find(fallbackName);
            if (found != null)
            {
                return found;
            }
            return FindInSceneByName(fallbackName);
        }

        return null;
    }

    public static Transform ResolveTransform(string fallbackName)
    {
        GameObject go = Resolve(fallbackName);
        return (go != null) ? go.transform : null;
    }

    // NOTE: unlike the many "FindInScene" helpers seen throughout this codebase (which try
    // GameObject.Find first, then fall back to scanning), this one skips straight to the
    // Resources.FindObjectsOfTypeAll scan - because Resolve() above already tried
    // GameObject.Find itself before calling this as its own fallback.
    private static GameObject FindInSceneByName(string name)
    {
        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t.name == name && t.gameObject.scene.IsValid())
            {
                return t.gameObject;
            }
        }
        return null;
    }
}