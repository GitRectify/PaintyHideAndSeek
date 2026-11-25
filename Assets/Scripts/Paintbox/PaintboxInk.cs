using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Keeps a button built by OnlineUI in design B whatever colour its screen gives it later: the
/// fill is adapted to the palette (OnlineUI.Adapt) and its words and icons take the ink or white
/// that reads on it. Checks only when the colour changed.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Image))]
public sealed class PaintboxInk : MonoBehaviour
{
    private Image image;
    private Color seen = new Color(-1f, 0f, 0f, 0f);

    private void Awake() => image = GetComponent<Image>();

    private void LateUpdate()
    {
        if (image == null || image.color == seen) return;
        Color adapted = OnlineUI.Adapt(image.color);
        if (adapted != image.color) image.color = adapted;
        seen = image.color;
        Color ink = OnlineUI.InkOn(seen);
        foreach (Text t in GetComponentsInChildren<Text>(true))
        {
            if (t.transform.parent != transform) continue;   // a label of this button, not of something inside it
            t.color = new Color(ink.r, ink.g, ink.b, t.color.a);
        }
    }
}

/// <summary>
/// The press of a design B button whose Image is its fill (OnlineUI.NewButton): it sinks into its
/// shadow while held and springs back.
/// </summary>
[DisallowMultipleComponent]
public sealed class PaintboxPressFlat : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    private Shadow drop;
    private Selectable selectable;
    private Vector2 depth;
    private Vector2 restAt;
    private bool down;

    private void Awake()
    {
        selectable = GetComponent<Selectable>();
        foreach (Shadow s in GetComponents<Shadow>()) if (!(s is Outline)) drop = s;
        if (drop != null) depth = drop.effectDistance;
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
        if (down == pressed || drop == null) return;
        down = pressed;
        float sink = Mathf.Abs(depth.y) * 0.7f;
        drop.effectDistance = pressed ? new Vector2(depth.x, depth.y + sink) : depth;
        var rt = (RectTransform)transform;
        if (pressed)
        {
            restAt = rt.anchoredPosition;
            rt.anchoredPosition = restAt + new Vector2(0f, -sink);
        }
        else rt.anchoredPosition = restAt;   // where it was, never a step further
    }
}
