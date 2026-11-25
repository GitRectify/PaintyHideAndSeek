using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Design B for the screens the scene itself holds (2026-11-24): Home, the play setup's buttons,
/// the match HUD, Settings, the rating popups, Single Play's results, matchmaking and loading.
/// The scene file is left as it is: once it has loaded, each screen is restyled in place, by the
/// names its objects have, and the scripts that run them keep working on the same objects (their
/// buttons, their texts). Pictures of words become words; game art buttons become design B's
/// shapes with Lucide icons; every label takes Rubik.
/// </summary>
public static class PaintboxSkin
{
    private const string Done = "PbSkinned";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        var go = new GameObject("PaintboxSkin");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<Runner>();
    }

    private sealed class Runner : MonoBehaviour
    {
        private void OnEnable() => SceneManager.sceneLoaded += Loaded;
        private void OnDisable() => SceneManager.sceneLoaded -= Loaded;
        private void Loaded(Scene scene, LoadSceneMode mode) => StartCoroutine(Later());
        private IEnumerator Start() { yield return Later(); }

        // After every screen's Awake and Start have made and bound their objects.
        private IEnumerator Later()
        {
            yield return null;
            yield return null;
            ApplyAll();
        }
    }

    /// <summary>Restyles every screen of the loaded scene that is not restyled yet.</summary>
    public static void ApplyAll()
    {
        Try(Home);
        Try(RoleSelect);
        Try(Hud);
        Try(Popups);
        Try(Results);
        Try(Matchmaking);
        Try(Loading);
        GameObject settings = Find("SettingsCanvas");
        if (settings != null) Try(() => Settings(settings.transform));
        Try(Fonts);
    }

    private static void Try(System.Action step)
    {
        try { step(); }
        catch (System.Exception e) { Debug.LogWarning("[Paintbox] A screen could not be restyled: " + e); }
    }

    // ================= Finding =================

    private static GameObject Find(string name)
    {
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t.name == name && t.gameObject.scene.IsValid()) return t.gameObject;
        return null;
    }

    private static Transform Under(Transform root, string path) => root != null ? root.Find(path) : null;

    private static bool Mark(GameObject go)
    {
        if (go == null) return false;
        if (go.transform.Find(Done) != null) return false;
        var marker = new GameObject(Done, typeof(RectTransform));
        marker.transform.SetParent(go.transform, false);
        marker.SetActive(false);
        return true;
    }

    // ================= Pieces =================

    /// <summary>A plain image made a design B panel: paper, rounded, an outline and a shadow.</summary>
    public static void Panel(Image image, float radius = Paintbox.RadiusLarge, float shadow = 8f, Color? edge = null)
    {
        if (image == null) return;
        image.color = Paintbox.Paper;
        image.preserveAspect = false;
        OnlineUI.Chunky(image, radius, edge ?? Paintbox.PanelLine, shadow);
    }

    /// <summary>A scene button made a design B button of a tone, its words in the tone's ink and pictures of words hidden.</summary>
    public static void Button(Image image, Paintbox.Tone tone, string label = null, string icon = null)
    {
        if (image == null) return;
        image.color = Paintbox.FillOf(tone);
        image.preserveAspect = false;
        RectTransform rt = image.rectTransform;
        OnlineUI.Chunky(image, Mathf.Min(Paintbox.Radius, rt.rect.height > 1f ? rt.rect.height * 0.32f : Paintbox.Radius),
                        Paintbox.Ink, Mathf.Clamp(rt.rect.height * 0.06f, 4f, 8f));
        Color ink = Paintbox.InkOf(tone);
        foreach (Text t in image.GetComponentsInChildren<Text>(true))
        {
            t.color = ink;
            NoOutline(t);
            UiTranslator.Restyle(t, Paintbox.Bold);
            Loose(t);
        }

        // Pictures inside: words drawn as images and ad badges go; icons keep their place.
        bool hasText = image.GetComponentInChildren<Text>(true) != null;
        foreach (Image child in image.GetComponentsInChildren<Image>(true))
        {
            if (child == image) continue;
            string n = child.name;
            if (n == "Ads" || (n == "Image" && child.sprite != null && !hasText)) child.enabled = false;
            if (n == "Icon" && icon != null) child.enabled = false;
        }

        if (label != null && !hasText)
        {
            Text words = Paintbox.Label(rt, label, Mathf.Clamp(rt.rect.height * 0.36f, 22f, 44f), ink, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
            Paintbox.Fill(words.rectTransform, 10f, 10f, 4f, 4f);
        }

        if (icon != null && Paintbox.Icon(icon) != null)
        {
            float size = Mathf.Clamp(rt.rect.height * 0.42f, 24f, 56f);
            Image glyph = Paintbox.IconImage(rt, icon, size, ink);
            glyph.name = "PbIcon";
            Text words = image.GetComponentInChildren<Text>(true);
            if (words != null && words.gameObject.activeSelf)
            {
                Paintbox.Pin(glyph.rectTransform, new Vector2(0f, 0.5f), new Vector2(size * 0.9f, 0f), new Vector2(size, size));
                RectTransform w = words.rectTransform;
                w.anchorMin = Vector2.zero;
                w.anchorMax = Vector2.one;
                w.offsetMin = new Vector2(size * 1.6f, 0f);
                w.offsetMax = new Vector2(-12f, 0f);
                words.alignment = TextAnchor.MiddleCenter;
            }
        }
    }

    /// <summary>A picture of a title's words replaced by the words, in design B's title type.</summary>
    public static Text Title(Image picture, string words, float size = 56f)
    {
        if (picture == null) return null;
        picture.enabled = false;
        RectTransform rt = picture.rectTransform;
        Text title = Paintbox.Label(rt, words, size, Paintbox.Ink, Paintbox.Weight.Title, TextAnchor.MiddleCenter);
        title.horizontalOverflow = HorizontalWrapMode.Overflow;
        Paintbox.Fill(title.rectTransform, -200f, -200f, -10f, -10f);
        return title;
    }

    /// <summary>A label on a design B surface: Rubik, ink (or a colour), no outline.</summary>
    public static void Ink(Text text, Color? color = null, bool bold = false)
    {
        if (text == null) return;
        NoOutline(text);
        text.color = color ?? Paintbox.Ink;
        UiTranslator.Restyle(text, bold ? Paintbox.Bold : Paintbox.Regular);
        Loose(text);
    }

    /// <summary>
    /// Rubik's lines are taller than the old fonts', so a label in a box cut to the old line height
    /// would draw nothing at all: such a label may run past its box's top and bottom instead.
    /// </summary>
    public static void Loose(Text text)
    {
        if (text == null) return;
        if (text.rectTransform.rect.height < text.fontSize * 1.4f) text.verticalOverflow = VerticalWrapMode.Overflow;
    }

    private static void NoOutline(Graphic g)
    {
        foreach (Shadow s in g.GetComponents<Shadow>()) s.enabled = false;
    }

    /// <summary>A close (x) image button made a small paper icon button.</summary>
    public static void CloseButton(Image image, string icon = "X")
    {
        if (image == null) return;
        image.sprite = null;
        image.color = Paintbox.Paper;
        image.preserveAspect = false;
        OnlineUI.Chunky(image, 18f, Paintbox.Ink, 5f);
        RectTransform rt = image.rectTransform;
        float size = Mathf.Max(rt.rect.width, rt.rect.height);
        if (size < 1f) size = 64f;
        Image glyph = Paintbox.IconImage(rt, icon, size * 0.55f, Paintbox.Ink);
        glyph.name = "PbIcon";
        foreach (Text t in image.GetComponentsInChildren<Text>(true)) t.enabled = false;
    }

    private static void Dim(Transform canvas)
    {
        Image dim = Under(canvas, "Dim")?.GetComponent<Image>();
        if (dim != null) dim.color = Paintbox.Dim;
    }

    // ================= Home =================

    private static void Home()
    {
        GameObject home = Find("HomeCanvas");
        if (!Mark(home)) return;

        Button play = Under(home.transform, "PlayBtn")?.GetComponent<Button>();
        Button multi = Under(home.transform, "MultiplayerBtn")?.GetComponent<Button>();
        Button shop = Under(home.transform, "ShopBtn")?.GetComponent<Button>();
        Button settings = Under(home.transform, "HomeSettingsBtn")?.GetComponent<Button>();

        RectTransform page = PaintboxUnits.Create(home.transform, "HomeDesign");
        page.SetAsFirstSibling();

        // The hero picture, edge to edge, with a soft fade at its foot for the two big buttons.
        Image hero = Paintbox.Image(page, "Hero", Paintbox.Art("heroB"), Color.white, true);
        Paintbox.Fill(hero.rectTransform);
        AspectRatioFitter fit = hero.gameObject.AddComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        fit.aspectRatio = 764f / 508f;
        Image fade = Paintbox.Image(page, "Fade", null, Color.white);
        Paintbox.Fill(fade.rectTransform);
        fade.rectTransform.anchorMax = new Vector2(1f, 0.42f);
        fade.gameObject.AddComponent<UiGradient>().Set(new Color(0.22f, 0.137f, 0.263f, 0f), new Color(0.22f, 0.137f, 0.263f, 0.55f));

        RectTransform safe = Paintbox.SafeArea(Paintbox.NewRect(page, "Safe"));

        // The title, top left, as the prototype draws it: bold italic, white on an ink shadow.
        Text titleShadow = Paintbox.Label(safe, "HIDE\n& SEEK", 150f, Paintbox.Ink, Paintbox.Weight.Title);
        Text title = Paintbox.Label(safe, "HIDE\n& SEEK", 150f, Color.white, Paintbox.Weight.Title);
        foreach (Text t in new[] { titleShadow, title })
        {
            t.lineSpacing = 0.82f;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.alignment = TextAnchor.UpperLeft;
            Paintbox.Pin(t.rectTransform, new Vector2(0f, 1f), new Vector2(100f, -170f), new Vector2(700f, 330f));
        }
        titleShadow.rectTransform.anchoredPosition += new Vector2(8f, -10f);
        title.gameObject.AddComponent<Outline>().effectColor = Paintbox.Ink;
        title.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);

        // Top right: the balance, Shop and Settings.
        RectTransform top = Paintbox.TopBand(Paintbox.NewRect(safe, "TopRight"), Paintbox.Gutter, 84f, Paintbox.Gutter, Paintbox.Gutter);
        HorizontalLayoutGroup row = top.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.childAlignment = TextAnchor.MiddleRight;
        row.spacing = 18f;
        row.childControlWidth = row.childControlHeight = false;
        row.childForceExpandWidth = row.childForceExpandHeight = false;

        Paintbox.CoinPill(top, out RectTransform pill);
        pill.name = "HomeBalance";
        pill.sizeDelta = new Vector2(250f, 80f);
        Button shopNew = Paintbox.Button(top, "Shop", Paintbox.Tone.Secondary, "ShoppingBag", () => shop?.onClick.Invoke(), 32f);
        shopNew.name = "HomeShop";
        ((RectTransform)shopNew.transform).sizeDelta = new Vector2(220f, 80f);
        Button gear = Paintbox.IconButton(top, "Settings", Paintbox.Tone.Paper, () => settings?.onClick.Invoke(), 80f);
        gear.name = "HomeSettings";

        // The two ways to play, along the foot.
        RectTransform foot = Paintbox.NewRect(safe, "Modes");
        foot.anchorMin = new Vector2(0f, 0f);
        foot.anchorMax = new Vector2(1f, 0f);
        foot.pivot = new Vector2(0.5f, 0f);
        foot.offsetMin = new Vector2(Paintbox.Gutter, Paintbox.Gutter);
        foot.offsetMax = new Vector2(-Paintbox.Gutter, Paintbox.Gutter + 170f);
        HorizontalLayoutGroup modes = foot.gameObject.AddComponent<HorizontalLayoutGroup>();
        modes.spacing = 30f;
        modes.childControlWidth = modes.childControlHeight = true;
        modes.childForceExpandWidth = modes.childForceExpandHeight = true;

        ModeCard(foot, "HomePlay", "Play", "Single Player · Offline", "Play", Paintbox.Tone.Primary, () => play?.onClick.Invoke());
        ModeCard(foot, "HomeMultiplayer", "Multiplayer", "Join an online match", "Users", Paintbox.Tone.Secondary, () => multi?.onClick.Invoke());

        // The scene's own Home pieces stay (scripts hold them) but are not drawn.
        foreach (string old in new[] { "HomeLogo", "HomeCoinPill", "HomeSettingsBtn", "PlayBtn", "MultiplayerBtn", "ShopBtn", "HomeToast" })
        {
            Transform t = Under(home.transform, old);
            if (t != null) t.gameObject.SetActive(false);
        }
    }

    private static void ModeCard(Transform parent, string name, string title, string subtitle, string icon, Paintbox.Tone tone, UnityEngine.Events.UnityAction onClick)
    {
        Button card = Paintbox.Button(parent, "", tone, null, onClick);
        card.name = name;
        PaintboxFrame frame = card.GetComponent<PaintboxFrame>();
        Transform content = frame.Body.Find("Content");
        if (content != null) Object.DestroyImmediate(content.gameObject);
        Color ink = Paintbox.InkOf(tone);

        Image badge = Paintbox.Rounded(frame.Body, "Badge", Paintbox.Paper, 24f);
        RectTransform b = badge.rectTransform;
        b.anchorMin = b.anchorMax = b.pivot = new Vector2(0f, 0.5f);
        b.anchoredPosition = new Vector2(32f, 0f);
        b.sizeDelta = new Vector2(96f, 96f);
        badge.gameObject.AddComponent<Outline>().effectColor = Paintbox.Ink;
        badge.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);
        Paintbox.IconImage(b, icon, 54f, Paintbox.Ink);

        Text head = Paintbox.Label(frame.Body, title, 46f, ink, Paintbox.Weight.Bold);
        head.rectTransform.anchorMin = new Vector2(0f, 0.5f);
        head.rectTransform.anchorMax = new Vector2(1f, 1f);
        head.rectTransform.offsetMin = new Vector2(150f, -6f);
        head.rectTransform.offsetMax = new Vector2(-80f, -18f);
        head.alignment = TextAnchor.LowerLeft;
        Text sub = Paintbox.Label(frame.Body, subtitle, 27f, ink);
        sub.rectTransform.anchorMin = new Vector2(0f, 0f);
        sub.rectTransform.anchorMax = new Vector2(1f, 0.5f);
        sub.rectTransform.offsetMin = new Vector2(150f, 18f);
        sub.rectTransform.offsetMax = new Vector2(-80f, -2f);
        sub.alignment = TextAnchor.UpperLeft;

        Image chevron = Paintbox.IconImage(frame.Body, "ChevronRight", 44f, ink);
        Paintbox.Pin(chevron.rectTransform, new Vector2(1f, 0.5f), new Vector2(-30f, 0f), new Vector2(44f, 44f));
        chevron.rectTransform.pivot = new Vector2(1f, 0.5f);
    }

    // ================= The play setup's buttons =================

    private static void RoleSelect()
    {
        GameObject role = Find("RoleSelectCanvas");
        if (!Mark(role)) return;
        Hud(Under(role.transform, "BackBtn"), "ArrowLeft", null, false, Paintbox.Tone.Paper, "Icon");
        Hud(Under(role.transform, "SettingsBtn"), "Settings", null, false, Paintbox.Tone.Paper);
        Hud(Under(role.transform, "Ratebtn"), "Star", null, false, Paintbox.Tone.Paper);
        Hud(Under(role.transform, "RoleShopBtn"), "ShoppingBag", "Shop", false, Paintbox.Tone.Secondary, "Icon", "Label");
    }

    // ================= The match HUD =================

    private static void Hud()
    {
        // The layout first (PaintboxHud): each button's look is made for the size it is given.
        GameObject paintUi = Find("PaintUICanvas");
        if (Mark(paintUi))
        {
            Transform p = paintUi.transform;
            PaintboxHud.Attach(p);
            PaintboxHud.StylePoseTray(Under(p, "PosePanel"));
            Hud(Under(p, "Btn_Setting"), "Menu", null, false, Paintbox.Tone.Glass);
            Hud(Under(p, "Btn_Shop"), "ShoppingBag", "Shop", false, Paintbox.Tone.Glass, "Icon", "Label");
            Hud(Under(p, "PaintGateButton"), "Paintbrush", "Paint", true, Paintbox.Tone.Hot, "Icon", "Label");
            Hud(Under(p, "Btn_DoiDang"), "PersonStanding", "Pose", false, Paintbox.Tone.Glass, "Icon_DoiDang");
            Hud(Under(p, "Btn_XoayNhanVat"), "RotateCw", "Rotate", false, Paintbox.Tone.Glass, "Icon_DoiDang");
            Hud(Under(p, "ButtonCamera"), "Camera", "Camera", false, Paintbox.Tone.Glass);
            Transform start = Under(p, "ButtonStart");
            if (start != null && start.Find("PbSkin") == null)
                PaintboxHudButton.ApplyWide(start.gameObject, "Flag", "Start", "· End hiding", false, Paintbox.Tone.Glass, "Text");
            Hud(Under(p, "ButtonRotationLeft"), "RotateCcw", null, true, Paintbox.Tone.Paper);
            Hud(Under(p, "ButtonRotationRight"), "RotateCw", null, true, Paintbox.Tone.Paper);
        }

        GameObject paintSheet = Find("ChameleonPaintCanvas");
        if (Mark(paintSheet))
            PaintboxHud.StylePaintSheet(paintSheet.transform, paintSheet.GetComponentInChildren<ChameleonPaint>(true) ?? Object.FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include));

        GameObject fire = Find("FireButtonCanvas");
        if (Mark(fire))
        {
            Transform f = fire.transform;
            Hud(Under(f, "Jump"), "ArrowBigUp", "Jump", false, Paintbox.Tone.Glass);
            Hud(Under(f, "btnnam"), "PersonStanding", "Lie down", false, Paintbox.Tone.Glass);
            Hud(Under(f, "FireBtn"), "Crosshair", "Fire", true, Paintbox.Tone.Hot, "GunIcon", "L");
        }

        GameObject climb = Find("ClimbButtonsCanvas");
        if (Mark(climb))
        {
            Hud(Under(climb.transform, "ClimbUp"), "ChevronsUp", "Climb", false, Paintbox.Tone.Hot, "Arrow");
            Hud(Under(climb.transform, "ClimbDown"), "ChevronsDown", "Down", false, Paintbox.Tone.Glass, "Arrow");
        }

        GameObject joy = Find("JoystickCanvas");
        if (Mark(joy))
        {
            Image ring = joy.GetComponentInChildren<Transform>(true).Find("Fixed Joystick/JoyOutline")?.GetComponent<Image>();
            if (ring != null) ring.color = new Color(1f, 1f, 1f, 0.85f);
            Image handle = joy.transform.Find("Fixed Joystick/Handle")?.GetComponent<Image>();
            if (handle != null)
            {
                handle.sprite = UiArt.Circle;
                handle.color = new Color(Paintbox.Paper.r, Paintbox.Paper.g, Paintbox.Paper.b, 0.92f);
                Outline o = handle.gameObject.AddComponent<Outline>();
                o.effectColor = Paintbox.Ink;
                o.effectDistance = new Vector2(3f, -3f);
                Image move = Paintbox.IconImage(handle.rectTransform, "Move", 56f, Paintbox.Ink);
                Paintbox.Fill(move.rectTransform, 26f, 26f, 26f, 26f);   // grows and shrinks with the handle
            }
        }

        // The timer and the two teams: the prototype's pill.
        GameObject matchHud = Find("MatchHudCanvas");
        if (Mark(matchHud)) PaintboxHud.StyleRoundPill(matchHud.transform);

        foreach (string status in new[] { "HideStatus", "SeekStatus", "SeekResult" })
        {
            GameObject go = Find(status);
            Text t = go != null ? go.GetComponent<Text>() : null;
            if (t == null) continue;
            UiTranslator.Restyle(t, Paintbox.Bold);
            PaintboxTextPill.Attach(t);
        }
    }

    /// <summary>A HUD button (or any scene button) given design B's HUD look; null-safe.</summary>
    public static void Hud(Transform button, string icon, string label, bool round, Paintbox.Tone tone, params string[] hide)
    {
        if (button == null || button.Find("PbSkin") != null) return;
        PaintboxHudButton.Apply(button.gameObject, icon, label, round, tone, hide);
    }

    // ================= Popups: rate, help, thanks =================

    private static void Popups()
    {
        GameObject rate = Find("RateCanvas");
        if (Mark(rate))
        {
            Dim(rate.transform);
            Transform popup = Under(rate.transform, "Popup");
            Panel(popup?.GetComponent<Image>());
            Header(popup, "Rate us", "Do you enjoy the game?");
            Button(Under(popup, "ButtonRateNow")?.GetComponent<Image>(), Paintbox.Tone.Primary, null, "Star");
            Button(Under(popup, "ButtonLate")?.GetComponent<Image>(), Paintbox.Tone.Paper);
        }

        GameObject help = Find("HelpCanvas");
        if (Mark(help))
        {
            Dim(help.transform);
            Transform popup = Under(help.transform, "Popup");
            Panel(popup?.GetComponent<Image>());
            Title(Under(popup, "Image")?.GetComponent<Image>(), "Help us improve", 50f);
            Ink(Under(popup, "Rate Now")?.GetComponent<Text>(), Paintbox.Muted);
            Button(Under(popup, "ButtonSubmit")?.GetComponent<Image>(), Paintbox.Tone.Primary, "Submit", "Send");
            Image field = Under(popup, "InputField")?.GetComponent<Image>();
            if (field != null) { field.color = Color.white; OnlineUI.Chunky(field, 20f, Paintbox.Line, 0f); }
        }

        GameObject thanks = Find("ThanksCanvas");
        if (Mark(thanks))
        {
            Dim(thanks.transform);
            Transform popup = Under(thanks.transform, "Popup");
            Panel(popup?.GetComponent<Image>());
            Title(Under(popup, "Image")?.GetComponent<Image>(), "Thank you!", 54f);
            Ink(Under(popup, "text")?.GetComponent<Text>(), Paintbox.Muted);
            Button(Under(popup, "ButtonContinue")?.GetComponent<Image>(), Paintbox.Tone.Primary, "Continue");
        }
    }

    // A popup without a title of its own gets one.
    private static void Header(Transform popup, string title, string subtitle)
    {
        if (popup == null) return;
        var rt = (RectTransform)popup;
        Text head = Paintbox.Label(rt, title, 54f, Paintbox.Ink, Paintbox.Weight.Title, TextAnchor.MiddleCenter);
        Paintbox.TopBand(head.rectTransform, 40f, 70f, 30f, 30f);
        Text sub = Paintbox.Label(rt, subtitle, 30f, Paintbox.Muted, Paintbox.Weight.Regular, TextAnchor.MiddleCenter);
        Paintbox.TopBand(sub.rectTransform, 110f, 44f, 30f, 30f);
    }

    // ================= Single Play's results =================

    private static void Results()
    {
        GameObject results = Find("HideResultCanvas");
        if (!Mark(results)) return;

        foreach (string name in new[] { "CaughtPopup", "TimeUpPopup" })
        {
            Transform popup = Under(results.transform, name);
            if (popup == null) continue;
            Dim(popup);
            Transform panel = Under(popup, "Panel");
            Panel(panel?.GetComponent<Image>());
            Title(Under(panel, "Title")?.GetComponent<Image>(), name == "CaughtPopup" ? "You got caught!" : "Time's up!", 58f);
            Ink(Under(panel, "Body")?.GetComponent<Text>(), Paintbox.Muted);
            Button(Under(panel, "ActionBtn")?.GetComponent<Image>(), Paintbox.Tone.Primary);
            Button(Under(panel, "GiveUpBtn")?.GetComponent<Image>(), Paintbox.Tone.Paper);
        }

        foreach (string name in new[] { "VictoryPopup", "DefeatPopup" })
        {
            Transform popup = Under(results.transform, name);
            if (popup == null) continue;
            Dim(popup);
            for (int i = 0; i < 3; i++)
            {
                Image row = Under(popup, "Row" + i)?.GetComponent<Image>();
                if (row == null) continue;
                row.color = Paintbox.Paper;
                OnlineUI.Chunky(row, 24f, Paintbox.Ink, 5f);
                Ink(Under(row.transform, "Label")?.GetComponent<Text>(), Paintbox.Muted);
                Ink(Under(row.transform, "Value")?.GetComponent<Text>(), Paintbox.Ink, true);
            }
            Button(Under(popup, "HomeBtn")?.GetComponent<Image>(), Paintbox.Tone.Paper, null, "House");
            Button(Under(popup, "GoldBtn")?.GetComponent<Image>(), Paintbox.Tone.Secondary);
            Button(Under(popup, "NextBtn")?.GetComponent<Image>(), Paintbox.Tone.Primary, null, "ArrowRight");
        }
    }

    // ================= Matchmaking =================

    private static void Matchmaking()
    {
        GameObject mm = Find("MatchmakingCanvas");
        if (!Mark(mm)) return;
        Dim(mm.transform);
        Transform panel = Under(mm.transform, "Panel");
        Panel(panel?.GetComponent<Image>());
        Title(Under(panel, "Title")?.GetComponent<Image>(), "Finding players...", 54f);
        Ink(Under(panel, "Counter")?.GetComponent<Text>(), Paintbox.Ink, true);
        Ink(Under(panel, "Hint")?.GetComponent<Text>(), Paintbox.Muted);

        Image track = Under(panel, "BarTrack")?.GetComponent<Image>();
        if (track != null) { track.sprite = null; track.color = Paintbox.Tab; }
        Image bar = Under(panel, "BarTrack/BarFill")?.GetComponent<Image>();
        if (bar != null) { bar.sprite = Paintbox.WhiteSprite; bar.color = Paintbox.Yellow; }
        Image divH = Under(panel, "DivH")?.GetComponent<Image>();
        if (divH != null) { divH.sprite = null; divH.color = Paintbox.Line; }
        Image divV = Under(panel, "DivV")?.GetComponent<Image>();
        if (divV != null) divV.enabled = false;   // the two teams' boxes part them already

        foreach (string box in new[] { "FugitiveBox", "HunterBox" })
        {
            Image b = Under(panel, box)?.GetComponent<Image>();
            if (b == null) continue;
            b.color = Paintbox.Tab;
            UiArt.Apply(b, UiArt.Rounded, 28f);
            foreach (Text t in b.GetComponentsInChildren<Text>(true)) Ink(t, Paintbox.Ink, true);
        }
    }

    /// <summary>A matchmaking seat: violet when a player has it, quiet when it is still open.</summary>
    public static void Seat(Image bg, Text label, bool taken)
    {
        if (bg == null) return;
        bg.sprite = UiArt.Rounded;
        bg.type = Image.Type.Sliced;
        bg.pixelsPerUnitMultiplier = 32f / 18f;
        bg.color = taken ? Paintbox.Violet : Paintbox.Paper;
        if (label != null)
        {
            NoOutline(label);
            label.color = taken ? Paintbox.WashInk : Paintbox.Muted;
        }
    }

    // ================= Loading =================

    private static void Loading()
    {
        GameObject loading = Find("LoadingCanvas");
        if (!Mark(loading)) return;
        Image bg = Under(loading.transform, "Bg")?.GetComponent<Image>();
        if (bg == null) return;
        bg.sprite = null;
        bg.color = Paintbox.Ink;
        Image art = Paintbox.Image(bg.rectTransform, "PbHero", Paintbox.Art("heroA"), Color.white);
        art.transform.SetAsFirstSibling();
        Paintbox.Fill(art.rectTransform);
        AspectRatioFitter fit = art.gameObject.AddComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        fit.aspectRatio = 764f / 508f;

        // The bar: centred along the foot, an ink edge, paper inside, filling with yellow; its word
        // over it. LoadingScreen only moves the fill (fillAmount), so the shapes are free to change.
        Image border = Under(bg.transform, "Pro")?.GetComponent<Image>();
        if (border != null)
        {
            border.sprite = null;
            border.color = Paintbox.Ink;
            RectTransform b = border.rectTransform;
            b.anchorMin = new Vector2(0.2f, 0f);
            b.anchorMax = new Vector2(0.8f, 0f);
            b.pivot = new Vector2(0.5f, 0f);
            b.anchoredPosition = new Vector2(0f, 130f);
            b.sizeDelta = new Vector2(0f, 44f);
        }
        Image back = Under(bg.transform, "Pro/mg")?.GetComponent<Image>();
        if (back != null)
        {
            back.sprite = null;
            back.color = Paintbox.Paper;
            Paintbox.Fill(back.rectTransform, 6f, 6f, 6f, 6f);
        }
        Image fill = Under(bg.transform, "Pro/Fill")?.GetComponent<Image>();
        if (fill != null)
        {
            fill.sprite = Paintbox.WhiteSprite;   // filled partway: it needs a sprite to cut
            fill.color = Paintbox.Yellow;
            Paintbox.Fill(fill.rectTransform, 6f, 6f, 6f, 6f);
        }
        Text words = Under(bg.transform, "Pro/Text (Legacy)")?.GetComponent<Text>();
        if (words != null)
        {
            UiTranslator.Restyle(words, Paintbox.Title);
            words.color = Color.white;
            words.fontSize = 64;
            words.alignment = TextAnchor.LowerLeft;
            words.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform w = words.rectTransform;
            w.anchorMin = new Vector2(0f, 1f);
            w.anchorMax = new Vector2(1f, 1f);
            w.pivot = new Vector2(0f, 0f);
            w.anchoredPosition = new Vector2(0f, 14f);
            w.sizeDelta = new Vector2(0f, 90f);
            Outline o = Paintbox.Ensure<Outline>(words.gameObject);
            o.enabled = true;
            o.effectColor = Paintbox.Ink;
            o.effectDistance = new Vector2(3f, -3f);
        }
    }

    // ================= Settings =================

    /// <summary>The Settings popup, once SettingsUI has built it (it calls this itself).</summary>
    public static void Settings(Transform canvas)
    {
        if (canvas == null) return;
        Transform popup = Under(canvas, "Popup");
        if (popup == null || !Mark(popup.gameObject)) return;
        Dim(canvas);
        Panel(popup.GetComponent<Image>());
        Title(Under(popup, "Title")?.GetComponent<Image>(), "Settings", 56f);
        Image close = Under(popup, "CloseBtn")?.GetComponent<Image>();
        CloseButton(close);

        foreach (string label in new[] { "LblMusic", "LblSound", "LblGfx", "LblLang" })
            Ink(Under(popup, label)?.GetComponent<Text>(), Paintbox.Ink, true);
        foreach (string value in new[] { "GfxValue", "LangValue" })
            Ink(Under(popup, value)?.GetComponent<Text>(), Paintbox.Ink, true);
        foreach (string arrow in new[] { "GfxBack", "LangBack" })
            Stepper(Under(popup, arrow)?.GetComponent<Image>(), "ChevronLeft");
        foreach (string arrow in new[] { "GfxNext", "LangNext" })
            Stepper(Under(popup, arrow)?.GetComponent<Image>(), "ChevronRight");

        Button(Under(popup, "HomeBtn")?.GetComponent<Image>(), Paintbox.Tone.Primary, null, "House");
        Button(Under(popup, "RestoreBtn")?.GetComponent<Image>(), Paintbox.Tone.Paper);
        Button(Under(popup, "ControlsBtn")?.GetComponent<Image>(), Paintbox.Tone.Paper, null, "Keyboard");
        Button(Under(popup, "ExitBtn")?.GetComponent<Image>(), Paintbox.Tone.Danger, null, "LogOut");
        Image homeIcon = Under(popup, "HomeBtn/Icon")?.GetComponent<Image>();
        if (homeIcon != null) homeIcon.enabled = false;

        foreach (string toggle in new[] { "TogMusic", "TogSound" })
        {
            Image track = Under(popup, toggle)?.GetComponent<Image>();
            if (track == null) continue;
            UiArt.Apply(track, UiArt.Rounded, 21f);
            Outline o = track.gameObject.AddComponent<Outline>();
            o.effectColor = Paintbox.Ink;
            o.effectDistance = new Vector2(3f, -3f);
            foreach (Text t in track.GetComponentsInChildren<Text>(true)) { NoOutline(t); UiTranslator.Restyle(t, Paintbox.Bold); }
        }
    }

    private static void Stepper(Image arrow, string icon)
    {
        if (arrow == null) return;
        arrow.sprite = null;
        arrow.color = Paintbox.Paper;
        arrow.preserveAspect = false;
        OnlineUI.Chunky(arrow, 14f, Paintbox.Ink, 4f);
        RectTransform rt = arrow.rectTransform;
        rt.sizeDelta = new Vector2(52f, 52f);
        Paintbox.IconImage(rt, icon, 34f, Paintbox.Ink).name = "PbIcon";
    }

    /// <summary>A Settings switch, drawn as design B: violet track when on, a paper knob.</summary>
    public static void Toggle(Image track, Image knob, Text on, Text off, bool isOn)
    {
        if (track != null) track.color = isOn ? Paintbox.Violet : Paintbox.Tab;
        if (knob != null)
        {
            knob.sprite = UiArt.Circle;
            knob.color = Paintbox.Paper;
            if (knob.GetComponent<Outline>() == null)
            {
                Outline o = knob.gameObject.AddComponent<Outline>();
                o.effectColor = Paintbox.Ink;
                o.effectDistance = new Vector2(2.5f, -2.5f);
            }
        }
        if (on != null) on.color = isOn ? Paintbox.WashInk : new Color(0f, 0f, 0f, 0f);
        if (off != null) off.color = isOn ? new Color(0f, 0f, 0f, 0f) : Paintbox.Muted;
    }

    // ================= Every other label =================

    // The scene's labels still in the old fonts take Rubik (bold, as the game's labels were).
    private static void Fonts()
    {
        foreach (Text t in Resources.FindObjectsOfTypeAll<Text>())
        {
            if (!t.gameObject.scene.IsValid() || t.font == null) continue;
            string f = t.font.name;
            if (f == "MikadoMedium" || f == "aline_font" || f == "LegacyRuntime")
            {
                if (t.font == UiTranslator.WideFont && t.text != null && IsWide(t.text)) continue;
                UiTranslator.Restyle(t, Paintbox.Bold);
                Loose(t);
            }
        }
    }

    private static bool IsWide(string s)
    {
        foreach (char c in s) if (c >= 0x400) return true;
        return false;
    }
}

/// <summary>
/// A design B layout inside one of the scene's canvases: this rectangle fills its canvas and is
/// scaled so that it is 1080 units high, whatever the canvas's own reference size, so design B's
/// sizes (those of OnlineUI's 1920 x 1080 canvases) mean the same everywhere.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public sealed class PaintboxUnits : MonoBehaviour
{
    private Vector2 seen;

    public static RectTransform Create(Transform canvas, string name)
    {
        RectTransform rt = Paintbox.NewRect(canvas, name);
        rt.gameObject.AddComponent<PaintboxUnits>().Fit();
        return rt;
    }

    // Also just before the canvases are drawn: the canvas may have changed size this frame
    // (a rotation, a resize) after LateUpdate.
    private void OnEnable()
    {
        Canvas.willRenderCanvases += Fit;
        Fit();
    }

    private void OnDisable() => Canvas.willRenderCanvases -= Fit;
    private void LateUpdate() => Fit();

    private void Fit()
    {
        var parent = transform.parent as RectTransform;
        if (parent == null) return;
        Vector2 size = parent.rect.size;
        if (size == seen || size.y < 1f) return;
        seen = size;
        float scale = size.y / 1080f;
        var rt = (RectTransform)transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.localScale = new Vector3(scale, scale, 1f);
        rt.sizeDelta = size / scale;
    }
}
