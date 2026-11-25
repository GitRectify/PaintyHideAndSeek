using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A scene button in design B's HUD look (2026-11-24): a rounded (or round) shape of dark glass
/// (or hot yellow, paper, violet) with an ink edge, a hard shadow, a Lucide icon and a short word
/// under it. The button's own Image stays the thing that is tapped, but is no longer drawn, and
/// its old pictures go; the scripts that swap its picture for an "on" one (paint, poses, the free
/// camera, lying down) still do, and the button shows that as its "on" colour.
/// </summary>
[DisallowMultipleComponent]
public sealed class PaintboxHudButton : MonoBehaviour
{
    private Image root;
    private Sprite offSprite;
    private readonly List<Graphic> hidden = new List<Graphic>();
    private Image fill, glyph;
    private Text word;
    private Paintbox.Tone tone;
    private bool wasOn;

    /// <summary>Gives <paramref name="go"/> the look; <paramref name="hide"/> names old children not to draw any more.</summary>
    public static PaintboxHudButton Apply(GameObject go, string icon, string label, bool round, Paintbox.Tone tone, params string[] hide)
    {
        var rt = go.transform as RectTransform;
        if (rt == null) return null;
        PaintboxHudButton skin = go.GetComponent<PaintboxHudButton>() ?? go.AddComponent<PaintboxHudButton>();
        skin.Build(rt, icon, label, round, tone, hide);
        return skin;
    }

    private void Build(RectTransform rt, string icon, string label, bool round, Paintbox.Tone buttonTone, string[] hide)
    {
        tone = buttonTone;
        root = GetComponent<Image>();
        if (root != null) offSprite = root.sprite;

        foreach (string n in hide)
        {
            Transform t = rt.Find(n);
            if (t == null) continue;
            foreach (Graphic g in t.GetComponentsInChildren<Graphic>(true)) hidden.Add(g);
        }

        Vector2 size = rt.rect.size;
        if (size.x < 1f || size.y < 1f) size = rt.sizeDelta;
        float side = Mathf.Max(24f, Mathf.Min(size.x, size.y));
        float border = Mathf.Clamp(side * 0.035f, 2.5f, 7f);
        float shadow = Mathf.Clamp(side * 0.05f, 3f, 10f);

        // The shape: behind everything else the button holds (its cooldown shade, its text).
        RectTransform skin = Paintbox.NewRect(rt, "PbSkin");
        skin.SetAsFirstSibling();
        Paintbox.Fill(skin);
        if (!round && Mathf.Abs(size.x - size.y) < 2f && size.x > 1f)
        {
            // Square art buttons were drawn a little inside their box; so is the shape.
            float inset = side * 0.06f;
            Paintbox.Fill(skin, inset, inset, inset, inset);
        }

        Sprite shape = round ? UiArt.Circle : UiArt.Rounded;
        float radius = Mathf.Min(side * 0.28f, 34f);
        bool glass = tone == Paintbox.Tone.Glass;
        Color edge = glass ? Paintbox.GlassEdge : Paintbox.Ink;

        Image drop = Part(skin, "Shadow", shape, glass ? Paintbox.GlassShadow : Paintbox.Ink, round, radius);
        Paintbox.Fill(drop.rectTransform, 0f, 0f, shadow, -shadow);
        Image rim = Part(skin, "Edge", shape, edge, round, radius);
        Paintbox.Fill(rim.rectTransform);
        fill = Part(skin, "Fill", shape, Paintbox.FillOf(tone), round, Mathf.Max(2f, radius - border));
        Paintbox.Fill(fill.rectTransform, border, border, border, border);

        bool words = !string.IsNullOrEmpty(label) && side >= 90f;   // a word on a small button is too small to read
        Color ink = Paintbox.InkOf(tone);
        float iconSize = side * (words ? 0.38f : 0.5f);
        glyph = Paintbox.IconImage(skin, icon, iconSize, ink);
        glyph.name = "PbIcon";
        glyph.rectTransform.anchoredPosition = new Vector2(0f, words ? side * 0.1f : 0f);

        if (words)
        {
            word = Paintbox.Label(skin, label, Mathf.Clamp(side * 0.17f, 14f, 44f), ink, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
            word.horizontalOverflow = HorizontalWrapMode.Overflow;
            RectTransform w = word.rectTransform;
            w.anchorMin = new Vector2(0f, 0f);
            w.anchorMax = new Vector2(1f, 0f);
            w.pivot = new Vector2(0.5f, 0f);
            w.offsetMin = new Vector2(0f, side * 0.1f);
            w.offsetMax = new Vector2(0f, side * 0.1f + side * 0.24f);
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

    private void LateUpdate()
    {
        // The button's own picture and its old children are not drawn (they still take taps).
        if (root != null && root.color.a > 0f) root.color = new Color(root.color.r, root.color.g, root.color.b, 0f);
        for (int i = 0; i < hidden.Count; i++)
        {
            Graphic g = hidden[i];
            if (g != null && g.color.a > 0f) g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
        }

        // "On": a script swapped the button's picture for its on one.
        bool on = root != null && offSprite != null && root.sprite != offSprite;
        if (on == wasOn) return;
        wasOn = on;
        Paintbox.Tone shown = on ? (tone == Paintbox.Tone.Hot ? Paintbox.Tone.Secondary : Paintbox.Tone.Hot) : tone;
        if (fill != null) fill.color = Paintbox.FillOf(shown);
        Color ink = Paintbox.InkOf(shown);
        if (glyph != null) glyph.color = ink;
        if (word != null) word.color = ink;
    }
}
