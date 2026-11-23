using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// A button that answers the finger: it sinks a little while held and springs back. Only its own
/// scale changes, never what the button does. Runs only while it moves; put back to rest when hidden.
/// </summary>
[DisallowMultipleComponent]
public class UiPress : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
                       IPointerExitHandler
{
    private const float Pressed = 0.95f;
    private float target = 1f, current = 1f;
    private Selectable selectable;

    private void Awake() => selectable = GetComponent<Selectable>();

    public void OnPointerDown(PointerEventData e)
    {
        if (selectable == null || selectable.IsInteractable()) target = Pressed;
    }

    public void OnPointerUp(PointerEventData e) => target = 1f;
    public void OnPointerExit(PointerEventData e) => target = 1f;

    private void OnDisable()
    {
        target = current = 1f;
        transform.localScale = Vector3.one;
    }

    private void Update()
    {
        if (current == target) return;
        current = Mathf.MoveTowards(current, target, Time.unscaledDeltaTime * 0.9f);
        transform.localScale = Vector3.one * current;
    }
}
