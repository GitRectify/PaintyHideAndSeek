using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A scene button in design B's HUD look (2026-11-24): a rounded (or round) shape of dark glass
/// (or hot yellow, paper, violet) with an ink edge, a hard shadow, a Lucide icon and a short word
/// under it (or, on a wide button, beside it). The button's own Image stays the thing that is
/// tapped, but is no longer drawn, and its old pictures go; the scripts that swap its picture for
/// an "on" one (paint, poses, the free camera, lying down) still do, and the button shows that as
/// its "on" colour. Sizes are worked out in design units (a 1080-tall screen), and the look is
/// made again when the button is resized (PaintboxHud lays the HUD out every frame).
/// </summary>
[DisallowMultipleComponent]
public sealed class PaintboxHudButton : MonoBehaviour
{
    private Image root;
    private Sprite offSprite;
    private bool haveOff;
    private readonly List<Graphic> hidden = new List<Graphic>();
    private Image fill, glyph;
    private Text word, sub;
    private Paintbox.Tone tone;
    private bool wasOn;

    // What the look was made from, to make it again at another size.
    private string iconName, labelText, subText;
    private bool roundShape;
    private string[] hideNames;
    private Vector2 builtSize;
    private float builtUnit;
    private Canvas rootCanvas;

    /// <summary>When set, the button is "on" while this child of it is shown (the paint tools' Icon).</summary>
    public string OnWhenChildShown;

    /// <summary>Gives <paramref name="go"/> the look; <paramref name="hide"/> names old children not to draw any more.</summary>
    public static PaintboxHudButton Apply(GameObject go, string icon, string label, bool round, Paintbox.Tone tone, params string[] hide) =>
        ApplyWide(go, icon, label, null, round, tone, hide);

    /// <summary>The same, with a smaller second part after the word on a wide button ("Start" "· End hiding").</summary>
    public static PaintboxHudButton ApplyWide(GameObject go, string icon, string label, string subLabel, bool round, Paintbox.Tone tone, params string[] hide)
    {
        var rt = go.transform as RectTransform;
        if (rt == null) return null;
        PaintboxHudButton skin = Paintbox.Ensure<PaintboxHudButton>(go.gameObject);
        skin.iconName = icon;
        skin.labelText = label;
        skin.subText = subLabel;
        skin.roundShape = round;
        skin.tone = tone;
        skin.hideNames = hide ?? new string[0];
        skin.Build();
        return skin;
    }

    // Canvas units of this button per design unit.
    private float UnitHere()
    {
        if (rootCanvas == null)
        {
            Canvas c = GetComponentInParent<Canvas>(true);
            rootCanvas = c != null ? c.rootCanvas : null;
        }
        if (rootCanvas == null) return 1f;
        var canvasRect = (RectTransform)rootCanvas.transform;
        float height = canvasRect.rect.height;
        if (height < 1f) return 1f;
        float own = transform.lossyScale.y, top = rootCanvas.transform.lossyScale.y;
        float rel = own > 1e-6f && top > 1e-6f ? own / top : 1f;
        return height / 1080f / rel;
    }

    private void Build()
    {
        var rt = (RectTransform)transform;
        Transform old = rt.Find("PbSkin");
        if (old != null)
        {
            old.SetParent(null, false);
            Destroy(old.gameObject);
        }
        hidden.Clear();
        wasOn = false;

        root = GetComponent<Image>();
        if (root != null && !haveOff)
        {
            offSprite = root.sprite;
            haveOff = true;
        }

        foreach (string n in hideNames)
        {
            Transform t = rt.Find(n);
            if (t == null) continue;
            foreach (Graphic g in t.GetComponentsInChildren<Graphic>(true)) hidden.Add(g);
        }

        Vector2 size = rt.rect.size;
        if (size.x < 1f || size.y < 1f) size = rt.sizeDelta;
        builtSize = size;
        float unit = UnitHere();
        builtUnit = unit;

        float side = Mathf.Max(24f * unit, Mathf.Min(size.x, size.y));
        float d = side / unit;                                      // the button's side in design units
        bool wide = size.x > size.y * 1.7f;
        float border = Mathf.Clamp(d * 0.035f, 2.5f, 5f) * unit;
        float shadow = Mathf.Clamp(d * 0.05f, 3f, 6f) * unit;

        // The shape: behind everything else the button holds (its cooldown shade, its text).
        RectTransform skin = Paintbox.NewRect(rt, "PbSkin");
        skin.SetAsFirstSibling();
        Paintbox.Fill(skin);

        Sprite shape = roundShape ? UiArt.Circle : UiArt.Rounded;
        float radius = wide ? side * 0.5f : Mathf.Min(side * 0.28f, 34f * unit);
        bool glass = tone == Paintbox.Tone.Glass;
        Color edge = glass ? Paintbox.GlassEdge : Paintbox.Ink;

        Image drop = Part(skin, "Shadow", shape, glass ? Paintbox.GlassShadow : Paintbox.Ink, roundShape, radius);
        Paintbox.Fill(drop.rectTransform, 0f, 0f, shadow, -shadow);
        Image rim = Part(skin, "Edge", shape, edge, roundShape, radius);
        Paintbox.Fill(rim.rectTransform);
        fill = Part(skin, "Fill", shape, Paintbox.FillOf(tone), roundShape, Mathf.Max(2f, radius - border));
        Paintbox.Fill(fill.rectTransform, border, border, border, border);
        Color ink = Paintbox.InkOf(tone);
        word = sub = null;

        if (wide)
        {
            // Icon, word and the smaller part side by side, centred together.
            RectTransform row = Paintbox.Fill(Paintbox.NewRect(skin, "PbRow"), 12f * unit, 12f * unit, 0f, 0f);
            HorizontalLayoutGroup h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleCenter;
            h.spacing = 10f * unit;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            glyph = Paintbox.Image(row, "PbIcon", Paintbox.Icon(iconName), ink);
            glyph.preserveAspect = true;
            LayoutElement le = glyph.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = le.preferredHeight = d * 0.4f * unit;
            word = Paintbox.Label(row, labelText ?? "", d * 0.32f * unit, ink, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
            word.horizontalOverflow = HorizontalWrapMode.Overflow;
            word.gameObject.SetActive(!string.IsNullOrEmpty(labelText));
            if (!string.IsNullOrEmpty(subText))
            {
                sub = Paintbox.Label(row, subText, d * 0.24f * unit, new Color(ink.r, ink.g, ink.b, 0.8f), Paintbox.Weight.Regular, TextAnchor.MiddleCenter);
                sub.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
        }
        else
        {
            bool words = !string.IsNullOrEmpty(labelText) && d >= 70f;   // a word on a small button is too small to read
            float iconSize = side * (words ? 0.36f : 0.5f);
            glyph = Paintbox.IconImage(skin, iconName, iconSize, ink);
            glyph.name = "PbIcon";
            glyph.rectTransform.anchoredPosition = new Vector2(0f, words ? side * 0.12f : 0f);

            if (words)
            {
                word = Paintbox.Label(skin, labelText, Mathf.Clamp(d * 0.2f, 16f, 40f) * unit, ink, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
                word.horizontalOverflow = HorizontalWrapMode.Overflow;
                RectTransform w = word.rectTransform;
                w.anchorMin = new Vector2(0f, 0f);
                w.anchorMax = new Vector2(1f, 0f);
                w.pivot = new Vector2(0.5f, 0f);
                w.offsetMin = new Vector2(0f, side * 0.08f);
                w.offsetMax = new Vector2(0f, side * 0.08f + side * 0.26f);
            }
        }

        if (GetComponent<UiPress>() == null) gameObject.AddComponent<UiPress>();
        LateUpdate();
    }

    private static Image Part(Transform parent, string name, Sprite shape, Color color, bool round, float radius)
    {
        Image image = Paintbox.Image(parent, name, shape, color);
        if (!round) UiArt.Apply(image, UiArt.Rounded, radius);
        else image.preserveAspect = true;
        return image;
    }

    /// <summary>Makes the look again when the button was resized or the screen's scale changed; true when it did.</summary>
    public bool Refit()
    {
        if (iconName == null) return false;
        var rt = (RectTransform)transform;
        Vector2 now = rt.rect.size;
        if (now.x <= 1f || now.y <= 1f) return false;
        float unit = UnitHere();
        if ((now - builtSize).sqrMagnitude <= 1f && Mathf.Abs(unit - builtUnit) <= builtUnit * 0.02f) return false;
        Build();
        return true;
    }

    private void LateUpdate()
    {
        // Resized (the HUD's layout) or the screen's scale changed: the look is made again.
        if (Refit()) return;

        // The button's own picture and its old children are not drawn (they still take taps).
        if (root != null && root.color.a > 0f) root.color = new Color(root.color.r, root.color.g, root.color.b, 0f);
        for (int i = 0; i < hidden.Count; i++)
        {
            Graphic g = hidden[i];
            if (g != null && g.color.a > 0f) g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
        }

        // "On": a script swapped the button's picture for its on one (or showed its on child).
        bool on;
        if (!string.IsNullOrEmpty(OnWhenChildShown))
        {
            Transform child = transform.Find(OnWhenChildShown);
            on = child != null && child.gameObject.activeSelf;
        }
        else on = root != null && haveOff && offSprite != null && root.sprite != offSprite;
        if (on == wasOn) return;
        wasOn = on;
        Paintbox.Tone shown = on ? (tone == Paintbox.Tone.Hot ? Paintbox.Tone.Secondary : Paintbox.Tone.Hot) : tone;
        if (fill != null) fill.color = Paintbox.FillOf(shown);
        Color ink = Paintbox.InkOf(shown);
        if (glyph != null) glyph.color = ink;
        if (word != null) word.color = ink;
        if (sub != null) sub.color = new Color(ink.r, ink.g, ink.b, 0.8f);
    }
}
