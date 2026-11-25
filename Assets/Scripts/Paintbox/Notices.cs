using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>What a notice is about: it sets the notice's colour and icon.</summary>
public enum NoticeKind
{
    Info,
    Purchase,   // bought, equipped, a payment opened
    Match,      // matches: found, started, a hider out, results ready
    Reward,     // coins in: a top-up credited, coins won
    Error,      // something failed or was refused
}

/// <summary>
/// Every short message of the game, on every screen, in one place and one look (2026-11-24,
/// design B): a paper card with a coloured badge for its kind, in the notice slot at the top right
/// (under the coin balance, clear of the timer, the titles and the round's buttons), sliding in
/// from the right and out again by itself. Three at most, the newest on top; the same message again
/// only keeps its card up longer. One that offers something (open the results) does it when tapped;
/// every other notice lets taps through to what is under it, so it never gets in the way of play
/// or a screen's buttons. It waits while the loading screen is up.
/// </summary>
public sealed class Notices : MonoBehaviour
{
    private const int CanvasOrder = 90;          // over everything, the paint panel (80) included
    private const int MaxShown = 3;
    private const float Gap = 14f;
    private const float SlideSeconds = 0.28f;
    private const float OneLine = 92f;
    private const float TwoLines = 120f;

    private sealed class Card
    {
        public RectTransform Rt;
        public CanvasGroup Group;
        public Text Title, Body;
        public RectTransform Bar;
        public string Key;
        public float Shown, Seconds, Until, Y;
        public bool Leaving;
        public bool Tappable;     // it offers something (the results...): only then does it take taps
        public float LeftAt;
        public float Height;
    }

    private static Notices instance;
    private readonly List<Card> cards = new List<Card>();
    private RectTransform slot;

    /// <summary>The cards on screen, newest first, "title | body" each (the play tests read it).</summary>
    public static string Visible
    {
        get
        {
            if (instance == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (Card c in instance.cards)
            {
                if (c.Leaving) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(c.Title.text);
                if (c.Body.gameObject.activeSelf) sb.Append(" | ").Append(c.Body.text);
            }
            return sb.ToString();
        }
    }

    /// <summary>How many notices were shown this session (the play tests read it).</summary>
    public static int ShownCount { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (instance != null) return;
        var go = new GameObject("Notices");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<Notices>();
    }

    /// <summary>A one-line notice.</summary>
    public static void Show(NoticeKind kind, string title, float seconds = 3.5f, Action onTap = null) =>
        Show(kind, title, null, seconds, onTap);

    /// <summary>
    /// A notice: a title and, under it, an optional line of detail (both in English: they are
    /// translated as they are drawn). <paramref name="onTap"/> runs when it is tapped.
    /// </summary>
    public static void Show(NoticeKind kind, string title, string body, float seconds = 4f, Action onTap = null)
    {
        if (string.IsNullOrEmpty(title)) return;
        if (instance == null) Create();
        instance.Add(kind, title, body, Mathf.Max(1.2f, seconds), onTap);
    }

    /// <summary>Takes every notice away at once (a screen that has its own message now).</summary>
    public static void Clear()
    {
        if (instance == null) return;
        foreach (Card c in instance.cards) instance.Leave(c);
    }

    private void Awake()
    {
        Canvas canvas = OnlineUI.CreateCanvas("NoticesCanvas", transform, CanvasOrder);
        RectTransform safe = Paintbox.SafeArea(Paintbox.NewRect(canvas.transform, "Safe"));
        slot = Paintbox.NewRect(safe, "Slot");
        slot.anchorMin = slot.anchorMax = slot.pivot = new Vector2(1f, 1f);
        slot.anchoredPosition = new Vector2(OnlineUI.NoticeRight, -150f);
        slot.sizeDelta = new Vector2(OnlineUI.NoticeWidth, 10f);
    }

    private void Add(NoticeKind kind, string title, string body, float seconds, Action onTap)
    {
        string key = title + "\n" + body;
        foreach (Card same in cards)
        {
            if (same.Leaving || same.Key != key) continue;
            same.Until = Time.unscaledTime + seconds;   // the same news again: kept up longer, no second card
            same.Seconds = seconds;
            return;
        }

        ShownCount++;
        while (CountShown() >= MaxShown)
        {
            Card oldest = cards.FindLast(c => !c.Leaving);
            if (oldest == null) break;
            Leave(oldest);
        }

        Card card = Build(kind, title, body, onTap);
        card.Key = key;
        card.Seconds = seconds;
        card.Shown = Time.unscaledTime;
        card.Until = Time.unscaledTime + seconds;
        cards.Insert(0, card);
        card.Y = 0f;
        Layout(true);
    }

    private int CountShown()
    {
        int n = 0;
        foreach (Card c in cards) if (!c.Leaving) n++;
        return n;
    }

    private static Color KindColor(NoticeKind kind)
    {
        switch (kind)
        {
            case NoticeKind.Purchase: return Paintbox.Yellow;
            case NoticeKind.Match: return Paintbox.Violet;
            case NoticeKind.Reward: return Paintbox.Hex(0x8fd3a3);
            case NoticeKind.Error: return Paintbox.Hex(0xf08a7e);
            default: return Paintbox.Wash;
        }
    }

    private static string KindIcon(NoticeKind kind)
    {
        switch (kind)
        {
            case NoticeKind.Purchase: return "ShoppingBag";
            case NoticeKind.Match: return "Flag";
            case NoticeKind.Reward: return "Coins";
            case NoticeKind.Error: return "CircleAlert";
            default: return "Info";
        }
    }

    private Card Build(NoticeKind kind, string title, string body, Action onTap)
    {
        bool two = !string.IsNullOrEmpty(body);
        float height = two ? TwoLines : OneLine;
        float width = Mathf.Min(OnlineUI.NoticeWidth, WidthAvailable());

        PaintboxFrame frame = Paintbox.Frame(slot, "Notice", Paintbox.Paper, 24f, Paintbox.Border, 5f);
        RectTransform rt = frame.Root;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.sizeDelta = new Vector2(width, height);
        CanvasGroup group = rt.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;

        var card = new Card { Rt = rt, Group = group, Height = height, Tappable = onTap != null };
        group.blocksRaycasts = card.Tappable;

        Button button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = frame.Fill;
        button.transition = Selectable.Transition.None;
        Navigation none = button.navigation;
        none.mode = Navigation.Mode.None;
        button.navigation = none;
        button.onClick.AddListener(() =>
        {
            AudioManager.ButtonTap();
            Leave(card);
            onTap?.Invoke();
        });

        // The kind's badge: a coloured square with its icon.
        Color tint = KindColor(kind);
        Image badge = Paintbox.Rounded(frame.Body, "Badge", tint, 18f);
        Paintbox.Pin(badge.rectTransform, new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(62f, 62f));
        badge.rectTransform.pivot = new Vector2(0f, 0.5f);
        Image icon = Paintbox.IconImage(badge.rectTransform, KindIcon(kind), 36f, Paintbox.Ink);
        icon.name = "KindIcon";

        float left = 94f, right = onTap != null ? 56f : 20f;
        card.Title = Paintbox.Label(frame.Body, title, 27f, Paintbox.Ink, Paintbox.Weight.Bold);
        Paintbox.Fit(card.Title, 18);
        RectTransform t = card.Title.rectTransform;
        t.anchorMin = new Vector2(0f, two ? 0.5f : 0f);
        t.anchorMax = new Vector2(1f, 1f);
        t.offsetMin = new Vector2(left, two ? 0f : 8f);
        t.offsetMax = new Vector2(-right, two ? -10f : -8f);
        card.Title.alignment = two ? TextAnchor.LowerLeft : TextAnchor.MiddleLeft;

        card.Body = Paintbox.Label(frame.Body, body ?? "", 23f, Paintbox.Muted);
        Paintbox.Fit(card.Body, 15);
        RectTransform b = card.Body.rectTransform;
        b.anchorMin = new Vector2(0f, 0f);
        b.anchorMax = new Vector2(1f, 0.5f);
        b.offsetMin = new Vector2(left, 10f);
        b.offsetMax = new Vector2(-right, -2f);
        card.Body.alignment = TextAnchor.UpperLeft;
        card.Body.gameObject.SetActive(two);

        if (onTap != null)
        {
            Image go = Paintbox.IconImage(frame.Body, "ChevronRight", 34f, Paintbox.Muted);
            Paintbox.Pin(go.rectTransform, new Vector2(1f, 0.5f), new Vector2(-14f, 0f), new Vector2(34f, 34f));
        }

        // The time it has left, as a thin bar along its foot in the kind's colour.
        Image bar = Paintbox.Image(frame.Body, "Time", null, Color.Lerp(tint, Paintbox.Ink, 0.25f));
        card.Bar = bar.rectTransform;
        card.Bar.anchorMin = new Vector2(0f, 0f);
        card.Bar.anchorMax = new Vector2(1f, 0f);
        card.Bar.pivot = new Vector2(0f, 0f);
        card.Bar.offsetMin = new Vector2(22f, 7f);
        card.Bar.offsetMax = new Vector2(-22f, 12f);

        AudioManager.ButtonTap();
        return card;
    }

    private float WidthAvailable()
    {
        // Never past the middle of the screen: a narrow (4:3) screen gets a narrower card.
        Canvas canvas = slot.GetComponentInParent<Canvas>();
        var root = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
        float canvasWidth = root != null && root.rect.width > 1f ? root.rect.width : 1920f;
        return Mathf.Clamp(canvasWidth * 0.5f + OnlineUI.NoticeRight - 20f, 400f, OnlineUI.NoticeWidth);
    }

    private void Leave(Card card)
    {
        if (card.Leaving) return;
        card.Leaving = true;
        card.LeftAt = Time.unscaledTime;
        Layout(false);
    }

    // Where each card belongs: the newest at the top of the slot, the others under it.
    private void Layout(bool snapNewest)
    {
        float y = 0f;
        foreach (Card c in cards)
        {
            if (c.Leaving) continue;
            c.Y = -y;
            y += c.Height + Gap;
        }
        if (snapNewest && cards.Count > 0) cards[0].Rt.anchoredPosition = new Vector2(0f, cards[0].Y);
    }

    private void Update()
    {
        if (cards.Count == 0) return;
        float now = Time.unscaledTime;

        // Behind the loading screen nothing is seen: the clocks wait.
        bool hidden = LoadingScreen.Holding;
        for (int i = cards.Count - 1; i >= 0; i--)
        {
            Card c = cards[i];
            if (c.Rt == null) { cards.RemoveAt(i); continue; }

            if (hidden && !c.Leaving)
            {
                c.Until += Time.unscaledDeltaTime;
                c.Group.alpha = 0f;
                continue;
            }

            if (!c.Leaving && now >= c.Until) Leave(c);

            float inT = Mathf.Clamp01((now - c.Shown) / SlideSeconds);
            float outT = c.Leaving ? Mathf.Clamp01((now - c.LeftAt) / SlideSeconds) : 0f;
            float shown = Ease(inT) * (1f - Ease(outT));
            c.Group.alpha = shown;
            c.Group.blocksRaycasts = c.Tappable && !c.Leaving;

            Vector2 at = c.Rt.anchoredPosition;
            at.y = Mathf.Lerp(at.y, c.Y, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
            at.x = (1f - shown) * 140f;
            c.Rt.anchoredPosition = at;

            if (!c.Leaving)
            {
                float left = Mathf.Clamp01((c.Until - now) / Mathf.Max(0.01f, c.Seconds));
                c.Bar.anchorMax = new Vector2(left, 0f);
                c.Bar.gameObject.SetActive(left > 0.08f);
            }

            if (c.Leaving && outT >= 1f)
            {
                Destroy(c.Rt.gameObject);
                cards.RemoveAt(i);
            }
        }
    }

    private static float Ease(float t) => 1f - (1f - t) * (1f - t);
}
