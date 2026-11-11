// From the Meccha Client SDK sample "Online integration" (v1.0.0); CoinsText now writes amounts the
// way the rest of the game does (CoinBalance.Format). Don't import the sample's own copy of this
// file as well, or OnlineUI is defined twice.

using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Small builder for the online screens, in the same runtime-built style as MatchmakingUI and
// MatchHUD (legacy uGUI Text, UIFont.Default, a 1920x1080 reference canvas matched on height).
// Flat colours for now; the screens only use these helpers, so reskinning them with the game's
// sprites later is a change in one place.
public static class OnlineUI
{
    public static readonly Color Dim = new Color(0f, 0f, 0f, 0.6f);
    public static readonly Color PanelColor = new Color(0.12f, 0.14f, 0.20f, 0.97f);
    public static readonly Color Green = new Color(0.20f, 0.60f, 0.32f, 0.97f);
    public static readonly Color Blue = new Color(0.20f, 0.42f, 0.75f, 0.97f);
    public static readonly Color Gray = new Color(0.30f, 0.32f, 0.38f, 0.95f);
    public static readonly Color Red = new Color(0.75f, 0.25f, 0.22f, 0.97f);
    public static readonly Color Muted = new Color(1f, 1f, 1f, 0.65f);
    public static readonly Color Warning = new Color(1f, 0.78f, 0.35f, 1f);

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

    /// <summary>A full-screen dimmer with a centred panel; returns the panel.</summary>
    public static RectTransform Modal(Transform canvas, string name, Vector2 panelSize)
    {
        Image dim = NewImage(name, canvas, Dim);
        Stretch(dim.rectTransform);

        Image panel = NewImage("Panel", dim.transform, PanelColor);
        Place(panel.rectTransform, Vector2.zero, panelSize);
        return panel.rectTransform;
    }

    public static Image NewImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Image image = go.AddComponent<Image>();
        image.color = color;
        return image;
    }

    public static Text NewLabel(Transform parent, string text, int size, Vector2 pos, Vector2 box,
                             TextAnchor anchor = TextAnchor.MiddleCenter, bool bold = false)
    {
        var go = new GameObject("Label");
        go.transform.SetParent(parent, false);

        Text label = go.AddComponent<Text>();
        label.font = UIFont.Default;
        label.text = text;
        label.fontSize = size;
        label.color = Color.white;
        label.alignment = anchor;
        label.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        label.supportRichText = true;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;

        Place(label.rectTransform, pos, box);
        return label;
    }

    public static Button NewButton(Transform parent, string text, Vector2 pos, Vector2 box, Color color, UnityAction onClick)
    {
        Image bg = NewImage("Button", parent, color);
        Place(bg.rectTransform, pos, box);

        Button button = bg.gameObject.AddComponent<Button>();
        button.targetGraphic = bg;
        if (onClick != null) button.onClick.AddListener(onClick);

        Text label = NewLabel(bg.transform, text, 36, Vector2.zero, box, TextAnchor.MiddleCenter, true);
        Stretch(label.rectTransform);
        return button;
    }

    public static InputField NewInput(Transform parent, string placeholder, Vector2 pos, Vector2 box)
    {
        Image bg = NewImage("Input", parent, new Color(1f, 1f, 1f, 0.95f));
        Place(bg.rectTransform, pos, box);

        Text hint = NewLabel(bg.transform, placeholder, 34, Vector2.zero, box, TextAnchor.MiddleLeft);
        hint.color = new Color(0f, 0f, 0f, 0.4f);
        hint.fontStyle = FontStyle.Italic;
        Inset(hint.rectTransform, 20f);

        Text text = NewLabel(bg.transform, "", 34, Vector2.zero, box, TextAnchor.MiddleLeft);
        text.color = Color.black;
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
