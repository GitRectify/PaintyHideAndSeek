using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// NOTE: Runtime-built settings panel (music/SFX toggles, a graphics-quality stepper and a home
// button). The original's "Restore purchase" button is not built: the game sells nothing through
// a store, and coin top-ups are credited on the server, so there is nothing to restore. Same rebuild-safe HashSet<GameObject> dedup pattern as
// RemoveAdsUI. New confirmed members: AudioManager.SetMusicEnabled(bool)/SetSfxEnabled(bool)
// (static), HomeUI.GoHome(), and a fully-qualified reference to a new, still-unreversed class
// Dev.Scripts.UI.HomeScene.IAPPackage.CheckPurchased() (static). The tail of this paste also
// contained UnityEngine.Rendering.DebugDisplaySettingsUI - pure URP/Unity engine internal debug-
// rendering plumbing, not game code - and is not reconstructed here.
public class SettingsUI : MonoBehaviour
{
    private class Tog
    {
        public Image track;
        public Image knob;
        public Text onLabel;
        public Text offLabel;
        public string key;
        public bool on;
    }

    [SerializeField] private Font font;
    [SerializeField] private Sprite panelSprite;
    [SerializeField] private Sprite titleSprite;
    [SerializeField] private Sprite closeIcon;
    [SerializeField] private Sprite homeBg;
    [SerializeField] private Sprite homeIcon;
    [SerializeField] private Sprite restoreBg;
    [SerializeField] private Sprite toggleTrack;
    [SerializeField] private Sprite knobOn;
    // NOTE: confirmed to exist via a raw field offset in ApplyToggle (0x48, used when the
    // toggle is off) but never referenced by name anywhere in MakeToggle - genuinely present
    // but otherwise unused in what was decompiled here.
    [SerializeField] private Sprite knobOff;
    [SerializeField] private Sprite arrowBack;
    [SerializeField] private Sprite arrowNext;

    private GameObject canvasGO;
    private Text qualityLabel;
    private int qualityIndex;
    private readonly List<Tog> toggles = new List<Tog>();
    private readonly HashSet<GameObject> _reused = new HashSet<GameObject>();

    // NOTE: these four static readonly fields are decoded directly from the .cctor's raw writes
    // into the class's static-fields block (a string[3] array plus a packed Vector2 and two
    // packed Colors immediately following it) - not a guess.
    private static readonly string[] QualityNames = { "Low", "Medium", "High" };
    private static readonly Vector2 CenterAnchor = new Vector2(0.5f, 0.5f);
    private static readonly Color OnColor = new Color(1f, 1f, 1f, 1f);
    private static readonly Color OffColor = new Color(1f, 1f, 1f, 0.4f);

    private void Start()
    {
        EnsureBuilt();
        LoadState();
        Hide();
    }

    private void EnsureBuilt()
    {
        if (canvasGO != null)
        {
            return;
        }

        if (font == null)
        {
            font = UIFont.Default;
        }

        _reused.Clear();
        toggles.Clear();

        bool canvasCreated;
        canvasGO = SceneUI.GetOrCreate("SettingsCanvas", null, out canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);

        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 52;

            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);

        Transform canvasT = canvasGO.transform;

        Image dim = NewImage("Dim", canvasT, null, new Color(0f, 0f, 0f, 0.6f), true, false);
        Stretch(dim);

        Image popup = NewImage("Popup", canvasT, panelSprite, Color.white, true, false);
        Place(popup, CenterAnchor, CenterAnchor, Vector2.zero, new Vector2(760f, 644f));
        Transform parent = popup.transform;

        Image title = NewImage("Title", parent, titleSprite, Color.white, false, true);
        Place(title, CenterAnchor, CenterAnchor, new Vector2(20f, 258f), new Vector2(230f, 70f));

        Image closeImg = NewImage("CloseBtn", parent, closeIcon, Color.white, true, true);
        Place(closeImg, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-14f, -14f), new Vector2(62f, 65f));

        Button closeBtn = SceneUI.GetOrAdd<Button>(closeImg.gameObject);
        closeBtn.targetGraphic = closeImg;

        MakeLabel("LblMusic", parent, "Music/Ambient", 120f);
        MakeToggle("TogMusic", parent, "set_music", 120f);
        MakeLabel("LblSound", parent, "Sound Effects", 50f);
        MakeToggle("TogSound", parent, "set_sound", 50f);
        MakeLabel("LblGfx", parent, "Graphic Quality", -20f);
        MakeStepper(parent, -20f);

        // Language (L): English, Chinese, Russian, with the same arrows as the quality.
        MakeLabel("LblLang", parent, "Language", -90f);
        MakeLanguageStepper(parent, -90f);

        Button homeBtn = NewButton("HomeBtn", parent, homeBg, new Vector2(0f, -195f), new Vector2(244f, 88f));
        if (homeIcon != null)
        {
            Image homeIconImg = NewImage("Icon", homeBtn.transform, homeIcon, Color.white, false, true);
            Place(homeIconImg, CenterAnchor, CenterAnchor, new Vector2(0f, -52f), new Vector2(56f, 56f));
        }
        Text homeLabel = NewText("Label", homeBtn.transform, "Home", 30, Color.white, (int)TextAnchor.MiddleCenter, (int)HorizontalWrapMode.Overflow);
        Place(homeLabel, CenterAnchor, CenterAnchor, new Vector2(0f, 24f), new Vector2(150f, 50f));

        // The keyboard's keys (KeyBindingsUI) and closing the game for good. In the scene's popup Home
        // and Restore Purchase share the bottom row, so these go in the space above them, one over
        // each; a popup built from code has Home alone at the bottom, and these go either side of it.
        Vector2 extraSize = new Vector2(220f, 88f);
        Vector2 controlsPos = new Vector2(-252f, -195f);
        Vector2 exitPos = new Vector2(252f, -195f);
        if (homeBtn.transform is RectTransform homeRT && parent.Find("RestoreBtn") is RectTransform restoreRT)
        {
            extraSize = new Vector2(restoreRT.sizeDelta.x, 66f);
            float y = homeRT.anchoredPosition.y + homeRT.sizeDelta.y / 2f + 8f + extraSize.y / 2f;
            controlsPos = new Vector2(restoreRT.anchoredPosition.x, y);
            exitPos = new Vector2(homeRT.anchoredPosition.x, y);
        }

        Button controlsBtn = NewButton("ControlsBtn", parent, homeBg, controlsPos, extraSize);
        Text controlsLabel = NewText("Label", controlsBtn.transform, "Controls", 30, Color.white, (int)TextAnchor.MiddleCenter, (int)HorizontalWrapMode.Overflow);
        Place(controlsLabel, CenterAnchor, CenterAnchor, Vector2.zero, new Vector2(extraSize.x - 20f, 50f));
        controlsBtn.onClick.RemoveAllListeners();
        controlsBtn.onClick.AddListener(() =>
        {
            AudioManager.ButtonTap();
            KeyBindingsUI.Open(font, null);
        });

        Button exitBtn = NewButton("ExitBtn", parent, homeBg, exitPos, extraSize);
        Text exitLabel = NewText("Label", exitBtn.transform, "Exit Game", 30, Color.white, (int)TextAnchor.MiddleCenter, (int)HorizontalWrapMode.Overflow);
        Place(exitLabel, CenterAnchor, CenterAnchor, Vector2.zero, new Vector2(extraSize.x - 20f, 50f));
        exitBtn.onClick.RemoveAllListeners();
        exitBtn.onClick.AddListener(() =>
        {
            AudioManager.ButtonTap();
            ShowExitConfirm(true);
        });
        UiTranslator.Track(controlsLabel);
        UiTranslator.Track(exitLabel);

        WireControls();
        BuildPage();   // design B's page over the scene's popup (2026-11-24)
        canvasGO.SetActive(false);
    }

    // ================= Design B: the Settings page (2026-11-24) =================
    //
    // A full page: Audio (music, sound effects: switches that say On or Off) and Preferences
    // (graphics quality, language: a tap moves to the next) side by side, a row to the Controls,
    // and Exit game and Home in the foot. The scene's popup stays (its switches and arrows are
    // what this page moves) but is not drawn.

    private PaintboxPage page;
    private readonly Dictionary<Tog, (Image track, RectTransform knob, Text word)> switches = new Dictionary<Tog, (Image, RectTransform, Text)>();
    private Text pageQuality, pageLanguage;
    private GameObject exitDialog;

    private void BuildPage()
    {
        if (page != null) return;
        Transform canvasT = canvasGO.transform;
        foreach (string old in new[] { "Dim", "Popup" })
        {
            Transform t = canvasT.Find(old);
            if (t == null) continue;
            if (t.GetComponent<CanvasGroup>() == null) t.gameObject.AddComponent<CanvasGroup>();
            CanvasGroup g = t.GetComponent<CanvasGroup>();
            g.alpha = 0f;
            g.blocksRaycasts = false;
            g.interactable = false;
        }

        Paintbox.Canvas1080(canvasGO);   // the page's words drawn at the screen's own size, not scaled up
        RectTransform units = PaintboxUnits.Create(canvasT, "SettingsDesign");
        page = PaintboxPage.Build(units, "SettingsPage", "Settings", () => { AudioManager.ButtonTap(); Hide(); }, PaintboxPage.Top.None, null, foot: true);

        // Audio and Preferences, side by side.
        RectTransform audio = Card(page.Content, "Audio", "AUDIO", "Headphones", 0f, 0.5f);
        RectTransform prefs = Card(page.Content, "Preferences", "PREFERENCES", "SlidersHorizontal", 0.5f, 1f);
        for (int i = 0; i < toggles.Count; i++)
        {
            Tog t = toggles[i];
            RectTransform row = Row(audio, i, t.key == "set_music" ? "Music" : "Sound effects", i == 0 ? "LblMusic" : "LblSound");
            AddSwitch(row, t);
        }
        RectTransform gfx = Row(prefs, 0, "Graphics", "LblGfx");
        pageQuality = Chooser(gfx, "GfxChoice", () => StepQuality(1));
        RectTransform lang = Row(prefs, 1, "Language", "LblLang");
        pageLanguage = Chooser(lang, "LangChoice", () => StepLanguage(1));

        // Controls: a row of its own.
        PaintboxFrame controls = Paintbox.Frame(page.Content, "ControlsRow", Paintbox.Paper, 26f, Paintbox.Border, 0f, Paintbox.PanelLine);
        Paintbox.TopBand(controls.Root, 330f, 112f);
        Button open = controls.Root.gameObject.AddComponent<Button>();
        open.targetGraphic = controls.Fill;
        open.transition = Selectable.Transition.None;
        open.onClick.AddListener(() => { AudioManager.ButtonTap(); KeyBindingsUI.Open(font, null); });
        controls.Root.gameObject.AddComponent<UiPress>();
        controls.Root.name = "ControlsBtn";
        Image badge = Paintbox.Rounded(controls.Body, "Badge", Paintbox.Wash, 16f);
        Paintbox.Pin(badge.rectTransform, new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(64f, 64f));
        badge.rectTransform.pivot = new Vector2(0f, 0.5f);
        Paintbox.IconImage(badge.rectTransform, "Keyboard", 36f, Paintbox.WashInk);
        Text ct = Paintbox.Label(controls.Body, "Controls", 32f, Paintbox.Ink, Paintbox.Weight.Bold);
        ct.rectTransform.anchorMin = new Vector2(0f, 0.5f);
        ct.rectTransform.anchorMax = new Vector2(1f, 1f);
        ct.rectTransform.offsetMin = new Vector2(108f, 0f);
        ct.rectTransform.offsetMax = new Vector2(-80f, -14f);
        ct.alignment = TextAnchor.LowerLeft;
        Text cd = Paintbox.Label(controls.Body, "View and change keyboard bindings", 23f, Paintbox.Muted);
        cd.rectTransform.anchorMin = new Vector2(0f, 0f);
        cd.rectTransform.anchorMax = new Vector2(1f, 0.5f);
        cd.rectTransform.offsetMin = new Vector2(108f, 14f);
        cd.rectTransform.offsetMax = new Vector2(-80f, 0f);
        cd.alignment = TextAnchor.UpperLeft;
        Image chevron = Paintbox.IconImage(controls.Body, "ChevronRight", 38f, Paintbox.Ink);
        Paintbox.Pin(chevron.rectTransform, new Vector2(1f, 0.5f), new Vector2(-24f, 0f), new Vector2(38f, 38f));
        chevron.rectTransform.pivot = new Vector2(1f, 0.5f);

        // The foot: Exit game on the left, Home on the right.
        Button exit = page.FootLeftButton("Exit game", Paintbox.Tone.Paper, "LogOut", () => { AudioManager.ButtonTap(); ShowExitConfirm(true); }, 250f);
        exit.name = "ExitBtn";
        foreach (Text t in exit.GetComponentsInChildren<Text>(true)) t.color = Paintbox.Bad;
        foreach (Image i in exit.GetComponentsInChildren<Image>(true)) if (i.name == "Icon") i.color = Paintbox.Bad;
        Button home = page.FootButton("Home", Paintbox.Tone.Primary, "House", () => { AudioManager.ButtonTap(); OnHome(); }, 220f);
        home.name = "HomeBtn";

        foreach (Tog t in toggles) PaintSwitch(t);
        RefreshPage();
    }

    private static RectTransform Card(RectTransform content, string name, string title, string icon, float from, float to)
    {
        PaintboxFrame card = Paintbox.Panel(content, name, 30f);
        card.Root.anchorMin = new Vector2(from, 1f);
        card.Root.anchorMax = new Vector2(to, 1f);
        card.Root.pivot = new Vector2(0.5f, 1f);
        card.Root.offsetMin = new Vector2(from > 0f ? 12f : 0f, -312f);
        card.Root.offsetMax = new Vector2(to < 1f ? -12f : 0f, 0f);
        Image glyph = Paintbox.IconImage(card.Body, icon, 30f, Paintbox.Muted);
        Paintbox.Pin(glyph.rectTransform, new Vector2(0f, 1f), new Vector2(30f, -28f), new Vector2(30f, 30f));
        glyph.rectTransform.pivot = new Vector2(0f, 1f);
        Text kicker = PaintboxPage.Kicker(card.Body, title);
        Paintbox.TopBand(kicker.rectTransform, 26f, 34f, 74f, 30f);
        return card.Body;
    }

    // A row of a card: its name on the left; the control goes on the right.
    private static RectTransform Row(RectTransform card, int index, string label, string name)
    {
        RectTransform row = Paintbox.TopBand(Paintbox.NewRect(card, "Row_" + name), 78f + index * 112f, 104f, 30f, 30f);
        Text t = Paintbox.Label(row, label, 32f, Paintbox.Ink, Paintbox.Weight.Bold);
        t.name = name;
        Paintbox.Fill(t.rectTransform, 0f, 320f, 0f, 0f);
        if (index == 0)
        {
            Image rule = Paintbox.Divider(row);
            rule.rectTransform.anchorMin = new Vector2(0f, 0f);
            rule.rectTransform.anchorMax = new Vector2(1f, 0f);
            rule.rectTransform.offsetMin = new Vector2(0f, -4f);
            rule.rectTransform.offsetMax = new Vector2(0f, -1f);
        }
        return row;
    }

    // A switch: "On" or "Off" in words, and a yellow track with the knob to the right when on.
    private void AddSwitch(RectTransform row, Tog t)
    {
        Image hit = Paintbox.Image(row, "Switch", null, Color.clear, true);
        Paintbox.Pin(hit.rectTransform, new Vector2(1f, 0.5f), Vector2.zero, new Vector2(230f, 80f));
        hit.rectTransform.pivot = new Vector2(1f, 0.5f);
        Button b = hit.gameObject.AddComponent<Button>();
        b.targetGraphic = hit;
        b.onClick.AddListener(() => { AudioManager.ButtonTap(); FlipToggle(t); });
        Image track = Paintbox.Rounded(hit.rectTransform, "Track", Paintbox.Yellow, 26f);
        Paintbox.Pin(track.rectTransform, new Vector2(1f, 0.5f), Vector2.zero, new Vector2(104f, 56f));
        track.rectTransform.pivot = new Vector2(1f, 0.5f);
        Outline edge = track.gameObject.AddComponent<Outline>();
        edge.effectColor = Paintbox.Ink;
        edge.effectDistance = new Vector2(3f, -3f);
        Image knob = Paintbox.Image(track.rectTransform, "Knob", UiArt.Circle, Paintbox.Ink);
        knob.rectTransform.anchorMin = knob.rectTransform.anchorMax = knob.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        knob.rectTransform.sizeDelta = new Vector2(40f, 40f);
        Text word = Paintbox.Label(hit.rectTransform, "On", 26f, Paintbox.Ink, Paintbox.Weight.Bold, TextAnchor.MiddleRight);
        Paintbox.Fill(word.rectTransform, 0f, 118f, 0f, 0f);
        switches[t] = (track, knob.rectTransform, word);
    }

    private void PaintSwitch(Tog t)
    {
        if (!switches.TryGetValue(t, out var s)) return;
        s.track.color = t.on ? Paintbox.Yellow : Paintbox.Tab;
        s.knob.anchoredPosition = new Vector2(t.on ? 24f : -24f, 0f);
        s.knob.GetComponent<Image>().color = t.on ? Paintbox.Ink : Paintbox.Paper;
        Outline o = Paintbox.Ensure<Outline>(s.knob.gameObject);
        o.effectColor = Paintbox.Ink;
        o.effectDistance = new Vector2(2f, -2f);
        o.enabled = !t.on;
        s.word.text = t.on ? "On" : "Off";
        s.word.color = t.on ? Paintbox.Ink : Paintbox.Muted;
    }

    // A choice that moves to the next one on a tap (a dropdown's look: the value and a chevron).
    private static Text Chooser(RectTransform row, string name, UnityEngine.Events.UnityAction next)
    {
        Button b = Paintbox.Button(row, "", Paintbox.Tone.Quiet, null, () => { AudioManager.ButtonTap(); next(); }, 28f, 20f);
        b.name = name;
        Paintbox.Pin((RectTransform)b.transform, new Vector2(1f, 0.5f), Vector2.zero, new Vector2(230f, 76f));
        ((RectTransform)b.transform).pivot = new Vector2(1f, 0.5f);
        PaintboxFrame f = b.GetComponent<PaintboxFrame>();
        f.Edge.color = Paintbox.PanelLine;
        Text value = Paintbox.Label(f.Body, "", 28f, Paintbox.Ink, Paintbox.Weight.Bold);
        value.name = name == "LangChoice" ? "LangValue" : "GfxValue";
        Paintbox.Fill(value.rectTransform, 22f, 52f, 0f, 0f);
        Paintbox.Fit(value, 16);
        Image down = Paintbox.IconImage(f.Body, "ChevronDown", 28f, Paintbox.Ink);
        Paintbox.Pin(down.rectTransform, new Vector2(1f, 0.5f), new Vector2(-16f, 0f), new Vector2(28f, 28f));
        down.rectTransform.pivot = new Vector2(1f, 0.5f);
        return value;
    }

    private void RefreshPage()
    {
        if (pageQuality != null) pageQuality.text = QualityNames[Mathf.Clamp(qualityIndex, 0, QualityNames.Length - 1)];
        if (pageLanguage != null) pageLanguage.text = L.NameOf(L.Current);
    }

    private void LoadState()
    {
        foreach (Tog t in toggles)
        {
            t.on = PlayerPrefs.GetInt(t.key, 1) == 1;
            ApplyToggle(t);
            ApplyAudioSetting(t);
        }

        int saved = PlayerPrefs.GetInt("set_quality", 1);
        qualityIndex = Mathf.Clamp(saved, 0, QualityNames.Length - 1);
        RefreshQuality(Application.isPlaying);
    }

    public void Hide()
    {
        KeyBindingsUI.Close();
        ShowExitConfirm(false);
        if (canvasGO != null)
        {
            canvasGO.SetActive(false);
        }
    }

    /// <summary>The Settings popup is on screen.</summary>
    public bool IsShown => canvasGO != null && canvasGO.activeSelf;

    private GameObject exitConfirm;

    // "Exit the game?" over the popup: Exit closes the game (GameExit), Cancel goes back.
    private void ShowExitConfirm(bool show)
    {
        if (!show)
        {
            if (exitDialog != null) exitDialog.SetActive(false);
            return;
        }

        if (exitDialog == null && page != null)
        {
            Image dim = Paintbox.Image(page.Root, "ExitConfirm", null, Paintbox.Dim, true);
            Paintbox.Fill(dim.rectTransform);
            exitDialog = dim.gameObject;
            PaintboxFrame card = Paintbox.Frame(dim.rectTransform, "ExitBox", Paintbox.Paper, Paintbox.RadiusLarge, Paintbox.Border, 0f, Paintbox.PanelLine);
            Paintbox.Pin(card.Root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(860f, 380f));
            Image badge = Paintbox.Rounded(card.Body, "Badge", Paintbox.BadWash, 16f);
            Paintbox.Pin(badge.rectTransform, new Vector2(0f, 1f), new Vector2(40f, -36f), new Vector2(64f, 64f));
            badge.rectTransform.pivot = new Vector2(0f, 1f);
            Paintbox.IconImage(badge.rectTransform, "LogOut", 34f, Paintbox.Bad);
            Text question = Paintbox.Label(card.Body, "Exit the game?", 50f, Paintbox.Ink, Paintbox.Weight.Title);
            question.name = "Question";
            Paintbox.TopBand(question.rectTransform, 114f, 66f, 40f, 40f);
            Text why = Paintbox.Label(card.Body, "The game closes. An online match goes on without you; rejoin it from Matches.", 25f, Paintbox.Muted);
            why.verticalOverflow = VerticalWrapMode.Overflow;
            Paintbox.TopBand(why.rectTransform, 182f, 70f, 40f, 40f);
            RectTransform row = Paintbox.NewRect(card.Body, "Buttons");
            row.anchorMin = new Vector2(0f, 0f);
            row.anchorMax = new Vector2(1f, 0f);
            row.pivot = new Vector2(0.5f, 0f);
            row.offsetMin = new Vector2(40f, 30f);
            row.offsetMax = new Vector2(-40f, 110f);
            HorizontalLayoutGroup h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleRight;
            h.spacing = 16f;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = true;
            Button yes = Paintbox.Button(row, "Exit", Paintbox.Tone.Paper, "LogOut", () => { AudioManager.ButtonTap(); GameExit.Quit(); }, 28f);
            yes.name = "ExitYes";
            foreach (Text t in yes.GetComponentsInChildren<Text>(true)) t.color = Paintbox.Bad;
            foreach (Image i in yes.GetComponentsInChildren<Image>(true)) if (i.name == "Icon") i.color = Paintbox.Bad;
            yes.gameObject.AddComponent<LayoutElement>().preferredWidth = 190f;
            Button no = Paintbox.Button(row, "Stay", Paintbox.Tone.Primary, "Play", () => { AudioManager.ButtonTap(); ShowExitConfirm(false); }, 28f);
            no.name = "ExitNo";
            BackKey.Mark(no);
            no.gameObject.AddComponent<LayoutElement>().preferredWidth = 190f;
        }

        if (exitDialog != null)
        {
            exitDialog.transform.SetAsLastSibling();
            exitDialog.SetActive(true);
        }
    }

    private Button ConfirmButton(Transform parent, string name, string label, Vector2 pos, Color tint)
    {
        Image img = NewImage(name, parent, null, tint, true, false);
        Place(img, CenterAnchor, CenterAnchor, pos, new Vector2(240f, 90f));
        Button button = SceneUI.GetOrAdd<Button>(img.gameObject);
        button.targetGraphic = img;
        button.onClick.RemoveAllListeners();

        Text text = NewText("Label", img.transform, label, 38, Color.white, (int)TextAnchor.MiddleCenter, (int)HorizontalWrapMode.Overflow);
        Place(text, CenterAnchor, CenterAnchor, Vector2.zero, new Vector2(230f, 90f));
        UiTranslator.Track(text);
        PaintboxSkin.Button(img, name == "ExitYes" ? Paintbox.Tone.Danger : Paintbox.Tone.Paper);
        return button;
    }

    public void AuthorUI()
    {
        EnsureBuilt();
        LoadState();
        Hide();
    }

    /// <summary>The phone's Back at Home (2026-11-25): Settings with "Exit the game?" over it.</summary>
    public void AskExit()
    {
        Show();
        ShowExitConfirm(true);
    }

    public void Show()
    {
        RefreshLanguage();
        EnsureBuilt();
        LoadState();
        if (canvasGO != null)
        {
            canvasGO.SetActive(true);
        }
    }

    public void OnHome()
    {
        HomeUI homeUI = FindAnyObjectByType<HomeUI>();
        if (homeUI != null)
        {
            homeUI.GoHome();
        }
        Hide();
    }

    private void ApplyToggle(Tog t)
    {
        if (t.knob != null)
        {

            RectTransform knobRT = t.knob.rectTransform;
            float half = (t.track != null) ? t.track.rectTransform.rect.width * 0.5f : 48f;
            float offset = half + knobRT.rect.width * -0.5f - 3f;
            if (t.on)
            {
                offset = -offset;
            }
            knobRT.anchoredPosition = new Vector2(offset, 0f);
        }

        // Design B's switch: violet when on, a paper knob (2026-11-24).
        PaintboxSkin.Toggle(t.track, t.knob, t.onLabel, t.offLabel, t.on);
        PaintSwitch(t);
    }

    private void ApplyAudioSetting(Tog t)
    {
        if (!Application.isPlaying)
        {
            return;
        }

        if (t.key == "set_music")
        {
            AudioManager.SetMusicEnabled(t.on);
            return;
        }
        if (t.key == "set_sound")
        {
            AudioManager.SetSfxEnabled(t.on);
        }
    }

    private void RefreshQuality(bool apply)
    {
        if (qualityLabel != null)
        {
            int idx = Mathf.Clamp(qualityIndex, 0, QualityNames.Length - 1);
            qualityLabel.text = QualityNames[idx];
        }
        RefreshPage();

        if (apply)
        {
            ApplyGraphics(qualityIndex);
        }
    }

    // Confirmed empty - decompiles to a bare `return;` with no other statements.
    private void BtnShowInter()
    {
    }

    private void FlipToggle(Tog t)
    {
        t.on = !t.on;
        PlayerPrefs.SetInt(t.key, t.on ? 1 : 0);
        ApplyToggle(t);
        ApplyAudioSetting(t);
    }

    public void StepQuality(int dir)
    {
        int count = QualityNames.Length;
        int next = ((qualityIndex + dir) % count + count) % count;
        qualityIndex = next;
        PlayerPrefs.SetInt("set_quality", next);
        RefreshQuality(true);
    }

    // NOTE: URP's UniversalRenderPipelineAsset.m_MSAA is written via a raw field access in the
    // decompiled code rather than a visible property call - could reflect direct field access
    // via some internal/reflection trick in the original, or just be how IL2CPP represents a
    // property setter call here. Written using the public msaaSampleCount property since that's
    // the ordinary, available way to do this from outside URP's own assembly.
    private void ApplyGraphics(int idx)
    {
        float lodBias;
        int shadowQuality;

        if (idx == 0)
        {
            QualitySettings.globalTextureMipmapLimit = 2;
            lodBias = 0.5f;
            shadowQuality = 1;
        }
        else
        {
            lodBias = (idx == 1) ? 1.0f : 2.0f;
            QualitySettings.globalTextureMipmapLimit = (idx == 1) ? 1 : 0;
            shadowQuality = 2;
        }

        int pixelLightCount = (idx != 1) ? 4 : 2;
        float shadowDistance = (idx != 1) ? 150f : 100f;

        if (idx == 0)
        {
            pixelLightCount = 1;
            shadowDistance = 50f;
        }

        QualitySettings.lodBias = lodBias;
        QualitySettings.maximumLODLevel = 0;
        QualitySettings.shadows = (UnityEngine.ShadowQuality)shadowQuality;
        QualitySettings.shadowDistance = shadowDistance;
        // URP controls multisample anti-aliasing on the URP asset below.
        // Avoid changing the legacy quality setting at the same time.
        QualitySettings.antiAliasing = 0;
        QualitySettings.pixelLightCount = pixelLightCount;
        QualitySettings.softParticles = idx > 0;

        UniversalRenderPipelineAsset urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urpAsset != null)
        {
            // LDPlayer compatibility test: High retains the better texture, LOD
            // and shadow settings, but avoids switching render target size to
            // 100% and avoids 4x MSAA. These can trigger renderer/driver issues
            // on some emulator configurations.
            urpAsset.renderScale = idx == 0 ? 0.6f : 0.85f;
            urpAsset.shadowDistance = shadowDistance;
            urpAsset.msaaSampleCount = idx == 0 ? 1 : 2;

            Debug.Log($"[SettingsUI] Quality={QualityNames[Mathf.Clamp(idx, 0, 2)]}, " +
                      $"Screen={Screen.width}x{Screen.height}, " +
                      $"URP renderScale={urpAsset.renderScale}, " +
                      $"MSAA={urpAsset.msaaSampleCount}, " +
                      $"Graphics API={SystemInfo.graphicsDeviceType}");
        }
    }

    // Confirmed empty - decompiles to a bare `return;` with no other statements.
    private void ResolveSprites()
    {
    }

    private Image NewImage(string n, Transform parent, Sprite spr, Color col, bool raycast, bool preserveAspect)
    {
        bool created;
        GameObject go = SceneUI.GetOrCreate(n, parent, out created);
        Image img = SceneUI.GetOrAdd<Image>(go);

        if (!created)
        {
            _reused.Add(go);
        }
        else
        {
            img.raycastTarget = raycast;
            img.color = col;
            img.preserveAspect = preserveAspect;
            if (spr != null)
            {
                img.sprite = spr;
            }
        }

        return img;
    }

    private void Stretch(Component c)
    {
        if (_reused.Contains(c.gameObject))
        {
            return;
        }

        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private void Place(Component c, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        if (_reused.Contains(c.gameObject))
        {
            return;
        }

        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMax = anchor;
        rt.anchorMin = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    private void MakeLabel(string n, Transform parent, string text, float y)
    {
        Text label = NewText(n, parent, text, 28, Color.white, (int)TextAnchor.MiddleLeft, (int)HorizontalWrapMode.Overflow);
        Place(label, CenterAnchor, CenterAnchor, new Vector2(-110f, y), new Vector2(330f, 46f));
    }

    private void MakeToggle(string n, Transform parent, string key, float y)
    {
        Image track = NewImage(n, parent, toggleTrack, Color.white, true, false);
        Place(track, CenterAnchor, CenterAnchor, new Vector2(192f, y), new Vector2(96f, 42f));

        Button btn = SceneUI.GetOrAdd<Button>(track.gameObject);
        btn.targetGraphic = track;

        Transform trackT = track.transform;
        Image knob = NewImage("Knob", trackT, knobOn, Color.white, false, true);
        Place(knob, CenterAnchor, CenterAnchor, Vector2.zero, new Vector2(40f, 40f));

        Text onLabel = NewText("On", trackT, "On", 22, OnColor, (int)TextAnchor.MiddleRight, (int)HorizontalWrapMode.Overflow);
        Place(onLabel, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(52f, 40f));

        Text offLabel = NewText("Off", trackT, "Off", 22, OffColor, (int)TextAnchor.MiddleLeft, (int)HorizontalWrapMode.Overflow);
        Place(offLabel, new Vector2(1f, 0.5f), new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(52f, 40f));

        Tog tog = new Tog
        {
            track = track,
            knob = knob,
            onLabel = onLabel,
            offLabel = offLabel,
            key = key
        };
        toggles.Add(tog);
    }

    private void MakeStepper(Transform parent, float y)
    {
        Button back = NewButton("GfxBack", parent, arrowBack, new Vector2(108f, y), new Vector2(40f, 52f));
        Image backImg = back.GetComponent<Image>();
        if (backImg != null)
        {
            backImg.preserveAspect = true;
        }

        qualityLabel = NewText("GfxValue", parent, "Text", 26, Color.white, (int)TextAnchor.MiddleCenter, (int)HorizontalWrapMode.Overflow);
        Place(qualityLabel, CenterAnchor, CenterAnchor, new Vector2(192f, y), new Vector2(130f, 44f));

        Button next = NewButton("GfxNext", parent, arrowNext, new Vector2(276f, y), new Vector2(40f, 52f));
        Image nextImg = next.GetComponent<Image>();
        if (nextImg != null)
        {
            nextImg.preserveAspect = true;
        }
    }

    private Text languageLabel;

    private void MakeLanguageStepper(Transform parent, float y)
    {
        Button back = NewButton("LangBack", parent, arrowBack, new Vector2(108f, y), new Vector2(40f, 52f));
        Image backImg = back.GetComponent<Image>();
        if (backImg != null) backImg.preserveAspect = true;

        languageLabel = NewText("LangValue", parent, L.NameOf(L.Current), 26, Color.white, (int)TextAnchor.MiddleCenter, (int)HorizontalWrapMode.Overflow);
        Place(languageLabel, CenterAnchor, CenterAnchor, new Vector2(192f, y), new Vector2(130f, 44f));

        Button next = NewButton("LangNext", parent, arrowNext, new Vector2(276f, y), new Vector2(40f, 52f));
        Image nextImg = next.GetComponent<Image>();
        if (nextImg != null) nextImg.preserveAspect = true;

        back.onClick.RemoveAllListeners();
        back.onClick.AddListener(() => StepLanguage(-1));
        next.onClick.RemoveAllListeners();
        next.onClick.AddListener(() => StepLanguage(1));
    }

    private void StepLanguage(int dir)
    {
        AudioManager.ButtonTap();
        int count = System.Enum.GetValues(typeof(GameLanguage)).Length;
        L.Set((GameLanguage)(((int)L.Current + dir + count) % count));
        RefreshLanguage();
    }

    // Each language is named in itself, so it can be found whatever the game is in now.
    private void RefreshLanguage()
    {
        if (languageLabel != null) languageLabel.text = L.NameOf(L.Current);
        RefreshPage();
    }

    private Button NewButton(string n, Transform parent, Sprite spr, Vector2 pos, Vector2 size)
    {
        Image img = NewImage(n, parent, spr, Color.white, true, false);
        Place(img, CenterAnchor, CenterAnchor, pos, size);

        Button btn = SceneUI.GetOrAdd<Button>(img.gameObject);
        btn.targetGraphic = img;
        return btn;
    }

    private Text NewText(string n, Transform parent, string content, int size, Color col, int anchor, int wrap)
    {
        bool created;
        GameObject go = SceneUI.GetOrCreate(n, parent, out created);
        Text text = SceneUI.GetOrAdd<Text>(go);

        if (!created)
        {
            _reused.Add(go);
        }
        else
        {
            text.font = font;
            text.raycastTarget = false;
            text.text = content;
            text.fontSize = size;
            text.color = col;
            text.alignment = (TextAnchor)anchor;
            text.horizontalOverflow = (HorizontalWrapMode)wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.lineSpacing = 0.8f;
        }

        return text;
    }

    // NOTE: click-handler lambda (`<WireControls>b__0`) itself wasn't decompiled - only that
    // it's cached in a closure capturing (this, the current Tog). Reconstructed as
    // FlipToggle(t), the clear intent given exactly those captured variables. StepQualityDown/Up
    // below, by contrast, ARE fully decompiled (as SettingsUI.<>b__40_1/b__40_2) - simple
    // wrapper methods, not guessed.
    private void WireControls()
    {
        foreach (Tog t in toggles)
        {
            if (t.track == null)
            {
                continue;
            }
            Button btn = t.track.GetComponent<Button>();
            if (btn == null)
            {
                continue;
            }

            Tog captured = t;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => FlipToggle(captured));
        }

        if (canvasGO == null)
        {
            return;
        }

        Transform back = canvasGO.transform.Find("Popup/GfxBack");
        if (back != null)
        {
            Button backBtn = back.GetComponent<Button>();
            if (backBtn != null)
            {
                backBtn.onClick.RemoveAllListeners();
                backBtn.onClick.AddListener(StepQualityDown);
            }
        }

        Transform next = canvasGO.transform.Find("Popup/GfxNext");
        if (next != null)
        {
            Button nextBtn = next.GetComponent<Button>();
            if (nextBtn != null)
            {
                nextBtn.onClick.RemoveAllListeners();
                nextBtn.onClick.AddListener(StepQualityUp);
            }
        }
    }

    // Confirmed via SettingsUI.<>b__40_1 / <>b__40_2 in the decompiled output.
    private void StepQualityDown()
    {
        StepQuality(-1);
    }

    private void StepQualityUp()
    {
        StepQuality(1);
    }

}