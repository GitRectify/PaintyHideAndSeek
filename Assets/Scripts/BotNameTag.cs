using UnityEngine;
using UnityEngine.UI;

public class BotNameTag : MonoBehaviour
{
    [Header("Layout")]
    public int fontSize = 46;
    public float height = 10f;
    public float worldScale = 0.07f;
    public Color textColor = Color.white;

    private string pendingName = "Player";
    private Transform tag;
    private Text label;
    private Camera cam;

    public void SetName(string n)
    {
        pendingName = n;
        if (label != null)
        {
            label.text = n;
        }
    }

    public void SetVisible(bool v)
    {
        if (tag == null) return;

        GameObject go = tag.gameObject;
        if (go.activeSelf == v) return;

        go.SetActive(v);
    }

    private void Start()
    {
        cam = Camera.main;
        Build();
    }

    private void Build()
    {
        if (tag != null) return;

        // Force the shared UI font to resolve/load before anything below uses it.
        _ = UIFont.Default;

        GameObject root = new GameObject("NameTag");
        root.transform.SetParent(transform, false);
        root.transform.localPosition = Vector3.up * height;

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        RectTransform rootRect = (RectTransform)root.transform;
        rootRect.sizeDelta = new Vector2(280f, 76f);
        root.transform.localScale = Vector3.one * worldScale;

        Image bg = NewImage("BG", root.transform, new Color(0f, 0f, 0f, 0.5f));
        Stretch(bg, 0f);

        label = NewText("Text", root.transform, pendingName, fontSize, textColor);
        Stretch(label, 8f);

        tag = root.transform;
    }

    private Image NewImage(string name, Transform parent, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        Image img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false; // NOTE: inferred — the decompiled code calls a second single-bool
                                    // setter (vtable slot 0x19) right after set_color with no other
                                    // context; raycastTarget=false is the obvious choice for a
                                    // decorative background panel, matching the same pattern used
                                    // explicitly on the Text label below.
        return img;
    }

    private void Stretch(Component c, float pad)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
    }

    private Text NewText(string name, Transform parent, string content, int size, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        Text text = go.AddComponent<Text>();
        text.font = UIFont.Default;
        text.text = content;
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = color;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;

        return text;
    }

    private void LateUpdate()
    {
        if (tag == null) return;

        if (cam == null)
        {
            cam = Camera.main;
        }

        // CORRECTION: the earlier draft silently returned here if `cam` was still null after the
        // Camera.main fallback attempt. Confirmed via trace — the real decompile only has a
        // silent return for the `tag == null` case above; if tag exists but no camera can be
        // found, it falls through to a throw instead. Natural crash-on-null reproduces that
        // correctly without an explicit guard.
        Vector3 forward = tag.position - cam.transform.position;

        // Billboard: face the same direction the camera is looking (i.e. point away from the
        // camera), rather than facing the camera directly — keeps the text readable and upright.
        tag.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }
}