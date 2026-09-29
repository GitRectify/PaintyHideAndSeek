using UnityEngine;
using UnityEngine.EventSystems;

// NOTE: Base class for the on-screen UI joystick asset ("Joystick Pack").
// FixedJoystick, FloatingJoystick, DynamicJoystick, and VariableJoystick all derive from this.
public class Joystick : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
{
    public float Horizontal => snapX ? SnapFloat(input.x, AxisOptions.Horizontal) : input.x;
    public float Vertical => snapY ? SnapFloat(input.y, AxisOptions.Vertical) : input.y;
    public Vector2 Direction => new Vector2(Horizontal, Vertical);

    public float HandleRange
    {
        get => handleRange;
        set => handleRange = Mathf.Abs(value);
    }

    public float DeadZone
    {
        get => deadZone;
        set => deadZone = Mathf.Abs(value);
    }

    // NOTE: decompiled get_AxisOptions was `return (int32_t)__this;` — clearly a decompiler
    // artifact (casting the instance pointer to int), not real logic. Reconstructed as a normal
    // field getter to match the pattern of SnapX/SnapY and the AxisOptions setter below.
    public AxisOptions AxisOptions
    {
        get => axisOptions;
        set => axisOptions = value;
    }

    public bool SnapX
    {
        get => snapX;
        set => snapX = value;
    }

    public bool SnapY
    {
        get => snapY;
        set => snapY = value;
    }

    [SerializeField] private float handleRange = 1f;
    [SerializeField] private float deadZone = 0f;
    [SerializeField] private AxisOptions axisOptions = AxisOptions.Both;
    [SerializeField] private bool snapX = false;
    [SerializeField] private bool snapY = false;

    [SerializeField] protected RectTransform background = null;
    [SerializeField] private RectTransform handle = null;
    protected RectTransform baseRect = null;

    private Canvas canvas;
    private Camera cam;

    protected Vector2 input = Vector2.zero;

    protected virtual void Start()
    {
        HandleRange = handleRange;
        DeadZone = deadZone;
        baseRect = GetComponent<RectTransform>();
        canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("The Joystick is not placed inside a canvas");
        }

        Vector2 center = new Vector2(0.5f, 0.5f);
        background.pivot = center;
        handle.anchorMin = center;
        handle.anchorMax = center;
        handle.pivot = center;
        handle.anchoredPosition = Vector2.zero;
    }

    // NOTE: decompiled Joystick$$OnPointerDown has a body byte-identical to Joystick$$OnDrag
    // (same input-computation logic duplicated in full) — this is the exact-duplicate-method
    // pattern called out in prior sessions. Reconstructed as the real source almost certainly
    // is: OnPointerDown just forwards to OnDrag.
    public virtual void OnPointerDown(PointerEventData eventData)
    {
        OnDrag(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        cam = null;
        if (canvas.renderMode == RenderMode.ScreenSpaceCamera)
        {
            cam = canvas.worldCamera;
        }

        Vector2 position = RectTransformUtility.WorldToScreenPoint(cam, background.position);
        Vector2 radius = background.sizeDelta / 2f;
        input = (eventData.position - position) / (radius * canvas.scaleFactor);
        FormatInput();
        HandleInput(input.magnitude, input.normalized, radius, cam);
        handle.anchoredPosition = input * radius * handleRange;
    }

    protected virtual void HandleInput(float magnitude, Vector2 normalised, Vector2 radius, Camera cam)
    {
        if (magnitude > deadZone)
        {
            if (magnitude > 1f)
            {
                input = normalised;
            }
        }
        else
        {
            input = Vector2.zero;
        }
    }

    private void FormatInput()
    {
        if (axisOptions == AxisOptions.Horizontal)
        {
            input = new Vector2(input.x, 0f);
        }
        else if (axisOptions == AxisOptions.Vertical)
        {
            input = new Vector2(0f, input.y);
        }
    }

    private float SnapFloat(float value, AxisOptions snapAxis)
    {
        if (value == 0f)
        {
            return value;
        }

        if (axisOptions == AxisOptions.Both)
        {
            // NOTE: decompiled code manually replicates Vector2.Angle(input, Vector2.up) via
            // dot product + acos rather than calling the API method directly (likely inlined
            // by IL2CPP). Behaviourally equivalent to Vector2.Angle.
            float angle = Vector2.Angle(input, Vector2.up);
            if (snapAxis == AxisOptions.Horizontal)
            {
                if (angle < 22.5f || angle > 157.5f)
                {
                    return 0f;
                }
                return (value > 0f) ? 1f : -1f;
            }
            if (snapAxis == AxisOptions.Vertical)
            {
                if (angle > 67.5f && angle < 112.5f)
                {
                    return 0f;
                }
                return (value > 0f) ? 1f : -1f;
            }
            return value;
        }
        else
        {
            return (value > 0f) ? 1f : -1f;
        }
    }

    public virtual void OnPointerUp(PointerEventData eventData)
    {
        input = Vector2.zero;
        handle.anchoredPosition = Vector2.zero;
    }

    protected Vector2 ScreenPointToAnchoredPosition(Vector2 screenPosition)
    {
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(baseRect, screenPosition, cam, out Vector2 localPoint))
        {
            Vector2 pivotOffset = baseRect.pivot * baseRect.sizeDelta;
            return localPoint - (background.anchorMax * baseRect.sizeDelta) + pivotOffset;
        }
        return Vector2.zero;
    }
}

public enum AxisOptions
{
    Both,
    Horizontal,
    Vertical
}