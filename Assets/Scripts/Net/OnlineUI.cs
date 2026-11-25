// From the Meccha Client SDK sample "Online integration" (v1.0.0); CoinsText now writes amounts the
// way the rest of the game does (CoinBalance.Format). Don't import the sample's own copy of this
// file as well, or OnlineUI is defined twice.

using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Small builder for the online screens (legacy uGUI Text, a 1920x1080 reference canvas matched on
// height). Since 2026-11-24 it draws design B, "Paintbox" (Paintbox.cs): paper panels with an ink
// outline and a hard shadow, yellow for the main action, violet for what is picked, Rubik type.
// The screens only use these helpers, so they all took the new look from this one place; the
// colour names below are the roles the screens ask for, in the new palette.
public static class OnlineUI
{
    public static readonly Color Dim = Paintbox.Dim;
    public static readonly Color PanelColor = Paintbox.Paper;
    public static readonly Color Green = Paintbox.Yellow;      // the main action
    public static readonly Color Blue = Paintbox.Violet;       // picked / secondary
    public static readonly Color Gray = Paintbox.Paper;        // neutral
    public static readonly Color Red = Paintbox.Bad;
    public static readonly Color Muted = Paintbox.Muted;
    public static readonly Color Warning = Paintbox.Hex(0x9a4f12);

    public static Canvas CreateCanvas(string name, Transform parent, int sortingOrder)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        Canvas canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        CanvasScaler scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f;

        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    /// <summary>A full-screen dimmer with a centred panel (paper, ink outline, a hard shadow); returns the panel.</summary>
    public static RectTransform Modal(Transform canvas, string name, Vector2 panelSize)
    {
        Image dim = NewImage(name, canvas, Dim);
        Stretch(dim.rectTransform);

        Image panel = NewImage("Panel", dim.transform, PanelColor);
        Place(panel.rectTransform, Vector2.zero, panelSize);
        Chunky(panel, Paintbox.RadiusLarge, Paintbox.PanelLine, 8f);
        return panel.rectTransform;
    }

    public static Image NewImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Image image = go.AddComponent<Image>();
        image.color = Adapt(color);
        return image;
    }

    /// <summary>
    /// A colour asked for by a screen made before design B, in design B: dark surfaces become
    /// paper, the faint white rows of a dark panel become the quiet tab colour, an orange call to
    /// action the hot yellow. Design B's own colours come back as they are.
    /// </summary>
    public static Color Adapt(Color c)
    {
        if (c.a <= 0.01f) return c;
        float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        if (c.r > 0.95f && c.g > 0.95f && c.b > 0.95f && c.a < 0.25f) return Paintbox.Tab;          // a row on a dark panel
        if (c == Paintbox.Dim) return c;
        if (lum < 0.08f && c.a >= 0.3f && c.a <= 0.8f) return Paintbox.Dim;                           // a black dimmer behind a dialog
        if (lum < 0.22f && c.a > 0.8f) return new Color(Paintbox.Paper.r, Paintbox.Paper.g, Paintbox.Paper.b, Mathf.Max(c.a, 0.97f));
        if (Mathf.Abs(c.r - 0.85f) < 0.03f && Mathf.Abs(c.g - 0.52f) < 0.03f && Mathf.Abs(c.b - 0.12f) < 0.03f) return Paintbox.Hot;
        return c;
    }

    /// <summary>The ink or white that reads on a fill.</summary>
    public static Color InkOn(Color fill)
    {
        if (fill.a < 0.3f) return Paintbox.Ink;
        float lum = 0.299f * fill.r + 0.587f * fill.g + 0.114f * fill.b;
        return lum > 0.5f ? Paintbox.Ink : Color.white;
    }

    /// <summary>
    /// Gives a plain Image design B's shape: rounded corners, an outline and a hard drop shadow
    /// (mesh effects, so the Image stays the object's only graphic and its colour stays its fill).
    /// </summary>
    public static void Chunky(Image image, float radius, Color edge, float shadow)
    {
        UiArt.Apply(image, UiArt.Rounded, radius);
        Outline outline = image.GetComponent<Outline>() ?? image.gameObject.AddComponent<Outline>();
        outline.effectColor = edge;
        outline.effectDistance = new Vector2(Paintbox.Border, -Paintbox.Border);
        outline.useGraphicAlpha = true;
        if (shadow > 0f)
        {
            Shadow drop = null;
            foreach (Shadow s in image.GetComponents<Shadow>()) if (!(s is Outline)) drop = s;
            if (drop == null) drop = image.gameObject.AddComponent<Shadow>();
            drop.effectColor = Paintbox.Ink;
            drop.effectDistance = new Vector2(0f, -shadow);
            drop.useGraphicAlpha = true;
        }
    }

    public static Text NewLabel(Transform parent, string text, int size, Vector2 pos, Vector2 box,
                             TextAnchor anchor = TextAnchor.MiddleCenter, bool bold = false)
    {
        var go = new GameObject("Label");
        go.transform.SetParent(parent, false);

        Text label = go.AddComponent<Text>();
        label.font = bold ? Paintbox.Bold : Paintbox.Regular;
        label.text = text;
        label.fontSize = size;
        label.color = Paintbox.Ink;
        label.alignment = anchor;
        label.fontStyle = FontStyle.Normal;
        label.supportRichText = true;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;

        Place(label.rectTransform, pos, box);
        UiTranslator.Track(label);      // in the player's language from its first frame
        return label;
    }

    public static Button NewButton(Transform parent, string text, Vector2 pos, Vector2 box, Color color, UnityAction onClick)
    {
        Image bg = NewImage("Button", parent, color);
        Place(bg.rectTransform, pos, box);
        Chunky(bg, Mathf.Min(Paintbox.Radius, box.y * 0.36f), Paintbox.Ink, Paintbox.Shadow);

        Button button = bg.gameObject.AddComponent<Button>();
        button.targetGraphic = bg;
        Navigation none = button.navigation;
        none.mode = Navigation.Mode.None;
        button.navigation = none;
        if (onClick != null) button.onClick.AddListener(onClick);

        Text label = NewLabel(bg.transform, text, 36, Vector2.zero, box, TextAnchor.MiddleCenter, true);
        Stretch(label.rectTransform);
        label.rectTransform.offsetMin = new Vector2(12f, 2f);
        label.rectTransform.offsetMax = new Vector2(-12f, -2f);
        label.color = InkOn(bg.color);
        bg.gameObject.AddComponent<PaintboxInk>();
        bg.gameObject.AddComponent<PaintboxPressFlat>();
        return button;
    }

    public static InputField NewInput(Transform parent, string placeholder, Vector2 pos, Vector2 box)
    {
        Image bg = NewImage("Input", parent, Color.white);
        Place(bg.rectTransform, pos, box);
        Chunky(bg, 20f, Paintbox.Line, 0f);

        Text hint = NewLabel(bg.transform, placeholder, 34, Vector2.zero, box, TextAnchor.MiddleLeft);
        hint.color = new Color(Paintbox.Muted.r, Paintbox.Muted.g, Paintbox.Muted.b, 0.6f);
        hint.fontStyle = FontStyle.Italic;
        Inset(hint.rectTransform, 20f);

        Text text = NewLabel(bg.transform, "", 34, Vector2.zero, box, TextAnchor.MiddleLeft);
        text.color = Paintbox.Ink;
        text.supportRichText = false;   // a user id is shown as typed, never as markup
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        Inset(text.rectTransform, 20f);

        InputField field = bg.gameObject.AddComponent<InputField>();
        field.targetGraphic = bg;
        field.textComponent = text;
        field.placeholder = hint;
        field.lineType = InputField.LineType.SingleLine;
        return field;
    }

    public static void SetButtonText(Button button, string text)
    {
        Text label = button != null ? button.GetComponentInChildren<Text>(true) : null;
        if (label != null) label.text = text;
    }

    /// <summary>
    /// Sets the caption of a scene object whose label may be legacy Text or TextMesh Pro. TMP is
    /// reached by reflection so this code does not take a compile-time dependency on it.
    /// </summary>
    public static bool SetAnyText(GameObject root, string text)
    {
        Text legacy = root.GetComponentInChildren<Text>(true);
        if (legacy != null) { legacy.text = text; return true; }

        foreach (Component c in root.GetComponentsInChildren<Component>(true))
        {
            if (c == null || c.GetType().Name.IndexOf("TextMeshPro", StringComparison.Ordinal) < 0) continue;

            PropertyInfo prop = c.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite) continue;

            prop.SetValue(c, text);
            return true;
        }

        return false;
    }

    /// <summary>The caption under a scene object (legacy Text or TextMesh Pro), or "" when there is none.</summary>
    public static string GetAnyText(GameObject root)
    {
        Text legacy = root.GetComponentInChildren<Text>(true);
        if (legacy != null) return legacy.text ?? "";

        foreach (Component c in root.GetComponentsInChildren<Component>(true))
        {
            if (c == null || c.GetType().Name.IndexOf("TextMeshPro", StringComparison.Ordinal) < 0) continue;

            PropertyInfo prop = c.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanRead) return prop.GetValue(c) as string ?? "";
        }

        return "";
    }

    /// <summary>"1 coin", "5 coins", "0.50 coins", "1,250 coins": the server counts in cents.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Sprite> sceneSprites =
        new System.Collections.Generic.Dictionary<string, Sprite>();

    /// <summary>
    /// The sprite of a scene image found by its object name, shown or hidden ("LockIcon": the shop
    /// cards' lock, "CoinIcon": Home's coin), so art made for one screen serves another. Null when
    /// the scene has none.
    /// </summary>
    public static Sprite SceneSprite(string objectName)
    {
        if (sceneSprites.TryGetValue(objectName, out Sprite cached) && cached != null) return cached;
        foreach (Image image in Resources.FindObjectsOfTypeAll<Image>())
        {
            if (image.name != objectName || image.sprite == null || !image.gameObject.scene.IsValid()) continue;
            sceneSprites[objectName] = image.sprite;
            return image.sprite;
        }
        return null;
    }

    // ---- Notices (2026-11-13) ----
    // Every notice goes to one place: the top right, under the coin balance and left of the round's
    // button column, where no screen has anything that matters (the middle and the top centre hold
    // the timer, titles and buttons). Reference units of a 1920 x 1080 canvas (CreateCanvas).

    public const float NoticeWidth = 620f;
    public const float NoticeRight = -150f;      // clear of the right-hand button column
    public const float NoticeTop = -235f;        // under the coin balance and the round's banner line

    /// <summary>
    /// Puts a notice in the notice slot; <paramref name="below"/> stacks it under earlier ones. On a
    /// narrow screen (a 4:3 tablet) it narrows too, so it never reaches the middle; a card laid out
    /// for the full width keeps it (<paramref name="keepWidth"/>).
    /// </summary>
    public static void PlaceNotice(RectTransform rt, float height, float below = 0f, bool keepWidth = false)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.sizeDelta = new Vector2(keepWidth ? NoticeWidth : NoticeWidthFor(rt), height);
        rt.anchoredPosition = new Vector2(NoticeRight, NoticeTop - below);
    }

    private static float NoticeWidthFor(RectTransform rt)
    {
        Canvas canvas = rt.GetComponentInParent<Canvas>();
        float canvasWidth = canvas != null && canvas.rootCanvas.transform is RectTransform root && root.rect.width > 1f ? root.rect.width : 1920f;
        return Mathf.Clamp(canvasWidth * 0.5f + NoticeRight - 20f, 380f, NoticeWidth);
    }

    /// <summary>A notice sliding in from the right as it fades in, and out the same way: t is 0 hidden, 1 in place.</summary>
    public static void SlideNotice(RectTransform rt, CanvasGroup group, Vector2 home, float t)
    {
        float e = 1f - (1f - Mathf.Clamp01(t)) * (1f - Mathf.Clamp01(t));   // ease out
        rt.anchoredPosition = home + Vector2.right * (1f - e) * 90f;
        if (group != null) group.alpha = e;
    }

    private static Sprite lockSprite;

    /// <summary>
    /// A padlock, for anything locked until bought (maps and poses online, shop cards). The scene's
    /// lock images carry no picture and the project has no lock art, so it is drawn here once: a
    /// gold padlock with a dark edge, 64 x 64.
    /// </summary>
    public static Sprite LockSprite()
    {
        if (lockSprite != null) return lockSprite;

        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "Padlock",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        var pixels = new Color32[size * size];
        var gold = new Color32(255, 209, 64, 255);
        var edge = new Color32(48, 34, 12, 255);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Color32 c = new Color32(0, 0, 0, 0);
                if (InPadlock(x, y)) c = InKeyhole(x, y) ? edge : gold;
                else if (NearPadlock(x, y)) c = edge;
                pixels[y * size + x] = c;
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        lockSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        lockSprite.name = "Padlock";
        return lockSprite;
    }

    // The body (a rounded box) and the shackle (the top half of a ring, its legs into the body).
    private static bool InPadlock(int x, int y)
    {
        if (x >= 12 && x <= 51 && y >= 6 && y <= 36)
        {
            const float r = 6f;
            float cx = Mathf.Clamp(x, 12 + r, 51 - r), cy = Mathf.Clamp(y, 6 + r, 36 - r);
            return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
        }

        float dx = x - 31.5f, dy = y - 37f;
        float d = Mathf.Sqrt(dx * dx + dy * dy);
        if (y >= 37 && d >= 9f && d <= 16f) return true;
        return y >= 30 && y < 37 && ((x >= 16 && x <= 22) || (x >= 41 && x <= 47));
    }

    private static bool InKeyhole(int x, int y)
    {
        float dx = x - 31.5f, dy = y - 24f;
        return dx * dx + dy * dy <= 16f || (x >= 30 && x <= 33 && y >= 13 && y <= 24);
    }

    private static bool NearPadlock(int x, int y)
    {
        for (int j = -2; j <= 2; j++)
            for (int i = -2; i <= 2; i++)
                if (i * i + j * j <= 5 && InPadlock(x + i, y + j)) return true;
        return false;
    }

    public static string CoinsText(long cents) => CoinBalance.Format(cents) + (cents == 100 ? " coin" : " coins");

    public static void Place(RectTransform rt, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    public static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void Inset(RectTransform rt, float x)
    {
        Stretch(rt);
        rt.offsetMin = new Vector2(x, 0f);
        rt.offsetMax = new Vector2(-x, 0f);
    }
}
