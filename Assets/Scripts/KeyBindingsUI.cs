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
                  : "Tap a keycap, then press the new key.";
    }

    // ---- Building ----

    // Design B (2026-11-24): a page with Movement and Actions as tabs, each action a row with its
    // key on a keycap, Reset defaults and Done in the foot.
    private static readonly GameAction[] Movement =
    {
        GameAction.MoveForward, GameAction.MoveBack, GameAction.MoveLeft, GameAction.MoveRight, GameAction.TurnLeft,
        GameAction.TurnRight, GameAction.AimUp, GameAction.AimDown, GameAction.Jump,
    };

    private bool actionsTab;
    private PaintboxPage page;
    private RectTransform tabsRow, list;

    private void Build()
    {
        if (canvasGO != null) return;

        Canvas canvas = OnlineUI.CreateCanvas("ControlsCanvas", transform, 53);   // over Settings (52)
        canvasGO = canvas.gameObject;
        page = PaintboxPage.Build(canvas.transform, "ControlsPage", "Controls", () => { AudioManager.ButtonTap(); Close(); }, PaintboxPage.Top.None, null, foot: true);
        Paintbox.Tag(page.Right, "Keyboard", Paintbox.Wash, Paintbox.WashInk, "Keyboard", 24f).name = "KeyboardChip";

        tabsRow = Paintbox.TopBand(Paintbox.NewRect(page.Content, "Tabs"), 0f, 84f);
        HorizontalLayoutGroup h = tabsRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 14f;
        h.padding = new RectOffset(0, 0, 4, 10);
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        Image rule = Paintbox.Divider(page.Content);
        Paintbox.TopBand(rule.rectTransform, 92f, 3f);
        hint = Paintbox.Label(page.Content, "", 24f, Paintbox.Muted);
        Paintbox.TopBand(hint.rectTransform, 102f, 40f);

        PaintboxFrame panel = Paintbox.Panel(page.Content, "Keys", 30f);
        Paintbox.Fill(panel.Root, 0f, 0f, 150f, 0f);
        list = Paintbox.Fill(Paintbox.NewRect(panel.Body, "List"), 30f, 30f, 16f, 16f);

        Button reset = page.FootLeftButton("Reset defaults", Paintbox.Tone.Paper, "RotateCcw", () =>
        {
            AudioManager.ButtonTap();
            StopWaiting();
            KeyBindings.ResetAll();
        }, 300f);
        reset.name = "ResetKeys";
        Button done = page.FootButton("Done", Paintbox.Tone.Primary, "Check", () =>
        {
            AudioManager.ButtonTap();
            Close();
        }, 220f);
        done.name = "BackKeys";

        BuildTab();
        canvasGO.SetActive(false);
    }

    // The tab's actions in two columns, each with its keycap.
    private void BuildTab()
    {
        for (int i = tabsRow.childCount - 1; i >= 0; i--) { tabsRow.GetChild(i).gameObject.SetActive(false); Destroy(tabsRow.GetChild(i).gameObject); }
        Button move = PaintboxPage.Chip(tabsRow, "Movement", "Footprints", !actionsTab, () => { AudioManager.ButtonTap(); actionsTab = false; StopWaiting(); BuildTab(); Refresh(); });
        move.gameObject.AddComponent<LayoutElement>().preferredWidth = 290f;
        Button act = PaintboxPage.Chip(tabsRow, "Actions", "Gamepad2", actionsTab, () => { AudioManager.ButtonTap(); actionsTab = true; StopWaiting(); BuildTab(); Refresh(); });
        act.gameObject.AddComponent<LayoutElement>().preferredWidth = 260f;

        for (int i = list.childCount - 1; i >= 0; i--) { list.GetChild(i).gameObject.SetActive(false); Destroy(list.GetChild(i).gameObject); }
        keyLabels.Clear();
        var actions = new List<GameAction>();
        foreach (GameAction a in KeyBindings.All)
            if ((System.Array.IndexOf(Movement, a) >= 0) != actionsTab) actions.Add(a);

        int rows = (actions.Count + 1) / 2;
        for (int i = 0; i < actions.Count; i++)
        {
            GameAction action = actions[i];
            int column = i / rows;
            RectTransform row = Paintbox.NewRect(list, "Row_" + action);
            row.anchorMin = new Vector2(column * 0.5f, 1f);
            row.anchorMax = new Vector2(column * 0.5f + 0.5f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.offsetMin = new Vector2(column == 0 ? 0f : 24f, -(i % rows + 1) * 70f);
            row.offsetMax = new Vector2(column == 0 ? -24f : 0f, -(i % rows) * 70f);
            Text name = Paintbox.Label(row, KeyBindings.NameOf(action), 28f, Paintbox.Ink);
            name.name = "Name_" + action;
            Paintbox.Fill(name.rectTransform, 0f, 200f, 0f, 0f);
            Image line = Paintbox.Image(row, "Rule", null, Paintbox.Line);
            line.rectTransform.anchorMin = new Vector2(0f, 0f);
            line.rectTransform.anchorMax = new Vector2(1f, 0f);
            line.rectTransform.offsetMin = Vector2.zero;
            line.rectTransform.offsetMax = new Vector2(0f, 2f);

            // The keycap: paper on a soft edge with a little depth.
            Button key = Paintbox.Button(row, "", Paintbox.Tone.Quiet, null, () => Listen(action), 24f, 12f);
            key.name = "Key_" + action;
            Paintbox.Pin((RectTransform)key.transform, new Vector2(1f, 0.5f), new Vector2(0f, 3f), new Vector2(170f, 52f));
            ((RectTransform)key.transform).pivot = new Vector2(1f, 0.5f);
            PaintboxFrame f = key.GetComponent<PaintboxFrame>();
            f.Edge.color = Paintbox.PanelLine;
            if (f.Shade != null) f.Shade.color = Paintbox.PanelLine;
            Text cap = Paintbox.Label(f.Body, "", 24f, Paintbox.Ink, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
            cap.name = "Label";
            Paintbox.Fill(cap.rectTransform, 6f, 6f, 0f, 0f);
            Paintbox.Fit(cap, 14);
            keyLabels[action] = cap;
        }
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
        Image image = Box(name, parent, tint, pos, size);
        OnlineUI.Chunky(image, Mathf.Min(Paintbox.Radius, size.y * 0.32f), Paintbox.Ink, 5f);
        Button button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        image.gameObject.AddComponent<PaintboxPressFlat>();
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
        label.font = Paintbox.Bold;
        label.text = text;
        label.fontSize = size;
        label.alignment = anchor;
        label.color = Paintbox.Ink;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        UiTranslator.Track(label);
        return label;
    }
}
