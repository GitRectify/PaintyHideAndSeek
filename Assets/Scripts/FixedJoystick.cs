using UnityEngine;
using UnityEngine.EventSystems;

public class FixedJoystick : MonoBehaviour,
    IPointerDownHandler,
    IDragHandler,
    IPointerUpHandler
{
    [Header("References")]
    [SerializeField] private RectTransform background;
    [SerializeField] private RectTransform handle;

    [Header("Settings")]
    [SerializeField, Range(0f, 1f)] private float deadZone = 0.1f;
    [SerializeField, Range(0.1f, 1f)] private float handleRange = 0.65f;

    private Vector2 input;

    public Vector2 Direction => input;
    public float Horizontal => input.x;
    public float Vertical => input.y;

    private void Awake()
    {
        if (background == null)
            background = transform as RectTransform;

        ResetJoystick();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        OnDrag(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (background == null || handle == null)
            return;

        Camera eventCamera = eventData.pressEventCamera;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                background,
                eventData.position,
                eventCamera,
                out Vector2 localPoint))
        {
            return;
        }

        // Convert local position to normalized joystick coordinates.
        Vector2 halfSize = background.rect.size * 0.5f;

        Vector2 normalized = new Vector2(
            halfSize.x > 0f ? localPoint.x / halfSize.x : 0f,
            halfSize.y > 0f ? localPoint.y / halfSize.y : 0f
        );

        // Circular joystick limit.
        input = Vector2.ClampMagnitude(normalized, 1f);

        // Dead zone.
        if (input.magnitude < deadZone)
        {
            input = Vector2.zero;
        }
        else
        {
            // Rescale so movement starts smoothly after the dead zone.
            float magnitude =
                (input.magnitude - deadZone) / (1f - deadZone);

            input = input.normalized * Mathf.Clamp01(magnitude);
        }

        UpdateHandle();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        ResetJoystick();
    }

    private void UpdateHandle()
    {
        Vector2 radius = background.rect.size * 0.5f * handleRange;

        handle.anchoredPosition = new Vector2(
            input.x * radius.x,
            input.y * radius.y
        );
    }

    private void ResetJoystick()
    {
        input = Vector2.zero;

        if (handle != null)
            handle.anchoredPosition = Vector2.zero;
    }
}