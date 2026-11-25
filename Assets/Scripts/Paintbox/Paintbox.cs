using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// The game's look (design option B, "Paintbox", 2026-11-24): warm paper surfaces, ink outlines
/// with a hard drop shadow, a yellow primary action and violet for what is picked, Rubik type with
/// bold italic titles, and Lucide line icons. Every screen is built from these pieces, so the
/// whole client stays consistent and a change of colour or shape is made here once.
///
/// Units are those of a 1920 x 1080 reference canvas matched on height (OnlineUI.CreateCanvas):
/// the prototype's 854 x 480 frame times 2.25.
/// </summary>
public static class Paintbox
{
    // ================= Colours =================

    public static readonly Color Bg = Hex(0xffebcf);        // the page
    public static readonly Color Paper = Hex(0xfff8e7);     // panels and cards
    public static readonly Color Card = Hex(0xfce5c6);      // pictures' backdrop, quiet fills
    public static readonly Color Tab = Hex(0xefd9bc);       // tab strip, wells
    public static readonly Color Ink = Hex(0x382343);       // text, outlines, shadows
    public static readonly Color Muted = Hex(0x674e65);     // secondary text
    public static readonly Color Line = Hex(0xc2a5b4);      // dividers
    public static readonly Color PanelLine = Hex(0xbba3a7); // a panel's soft outline
    public static readonly Color Yellow = Hex(0xfac352);    // the primary action
    public static readonly Color OnYellow = Hex(0x39253f);
    public static readonly Color Violet = Hex(0xbfa4f0);    // what is picked: tabs, choices
    public static readonly Color Lilac = Hex(0xb692ed);
    public static readonly Color Wash = Hex(0xe3d5ff);      // tags
    public static readonly Color WashInk = Hex(0x493065);
    public static readonly Color Good = Hex(0x2c684c);
    public static readonly Color Bad = Hex(0xa33d36);
    public static readonly Color GoodWash = Hex(0xd5efd9);
    public static readonly Color BadWash = Hex(0xf6d6d0);
    public static readonly Color Glow = new Color(0.98f, 0.686f, 0.502f, 0.40f);   // the page's warm corner
    public static readonly Color Dim = new Color(0.22f, 0.137f, 0.263f, 0.55f);     // behind a dialog

    // The match HUD: dark glass over the game.
    public static readonly Color Glass = new Color32(51, 34, 59, 217);
    public static readonly Color GlassEdge = Hex(0x34253e);
    public static readonly Color GlassShadow = new Color32(0x34, 0x25, 0x3e, 0x9c);
    public static readonly Color Hot = Hex(0xffcd65);
    public static readonly Color HotInk = Hex(0x402647);

    public static Color Hex(int rgb, float alpha = 1f) =>
        new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, alpha);

    // ================= Sizes =================

    public const float Radius = 27f;         // cards, buttons
    public const float RadiusLarge = 40f;    // panels
    public const float Border = 4f;          // the ink outline
    public const float Shadow = 6f;          // the hard drop shadow
    public const float Gutter = 30f;         // the page's edge

    public const float TitleSize = 56f;
    public const float HeadingSize = 42f;
    public const float BodySize = 30f;
    public const float SmallSize = 25f;
    public const float TinySize = 22f;

    // ================= Fonts =================

    public enum Weight { Regular, Bold, Title }

    private static Font regular, bold, title;

    public static Font Regular => regular ? regular : regular = LoadFont("RubikR");
    public static Font Bold => bold ? bold : bold = LoadFont("RubikB");
    /// <summary>Bold italic: titles and big numbers.</summary>
    public static Font Title => title ? title : title = LoadFont("RubikBI");

    public static Font FontOf(Weight weight) => weight == Weight.Title ? Title : weight == Weight.Bold ? Bold : Regular;

    private static Font LoadFont(string name)
    {
        Font font = Resources.Load<Font>("Paintbox/Fonts/" + name);
        return font != null ? font : UIFont.Default;
    }

    // ================= Icons and art =================

    private static readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

    // Names the server or older screens use for Lucide's.
    private static readonly Dictionary<string, string> iconAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "person", "PersonStanding" }, { "pose", "PersonStanding" }, { "poses", "PersonStanding" },
        { "coin", "Coins" }, { "coins", "Coins" }, { "bag", "ShoppingBag" }, { "shop", "ShoppingBag" },
        { "gear", "Settings" }, { "home", "House" }, { "back", "ArrowLeft" }, { "close", "X" },
        { "weapon", "Crosshair" }, { "weapons", "Crosshair" }, { "ammo", "Layers" }, { "bullets", "Layers" },
        { "avatar", "User" }, { "avatars", "User" }, { "characters", "User" }, { "shoes", "Footprints" },
        { "extras", "Sparkles" }, { "more", "Sparkles" }, { "map", "Map" }, { "maps", "Map" },
        { "star", "Star" }, { "error", "CircleAlert" }, { "alert", "CircleAlert" }, { "info", "Info" },
        { "reward", "Gift" }, { "match", "Flag" }, { "purchase", "ShoppingBag" }, { "check", "Check" },
    };

    /// <summary>
    /// A Lucide icon by name ("crosshair", "paint-roller", "PaintRoller" or an alias such as
    /// "coins"), white, to be tinted by its Image; null for one the game does not carry.
    /// </summary>
    public static Sprite Icon(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (icons.TryGetValue(name, out Sprite cached)) return cached;

        string file = iconAliases.TryGetValue(name, out string alias) ? alias : Pascal(name);
        Sprite sprite = Resources.Load<Sprite>("Paintbox/Icons/" + file);
        icons[name] = sprite;
        return sprite;
    }

    private static string Pascal(string name)
    {
        var parts = name.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (string p in parts) sb.Append(char.ToUpperInvariant(p[0])).Append(p.Substring(1));
        return sb.ToString();
    }

    private static readonly Dictionary<string, Sprite> art = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

    /// <summary>One of the game's illustrations (Resources/Paintbox/Art), or null.</summary>
    public static Sprite Art(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (art.TryGetValue(key, out Sprite cached)) return cached;
        Sprite sprite = Resources.Load<Sprite>("Paintbox/Art/" + key);
        art[key] = sprite;
        return sprite;
    }

    private static Sprite whiteSprite;

    /// <summary>
    /// A plain white sprite: what a progress bar's Image needs to fill partway (an Image with no
    /// sprite draws whole, whatever its fill amount).
    /// </summary>
    public static Sprite WhiteSprite
    {
        get
        {
            if (whiteSprite != null) return whiteSprite;
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            var px = new Color32[16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.Apply(false, true);
            whiteSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
            whiteSprite.hideFlags = HideFlags.DontSave;
            return whiteSprite;
        }
    }

    private static Sprite starSprite;

    /// <summary>A filled five-pointed star (white), the inside of Lucide's star outline.</summary>
    public static Sprite StarSprite
    {
        get
        {
            if (starSprite != null) return starSprite;
            const int size = 128;
            var points = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float angle = Mathf.PI / 2f + i * Mathf.PI / 5f;
                float r = i % 2 == 0 ? 0.5f : 0.21f;
                points[i] = new Vector2(0.5f + r * Mathf.Cos(angle), 0.47f + r * Mathf.Sin(angle));
            }
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < 3; sy++)
                        for (int sx = 0; sx < 3; sx++)
                            if (InPolygon(points, new Vector2((x + (sx + 0.5f) / 3f) / size, (y + (sy + 0.5f) / 3f) / size))) hits++;
                    px[y * size + x] = new Color(1f, 1f, 1f, hits / 9f);
                }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            starSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            starSprite.hideFlags = HideFlags.DontSave;
            return starSprite;
        }
    }

    private static bool InPolygon(Vector2[] poly, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        }
        return inside;
    }

    private static Sprite glowSprite;

    /// <summary>A soft white disc fading to nothing, for the page's warm corner.</summary>
    public static Sprite GlowSprite
    {
        get
        {
            if (glowSprite != null) return glowSprite;
            const int size = 128;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f).magnitude / (size * 0.5f);
                    float a = Mathf.Clamp01(1f - d);
                    px[y * size + x] = new Color(1f, 1f, 1f, a * a * (3f - 2f * a));
                }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            glowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            glowSprite.hideFlags = HideFlags.DontSave;
            return glowSprite;
        }
    }

    // ================= Layout =================

    /// <summary>
    /// Makes a scene canvas 1080 units tall (as OnlineUI's): design B's pages on it are then not
    /// scaled up, which would blur their words (text is drawn at the canvas's own scale). Returns
    /// how much smaller the canvas's old units now are (old height over 1080), or 1 when unchanged.
    /// </summary>
    public static float Canvas1080(GameObject canvas)
    {
        CanvasScaler scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
        if (scaler == null || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize) return 1f;
        if (scaler.referenceResolution == new Vector2(1920f, 1080f) && scaler.matchWidthOrHeight >= 1f) return 1f;
        float oldHeight = scaler.matchWidthOrHeight >= 1f ? scaler.referenceResolution.y : 0f;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 1f;
        return oldHeight > 0f ? oldHeight / 1080f : 1f;
    }

    /// <summary>The component on <paramref name="go"/>, added when it has none (not ??: Unity's missing component is not C# null).</summary>
    public static T Ensure<T>(GameObject go) where T : Component
    {
        T c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    public static RectTransform NewRect(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    /// <summary>Fills the parent, inset by the given margins.</summary>
    public static RectTransform Fill(RectTransform rt, float left = 0f, float right = 0f, float top = 0f, float bottom = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
        return rt;
    }

    /// <summary>Anchored at one point of the parent (0..1 each way), pivot the same, at an offset.</summary>
    public static RectTransform Pin(RectTransform rt, Vector2 anchor, Vector2 position, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
        return rt;
    }

    /// <summary>A band across the parent's width, <paramref name="top"/> down from its top edge.</summary>
    public static RectTransform TopBand(RectTransform rt, float top, float height, float left = 0f, float right = 0f)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(left, -top - height);
        rt.offsetMax = new Vector2(-right, -top);
        return rt;
    }

    // ================= Text =================

    public static Text Label(Transform parent, string text, float size, Color color, Weight weight = Weight.Regular,
                             TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Text label = go.AddComponent<Text>();
        label.font = FontOf(weight);
        label.text = text;
        label.fontSize = Mathf.RoundToInt(size);
        label.color = color;
        label.alignment = anchor;
        label.supportRichText = true;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.lineSpacing = 0.95f;
        UiTranslator.Track(label);
        return label;
    }

    /// <summary>Shrinks a label's text to fit its box (never below <paramref name="min"/>).</summary>
    public static Text Fit(Text label, int min = 14)
    {
        label.resizeTextForBestFit = true;
        label.resizeTextMaxSize = label.fontSize;
        label.resizeTextMinSize = Mathf.Min(min, label.fontSize);
        return label;
    }

    // ================= Images =================

    public static Image Image(Transform parent, string name, Sprite sprite, Color color, bool raycast = false)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Image image = go.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = raycast;
        return image;
    }

    /// <summary>A rounded rectangle, its corners <paramref name="radius"/> units round.</summary>
    public static Image Rounded(Transform parent, string name, Color color, float radius = Radius, bool raycast = false)
    {
        Image image = Image(parent, name, null, color, raycast);
        UiArt.Apply(image, UiArt.Rounded, radius);
        return image;
    }

    public static Image IconImage(Transform parent, string icon, float size, Color color)
    {
        Image image = Image(parent, "Icon", Icon(icon), color);
        image.preserveAspect = true;
        Pin(image.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
        image.enabled = image.sprite != null;
        return image;
    }

    // ================= Frames =================

    /// <summary>
    /// The basic shape: a rounded body with an outline and a hard shadow under it. Put content in
    /// <see cref="PaintboxFrame.Body"/> (it moves with a press); <see cref="PaintboxFrame.Fill"/>
    /// takes taps and its colour is the frame's colour.
    /// </summary>
    public static PaintboxFrame Frame(Transform parent, string name, Color fill, float radius = Radius, float border = Border,
                                      float shadow = Shadow, Color? edge = null, Color? shadowColor = null)
    {
        RectTransform root = NewRect(parent, name);
        PaintboxFrame frame = root.gameObject.AddComponent<PaintboxFrame>();
        frame.Root = root;
        frame.ShadowDepth = shadow;

        if (shadow > 0f)
        {
            frame.Shade = Rounded(root, "Shadow", shadowColor ?? Ink, radius);
            Fill(frame.Shade.rectTransform, 0f, 0f, shadow, -shadow);
        }

        frame.Body = Fill(NewRect(root, "Body"));
        if (border > 0f)
        {
            frame.Edge = Rounded(frame.Body, "Edge", edge ?? Ink, radius);
            Fill(frame.Edge.rectTransform);
        }

        frame.Fill = Rounded(frame.Body, "Fill", fill, Mathf.Max(2f, radius - border), true);
        Fill(frame.Fill.rectTransform, border, border, border, border);
        return frame;
    }

    /// <summary>A panel: paper, a soft outline, no drop shadow (the prototype's .panel).</summary>
    public static PaintboxFrame Panel(Transform parent, string name, float radius = RadiusLarge) =>
        Frame(parent, name, Paper, radius, Border, 0f, PanelLine);

    /// <summary>A product or choice card: paper, ink outline, a small shadow.</summary>
    public static PaintboxFrame CardFrame(Transform parent, string name, Color? fill = null) =>
        Frame(parent, name, fill ?? Paper, Radius, Border, 4f);

    // ================= Buttons =================

    public enum Tone { Primary, Secondary, Paper, Danger, Good, Glass, Hot, Quiet }

    public static Color FillOf(Tone tone)
    {
        switch (tone)
        {
            case Tone.Primary: return Yellow;
            case Tone.Secondary: return Violet;
            case Tone.Danger: return Bad;
            case Tone.Good: return Good;
            case Tone.Glass: return Glass;
            case Tone.Hot: return Hot;
            case Tone.Quiet: return Tab;
            default: return Paper;
        }
    }

    public static Color InkOf(Tone tone)
    {
        switch (tone)
        {
            case Tone.Danger:
            case Tone.Good:
            case Tone.Glass: return Color.white;
            case Tone.Hot: return HotInk;
            case Tone.Primary: return OnYellow;
            case Tone.Secondary: return WashInk;
            default: return Ink;
        }
    }

    /// <summary>
    /// A button: a frame in the tone's colour with an optional icon before its words. It sinks
    /// into its shadow while pressed. The label is the frame's only Text.
    /// </summary>
    public static Button Button(Transform parent, string text, Tone tone, string icon = null, UnityAction onClick = null,
                                float fontSize = BodySize, float radius = Radius)
    {
        bool glass = tone == Tone.Glass;
        PaintboxFrame frame = Frame(parent, "Button", FillOf(tone), radius, glass ? 3f : Border, glass ? 5f : Shadow,
                                    glass ? GlassEdge : Ink, glass ? GlassShadow : Ink);
        Color ink = InkOf(tone);

        Button button = frame.Root.gameObject.AddComponent<Button>();
        button.targetGraphic = frame.Fill;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = Color.white;
        colors.selectedColor = Color.white;
        colors.pressedColor = new Color(0.93f, 0.93f, 0.93f, 1f);
        colors.disabledColor = new Color(1f, 1f, 1f, 0.55f);
        colors.fadeDuration = 0.06f;
        button.colors = colors;
        Navigation none = button.navigation;
        none.mode = Navigation.Mode.None;
        button.navigation = none;
        if (onClick != null) button.onClick.AddListener(onClick);
        frame.Root.gameObject.AddComponent<PaintboxPress>();

        // Icon and words side by side, centred together.
        RectTransform row = Fill(NewRect(frame.Body, "Content"), 18f, 18f, 4f, 4f);
        HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = 12f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        if (!string.IsNullOrEmpty(icon) && Icon(icon) != null)
        {
            Image glyph = Image(row, "Icon", Icon(icon), ink);
            glyph.preserveAspect = true;
            LayoutElement le = glyph.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = le.preferredHeight = fontSize * 1.15f;
        }

        Text label = Label(row, text, fontSize, ink, Weight.Bold, TextAnchor.MiddleCenter);
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.gameObject.SetActive(!string.IsNullOrEmpty(text));
        return button;
    }

    /// <summary>A square button with only an icon (back, settings, close).</summary>
    public static Button IconButton(Transform parent, string icon, Tone tone, UnityAction onClick, float size = 76f)
    {
        Button button = Button(parent, "", tone, null, onClick, BodySize, Mathf.Min(Radius, size * 0.32f));
        PaintboxFrame frame = button.GetComponent<PaintboxFrame>();
        Transform content = frame.Body.Find("Content");
        if (content != null) UnityEngine.Object.Destroy(content.gameObject);
        IconImage(frame.Body, icon, size * 0.48f, InkOf(tone));
        frame.Root.sizeDelta = new Vector2(size, size);
        return button;
    }

    /// <summary>Changes a Paintbox button's tone: its fill, and its icon and words to match.</summary>
    public static void SetTone(Button button, Tone tone)
    {
        if (button == null) return;
        PaintboxFrame frame = button.GetComponent<PaintboxFrame>();
        if (frame == null) return;
        frame.Fill.color = FillOf(tone);
        Color ink = InkOf(tone);
        foreach (Text t in frame.Body.GetComponentsInChildren<Text>(true)) t.color = ink;
        foreach (Image i in frame.Body.GetComponentsInChildren<Image>(true))
            if (i.name == "Icon") i.color = ink;
    }

    public static void SetText(Button button, string text)
    {
        if (button == null) return;
        Text label = button.GetComponentInChildren<Text>(true);
        if (label == null) return;
        label.text = text;
        label.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    // ================= Small pieces =================

    /// <summary>A tag in a pill (Owned, Equipped, x10): its own colours, an optional icon.</summary>
    public static RectTransform Tag(Transform parent, string text, Color fill, Color ink, string icon = null, float size = TinySize)
    {
        Image pill = Rounded(parent, "Tag", fill, size);
        RectTransform rt = pill.rectTransform;
        HorizontalLayoutGroup layout = pill.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(14, 16, 6, 6);
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        ContentSizeFitter fit = pill.gameObject.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        if (!string.IsNullOrEmpty(icon) && Icon(icon) != null)
        {
            Image glyph = Image(rt, "Icon", Icon(icon), ink);
            glyph.preserveAspect = true;
            LayoutElement le = glyph.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = le.preferredHeight = size;
        }

        Text label = Label(rt, text, size, ink, Weight.Bold, TextAnchor.MiddleCenter);
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        return rt;
    }

    /// <summary>The coin: a yellow disc with an ink edge and a paint drop.</summary>
    public static RectTransform CoinGlyph(Transform parent, float size)
    {
        Image ring = Image(parent, "Coin", UiArt.Circle, Ink);
        RectTransform rt = ring.rectTransform;
        rt.sizeDelta = new Vector2(size, size);
        Image disc = Image(rt, "Disc", UiArt.Circle, Yellow);
        Fill(disc.rectTransform, size * 0.08f, size * 0.08f, size * 0.08f, size * 0.08f);
        Image drop = Image(rt, "Drop", Icon("Droplet"), Ink);
        drop.preserveAspect = true;
        Fill(drop.rectTransform, size * 0.24f, size * 0.24f, size * 0.24f, size * 0.24f);
        return rt;
    }

    /// <summary>A coin amount: the coin and the number, side by side, sized to fit.</summary>
    public static Text CoinAmount(Transform parent, string amount, float size, Color ink, out RectTransform row)
    {
        row = NewRect(parent, "Price");
        HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = size * 0.3f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;

        RectTransform coin = CoinGlyph(row, size * 1.05f);
        LayoutElement cle = coin.gameObject.AddComponent<LayoutElement>();
        cle.preferredWidth = cle.preferredHeight = size * 1.05f;

        Text label = Label(row, amount, size, ink, Weight.Bold);
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        return label;
    }

    /// <summary>
    /// The coin balance pill of every screen's top right: paper, a soft outline, the coin and the
    /// server's balance (CoinBalance); a tap opens the Shop on its Coins (PaintboxPage.OpenCoins).
    /// </summary>
    public static Text CoinPill(Transform parent, out RectTransform pill)
    {
        PaintboxFrame frame = Frame(parent, "CoinPill", Paper, 38f, Border, 0f, PanelLine);
        pill = frame.Root;
        pill.sizeDelta = new Vector2(230f, 76f);
        Text amount = CoinAmount(frame.Body, CoinBalance.Text, 32f, Ink, out RectTransform row);
        Fill(row, 18f, 18f, 0f, 0f);
        row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
        CoinBalance.Bind(amount);
        PaintboxPage.MakeCoinsOpener(amount);
        return amount;
    }

    /// <summary>A thin line across its parent.</summary>
    public static Image Divider(Transform parent, Color? color = null, float thickness = 3f)
    {
        Image line = Image(parent, "Divider", null, color ?? Line);
        line.rectTransform.sizeDelta = new Vector2(0f, thickness);
        return line;
    }

    /// <summary>The page itself: warm paper with a peach glow in its top right corner.</summary>
    public static Image Backdrop(Transform parent)
    {
        Image page = Image(parent, "Backdrop", null, Bg, true);
        Fill(page.rectTransform);
        Image glow = Image(page.rectTransform, "Glow", GlowSprite, Glow);
        RectTransform g = glow.rectTransform;
        g.anchorMin = g.anchorMax = new Vector2(1f, 1f);
        g.pivot = new Vector2(0.5f, 0.5f);
        g.anchoredPosition = new Vector2(-120f, -60f);
        g.sizeDelta = new Vector2(1500f, 1100f);
        return page;
    }

    /// <summary>
    /// A screen's top bar: a back button, the title in bold italic, and room on the right for the
    /// coin pill and other buttons (returned as <paramref name="right"/>, laid out right to left).
    /// </summary>
    public static Text TopBar(RectTransform page, string title, UnityAction back, out RectTransform right)
    {
        RectTransform bar = TopBand(NewRect(page, "TopBar"), Gutter, 76f, Gutter, Gutter);

        float x = 0f;
        if (back != null)
        {
            Button backButton = IconButton(bar, "ArrowLeft", Tone.Paper, back, 76f);
            backButton.name = "Back";
            BackKey.Mark(backButton);   // the phone's Back does what this does
            Pin((RectTransform)backButton.transform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(76f, 76f));
            x = 100f;
        }

        Text heading = Label(bar, title, TitleSize, Ink, Weight.Title);
        heading.horizontalOverflow = HorizontalWrapMode.Overflow;
        RectTransform h = heading.rectTransform;
        h.anchorMin = new Vector2(0f, 0f);
        h.anchorMax = new Vector2(0.6f, 1f);
        h.pivot = new Vector2(0f, 0.5f);
        h.offsetMin = new Vector2(x, -6f);
        h.offsetMax = new Vector2(0f, 6f);

        right = NewRect(bar, "Right");
        right.anchorMin = new Vector2(0.4f, 0f);
        right.anchorMax = new Vector2(1f, 1f);
        right.offsetMin = right.offsetMax = Vector2.zero;
        HorizontalLayoutGroup layout = right.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleRight;
        layout.spacing = 16f;
        layout.reverseArrangement = false;
        layout.childControlWidth = layout.childControlHeight = false;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        return heading;
    }

    /// <summary>Keeps <paramref name="rt"/> inside the screen's safe area (notches, rounded corners).</summary>
    public static RectTransform SafeArea(RectTransform rt)
    {
        Fill(rt);
        rt.gameObject.AddComponent<PaintboxSafeArea>();
        return rt;
    }
}

/// <summary>The parts of a Paintbox frame, for screens that recolour or fill one.</summary>
[DisallowMultipleComponent]
public sealed class PaintboxFrame : MonoBehaviour
{
    public RectTransform Root;
    public RectTransform Body;
    public Image Shade;
    public Image Edge;
    public Image Fill;
    public float ShadowDepth;
}
