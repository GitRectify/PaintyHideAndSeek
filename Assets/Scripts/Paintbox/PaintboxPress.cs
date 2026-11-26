using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// A Paintbox button's press: while a finger (or the mouse) is down on it, its body sinks into
/// its shadow and the shadow goes, as a toy button would; it springs back on release. Only the
/// look changes, never what the button does.
/// </summary>
[DisallowMultipleComponent]
public sealed class PaintboxPress : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    private PaintboxFrame frame;
    private Selectable selectable;
    private bool down;
    private bool shadeWasOn;

    private void Awake()
    {
        frame = GetComponent<PaintboxFrame>();
        selectable = GetComponent<Selectable>();
    }

    public void OnPointerDown(PointerEventData e)
    {
        if (selectable != null && !selectable.IsInteractable()) return;
        Set(true);
    }

    public void OnPointerUp(PointerEventData e) => Set(false);
    public void OnPointerExit(PointerEventData e) => Set(false);
    private void OnDisable() => Set(false);

    private void Set(bool pressed)
    {
        if (frame == null || frame.Body == null || down == pressed) return;
        down = pressed;
        float depth = pressed ? frame.ShadowDepth * 0.7f : 0f;
        frame.Body.offsetMin = new Vector2(0f, -depth);
        frame.Body.offsetMax = new Vector2(0f, -depth);
        if (frame.Shade == null) return;

        // Back as it was, not simply on: an unpicked tab has no shadow, and one switched on behind
        // its clear fill turned the tab black (2026-11-26).
        if (pressed) shadeWasOn = frame.Shade.enabled;
        frame.Shade.enabled = !pressed && shadeWasOn;
    }
}

/// <summary>Keeps a screen's content inside the device's safe area (notches, rounded corners).</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public sealed class PaintboxSafeArea : MonoBehaviour
{
    private Rect applied;
    private Vector2Int screen;

    private void OnEnable() => Apply(true);
    private void Update() => Apply(false);

    private void Apply(bool force)
    {
        Rect safe = Screen.safeArea;
        var size = new Vector2Int(Screen.width, Screen.height);
        if (!force && safe == applied && size == screen) return;
        applied = safe;
        screen = size;
        if (Screen.width <= 0 || Screen.height <= 0) return;

        var rt = (RectTransform)transform;
        rt.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
        rt.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
