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
// Every number is the server's; this only lays them out, in the same modal style as the rest of the
// online screens (OnlineUI). Opened from the play setup's HISTORY and from the wallet screen.
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
    private int matchPage, walletPage;
    private int recordsRetries, linesRetries;
    private float retryRecordsAt, retryLinesAt, refreshLinesAt;

    private GameObject screen;
    private RectTransform walletList, matchList;
    private Text status, pageLabel;
    private Button prevPage, nextPage;
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
        version++;

        records = new MetaMatchRecord[0];
        lines = new MetaTransaction[0];
        recordsLoaded = linesLoaded = false;
        recordsProblem = linesProblem = null;
        matchPage = walletPage = 0;
        recordsRetries = linesRetries = 0;
        retryRecordsAt = retryLinesAt = refreshLinesAt = 0f;

        SelectTab(tab);
        AskLines();
        AskRecords();
    }

    private void Close()
    {
        AudioManager.ButtonTap();
        screen.SetActive(false);
        version++;
        Action closed = onClose;
        onClose = null;
        closed?.Invoke();
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
        for (int i = 0; i < tabButtons.Length; i++)
            tabButtons[i].targetGraphic.color = i == (int)selected ? OnlineUI.Blue : OnlineUI.Gray;

        walletList.gameObject.SetActive(selected == Tab.Wallet);
        matchList.gameObject.SetActive(selected == Tab.Matches);
        BuildTab();
    }

    private void BuildTab()
    {
        if (tab == Tab.Wallet) BuildWallet();
        else BuildMatches();
    }

    private void TurnPage(int delta)
    {
        AudioManager.ButtonTap();
        if (tab == Tab.Wallet) walletPage += delta;
        else matchPage += delta;
        BuildTab();
    }

    // The arrows and "2/3" under the list; one page needs none. Returns the page clamped to the pages.
    private int Pager(int count, int perPage, int page)
    {
        int pages = Mathf.Max(1, (count + perPage - 1) / perPage);
        page = Mathf.Clamp(page, 0, pages - 1);
        prevPage.interactable = page > 0;
        nextPage.interactable = page < pages - 1;
        prevPage.gameObject.SetActive(pages > 1);
        nextPage.gameObject.SetActive(pages > 1);
        pageLabel.text = pages > 1 ? $"{page + 1}/{pages}" : "";
        return page;
    }

    private static void Clear(RectTransform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--) Destroy(parent.GetChild(i).gameObject);
    }

    // ================= Wallet history =================

    private void BuildWallet()
    {
        Clear(walletList);
        walletPage = Pager(lines.Length, WalletRowsPerPage, walletPage);
        status.text = linesProblem ?? (!linesLoaded ? "Loading..." : lines.Length == 0 ? "Nothing has moved your coins yet." : "");

        for (int i = walletPage * WalletRowsPerPage; i < Mathf.Min(lines.Length, (walletPage + 1) * WalletRowsPerPage); i++)
        {
            int row = i - walletPage * WalletRowsPerPage;
            MetaTransaction t = lines[i];

            Image bg = OnlineUI.NewImage("Line", walletList, new Color(1f, 1f, 1f, 0.06f));
            OnlineUI.Place(bg.rectTransform, new Vector2(0f, 200f - row * 100f), new Vector2(1000f, 88f));

            string when = DateTimeOffset.FromUnixTimeSeconds(t.UnixSeconds).ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
            Text what = OnlineUI.NewLabel(bg.transform, $"<b>{KindText(t)}</b>\n<size=24><color=#674E65>{when}  -  balance {CoinBalance.Format(t.BalanceAfterCents)}</color></size>",
                                          30, new Vector2(-140f, 0f), new Vector2(680f, 84f), TextAnchor.MiddleLeft);
            what.horizontalOverflow = HorizontalWrapMode.Overflow;

            bool plus = t.AmountCents >= 0;
            OnlineUI.NewLabel(bg.transform, $"<color={(plus ? Good : Bad)}>{(plus ? "+" : "-")}{CoinBalance.Format(Math.Abs(t.AmountCents))}</color>",
                              34, new Vector2(350f, 0f), new Vector2(260f, 84f), TextAnchor.MiddleRight, true);
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

    private void BuildMatches()
    {
        Clear(matchList);
        matchPage = Pager(records.Length, MatchRowsPerPage, matchPage);
        status.text = recordsProblem ?? (!recordsLoaded ? "Loading..." : records.Length == 0 ? "No matches yet. Matches in rooms with an entry fee are recorded here." : "");

        for (int i = matchPage * MatchRowsPerPage; i < Mathf.Min(records.Length, (matchPage + 1) * MatchRowsPerPage); i++)
        {
            int row = i - matchPage * MatchRowsPerPage;
            MetaMatchRecord r = records[i];

            Image bg = OnlineUI.NewImage("Record", matchList, new Color(1f, 1f, 1f, 0.06f));
            OnlineUI.Place(bg.rectTransform, new Vector2(0f, 195f - row * 128f), new Vector2(1000f, 118f));

            long net = r.PayoutCents - r.EntryCents;
            string netText = r.EntryCents == 0 && r.PayoutCents == 0
                ? "free"
                : $"<color={(net >= 0 ? Good : Bad)}>{(net >= 0 ? "+" : "-")}{OnlineUI.CoinsText(Math.Abs(net))}</color>";

            string headline = $"<b>{Outcome(r)}</b>  -  {(r.Role == Role.Hunter ? "Seeker" : "Hider")} in {RoomTitle(r.RoomId)}" +
                              $"  -  {r.PointsHundredths / 100f:0.00} pts  -  {netText}";
            string when = DateTimeOffset.FromUnixTimeSeconds(r.UnixSeconds).ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
            string detail = string.IsNullOrEmpty(r.Explanation) ? StatusText(r.Status) : r.Explanation;

            Text top = OnlineUI.NewLabel(bg.transform, headline, 30, new Vector2(0f, 26f), new Vector2(960f, 50f), TextAnchor.MiddleLeft);
            top.horizontalOverflow = HorizontalWrapMode.Overflow;
            Text bottom = OnlineUI.NewLabel(bg.transform, $"{when}  -  {detail}", 24, new Vector2(0f, -26f), new Vector2(960f, 56f), TextAnchor.MiddleLeft);
            bottom.color = OnlineUI.Muted;
        }
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

        RectTransform p = OnlineUI.Modal(canvas, "HistoryScreen", new Vector2(1100f, 960f));
        screen = p.parent.gameObject;
        OnlineUI.NewLabel(p, "HISTORY", 56, new Vector2(0f, 420f), new Vector2(1000f, 80f), TextAnchor.MiddleCenter, true);

        string[] tabNames = { "WALLET HISTORY", "MATCH HISTORY" };
        for (int i = 0; i < tabNames.Length; i++)
        {
            var which = (Tab)i;
            tabButtons[i] = OnlineUI.NewButton(p, tabNames[i], new Vector2(-255f + i * 510f, 330f), new Vector2(490f, 78f), OnlineUI.Gray, () =>
            {
                AudioManager.ButtonTap();
                SelectTab(which);
            });
            tabButtons[i].name = i == 0 ? "WalletHistoryTab" : "MatchHistoryTab";
        }

        walletList = NewList(p, "Lines");
        matchList = NewList(p, "Records");

        prevPage = OnlineUI.NewButton(p, "<", new Vector2(-120f, -300f), new Vector2(90f, 70f), OnlineUI.Gray, () => TurnPage(-1));
        nextPage = OnlineUI.NewButton(p, ">", new Vector2(120f, -300f), new Vector2(90f, 70f), OnlineUI.Gray, () => TurnPage(1));
        pageLabel = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, -300f), new Vector2(140f, 60f));

        status = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, 40f), new Vector2(960f, 80f));
        status.color = OnlineUI.Muted;
        OnlineUI.NewButton(p, "CLOSE", new Vector2(0f, -405f), new Vector2(300f, 74f), OnlineUI.Gray, Close);
    }

    private static RectTransform NewList(RectTransform parent, string name)
    {
        var list = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        list.SetParent(parent, false);
        OnlineUI.Place(list, new Vector2(0f, 20f), new Vector2(1040f, 540f));
        return list;
    }
}
