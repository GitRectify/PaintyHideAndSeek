using System;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Multiple Play's match history (MetaClient.MatchHistory, reward rule §13), opened from the room
// screen: the player's own records, newest first — when, which room, which side, how it went, the
// points, what the entry cost and what came back, and the server's one-line explanation. Every
// number is the server's; this only lays them out, in the same modal style as the rest of the
// online lobby (OnlineUI). Single Play has no history screen to reuse. Adapted from the SDK
// sample's OnlineHistory.
public class OnlineHistory : MonoBehaviour
{
    private const int RecordsToFetch = 20;
    private const int RowsPerPage = 4;

    private static MetaClient Meta => MetaClient.Instance;

    private Action onClose;
    private MetaMatchRecord[] records = new MetaMatchRecord[0];
    private int page;

    private GameObject screen;
    private RectTransform list;
    private Text status, pageLabel;
    private Button prevPage, nextPage;

    private void Awake()
    {
        BuildUI();
        screen.SetActive(false);
    }

    /// <summary>Opens the history over whatever is showing; <paramref name="closed"/> after Close.</summary>
    public static void Show(Transform owner, Action closed)
    {
        OnlineHistory history = FindAnyObjectByType<OnlineHistory>();
        if (history == null) history = owner.gameObject.AddComponent<OnlineHistory>();
        history.Open(closed);
    }

    public void Open(Action closed)
    {
        if (Meta == null) { closed?.Invoke(); return; }

        onClose = closed;
        screen.SetActive(true);
        records = new MetaMatchRecord[0];
        page = 0;
        BuildRows();
        status.text = "Loading...";

        Meta.MatchHistory(RecordsToFetch, reply =>
        {
            if (!screen.activeSelf) return;

            if (reply.TimedOut || reply.Error != MetaError.None)
            {
                status.text = reply.TimedOut ? "The server did not answer. Try again." : $"The server could not answer ({reply.Error}).";
                return;
            }

            records = reply.Records ?? new MetaMatchRecord[0];
            status.text = records.Length == 0 ? "No matches yet. Matches in rooms with an entry fee are recorded here." : "";
            BuildRows();
        });
    }

    private void Close()
    {
        AudioManager.ButtonTap();
        screen.SetActive(false);
        Action closed = onClose;
        onClose = null;
        closed?.Invoke();
    }

    private void TurnPage(int delta)
    {
        page += delta;
        BuildRows();
    }

    private void BuildRows()
    {
        for (int i = list.childCount - 1; i >= 0; i--) Destroy(list.GetChild(i).gameObject);

        int pages = Mathf.Max(1, (records.Length + RowsPerPage - 1) / RowsPerPage);
        page = Mathf.Clamp(page, 0, pages - 1);
        prevPage.interactable = page > 0;
        nextPage.interactable = page < pages - 1;
        // One page needs no arrows.
        prevPage.gameObject.SetActive(pages > 1);
        nextPage.gameObject.SetActive(pages > 1);
        pageLabel.text = records.Length > RowsPerPage ? $"{page + 1}/{pages}" : "";

        for (int i = page * RowsPerPage; i < Mathf.Min(records.Length, (page + 1) * RowsPerPage); i++)
        {
            int row = i - page * RowsPerPage;
            MetaMatchRecord r = records[i];

            Image bg = OnlineUI.NewImage("Record", list, new Color(1f, 1f, 1f, 0.06f));
            OnlineUI.Place(bg.rectTransform, new Vector2(0f, 195f - row * 130f), new Vector2(1000f, 120f));

            long net = r.PayoutCents - r.EntryCents;
            string netText = r.EntryCents == 0 && r.PayoutCents == 0
                ? "free"
                : $"<color={(net >= 0 ? "#7CFC9A" : "#FF8A80")}>{(net >= 0 ? "+" : "-")}{OnlineUI.CoinsText(Math.Abs(net))}</color>";

            string headline = $"<b>{Outcome(r)}</b>  -  {(r.Role == Role.Hunter ? "Seeker" : "Hider")} in {RoomTitle(r.RoomId)}" +
                              $"  -  {r.PointsHundredths / 100f:0.00} pts  -  {netText}";
            string when = DateTimeOffset.FromUnixTimeSeconds(r.UnixSeconds).ToLocalTime().ToString("MMM d, HH:mm");
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
            case TeamResult.Won: return "<color=#7CFC9A>WON</color>";
            case TeamResult.Lost: return "<color=#FF8A80>LOST</color>";
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

    private void BuildUI()
    {
        var root = new GameObject("OnlineHistoryUI");
        root.transform.SetParent(transform, false);
        Transform canvas = OnlineUI.CreateCanvas("OnlineHistoryCanvas", root.transform, 55).transform;

        RectTransform p = OnlineUI.Modal(canvas, "HistoryScreen", new Vector2(1100f, 900f));
        screen = p.parent.gameObject;
        OnlineUI.NewLabel(p, "MATCH HISTORY", 56, new Vector2(0f, 385f), new Vector2(1000f, 80f), TextAnchor.MiddleCenter, true);

        list = (RectTransform)new GameObject("Records", typeof(RectTransform)).transform;
        list.SetParent(p, false);
        OnlineUI.Place(list, new Vector2(0f, 40f), new Vector2(1040f, 560f));

        prevPage = OnlineUI.NewButton(p, "<", new Vector2(-120f, -290f), new Vector2(90f, 70f), OnlineUI.Gray, () => TurnPage(-1));
        nextPage = OnlineUI.NewButton(p, ">", new Vector2(120f, -290f), new Vector2(90f, 70f), OnlineUI.Gray, () => TurnPage(1));
        pageLabel = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, -290f), new Vector2(140f, 60f));

        status = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, -345f), new Vector2(1040f, 50f));
        status.color = OnlineUI.Warning;
        OnlineUI.NewButton(p, "CLOSE", new Vector2(0f, -405f), new Vector2(300f, 70f), OnlineUI.Gray, Close);
    }
}
