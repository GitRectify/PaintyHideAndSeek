using UnityEngine;
using UnityEngine.SceneManagement;

// NOTE: Fully resolves SceneUI - every member here was previously only known from call sites
// across many earlier sessions (GetOrCreate, GetOrCreateCanvas, GetOrAdd<T>, UIRoot, HUDRoot),
// used throughout this codebase's many runtime-built UI panels (MatchHUD, MatchmakingUI,
// MapSelectUI, RemoveAdsUI, Mode1FirstPersonGun, PoseSelectorUI, and others).
//
// RE-VERIFICATION PASS: found one real, wide-impact bug — see the FIXED note in
// GetOrCreateCanvas below. Because every canvas in the codebase is created through that method,
// this changes which transform they're all actually parented under.
public static class SceneUI
{
    // Finds (or creates, parented under it) a root-level GameObject named "UI" among the active
    // scene's root objects.
    public static Transform UIRoot()
    {
        Scene scene = SceneManager.GetActiveScene();
        GameObject[] roots = scene.GetRootGameObjects();

        for (int i = 0; i < roots.Length; i++)
        {
            if (roots[i] != null && roots[i].name == "UI")
            {
                return roots[i].transform;
            }
        }

        GameObject go = new GameObject("UI");
        return go.transform;
    }

    // Finds (or creates) a child of UIRoot() named "InGameHideCanvas".
    public static Transform HUDRoot()
    {
        Transform uiRoot = UIRoot();
        Transform existing = uiRoot.Find("InGameHideCanvas");
        if (existing != null)
        {
            return existing;
        }

        GameObject go = new GameObject("InGameHideCanvas");
        go.transform.SetParent(uiRoot, false);
        return go.transform;
    }

    // Finds (or creates) a direct child of `name` under HUDRoot() — NOT UIRoot() directly.
    // FIXED: raw calls SceneUI$$HUDRoot(...) for the parent here, not SceneUI$$UIRoot() — the
    // draft had this parenting new/found canvases directly under the scene's raw "UI" root
    // instead of under the "InGameHideCanvas" group HUDRoot() resolves. Since every canvas in
    // this codebase (status HUDs, paint UI, climb buttons, etc.) is created through this method,
    // this changes where all of them actually end up in the hierarchy.
    public static GameObject GetOrCreateCanvas(string name, out bool created)
    {
        Transform hudRoot = HUDRoot();
        Transform existing = hudRoot.Find(name);
        if (existing != null)
        {
            created = false;
            return existing.gameObject;
        }

        GameObject go = new GameObject(name);
        go.transform.SetParent(hudRoot, false);
        created = true;
        return go;
    }

    // Finds (or creates) a direct child of `parent` named `name`. If parent is null, defaults to
    // UIRoot().
    public static GameObject GetOrCreate(string name, Transform parent, out bool created)
    {
        if (parent == null)
        {
            parent = UIRoot();
        }

        Transform existing = parent.Find(name);
        if (existing != null)
        {
            created = false;
            return existing.gameObject;
        }

        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        created = true;
        return go;
    }

    public static T GetOrAdd<T>(GameObject go) where T : Component
    {
        T component = go.GetComponent<T>();
        if (component == null)
        {
            component = go.AddComponent<T>();
        }
        return component;
    }
}