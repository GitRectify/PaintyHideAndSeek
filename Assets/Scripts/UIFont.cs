using UnityEngine;

// New class this session (call-surface never seen before). Only the Default getter has been
// decompiled — there may be more members not yet referenced by anything pasted so far.
public static class UIFont
{
    // NOTE: backing field name is inferred (decompiled as a static field read off the type's
    // static-fields block, no name recoverable) — a private static cache is the obvious fit
    // for a lazily-initialized static property.
    private static Font _default;

    public static Font Default
    {
        get
        {
            if (_default == null)
            {
                _default = Resources.Load<Font>("MikadoMedium");
            }
            if (_default == null)
            {
                // Fallback to Unity's built-in legacy font if the custom one isn't found.
                _default = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            return _default;
        }
    }
}