using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Marks a button as its screen's way back (Back, Close, Cancel, Stay, Done): the phone's Back
/// button presses the top one of these (BackKey).
/// </summary>
[DisallowMultipleComponent]
public sealed class BackTarget : MonoBehaviour
{
    internal static readonly List<BackTarget> Shown = new List<BackTarget>();

    private void OnEnable() => Shown.Add(this);
    private void OnDisable() => Shown.Remove(this);
}

/// <summary>
/// The phone's Back button (2026-11-25), which Android sends as Escape, as a keyboard's Esc is:
/// it does what the screen's own way back does. The marked button (BackTarget) drawn on top is
/// pressed: a dialog's Cancel or Stay, a page's Back, a tray's Close, the Shop's Back (which closes
/// an item before the Shop). In a round with nothing open it opens the round menu, as the menu
/// button does; at Home it asks whether to exit the game.
/// </summary>
public sealed class BackKey : MonoBehaviour
{
    private static BackKey instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (instance != null) return;
        var go = new GameObject("BackKey");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<BackKey>();
    }

    /// <summary>Makes <paramref name="button"/> a way back: the phone's Back presses it while it is the top one shown.</summary>
    public static void Mark(Button button)
    {
        if (button != null) Paintbox.Ensure<BackTarget>(button.gameObject);
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;
        if (KeyBindings.Blocked) return;   // binding a key, or typing (the keyboard is closed first)
        Press();
    }

    /// <summary>Does what Back does now; false when there was nothing to go back from.</summary>
    public static bool Press()
    {
        // Loading: nothing to go back to yet.
        GameObject loading = GameObject.Find("LoadingCanvas");
        if (loading != null && loading.activeInHierarchy) return false;

        Button top = TopTarget();
        if (top != null)
        {
            top.onClick.Invoke();
            return true;
        }

        if (PaintboxHud.ToggleMenu()) return true;   // in a round: the round menu

        // Home, with nothing over it: the question before the game closes.
        SettingsUI settings = FindAnyObjectByType<SettingsUI>(FindObjectsInactive.Include);
        if (settings != null)
        {
            settings.AskExit();
            return true;
        }
        return false;
    }

    // The marked button drawn on top: on the highest canvas, and on it the last drawn.
    private static Button TopTarget()
    {
        Button best = null;
        int bestOrder = int.MinValue;
        List<int> bestPath = null;

        for (int i = BackTarget.Shown.Count - 1; i >= 0; i--)
        {
            BackTarget target = BackTarget.Shown[i];
            if (target == null) { BackTarget.Shown.RemoveAt(i); continue; }
            Button button = target.GetComponent<Button>();
            if (button == null || !button.isActiveAndEnabled || !button.IsInteractable() || !Visible(target.transform)) continue;

            int order = SortingOrder(target.transform);
            List<int> path = PathOf(target.transform);
            if (best == null || order > bestOrder || (order == bestOrder && Later(path, bestPath)))
            {
                best = button;
                bestOrder = order;
                bestPath = path;
            }
        }
        return best;
    }

    private static bool Visible(Transform t)
    {
        foreach (CanvasGroup g in t.GetComponentsInParent<CanvasGroup>())
        {
            if (!g.enabled) continue;
            if (g.alpha <= 0.01f || !g.interactable || !g.blocksRaycasts) return false;
            if (g.ignoreParentGroups) break;
        }
        Canvas canvas = t.GetComponentInParent<Canvas>();
        return canvas != null && canvas.isActiveAndEnabled;
    }

    private static int SortingOrder(Transform t)
    {
        Canvas c = t.GetComponentInParent<Canvas>();
        while (c != null && !c.isRootCanvas && !c.overrideSorting)
            c = c.transform.parent != null ? c.transform.parent.GetComponentInParent<Canvas>() : null;
        return c != null ? c.sortingOrder : 0;
    }

    // Sibling indices from the root down: a later one is drawn over an earlier one on the same canvas.
    private static List<int> PathOf(Transform t)
    {
        var path = new List<int>();
        for (; t != null; t = t.parent) path.Add(t.GetSiblingIndex());
        path.Reverse();
        return path;
    }

    private static bool Later(List<int> a, List<int> b)
    {
        if (b == null) return true;
        int n = Mathf.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) return a[i] > b[i];
        return a.Count > b.Count;
    }
}
