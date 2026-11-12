using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

/// <summary>
/// Settings &gt; Controls: every action with its key. Tap a key to change it, then press the new
/// key (Esc cancels); a key already used elsewhere swaps over, so no key does two things. "Reset
/// to defaults" puts every key back. Built at runtime over the Settings popup, in the game's font
/// and the player's language (UiTranslator).
/// </summary>
public sealed class KeyBindingsUI : MonoBehaviour
{
    private const float RowHeight = 66f;
    private const int Columns = 2;
    private const float ColumnWidth = 720f;

    private static KeyBindingsUI instance;

    private GameObject canvasGO;
    private Font font;
    private Sprite buttonSprite;
    private Text hint;
    private readonly Dictionary<GameAction, Text> keyLabels = new Dictionary<GameAction, Text>();

    // The action waiting for its new key, and the key that set it (play is held off until it is let go).
    private GameAction? waiting;
    private Key settledKey = Key.None;

    public static bool IsOpen => instance != null && instance.canvasGO != null && instance.canvasGO.activeSelf;

    /// <summary>Opens the screen, over Settings.</summary>
    public static void Open(Font font, Sprite buttonSprite)
    {
        if (instance == null)
        {
            var go = new GameObject("KeyBindingsUI");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<KeyBindingsUI>();
        }

        instance.font = font != null ? font : UIFont.Default;
        instance.buttonSprite = buttonSprite;
        instance.Build();
        instance.waiting = null;
        instance.Refresh();
        instance.canvasGO.SetActive(true);
    }

    public static void Close()
    {
        if (instance == null || instance.canvasGO == null) return;
        instance.StopWaiting();
        instance.canvasGO.SetActive(false);
    }

    private void OnEnable() => KeyBindings.Changed += Refresh;
    private void OnDisable() => KeyBindings.Changed -= Refresh;

    private void Update()
    {
        if (canvasGO == null || !canvasGO.activeSelf) return;

        Keyboard keyboard = Keyboard.current;

        // The key that was just bound: play waits until it is let go, or it would act at once.
        if (settledKey != Key.None)
        {
            bool stillDown = keyboard != null && keyboard[settledKey].isPressed;
            if (!stillDown)
            {
                settledKey = Key.None;
                KeyBindings.Listening = false;
            }
            return;
        }

        if (waiting == null || keyboard == null) return;

        if (keyboard.escapeKey.wasPressedThisFrame)
        {
            StopWaiting();
            Refresh();
            return;
        }

        foreach (KeyControl key in keyboard.allKeys)
        {
            if (key == null || !key.wasPressedThisFrame || key.keyCode == Key.None) continue;
            GameAction action = waiting.Value;
            waiting = null;
            settledKey = key.keyCode;            // Listening stays on until this key is let go
            KeyBindings.Set(action, key.keyCode);
            return;
        }
    }

    private void StopWaiting()
    {
        waiting = null;
        settledKey = Key.None;
        KeyBindings.Listening = false;
    }

    private void Listen(GameAction action)
    {
        AudioManager.ButtonTap();
        if (Keyboard.current == null)
        {
            hint.text = "No keyboard found. Connect one to change the keys.";
            return;
        }

        waiting = action;
        settledKey = Key.None;
        KeyBindings.Listening = true;
        Refresh();
    }

    private void Refresh()
    {
        if (canvasGO == null) return;
        foreach (KeyValuePair<GameAction, Text> row in keyLabels)
            row.Value.text = waiting == row.Key ? "Press a key..." : KeyBindings.KeyName(KeyBindings.Get(row.Key));

        hint.text = Keyboard.current == null ? "No keyboard found. Connect one to change the keys."
                  : waiting != null ? "Press the new key. Esc cancels."
                  : "Tap a key to change it.";
    }

    // ---- Building ----

    private void Build()
    {
        if (canvasGO != null) return;

        canvasGO = new GameObject("ControlsCanvas");
        canvasGO.transform.SetParent(transform, false);
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 53;                       // over Settings (52)
        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);    // landscape, as the game is played
        scaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        Image dim = Box("Dim", canvasGO.transform, new Color(0f, 0f, 0f, 0.7f), Vector2.zero, Vector2.zero);
        RectTransform dimRT = dim.rectTransform;
        dimRT.anchorMin = Vector2.zero;
        dimRT.anchorMax = Vector2.one;
        dimRT.offsetMin = dimRT.offsetMax = Vector2.zero;

        // Two columns of actions, each with its key.
        int count = KeyBindings.All.Length;
        int rows = (count + Columns - 1) / Columns;
        float height = 300f + rows * RowHeight;
        float width = Columns * ColumnWidth + 80f;
        Image panel = Box("Panel", canvasGO.transform, new Color(0.12f, 0.14f, 0.2f, 0.97f), Vector2.zero, new Vector2(width, height));
        Outline outline = panel.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(1f, 1f, 1f, 0.25f);
        outline.effectDistance = new Vector2(3f, -3f);
        Transform p = panel.transform;

        Label("Title", p, "Controls", 56, new Vector2(0f, height / 2f - 60f), new Vector2(width - 80f, 70f), TextAnchor.MiddleCenter);
        hint = Label("Hint", p, "", 32, new Vector2(0f, height / 2f - 115f), new Vector2(width - 80f, 44f), TextAnchor.MiddleCenter);
        hint.color = new Color(1f, 1f, 1f, 0.7f);

        float top = height / 2f - 175f;
        for (int i = 0; i < count; i++)
        {
            GameAction action = KeyBindings.All[i];
            int column = i / rows;
            float x = (column - (Columns - 1) / 2f) * ColumnWidth;
            float y = top - (i % rows) * RowHeight;
            Label("Name_" + action, p, KeyBindings.NameOf(action), 34, new Vector2(x - 110f, y), new Vector2(420f, RowHeight), TextAnchor.MiddleLeft);

            Button key = MakeButton("Key_" + action, p, new Vector2(x + 220f, y), new Vector2(230f, RowHeight - 10f), new Color(0.25f, 0.3f, 0.42f, 1f));
            keyLabels[action] = Label("Label", key.transform, "", 32, Vector2.zero, new Vector2(220f, RowHeight - 10f), TextAnchor.MiddleCenter);
            key.onClick.AddListener(() => Listen(action));
        }

        Button reset = MakeButton("ResetKeys", p, new Vector2(-200f, -height / 2f + 70f), new Vector2(360f, 84f), new Color(0.65f, 0.3f, 0.25f, 1f));
        Label("Label", reset.transform, "Reset to defaults", 34, Vector2.zero, new Vector2(350f, 84f), TextAnchor.MiddleCenter);
        reset.onClick.AddListener(() =>
        {
            AudioManager.ButtonTap();
            StopWaiting();
            KeyBindings.ResetAll();
        });

        Button back = MakeButton("BackKeys", p, new Vector2(200f, -height / 2f + 70f), new Vector2(360f, 84f), new Color(0.2f, 0.55f, 0.35f, 1f));
        Label("Label", back.transform, "Back", 34, Vector2.zero, new Vector2(350f, 84f), TextAnchor.MiddleCenter);
        back.onClick.AddListener(() =>
        {
            AudioManager.ButtonTap();
            Close();
        });

        canvasGO.SetActive(false);
    }

    private Image Box(string name, Transform parent, Color color, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        Image image = go.AddComponent<Image>();
        image.color = color;
        return image;
    }

    private Button MakeButton(string name, Transform parent, Vector2 pos, Vector2 size, Color tint)
    {
        Image image = Box(name, parent, Color.white, pos, size);
        if (buttonSprite != null)
        {
            image.sprite = buttonSprite;
            image.type = Image.Type.Simple;
        }
        image.color = buttonSprite != null ? Color.white : tint;
        Button button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        return button;
    }

    private Text Label(string name, Transform parent, string text, int size, Vector2 pos, Vector2 box, TextAnchor anchor)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = box;

        Text label = go.AddComponent<Text>();
        label.font = font;
        label.text = text;
        label.fontSize = size;
        label.alignment = anchor;
        label.color = Color.white;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.6f);
        outline.effectDistance = new Vector2(2f, -2f);
        UiTranslator.Track(label);
        return label;
    }
}
