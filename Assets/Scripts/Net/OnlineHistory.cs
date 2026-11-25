using System;
using System.Globalization;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Multiple Play's history, in two tabs (2026-11-24: the wallet's ledger moved here from the wallet
// screen, at the user's request):
//   WALLET HISTORY  the coin ledger (MetaClient.ListTransactions), newest first: every deposit,
//                   purchase, entry fee, payout and refund, and the withdrawals made while the game
//                   still had them;
//   MATCH HISTORY   the player's own match records (MetaClient.MatchHistory, reward rule §13): when,
//                   which room, which side, how it went, the points, what the entry cost and what
//                   came back, and the server's one-line explanation.
// Every number is the server's; this only lays them out. Design B (2026-11-24): Multiplayer's History
// tab, a page with the two kinds as sub-tabs over one table each (scrolling); opened from the hub's
// tab and from the wallet screen.
public class OnlineHistory : MonoBehaviour
{
    public enum Tab { Wallet, Matches }

    private const int RecordsToFetch = 20;
    private const int MatchRowsPerPage = 4;
    private const int WalletRowsPerPage = 5;
    private const float RateLimitRetrySeconds = 2f;
    private const int RateLimitRetries = 3;
    private const float BalanceRefreshSeconds = 1.5f;

    private const string Good = "#2C684C";   // design B (2026-11-24): readable on paper
    private const string Bad = "#A33D36";

    private static MetaClient Meta => MetaClient.Instance;

    private Action onClose;
    private Tab tab = Tab.Wallet;
    private int version;

    private MetaMatchRecord[] records = new MetaMatchRecord[0];
    private MetaTransaction[] lines = new MetaTransaction[0];
    private bool recordsLoaded, linesLoaded;
    private string recordsProblem, linesProblem;
    private int recordsRetries, linesRetries;
    private float retryRecordsAt, retryLinesAt, refreshLinesAt;

    private GameObject screen;
    private RectTransform walletList, matchList, walletTable, matchTable, tabsRow;
    private Text status;
    private readonly Button[] tabButtons = new Button[2];

    private void Awake()
    {
        BuildUI();
        screen.SetActive(false);
    }

    private void OnEnable() => CoinBalance.Changed += HandleBalanceChanged;
    private void OnDisable() => CoinBalance.Changed -= HandleBalanceChanged;

    /// <summary>Opens the history over whatever is showing, on the tab it was left on; <paramref name="closed"/> after Close.</summary>
    public static void Show(Transform owner, Action closed) => Find(owner).Open(null, closed);

    /// <summary>Opens the history on this tab.</summary>
    public static void Show(Transform owner, Tab startTab, Action closed) => Find(owner).Open(startTab, closed);

    private static OnlineHistory Find(Transform owner)
    {
        OnlineHistory history = FindAnyObjectByType<OnlineHistory>();
        if (history != null) return history;
        return owner != null ? owner.gameObject.AddComponent<OnlineHistory>() : new GameObject("OnlineHistory").AddComponent<OnlineHistory>();
    }

    public void Open(Tab? startTab, Action closed)
    {
        if (Meta == null) { closed?.Invoke(); return; }

        onClose = closed;
        if (startTab.HasValue) tab = startTab.Value;
        screen.SetActive(true);
        screen.transform.SetAsLastSibling();
        MultiplayerHub.Showing(HubTab.History, Hide);
        version++;

        records = new MetaMatchRecord[0];
        lines = new MetaTransaction[0];
        recordsLoaded = linesLoaded = false;
        recordsProblem = linesProblem = null;
        recordsRetries = linesRetries = 0;
        retryRecordsAt = retryLinesAt = refreshLinesAt = 0f;

        SelectTab(tab);
        AskLines();
        AskRecords();
    }

    private void Close()
    {
        AudioManager.ButtonTap();
        Hide();
        Action closed = onClose;
        onClose = null;
        closed?.Invoke();
    }

    // Put away for another of Multiplayer's tabs (or by Close).
    private void Hide()
    {
        screen.SetActive(false);
        version++;
        MultiplayerHub.Closed(HubTab.History);
    }

    // ================= What the server says =================

    private void AskLines()
    {
        if (Meta == null) return;
        int asked = version;
        retryLinesAt = 0f;
        Meta.ListTransactions(NetTuning.MaxTransactions, reply =>
        {
            if (!screen.activeSelf || asked != version) return;

            // Too many requests in a row: asked again shortly, a few times.
            if (reply.Error == MetaError.RateLimited && linesRetries < RateLimitRetries)
            {
                linesRetries++;
                retryLinesAt = Time.unscaledTime + RateLimitRetrySeconds;
                return;
            }

            if (reply.TimedOut || reply.Error != MetaError.None)
            {
                // What is shown stays; a first answer that failed says so.
                if (!linesLoaded) linesProblem = Problem(reply.TimedOut, reply.Error);
            }
            else
            {
                linesRetries = 0;
                linesLoaded = true;
                linesProblem = null;
                lines = reply.Lines ?? new MetaTransaction[0];
            }

            if (tab == Tab.Wallet) BuildTab();
        });
    }

    private void AskRecords()
    {
        if (Meta == null) return;
        int asked = version;
        retryRecordsAt = 0f;
        Meta.MatchHistory(RecordsToFetch, reply =>
        {
            if (!screen.activeSelf || asked != version) return;

            if (reply.Error == MetaError.RateLimited && recordsRetries < RateLimitRetries)
            {
                recordsRetries++;
                retryRecordsAt = Time.unscaledTime + RateLimitRetrySeconds;
                return;
            }

            if (reply.TimedOut || reply.Error != MetaError.None)
            {
                if (!recordsLoaded) recordsProblem = Problem(reply.TimedOut, reply.Error);
            }
            else
            {
                recordsRetries = 0;
                recordsLoaded = true;
                recordsProblem = null;
                records = reply.Records ?? new MetaMatchRecord[0];
            }

            if (tab == Tab.Matches) BuildTab();
        });
    }

    private static string Problem(bool timedOut, MetaError error) =>
        timedOut ? "The server did not answer. Try again." : $"The server could not answer ({error}).";

    // Coins moved while the history is open (a deposit paid, a purchase): the ledger is asked for
    // again, once things have settled.
    private void HandleBalanceChanged()
    {
        if (screen == null || !screen.activeSelf) return;
        refreshLinesAt = Time.unscaledTime + BalanceRefreshSeconds;
    }

    private void Update()
    {
        if (screen == null || !screen.activeSelf || Meta == null) return;

        float now = Time.unscaledTime;
        if (retryLinesAt > 0f && now >= retryLinesAt) AskLines();
        if (retryRecordsAt > 0f && now >= retryRecordsAt) AskRecords();
        if (refreshLinesAt > 0f && now >= refreshLinesAt)
        {
            refreshLinesAt = 0f;
            AskLines();
        }
    }

    // ================= Tabs =================

    private void SelectTab(Tab selected)
    {
        tab = selected;
        DrawSubTabs();
        walletTable.gameObject.SetActive(selected == Tab.Wallet);
        matchTable.gameObject.SetActive(selected == Tab.Matches);
        BuildTab();
    }

    private void BuildTab()
    {
        if (tab == Tab.Wallet) BuildWallet();
        else BuildMatches();
    }

    private static void Clear(RectTransform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            parent.GetChild(i).gameObject.SetActive(false);
            Destroy(parent.GetChild(i).gameObject);
        }
    }

    // The two sub-tabs: the one shown violet with an outline, the other quiet.
    private void DrawSubTabs()
    {
        Clear(tabsRow);
        tabButtons[(int)Tab.Matches] = PaintboxPage.Chip(tabsRow, "Match History", "Trophy", tab == Tab.Matches, () => { AudioManager.ButtonTap(); SelectTab(Tab.Matches); });
        tabButtons[(int)Tab.Matches].name = "MatchHistoryTab";
        tabButtons[(int)Tab.Wallet] = PaintboxPage.Chip(tabsRow, "Wallet History", "Wallet", tab == Tab.Wallet, () => { AudioManager.ButtonTap(); SelectTab(Tab.Wallet); });
        tabButtons[(int)Tab.Wallet].name = "WalletHistoryTab";
        foreach (Button b in tabButtons) b.gameObject.AddComponent<LayoutElement>().preferredWidth = 330f;
    }

    // ================= Wallet history =================

    // A row: an arrow in a soft square (coins in, coins out), the reason and when, the kind, the
    // signed change and the balance after it.
    private void BuildWallet()
    {
        Clear(walletList);
        status.text = linesProblem ?? (!linesLoaded ? "Loading..." : lines.Length == 0 ? "Nothing has moved your coins yet." : "");
        status.gameObject.SetActive(status.text.Length > 0);

        foreach (MetaTransaction t in lines)
        {
            bool plus = t.AmountCents >= 0;
            RectTransform row = Row(walletList, "Line");
            Badge(row, t.Kind == "topup" ? "Coins" : plus ? "ArrowDown" : "ArrowUp");
            string when = DateTimeOffset.FromUnixTimeSeconds(t.UnixSeconds).ToLocalTime().ToString("MMM d \u00b7 HH:mm", CultureInfo.InvariantCulture);
            TwoLines(row, KindText(t), when, 0f, 0.46f);
            PaintboxPage.Cell(row, TypeText(t), 0.46f, 0.68f, 25f, Paintbox.Ink, Paintbox.Weight.Regular, TextAnchor.MiddleLeft);
            PaintboxPage.Cell(row, (plus ? "+" : "\u2212") + CoinBalance.Format(Math.Abs(t.AmountCents)), 0.68f, 0.84f, 28f,
                              plus ? Paintbox.Good : Paintbox.Bad, Paintbox.Weight.Bold, TextAnchor.MiddleRight);
            PaintboxPage.Cell(row, CoinBalance.Format(t.BalanceAfterCents), 0.84f, 1f, 26f, Paintbox.Muted, Paintbox.Weight.Regular, TextAnchor.MiddleRight);
        }
    }

    private static string TypeText(MetaTransaction t)
    {
        switch (t.Kind)
        {
            case "topup": return "Purchased coins";
            case "purchase": return "Shop spend";
            case "entry": return "Entry fee";
            case "payout": return "Match reward";
            case "refund":
            case "withdrawal-refund": return "Refund";
            case "forfeit": return "Forfeit";
            case "withdrawal": return "Withdrawal";
            case "admin": return "Support";
            default: return "Coins";
        }
    }

    // Purchases, earnings and spending by the ledger's own kinds. Withdrawals are no longer offered
    // (2026-11-24), but the ones made before still show as they happened.
    private static string KindText(MetaTransaction t)
    {
        switch (t.Kind)
        {
            case "topup": return string.IsNullOrEmpty(t.ItemId) ? "Deposit" : $"Deposit ({t.ItemId})";
            case "withdrawal": return "Withdrawal to the wallet";
            case "withdrawal-refund": return "Withdrawal refused, coins returned";
            case "purchase": return string.IsNullOrEmpty(t.ItemId) ? "Shop purchase" : $"Shop: {t.ItemId}";
            case "entry": return "Room entry fee";
            case "payout": return "Match payout";
            case "refund": return "Entry fee refunded";
            case "forfeit": return "Entry fee forfeited";
            case "admin": return t.AmountCents >= 0 ? "Added by support" : "Taken by support";
            default: return string.IsNullOrEmpty(t.Kind) ? "Coins moved" : t.Kind;
        }
    }

    // ================= Match history =================

    // A row: the side's icon in a soft square, the room and when, how it went (a chip), the points
    // and the coins it brought (the entry taken off).
    private void BuildMatches()
    {
        Clear(matchList);
        status.text = recordsProblem ?? (!recordsLoaded ? "Loading..." : records.Length == 0 ? "No matches yet. Matches in rooms with an entry fee are recorded here." : "");
        status.gameObject.SetActive(status.text.Length > 0);

        foreach (MetaMatchRecord r in records)
        {
            RectTransform row = Row(matchList, "Record");
            Badge(row, r.Role == Role.Hunter ? "ScanEye" : "PaintRoller");
            string when = DateTimeOffset.FromUnixTimeSeconds(r.UnixSeconds).ToLocalTime().ToString("MMM d \u00b7 HH:mm", CultureInfo.InvariantCulture);
            TwoLines(row, RoomTitle(r.RoomId), $"{when} \u00b7 {L.T(r.Role == Role.Hunter ? "Seeker" : "Hider")}", 0f, 0.4f);

            bool won = r.TeamResult == TeamResult.Won;
            bool lost = r.TeamResult == TeamResult.Lost;
            RectTransform chip = Paintbox.Tag(row, won ? "Won" : lost ? "Lost" : StatusText(r.Status),
                                              won ? Paintbox.Wash : Paintbox.Card, won ? Paintbox.WashInk : Paintbox.Muted, won ? "Trophy" : "Flag", 22f);
            chip.anchorMin = chip.anchorMax = chip.pivot = new Vector2(0.4f, 0.5f);
            chip.pivot = new Vector2(0f, 0.5f);
            chip.anchoredPosition = new Vector2(20f, 0f);

            string detail = string.IsNullOrEmpty(r.Explanation) ? "" : r.Explanation;
            PaintboxPage.Cell(row, detail, 0.56f, 0.74f, 21f, Paintbox.Muted, Paintbox.Weight.Regular, TextAnchor.MiddleLeft);
            PaintboxPage.Cell(row, $"{r.PointsHundredths / 100f:0.00}", 0.74f, 0.86f, 26f, Paintbox.Ink, Paintbox.Weight.Regular, TextAnchor.MiddleRight);
            long net = r.PayoutCents - r.EntryCents;
            string coins = r.EntryCents == 0 && r.PayoutCents == 0 ? L.T("free") : (net >= 0 ? "+" : "\u2212") + CoinBalance.Format(Math.Abs(net));
            PaintboxPage.Cell(row, coins, 0.86f, 1f, 28f, net > 0 ? Paintbox.Good : net < 0 ? Paintbox.Bad : Paintbox.Muted, Paintbox.Weight.Bold, TextAnchor.MiddleRight);
        }
    }

    // ---- Table pieces ----

    private static RectTransform Row(RectTransform list, string name)
    {
        RectTransform row = Paintbox.NewRect(list, name);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 84f;
        Image rule = Paintbox.Image(row, "Rule", null, Paintbox.Line);
        rule.rectTransform.anchorMin = new Vector2(0f, 0f);
        rule.rectTransform.anchorMax = new Vector2(1f, 0f);
        rule.rectTransform.offsetMin = Vector2.zero;
        rule.rectTransform.offsetMax = new Vector2(0f, 2f);
        return row;
    }

    private static void Badge(RectTransform row, string icon)
    {
        Image square = Paintbox.Rounded(row, "Badge", Paintbox.Wash, 14f);
        Paintbox.Pin(square.rectTransform, new Vector2(0f, 0.5f), new Vector2(20f, 0f), new Vector2(56f, 56f));
        square.rectTransform.pivot = new Vector2(0f, 0.5f);
        Paintbox.IconImage(square.rectTransform, icon, 32f, Paintbox.WashInk);
    }

    private static void TwoLines(RectTransform row, string top, string bottom, float from, float to)
    {
        Text a = Paintbox.Label(row, top, 28f, Paintbox.Ink, Paintbox.Weight.Bold);
        Paintbox.Fit(a, 16);
        a.rectTransform.anchorMin = new Vector2(from, 0.5f);
        a.rectTransform.anchorMax = new Vector2(to, 1f);
        a.rectTransform.offsetMin = new Vector2(92f, -2f);
        a.rectTransform.offsetMax = new Vector2(-10f, -8f);
        a.alignment = TextAnchor.LowerLeft;
        Text b = Paintbox.Label(row, bottom, 21f, Paintbox.Muted);
        Paintbox.Fit(b, 14);
        b.rectTransform.anchorMin = new Vector2(from, 0f);
        b.rectTransform.anchorMax = new Vector2(to, 0.5f);
        b.rectTransform.offsetMin = new Vector2(92f, 8f);
        b.rectTransform.offsetMax = new Vector2(-10f, -2f);
        b.alignment = TextAnchor.UpperLeft;
    }

    private static string Outcome(MetaMatchRecord r)
    {
        switch (r.TeamResult)
        {
            case TeamResult.Won: return $"<color={Good}>WON</color>";
            case TeamResult.Lost: return $"<color={Bad}>LOST</color>";
            default: return StatusText(r.Status).ToUpperInvariant();
        }
    }

    private static string StatusText(MatchStatus status)
    {
        switch (status)
        {
            case MatchStatus.Completed: return "Completed";
            case MatchStatus.Reconnected: return "Completed after a reconnect";
            case MatchStatus.DisconnectExpired: return "Disconnected";
            case MatchStatus.Quit: return "Left";
            case MatchStatus.IdleQuit: return "Removed for idling";
            case MatchStatus.CaughtThenLeft: return "Left after being caught";
            case MatchStatus.RefundedPreStart: return "Refunded";
            case MatchStatus.VoidRefund: return "Cancelled, refunded";
            case MatchStatus.VoidForfeit: return "Cancelled, entry forfeited";
            case MatchStatus.HeldForReview: return "Under review";
            default: return status.ToString();
        }
    }

    private static string RoomTitle(string id)
    {
        if (string.IsNullOrEmpty(id)) return "a room";
        string spaced = id.Replace('-', ' ');
        return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1);
    }

    // ================= UI =================

    private void BuildUI()
    {
        var root = new GameObject("OnlineHistoryUI");
        root.transform.SetParent(transform, false);
        // Over the wallet screen (60), which opens it too.
        Transform canvas = OnlineUI.CreateCanvas("OnlineHistoryCanvas", root.transform, 62).transform;

        PaintboxPage page = PaintboxPage.Build(canvas, "HistoryScreen", "Multiplayer", Close,
                                               PaintboxPage.Top.Balance | PaintboxPage.Top.Shop | PaintboxPage.Top.Settings, HubTab.History);
        screen = page.Root.gameObject;

        tabsRow = Paintbox.TopBand(Paintbox.NewRect(page.Content, "SubTabs"), 0f, 84f);
        HorizontalLayoutGroup h = tabsRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 14f;
        h.padding = new RectOffset(0, 0, 4, 10);
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        Image rule = Paintbox.Divider(page.Content);
        Paintbox.TopBand(rule.rectTransform, 92f, 3f);

        matchTable = Table(page.Content, "MatchTable", new[]
        {
            ("MATCH", 0f, 0.4f, TextAnchor.MiddleLeft), ("RESULT", 0.4f, 0.56f, TextAnchor.MiddleLeft), ("NOTE", 0.56f, 0.74f, TextAnchor.MiddleLeft),
            ("POINTS", 0.74f, 0.86f, TextAnchor.MiddleRight), ("COINS", 0.86f, 1f, TextAnchor.MiddleRight),
        }, "Records", out matchList);
        walletTable = Table(page.Content, "WalletTable", new[]
        {
            ("TRANSACTION", 0f, 0.46f, TextAnchor.MiddleLeft), ("TYPE", 0.46f, 0.68f, TextAnchor.MiddleLeft),
            ("CHANGE", 0.68f, 0.84f, TextAnchor.MiddleRight), ("BALANCE", 0.84f, 1f, TextAnchor.MiddleRight),
        }, "Lines", out walletList);

        status = Paintbox.Label(page.Content, "", 28f, Paintbox.Muted, Paintbox.Weight.Regular, TextAnchor.MiddleCenter);
        Paintbox.Pin(status.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -40f), new Vector2(1200f, 120f));
        DrawSubTabs();
    }

    // A table: a panel with its head and a scrolling list of rows.
    private static RectTransform Table(RectTransform content, string name, (string, float, float, TextAnchor)[] columns, string listName, out RectTransform list)
    {
        PaintboxFrame panel = Paintbox.Panel(content, name, 30f);
        Paintbox.Fill(panel.Root, 0f, 0f, 108f, 0f);
        RectTransform head = PaintboxPage.TableHead(panel.Body, columns);
        Paintbox.TopBand(head, 4f, 54f, 4f, 4f);

        RectTransform view = Paintbox.Fill(Paintbox.NewRect(panel.Body, "View"), 8f, 8f, 60f, 8f);
        ScrollRect scroll = view.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.scrollSensitivity = 40f;
        view.gameObject.AddComponent<RectMask2D>();
        view.gameObject.AddComponent<Image>().color = Color.clear;
        list = Paintbox.NewRect(view, listName);
        list.anchorMin = new Vector2(0f, 1f);
        list.anchorMax = new Vector2(1f, 1f);
        list.pivot = new Vector2(0.5f, 1f);
        list.offsetMin = list.offsetMax = Vector2.zero;
        VerticalLayoutGroup rows = list.gameObject.AddComponent<VerticalLayoutGroup>();
        rows.childControlWidth = rows.childControlHeight = true;
        rows.childForceExpandWidth = true;
        rows.childForceExpandHeight = false;
        list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = list;
        scroll.viewport = view;
        return panel.Root;
    }
}
