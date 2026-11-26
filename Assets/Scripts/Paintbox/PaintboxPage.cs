using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>The places of Multiplayer: one page each, reached by the hub's tabs (design B).</summary>
public enum HubTab { NewMatch, Matches, History, Profile, Rate }

/// <summary>
/// A full screen in design B (2026-11-24): the warm page, a top bar (back, the title in bold italic,
/// the balance, Shop and Settings on the right), Multiplayer's tab row when it is one of its places,
/// the content, and a foot with a line of what is going on and the screen's main buttons. Every
/// page of the game is built from this, so they all sit and move the same.
/// </summary>
public sealed class PaintboxPage
{
    [Flags]
    public enum Top { None = 0, Balance = 1, Shop = 2, Settings = 4, Offline = 8 }

    public RectTransform Root;      // the whole page (it takes every tap under it)
    public RectTransform Safe;      // inside the device's safe area
    public RectTransform Content;   // between the bar (and tabs) and the foot
    public RectTransform Right;     // the bar's right-hand row
    public Text Title;
    public RectTransform Foot;      // null without one
    public Text FootTitle, FootDetail;
    public RectTransform FootButtons;
    public Button Back;

    public const float BarTop = Paintbox.Gutter;
    public const float BarHeight = 76f;
    public const float TabsTop = 118f;
    public const float TabsHeight = 82f;
    public const float FootHeight = 118f;

    /// <summary>
    /// A page under <paramref name="parent"/> (a 1920 x 1080 canvas, or a PaintboxUnits rectangle).
    /// <paramref name="tab"/>: Multiplayer's tab row with that place picked; null for none.
    /// </summary>
    public static PaintboxPage Build(Transform parent, string name, string title, UnityAction back, Top top,
                                     HubTab? tab = null, bool foot = false)
    {
        var page = new PaintboxPage();
        Image backdrop = Paintbox.Backdrop(parent);
        backdrop.name = name;
        page.Root = backdrop.rectTransform;
        page.Safe = Paintbox.SafeArea(Paintbox.NewRect(page.Root, "Safe"));

        page.Title = Paintbox.TopBar(page.Safe, title, back, out page.Right);
        page.Back = page.Safe.Find("TopBar/Back")?.GetComponent<Button>();
        if ((top & Top.Offline) != 0)
        {
            RectTransform chip = Paintbox.Tag(page.Right, "Offline", Paintbox.Wash, Paintbox.WashInk, "Gamepad2", 24f);
            chip.name = "OfflineChip";
        }
        if ((top & Top.Balance) != 0)
        {
            Paintbox.CoinPill(page.Right, out RectTransform pill);
            pill.name = "PageBalance";
            pill.gameObject.AddComponent<PaintboxPageBalance>();
            pill.sizeDelta = new Vector2(240f, 76f);
        }
        if ((top & Top.Shop) != 0)
        {
            Button shop = Paintbox.IconButton(page.Right, "ShoppingBag", Paintbox.Tone.Paper, OpenShop, 76f);
            shop.name = "PageShop";
        }
        if ((top & Top.Settings) != 0)
        {
            Button gear = Paintbox.IconButton(page.Right, "Settings", Paintbox.Tone.Paper, OpenSettings, 76f);
            gear.name = "PageSettings";
        }

        float contentTop = TabsTop;
        if (tab.HasValue)
        {
            HubTabs(page.Safe, tab.Value);
            contentTop = TabsTop + TabsHeight + 14f;
        }

        if (foot) BuildFoot(page);
        page.Content = Paintbox.Fill(Paintbox.NewRect(page.Safe, "Content"), Paintbox.Gutter, Paintbox.Gutter, contentTop,
                                     foot ? FootHeight + 10f : Paintbox.Gutter);
        page.Content.SetSiblingIndex(1);   // under the bar, the tabs and the foot
        return page;
    }

    private static void BuildFoot(PaintboxPage page)
    {
        RectTransform foot = Paintbox.NewRect(page.Safe, "Foot");
        foot.anchorMin = new Vector2(0f, 0f);
        foot.anchorMax = new Vector2(1f, 0f);
        foot.pivot = new Vector2(0.5f, 0f);
        foot.offsetMin = new Vector2(Paintbox.Gutter, 0f);
        foot.offsetMax = new Vector2(-Paintbox.Gutter, FootHeight);
        page.Foot = foot;

        Image rule = Paintbox.Divider(foot);
        Paintbox.TopBand(rule.rectTransform, 0f, 3f);

        page.FootTitle = Paintbox.Label(foot, "", 32f, Paintbox.Ink, Paintbox.Weight.Bold);
        page.FootTitle.name = "FootTitle";
        Paintbox.Fit(page.FootTitle, 20);
        RectTransform t = page.FootTitle.rectTransform;
        t.anchorMin = new Vector2(0f, 0.5f);
        t.anchorMax = new Vector2(0.62f, 1f);
        t.offsetMin = new Vector2(0f, -6f);
        t.offsetMax = new Vector2(0f, -12f);
        page.FootTitle.alignment = TextAnchor.LowerLeft;

        page.FootDetail = Paintbox.Label(foot, "", 24f, Paintbox.Muted);
        page.FootDetail.name = "FootDetail";
        Paintbox.Fit(page.FootDetail, 15);
        RectTransform d = page.FootDetail.rectTransform;
        d.anchorMin = new Vector2(0f, 0f);
        d.anchorMax = new Vector2(0.62f, 0.5f);
        d.offsetMin = new Vector2(0f, 14f);
        d.offsetMax = new Vector2(0f, -4f);
        page.FootDetail.alignment = TextAnchor.UpperLeft;

        page.FootButtons = Paintbox.NewRect(foot, "Buttons");
        page.FootButtons.anchorMin = new Vector2(0.3f, 0f);
        page.FootButtons.anchorMax = new Vector2(1f, 1f);
        page.FootButtons.offsetMin = new Vector2(0f, 14f);
        page.FootButtons.offsetMax = new Vector2(0f, -16f);
        HorizontalLayoutGroup row = page.FootButtons.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.childAlignment = TextAnchor.MiddleRight;
        row.spacing = 18f;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = false;
        row.childForceExpandHeight = true;
    }

    /// <summary>A button in the foot's row (right to left in the order added: the last is the main one).</summary>
    public Button FootButton(string text, Paintbox.Tone tone, string icon, UnityAction onClick, float width = 0f)
    {
        Button b = Paintbox.Button(FootButtons, text, tone, icon, onClick, 30f);
        LayoutElement le = b.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = width > 0f ? width : Mathf.Max(220f, text.Length * 18f + (icon != null ? 110f : 70f));
        return b;
    }

    /// <summary>A button at the foot's left edge (Back, Exit game, Reset defaults).</summary>
    public Button FootLeftButton(string text, Paintbox.Tone tone, string icon, UnityAction onClick, float width = 0f)
    {
        Button b = Paintbox.Button(Foot, text, tone, icon, onClick, 30f);
        RectTransform rt = (RectTransform)b.transform;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.offsetMin = new Vector2(0f, 14f);
        rt.offsetMax = new Vector2(width > 0f ? width : Mathf.Max(200f, text.Length * 18f + 110f), -16f);
        FootTitle.rectTransform.offsetMin += new Vector2(rt.offsetMax.x + 24f, 0f);
        FootDetail.rectTransform.offsetMin += new Vector2(rt.offsetMax.x + 24f, 0f);
        return b;
    }

    // ================= Multiplayer's tabs =================

    private static readonly (HubTab tab, string name, string label, string icon)[] Tabs =
    {
        (HubTab.NewMatch, "Tab_NEW", "New Match", "CirclePlus"),
        (HubTab.Matches, "Extra_MATCHES", "Matches", "Layers"),
        (HubTab.History, "Extra_HISTORY", "History", "History"),
        (HubTab.Profile, "Extra_PROFILE", "Profile", "User"),
        (HubTab.Rate, "Extra_RATE", "Rate", "Star"),
    };

    private static void HubTabs(RectTransform safe, HubTab active)
    {
        RectTransform row = Paintbox.TopBand(Paintbox.NewRect(safe, "HubTabs"), TabsTop, TabsHeight, Paintbox.Gutter, Paintbox.Gutter);
        HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 10f;
        layout.padding = new RectOffset(0, 0, 4, 10);
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = true;

        foreach (var t in Tabs)
        {
            HubTab tab = t.tab;
            bool on = tab == active;
            Button b = Paintbox.Button(row, t.label, on ? Paintbox.Tone.Secondary : Paintbox.Tone.Paper, t.icon,
                                       () => { AudioManager.ButtonTap(); MultiplayerHub.Go(tab); }, 28f);
            b.name = t.name;
            PaintboxFrame frame = b.GetComponent<PaintboxFrame>();
            // Picked: violet with the ink outline and shadow, as the Shop's picked tab (2026-11-25:
            // the soft wash it had was hard to tell from the others). The others are quiet.
            if (!on) Quiet(frame);

            if (tab == HubTab.Matches)
            {
                // How many matches are running without the player: a small ink badge in the tab's
                // corner, out of the icon and word's row (it pushed them off centre while hidden).
                Image badge = Paintbox.Image(frame.Body, "Badge", UiArt.Circle, Paintbox.Ink);
                badge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                Paintbox.Pin(badge.rectTransform, new Vector2(1f, 0.5f), new Vector2(-12f, 0f), new Vector2(34f, 34f));
                badge.rectTransform.pivot = new Vector2(1f, 0.5f);
                Text count = Paintbox.Label(badge.rectTransform, "", 21f, Color.white, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
                Paintbox.Fill(count.rectTransform);
                badge.gameObject.AddComponent<MatchesBadge>().Count = count;
            }
        }

        // The rule under the tabs is part of the row: hidden with it (Single Play's setup).
        Image rule = Paintbox.Divider(row);
        rule.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        RectTransform r = rule.rectTransform;
        r.anchorMin = Vector2.zero;
        r.anchorMax = new Vector2(1f, 0f);
        r.pivot = new Vector2(0.5f, 1f);
        r.offsetMin = new Vector2(0f, -3f);
        r.offsetMax = Vector2.zero;
    }

    /// <summary>An unpicked tab or chip: no outline or shadow, its words muted.</summary>
    public static void Quiet(PaintboxFrame frame)
    {
        if (frame == null) return;
        frame.Fill.color = Color.clear;
        if (frame.Edge != null) frame.Edge.enabled = false;
        if (frame.Shade != null) frame.Shade.enabled = false;
        frame.ShadowDepth = 0f;
        foreach (Text t in frame.Body.GetComponentsInChildren<Text>(true)) t.color = Paintbox.Ink;
        foreach (Image i in frame.Body.GetComponentsInChildren<Image>(true)) if (i.name == "Icon") i.color = Paintbox.Ink;
    }

    /// <summary>A sub-tab or filter chip row (Match History | Wallet History): picked = violet with an outline.</summary>
    public static Button Chip(Transform row, string text, string icon, bool on, UnityAction onClick, float fontSize = 28f)
    {
        Button b = Paintbox.Button(row, text, on ? Paintbox.Tone.Secondary : Paintbox.Tone.Paper, icon, onClick, fontSize, 22f);
        if (!on)
        {
            Quiet(b.GetComponent<PaintboxFrame>());
            foreach (Text t in b.GetComponentsInChildren<Text>(true)) t.color = Paintbox.Muted;
            foreach (Image i in b.GetComponentsInChildren<Image>(true)) if (i.name == "Icon") i.color = Paintbox.Muted;
        }
        return b;
    }

    // ================= Where the bar's buttons go =================

    /// <summary>
    /// The coin balance's tap, on every screen (2026-11-25; it opened the wallet, which is gone):
    /// the Shop on its Coins, signed in first when need be. In the Shop itself, its Coins tab.
    /// </summary>
    public static void OpenCoins()
    {
        AudioManager.ButtonTap();
        if (ShopScreen.IsOpen) { ShopScreen.ShowCoins(); return; }
        ShopUI shop = UnityEngine.Object.FindAnyObjectByType<ShopUI>(FindObjectsInactive.Include);
        if (shop == null) return;
        OnlineSignIn.Open(_ => shop.OpenFrom(null, ShopScreen.CoinsCategory), null);
    }

    /// <summary>Makes a coin label open the Shop's Coins when tapped.</summary>
    public static void MakeCoinsOpener(Text label)
    {
        if (label == null) return;
        Button button = label.GetComponent<Button>();
        if (button == null)
        {
            label.raycastTarget = true;
            button = label.gameObject.AddComponent<Button>();
            button.targetGraphic = label;
            button.transition = Selectable.Transition.None;
        }
        button.onClick.RemoveListener(OpenCoins);
        button.onClick.AddListener(OpenCoins);
    }

    public static void OpenShop()
    {
        ShopUI shop = UnityEngine.Object.FindAnyObjectByType<ShopUI>(FindObjectsInactive.Include);
        if (shop == null) return;
        OnlineSignIn.Open(_ => shop.OpenFrom(null), null);
    }

    public static void OpenSettings()
    {
        AudioManager.ButtonTap();
        SettingsUI settings = UnityEngine.Object.FindAnyObjectByType<SettingsUI>(FindObjectsInactive.Include);
        if (settings != null) settings.Show();
    }

    // ================= Small parts for pages =================

    /// <summary>A label for a group, in small bold capitals (ROLE, CHARACTER...).</summary>
    public static Text Kicker(Transform parent, string text)
    {
        Text t = Paintbox.Label(parent, text, Paintbox.TinySize, Paintbox.Muted, Paintbox.Weight.Bold);
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        return t;
    }

    /// <summary>A table's head row: column names in small bold capitals over a tinted band.</summary>
    public static RectTransform TableHead(Transform parent, (string text, float from, float to, TextAnchor anchor)[] columns)
    {
        Image band = Paintbox.Image(parent, "Head", null, Paintbox.Card);
        RectTransform rt = band.rectTransform;
        foreach (var c in columns)
        {
            Text t = Kicker(rt, c.text);
            t.alignment = c.anchor;
            t.rectTransform.anchorMin = new Vector2(c.from, 0f);
            t.rectTransform.anchorMax = new Vector2(c.to, 1f);
            t.rectTransform.offsetMin = new Vector2(20f, 0f);
            t.rectTransform.offsetMax = new Vector2(-20f, 0f);
        }
        return rt;
    }

    /// <summary>A text in a table row, between two fractions of its width.</summary>
    public static Text Cell(Transform row, string text, float from, float to, float size, Color color, Paintbox.Weight weight, TextAnchor anchor)
    {
        Text t = Paintbox.Label(row, text, size, color, weight, anchor);
        Paintbox.Fit(t, 14);
        t.rectTransform.anchorMin = new Vector2(from, 0f);
        t.rectTransform.anchorMax = new Vector2(to, 1f);
        t.rectTransform.offsetMin = new Vector2(20f, 4f);
        t.rectTransform.offsetMax = new Vector2(-20f, -4f);
        return t;
    }
}

/// <summary>The count on the Matches tab: matches running without the player (none: no badge).</summary>
public sealed class MatchesBadge : MonoBehaviour
{
    public Text Count;
    private int shown = -1;

    private void Update()
    {
        int n = OnlineMatches.AwayCount;
        if (n == shown) return;
        shown = n;
        if (Count != null) Count.text = n.ToString();
        GetComponent<Image>().enabled = n > 0;
        if (Count != null) Count.enabled = n > 0;
    }
}

/// <summary>
/// Multiplayer is one place with five tabs (design B): New Match (the play setup, always under the
/// others), Matches, History, Profile and Rate, each a page over it. Going to a tab puts the page
/// that is up away and opens the other; New Match is what is left when none is up.
/// </summary>
/// <summary>
/// A page's own coin balance, counted while it is on screen: the shared pill (OnlineMatches) stays
/// away while one is, or a page such as the match results showed two (2026-11-26).
/// </summary>
public sealed class PaintboxPageBalance : MonoBehaviour
{
    public static int Shown { get; private set; }
    private void OnEnable() => Shown++;
    private void OnDisable() => Shown--;
}

public static class MultiplayerHub
{
    private static HubTab current = HubTab.NewMatch;
    private static Action closeCurrent;

    /// <summary>The tab whose page is up (New Match when only the setup is).</summary>
    public static HubTab Current => closeCurrent != null ? current : HubTab.NewMatch;

    /// <summary>A hub page is up over the setup.</summary>
    public static bool PageOpen => closeCurrent != null;

    /// <summary>A page says it is up, and how to put it away for another tab.</summary>
    public static void Showing(HubTab tab, Action close)
    {
        if (closeCurrent != null && current != tab)
        {
            Action other = closeCurrent;
            closeCurrent = null;
            other();
        }
        current = tab;
        closeCurrent = close;
    }

    /// <summary>A page went away by itself (its Back, its Close).</summary>
    public static void Closed(HubTab tab)
    {
        if (current != tab) return;
        closeCurrent = null;
        current = HubTab.NewMatch;
    }

    /// <summary>A tab tapped.</summary>
    public static void Go(HubTab tab)
    {
        if (tab == Current) return;
        Action close = closeCurrent;
        closeCurrent = null;
        current = HubTab.NewMatch;
        close?.Invoke();

        OnlineLobby lobby = PlaySetupUI.Instance != null ? PlaySetupUI.Instance.Lobby : null;
        switch (tab)
        {
            case HubTab.Matches: OnlineMatches.ShowList(); break;
            case HubTab.History: lobby?.OpenHistory(); break;
            case HubTab.Profile: lobby?.OpenAccount(); break;
            case HubTab.Rate: RateScreen.Open(true); break;
        }
    }

    /// <summary>Puts any hub page away (the match starts, the player goes home).</summary>
    public static void CloseAll()
    {
        Action close = closeCurrent;
        closeCurrent = null;
        current = HubTab.NewMatch;
        close?.Invoke();
    }
}

/// <summary>
/// A design B dialog (the prototype's Leave match): a dimmed screen with a paper card in its middle,
/// a soft square with an icon, a bold italic question, a line under it, and its buttons on the right.
/// </summary>
public sealed class PaintboxDialog
{
    public GameObject Root;          // the dim: SetActive shows and hides it
    public RectTransform Card;       // the card's body
    public Image Badge, BadgeIcon;
    public Text Title, Body;
    public RectTransform Buttons;

    public static PaintboxDialog Build(Transform canvas, string name, string icon, string title, Vector2 size)
    {
        var d = new PaintboxDialog();
        Image dim = Paintbox.Image(canvas, name, null, Paintbox.Dim, true);
        Paintbox.Fill(dim.rectTransform);
        d.Root = dim.gameObject;

        PaintboxFrame card = Paintbox.Frame(dim.rectTransform, "Panel", Paintbox.Paper, Paintbox.RadiusLarge, Paintbox.Border, 0f, Paintbox.PanelLine);
        Paintbox.Pin(card.Root, new Vector2(0.5f, 0.5f), Vector2.zero, size);
        d.Card = card.Body;

        d.Badge = Paintbox.Rounded(card.Body, "Badge", Paintbox.Wash, 16f);
        Paintbox.Pin(d.Badge.rectTransform, new Vector2(0f, 1f), new Vector2(40f, -36f), new Vector2(64f, 64f));
        d.Badge.rectTransform.pivot = new Vector2(0f, 1f);
        d.BadgeIcon = Paintbox.IconImage(d.Badge.rectTransform, icon, 34f, Paintbox.WashInk);

        d.Title = Paintbox.Label(card.Body, title, 48f, Paintbox.Ink, Paintbox.Weight.Title);
        d.Title.name = "Title";
        Paintbox.Fit(d.Title, 28);
        Paintbox.TopBand(d.Title.rectTransform, 112f, 64f, 40f, 40f);
        d.Body = Paintbox.Label(card.Body, "", 26f, Paintbox.Muted);
        d.Body.name = "Body";
        d.Body.verticalOverflow = VerticalWrapMode.Overflow;
        d.Body.rectTransform.anchorMin = new Vector2(0f, 0f);
        d.Body.rectTransform.anchorMax = new Vector2(1f, 1f);
        d.Body.rectTransform.offsetMin = new Vector2(40f, 130f);
        d.Body.rectTransform.offsetMax = new Vector2(-40f, -184f);
        d.Body.alignment = TextAnchor.UpperLeft;

        d.Buttons = Paintbox.NewRect(card.Body, "Buttons");
        d.Buttons.anchorMin = new Vector2(0f, 0f);
        d.Buttons.anchorMax = new Vector2(1f, 0f);
        d.Buttons.pivot = new Vector2(0.5f, 0f);
        d.Buttons.offsetMin = new Vector2(40f, 32f);
        d.Buttons.offsetMax = new Vector2(-40f, 114f);
        HorizontalLayoutGroup h = d.Buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.childAlignment = TextAnchor.MiddleRight;
        h.spacing = 16f;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        return d;
    }

    /// <summary>A button in the dialog's row (left to right in the order added: the main one last).</summary>
    public Button Button(string text, Paintbox.Tone tone, string icon, UnityAction onClick, float width = 0f)
    {
        Button b = Paintbox.Button(Buttons, text, tone, icon, onClick, 28f);
        b.gameObject.AddComponent<LayoutElement>().preferredWidth = width > 0f ? width : Mathf.Max(190f, text.Length * 17f + (icon != null ? 110f : 70f));
        return b;
    }

    /// <summary>A button in the row that is also the dialog's way back: the phone's Back presses it (Cancel, Stay, OK, Done).</summary>
    public Button BackButton(string text, Paintbox.Tone tone, string icon, UnityAction onClick, float width = 0f)
    {
        Button b = Button(text, tone, icon, onClick, width);
        BackKey.Mark(b);
        return b;
    }

    /// <summary>A quiet button that reads as a way out of something (Leave, Exit): red words on paper.</summary>
    public Button DangerButton(string text, string icon, UnityAction onClick, float width = 0f)
    {
        Button b = Button(text, Paintbox.Tone.Paper, icon, onClick, width);
        foreach (Text t in b.GetComponentsInChildren<Text>(true)) t.color = Paintbox.Bad;
        foreach (Image i in b.GetComponentsInChildren<Image>(true)) if (i.name == "Icon") i.color = Paintbox.Bad;
        return b;
    }
}

/// <summary>A grid that fits as many columns as its width holds (at least MinWidth each), the cells sharing it.</summary>
[RequireComponent(typeof(GridLayoutGroup))]
public sealed class PaintboxGridFit : MonoBehaviour
{
    public float MinWidth = 300f;
    /// <summary>When above 0, each cell is this much as tall as it is wide.</summary>
    public float Aspect;
    private float seen = -1f;

    /// <summary>Fits the columns again now (a picture taken at another size).</summary>
    public void Refit()
    {
        seen = -1f;
        LateUpdate();
        LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)transform);
    }

    private void LateUpdate()
    {
        var rt = (RectTransform)transform;
        float width = rt.rect.width;
        if (Mathf.Abs(width - seen) < 1f || width < 10f) return;
        seen = width;
        GridLayoutGroup grid = GetComponent<GridLayoutGroup>();
        float inner = width - grid.padding.left - grid.padding.right;
        int columns = Mathf.Max(1, Mathf.FloorToInt((inner + grid.spacing.x) / (MinWidth + grid.spacing.x)));
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = columns;
        float cell = Mathf.Floor((inner - grid.spacing.x * (columns - 1)) / columns);
        grid.cellSize = new Vector2(cell, Aspect > 0f ? Mathf.Floor(cell * Aspect) : grid.cellSize.y);
    }
}
