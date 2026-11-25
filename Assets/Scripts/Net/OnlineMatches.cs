using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// The player's matches, on every screen (2026-11-13):
//   * The coin balance, top right, on any screen that does not show one of its own (CoinBalance:
//     the server's balance, updated by every payout, purchase and top-up).
//   * Matches in progress: an Exit keeps the seat (the server's away seat), so a player can have
//     several. Outside a round a small chip says how many and opens the list; the list rejoins any
//     of them (OnlineLobby.RejoinMatch) and shows the ones that ended while the player was away.
//   * Notifications: a match that ended while away gets one short toast (won or lost, coins); a
//     tap opens its full results. Several at once are stacked, two at most, each for a few seconds;
//     in a round they sit under the round's banner and never take a tap meant for the game.
//   * Results: every player's rank, score and coins (S2C.MatchResults), at the end of a match
//     (OnlineRound) and for any listed match.
// The list comes from the server (ListMyMatches), asked every few seconds while signed in.
public class OnlineMatches : MonoBehaviour
{
    private const float PollSeconds = 5f;
    private const float ToastSeconds = 6f;
    private const string SeenKey = "Matches.SeenEnded.";     // + the profile id
    private const long SameEndSeconds = 60;

    private static OnlineMatches instance;
    private static MecchaNetClient Net => MecchaNetClient.Instance;

    private MecchaNetClient hooked;
    private MyMatchEntry[] matches = Array.Empty<MyMatchEntry>();
    private float matchesAt;
    private float nextPollAt;
    private float nextLayoutCheckAt;
    private bool toldAwayThisSession;
    // Ended matches already announced to this profile, as (match id, when it ended in Unix seconds).
    // A server numbers its matches from 1 each time it starts, so the id alone would hide a later
    // match that got the same id.
    private readonly List<(uint Id, long EndedAt)> seenEnded = new List<(uint, long)>();
    private string seenFor;
    private uint wantResultsFor;
    private bool balanceDirty;

    // UI
    private Canvas overlayCanvas, screenCanvas;
    private GameObject coinPill, chip, listScreen, resultsScreen;
    private Text chipText, listTitle, listActive, resultsTitle, resultsHeading, resultsExtraHint, resultsFootTitle;
    private GameObject listEmpty, listHead;
    private RectTransform listRows, resultsRows;
    private Button resultsNext, resultsHome, resultsClose, resultsExtra;
    private Action onResultsNext, onResultsHome, onResultsClose, onResultsExtra;

    /// <summary>Matches this player left that are still running (rejoinable).</summary>
    public static int AwayCount
    {
        get
        {
            if (instance == null) return 0;
            int n = 0;
            foreach (MyMatchEntry m in instance.matches) if (m.State == MyMatchState.Away) n++;
            return n;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (instance != null) return;
        var go = new GameObject("OnlineMatches");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<OnlineMatches>();
    }

    private void Awake()
    {
        BuildUI();
    }

    // ================= Public =================

    // ================= Multiple Play only =================
    //
    // Single Play runs without the server (2026-11-13): no coin balance, no Shop button, no match
    // notices, no polling. What needs the server shows only while OnlineLobby.InMultiplayer.

    private static readonly List<GameObject> multiplayerOnly = new List<GameObject>();

    /// <summary>Shows this object only in Multiple Play (a Shop button made from an Ad button).</summary>
    public static void MultiplayerOnly(GameObject go)
    {
        if (go != null && !multiplayerOnly.Contains(go)) multiplayerOnly.Add(go);
    }

    private static GameObject homeCanvas;
    private static float nextHomeLook;

    // Home (the hub, before either mode) keeps its own coin pill and Shop button. This overlay is
    // made while the Loading scene is up, so Home is looked for until it is there (once a second).
    private static bool HomeShowing
    {
        get
        {
            if (homeCanvas == null && Time.unscaledTime >= nextHomeLook)
            {
                nextHomeLook = Time.unscaledTime + 1f;
                homeCanvas = SceneObject("HomeCanvas");
            }
            return homeCanvas != null && homeCanvas.activeInHierarchy;
        }
    }

    /// <summary>Single Play's screens: neither Home nor any part of Multiple Play.</summary>
    public static bool InSinglePlay => !OnlineLobby.InMultiplayer && !HomeShowing;

    private static GameObject SceneObject(string name)
    {
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t.name == name && t.gameObject.scene.IsValid()) return t.gameObject;
        return null;
    }

    private void UpdateModeObjects()
    {
        bool online = OnlineLobby.InMultiplayer;
        for (int i = multiplayerOnly.Count - 1; i >= 0; i--)
        {
            GameObject go = multiplayerOnly[i];
            if (go == null) { multiplayerOnly.RemoveAt(i); continue; }
            if (go.activeSelf != online) go.SetActive(online);
        }

        // The side screen's own coin panel is another design: Multiple Play shows design B's pill
        // instead (coinPill), and Single Play no balance at all.
        if (roleCoinPanel == null && Time.unscaledTime >= nextPillLook)
        {
            nextPillLook = Time.unscaledTime + 1f;
            GameObject role = SceneObject("RoleSelectCanvas");
            roleCoinPanel = role != null ? role.transform.Find("CoinPanel")?.gameObject : null;
        }
        if (roleCoinPanel != null && roleCoinPanel.activeSelf) roleCoinPanel.SetActive(false);
    }

    private GameObject roleCoinPanel;
    private float nextPillLook;

    /// <summary>A short notice in the notice slot (top right), gone by itself; a tap dismisses it.</summary>
    public static void Notify(string text) => Notices.Show(NoticeKind.Info, text, 4f);

    /// <summary>Opens the list of the player's matches (in progress and ended).</summary>
    public static void ShowList()
    {
        if (instance == null) Create();
        instance.OpenList();
    }

    /// <summary>A match's full results, from the list or a notice: Close only.</summary>
    public static void ShowResults(MatchResultTable table)
    {
        if (instance == null) Create();
        instance.OpenResults(table, new MatchEndButtons());
    }

    /// <summary>The same, from the match history (2026-11-25): <paramref name="closed"/> runs on its Close.</summary>
    public static void ShowResults(MatchResultTable table, Action closed)
    {
        if (instance == null) Create();
        instance.OpenResults(table, new MatchEndButtons { Close = closed });
    }

    /// <summary>What the results offer at the end of the player's own match (OnlineRound).</summary>
    public struct MatchEndButtons
    {
        public string Heading;          // "You won!" / "You lost.", over the table
        public bool Won;
        public Action Next, Home;       // leave the match: to the room list, or Home
        public Action Close;            // the results put away, the match's scene under them
        public string ExtraLabel;       // a fourth button (a seeker's Next Player), or null
        public Action Extra;            // its tap; the screen stays up (the caller decides)
    }

    /// <summary>
    /// The end of the player's own match (2026-11-20): the only results screen there is, with Next,
    /// Home and Close, and a seeker's Next Player when the hiders won.
    /// </summary>
    public static void ShowMatchEnd(MatchResultTable table, MatchEndButtons buttons)
    {
        if (instance == null) Create();
        instance.OpenResults(table, buttons);
    }

    /// <summary>The fourth button's text, as its price changes (null hides it).</summary>
    public static void SetExtraLabel(string label)
    {
        if (instance == null || instance.resultsExtra == null) return;
        bool show = label != null && instance.onResultsExtra != null;
        if (instance.resultsExtra.gameObject.activeSelf != show) instance.LayoutResultButtons(show);
        if (show) OnlineUI.SetButtonText(instance.resultsExtra, label);
    }

    /// <summary>Whether the results screen is up.</summary>
    public static bool ResultsOpen => instance != null && instance.resultsScreen != null && instance.resultsScreen.activeSelf;

    /// <summary>Closes the results screen (the round ending, the player leaving), without its Close.</summary>
    public static void HideResults()
    {
        if (instance != null && instance.resultsScreen != null) instance.resultsScreen.SetActive(false);
    }

    // ================= Server =================

    private void Update()
    {
        Hook();
        UpdateModeObjects();
        bool quiet = InSinglePlay;

        // Nothing is asked of the server during Single Play.
        if (!quiet && Net != null && Net.IsConnected && !string.IsNullOrEmpty(Net.SessionToken) && Time.unscaledTime >= nextPollAt)
        {
            nextPollAt = Time.unscaledTime + (listScreen.activeSelf ? 2f : PollSeconds);
            Net.RequestMyMatches();
        }

        if (Time.unscaledTime >= nextLayoutCheckAt)
        {
            nextLayoutCheckAt = Time.unscaledTime + 0.25f;
            UpdateCoinPill();
            UpdateChip();
        }

        if (listScreen.activeSelf) RefreshListTimes();
    }

    private void Hook()
    {
        MecchaNetClient net = Net;
        if (net == hooked) return;
        if (hooked != null)
        {
            hooked.OnMyMatches -= HandleMyMatches;
            hooked.OnMatchResults -= HandleMatchResults;
        }
        hooked = net;
        if (net != null)
        {
            net.OnMyMatches += HandleMyMatches;
            net.OnMatchResults += HandleMatchResults;
        }
    }

    private void HandleMyMatches(MyMatchEntry[] list)
    {
        matches = list ?? Array.Empty<MyMatchEntry>();
        matchesAt = Time.unscaledTime;

        // Single Play hears nothing of online matches: the notices wait until it is over.
        if (InSinglePlay)
        {
            if (listScreen.activeSelf) RefreshList();
            return;
        }

        // Once a session: matches still going from before (left, or the app was closed).
        int away = AwayCount;
        if (away > 0 && !toldAwayThisSession && !OnlineRound.Active)
        {
            toldAwayThisSession = true;
            Toast(away == 1 ? "You have a match in progress. Tap to rejoin it." : $"You have {away} matches in progress. Tap to see them.", 0, true);
        }

        // Each match that ended while the player was away: once.
        LoadSeen();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool announced = false;
        foreach (MyMatchEntry m in matches)
        {
            if (m.State != MyMatchState.Ended) continue;
            long endedAt = now - m.EndedSecondsAgo;
            if (Seen(m.MatchId, endedAt)) continue;
            seenEnded.Add((m.MatchId, endedAt));
            announced = true;
            balanceDirty = true;
            string coins = m.CoinCents > 0 ? $" <color=#2C684C>+{OnlineUI.CoinsText(m.CoinCents)}</color>" : "";
            Toast($"Your match in {RoomTitle(m.RoomId)} ended: {(m.Won ? "you won" : "you lost")}.{coins} Tap for the results.", m.MatchId, false);
        }
        if (announced) SaveSeen();

        // The payout is in the server's balance: fetch it, so every coin label shows it.
        if (balanceDirty)
        {
            balanceDirty = false;
            ShopInventory.Refresh(null);
        }

        if (listScreen.activeSelf) RefreshList();
    }

    private void HandleMatchResults(MatchResultTable table)
    {
        if (wantResultsFor != 0 && table.MatchId == wantResultsFor)
        {
            wantResultsFor = 0;
            OpenResults(table, new MatchEndButtons());
        }
    }

    // The announced list of whoever is signed in now (one device, several accounts).
    private void LoadSeen()
    {
        string profile = Net != null ? Net.ProfileId ?? "" : "";
        if (profile == seenFor) return;
        seenFor = profile;
        seenEnded.Clear();
        foreach (string item in PlayerPrefs.GetString(SeenKey + profile, "").Split(','))
        {
            string[] parts = item.Split(':');
            if (parts.Length == 2 &&
                uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id) &&
                long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long endedAt))
                seenEnded.Add((id, endedAt));
        }
    }

    // The same match: same id, and an end time that agrees to within the list's own rounding.
    private bool Seen(uint id, long endedAt)
    {
        foreach ((uint Id, long EndedAt) s in seenEnded)
            if (s.Id == id && Math.Abs(s.EndedAt - endedAt) <= SameEndSeconds) return true;
        return false;
    }

    private void SaveSeen()
    {
        if (seenEnded.Count > 60) seenEnded.RemoveRange(0, seenEnded.Count - 60);
        var keep = new List<string>();
        foreach ((uint Id, long EndedAt) s in seenEnded)
            keep.Add(s.Id.ToString(CultureInfo.InvariantCulture) + ":" + s.EndedAt.ToString(CultureInfo.InvariantCulture));
        PlayerPrefs.SetString(SeenKey + seenFor, string.Join(",", keep));
    }

    private void AskResults(uint matchId)
    {
        if (Net == null || !Net.IsConnected) return;
        wantResultsFor = matchId;
        Net.RequestMatchResults(matchId);

        // Looked at: a match that ended while away leaves the list now (2026-11-13). The server
        // drops it on this request too and sends the list again.
        matches = Array.FindAll(matches, m => !(m.State == MyMatchState.Ended && m.MatchId == matchId));
        if (listScreen.activeSelf) RefreshList();
    }

    // ================= Coin balance everywhere =================

    // The coin pill, the same on every Multiple Play screen (2026-11-13; design B's since
    // 2026-11-24): shown there unless Home or the shop shows the balance itself, never over the
    // loading screen, and never in Single Play, which has no coins.

    private void UpdateCoinPill()
    {
        // Home and the shop show the balance themselves; in a round it is in the round menu.
        bool other = HomeShowing || ShopScreen.IsOpen || PlaySetupUI.IsOpen || MatchmakingUI.PageOpen || PaintboxHud.InRound;
        GameObject loading = GameObject.Find("LoadingCanvas");
        bool show = OnlineLobby.InMultiplayer && !other && (loading == null || !loading.activeInHierarchy);
        if (coinPill.activeSelf != show) coinPill.SetActive(show);
    }

    // ================= The chip: matches in progress =================

    private void UpdateChip()
    {
        int away = AwayCount;
        GameObject loading = GameObject.Find("LoadingCanvas");
        bool show = away > 0 && !OnlineRound.Active && !InSinglePlay && !listScreen.activeSelf && !resultsScreen.activeSelf && !PlaySetupUI.IsOpen &&
                    (loading == null || !loading.activeInHierarchy);
        if (chip.activeSelf != show) chip.SetActive(show);
        if (show) chipText.text = away == 1 ? "1 match in progress: VIEW" : $"{away} matches in progress: VIEW";
    }

    // ================= Notices =================

    // A match's news goes to the game's notices (Notices: top right, gone by itself); a tap opens
    // the results (or the list).
    private void Toast(string text, uint matchId, bool openList)
    {
        Action onTap = null;
        if (matchId != 0) onTap = () => AskResults(matchId);
        else if (openList) onTap = OpenList;
        Notices.Show(NoticeKind.Match, text, null, ToastSeconds, onTap);
    }

    // ================= The list =================

    private void OpenList()
    {
        AudioManager.ButtonTap();
        listScreen.SetActive(true);
        listScreen.transform.SetAsLastSibling();
        MultiplayerHub.Showing(HubTab.Matches, CloseList);
        if (Net != null && Net.IsConnected) Net.RequestMyMatches();
        RefreshList();
    }

    private void CloseList()
    {
        listScreen.SetActive(false);
        MultiplayerHub.Closed(HubTab.Matches);
    }

    // Design B's rows (2026-11-24): the map's picture, its name with the side and room, how the
    // match stands (a chip, the time left) and Rejoin, or Result once it has ended. None: an empty
    // state that leads back to a new match or to the history.
    private void RefreshList()
    {
        for (int i = listRows.childCount - 1; i >= 0; i--)
        {
            listRows.GetChild(i).gameObject.SetActive(false);
            Destroy(listRows.GetChild(i).gameObject);
        }

        var shown = new List<MyMatchEntry>();
        foreach (MyMatchEntry m in matches) if (m.State != MyMatchState.Playing) shown.Add(m);
        int active = 0;
        foreach (MyMatchEntry m in shown) if (m.State == MyMatchState.Away) active++;
        listActive.text = L.F(active == 1 ? "{0} active" : "{0} active", active);
        listActive.transform.parent.gameObject.SetActive(active > 0);
        listEmpty.SetActive(shown.Count == 0);
        listHead.SetActive(shown.Count > 0);
        listTitle.text = shown.Count == 0 ? "No active matches" : "Your matches";

        foreach (MyMatchEntry m in shown)
        {
            PaintboxFrame row = Paintbox.Frame(listRows, "Match_" + m.MatchId, Paintbox.Paper, 24f, Paintbox.Border, 0f, Paintbox.PanelLine);
            row.Root.gameObject.AddComponent<LayoutElement>().preferredHeight = 112f;
            Transform b = row.Body;

            Image thumb = Paintbox.Rounded(b, "Thumb", Paintbox.Card, 16f);
            Paintbox.Pin(thumb.rectTransform, new Vector2(0f, 0.5f), new Vector2(18f, 0f), new Vector2(116f, 76f));
            thumb.rectTransform.pivot = new Vector2(0f, 0.5f);
            Image mask = Paintbox.Rounded(thumb.rectTransform, "Frame", Color.white, 16f);
            Paintbox.Fill(mask.rectTransform);
            mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            Sprite picture = MapPicture(m.Map);
            Image pic = Paintbox.Image(mask.rectTransform, "Picture", picture, Color.white);
            Paintbox.Fill(pic.rectTransform);
            AspectRatioFitter fit = pic.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            if (picture != null) fit.aspectRatio = picture.rect.width / Mathf.Max(1f, picture.rect.height);

            string side = m.Role == Role.Hunter ? "Seeker" : "Hider";
            Text name = Paintbox.Label(b, MapTitle(m.Map), 32f, Paintbox.Ink, Paintbox.Weight.Bold);
            name.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            name.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            name.rectTransform.offsetMin = new Vector2(156f, -4f);
            name.rectTransform.offsetMax = new Vector2(0f, -10f);
            name.alignment = TextAnchor.LowerLeft;
            Text where = Paintbox.Label(b, L.T(side) + " \u00b7 " + L.T(RoomTitle(m.RoomId)), 23f, Paintbox.Muted);
            where.name = "Info";
            where.rectTransform.anchorMin = new Vector2(0f, 0f);
            where.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            where.rectTransform.offsetMin = new Vector2(156f, 10f);
            where.rectTransform.offsetMax = new Vector2(0f, -2f);
            where.alignment = TextAnchor.UpperLeft;

            bool ended = m.State == MyMatchState.Ended;
            string phase = ended ? (m.Won ? "Won" : "Finished") : m.Phase == MatchPhase.Hide ? "Hiding" : m.Phase == MatchPhase.Seek ? "Seeking" : "Starting";
            RectTransform chip = Paintbox.Tag(b, phase, ended ? Paintbox.Card : Paintbox.Wash, ended ? Paintbox.Muted : Paintbox.WashInk, ended ? "Flag" : "Radio", 22f);
            chip.anchorMin = chip.anchorMax = chip.pivot = new Vector2(1f, 0.5f);
            chip.anchoredPosition = new Vector2(-440f, 0f);

            Text time = Paintbox.Label(b, ended ? EndedLine(m) : "", 26f, Paintbox.Muted, Paintbox.Weight.Regular, TextAnchor.MiddleRight);
            time.name = "Timer";
            time.horizontalOverflow = HorizontalWrapMode.Overflow;
            Paintbox.Pin(time.rectTransform, new Vector2(1f, 0.5f), new Vector2(-270f, 0f), new Vector2(160f, 40f));
            time.rectTransform.pivot = new Vector2(1f, 0.5f);

            uint id = m.MatchId;
            Button action = ended
                ? Paintbox.Button(b, "Result", Paintbox.Tone.Paper, "Trophy", () => { AudioManager.ButtonTap(); AskResults(id); }, 28f)
                : Paintbox.Button(b, "Rejoin", Paintbox.Tone.Primary, "LogIn", () =>
                {
                    AudioManager.ButtonTap();
                    CloseList();
                    OnlineLobby.RejoinMatch(id);
                }, 28f);
            action.name = ended ? "Results" : "Rejoin";
            Paintbox.Pin((RectTransform)action.transform, new Vector2(1f, 0.5f), new Vector2(-20f, 2f), new Vector2(210f, 76f));
            ((RectTransform)action.transform).pivot = new Vector2(1f, 0.5f);
        }
        RefreshListTimes();
    }

    // Time left in an away match, counted on from when the server said it.
    private void RefreshListTimes()
    {
        for (int i = 0; i < listRows.childCount; i++)
        {
            Transform row = listRows.GetChild(i);
            if (!row.name.StartsWith("Match_", StringComparison.Ordinal)) continue;
            if (!uint.TryParse(row.name.Substring(6), out uint id)) continue;
            foreach (MyMatchEntry m in matches)
            {
                if (m.MatchId != id || m.State != MyMatchState.Away) continue;
                Text timer = row.Find("Body/Timer")?.GetComponent<Text>();
                int left = Mathf.Max(0, m.SecondsLeft - Mathf.FloorToInt(Time.unscaledTime - matchesAt));
                if (timer != null) timer.text = $"{left / 60:00}:{left % 60:00}";
            }
        }
    }

    // An ended match in a few words: its place and coins.
    private static string EndedLine(MyMatchEntry m) =>
        "#" + m.Rank + (m.CoinCents > 0 ? "  +" + CoinBalance.Format(m.CoinCents) : "");

    // The map's picture (the game's own thumbnail), by its room name.
    private static Sprite MapPicture(string map)
    {
        LevelManager levels = LevelManager.Instance;
        MapSelectPopup picker = MapSelectPopup.Instance;
        if (levels != null && picker != null)
            for (int i = 0; i < levels.Count; i++)
                if (levels.RoomNameAt(i) == map) return picker.ThumbnailAt(i);
        return Paintbox.Art("studio");
    }

    // ================= Results =================

    private void OpenResults(MatchResultTable table, MatchEndButtons buttons)
    {
        onResultsNext = buttons.Next;
        onResultsHome = buttons.Home;
        onResultsClose = buttons.Close;
        onResultsExtra = buttons.Extra;
        resultsNext.gameObject.SetActive(buttons.Next != null);
        resultsHome.gameObject.SetActive(buttons.Home != null);
        bool extra = buttons.Extra != null && buttons.ExtraLabel != null;
        if (extra) OnlineUI.SetButtonText(resultsExtra, buttons.ExtraLabel);
        LayoutResultButtons(extra);

        // The player's own match says how it went, in the heading (won in green).
        resultsHeading.text = string.IsNullOrEmpty(buttons.Heading) ? "Match results" : buttons.Heading;
        resultsHeading.color = string.IsNullOrEmpty(buttons.Heading) ? Paintbox.Ink
                             : buttons.Won ? Paintbox.Good : Paintbox.Bad;
        resultsClose.gameObject.SetActive(true);

        string outcome = table.Outcome == MatchOutcome.HidersWin ? "Hiders win"
                       : table.Outcome == MatchOutcome.SeekersWin ? "Seekers win" : "No winner";
        resultsTitle.text = $"{RoomTitle(table.RoomId)}  \u00b7  {MapTitle(table.Map)}";
        resultsFootTitle.text = outcome;

        // One ranking for everyone, whichever side (most coins first; the server orders the rows),
        // this player's row in green. A hider's score is seconds survived of the seek, a seeker's the
        // hiders caught. Columns are labels of their own, so any name (Latin, Cyrillic, Chinese) lines up.
        for (int i = resultsRows.childCount - 1; i >= 0; i--) Destroy(resultsRows.GetChild(i).gameObject);
        byte me = Net != null && Net.InMatch ? Net.LocalSlot : (byte)0xFE;
        string myName = Net != null ? Net.UserId : null;

        MatchResultEntry[] players = table.Players ?? Array.Empty<MatchResultEntry>();
        // By rank; equal ranks keep the server's order (OrderBy is stable, List.Sort is not).
        List<MatchResultEntry> ordered = players.OrderBy(a => a.Rank == 0 ? int.MaxValue : a.Rank).ToList();

        foreach (MatchResultEntry p in ordered)
        {
            bool mine = (me != 0xFE && p.Slot == me) || (!p.IsBot && !string.IsNullOrEmpty(myName) && p.Name == myName);
            string name = SafeName(p.Name) + (p.IsBot ? " (bot)" : "") + (p.Away ? " (away)" : "") + (p.Slot == 0xFF ? " (left)" : "");
            ResultRow(p.Rank > 0 ? "#" + p.Rank : "-", Clip(name, 22), p.Role == Role.Hunter ? "Seeker" : "Hider",
                      ScoreText(p.Role, p.SurvivedSeconds, table.SeekSeconds, p.Catches, p.PointsHundredths, p.CaughtBy),
                      p.CoinCents > 0 ? "+" + CoinBalance.Format(p.CoinCents) : "0",
                      mine ? Paintbox.Good : Paintbox.Ink, mine);
        }

        resultsScreen.SetActive(true);
        resultsScreen.transform.SetAsLastSibling();
        if (listScreen.activeSelf) CloseList();
    }

    // A row of the results table: rank, player, side, score and coins, this player's in green.
    private void ResultRow(string rank, string player, string side, string score, string coins, Color color, bool bold)
    {
        Image row = Paintbox.Image(resultsRows, "Row", null, bold ? Paintbox.GoodWash : Color.clear);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 60f;
        Paintbox.Weight w = bold ? Paintbox.Weight.Bold : Paintbox.Weight.Regular;
        PaintboxPage.Cell(row.transform, rank, 0f, 0.09f, 28f, color, Paintbox.Weight.Bold, TextAnchor.MiddleLeft);
        PaintboxPage.Cell(row.transform, player, 0.09f, 0.42f, 28f, color, Paintbox.Weight.Bold, TextAnchor.MiddleLeft);
        PaintboxPage.Cell(row.transform, side, 0.42f, 0.55f, 26f, Paintbox.Muted, w, TextAnchor.MiddleLeft);
        PaintboxPage.Cell(row.transform, score, 0.55f, 0.86f, 26f, color, w, TextAnchor.MiddleRight);
        PaintboxPage.Cell(row.transform, coins, 0.86f, 1f, 28f, coins.StartsWith("+") ? Paintbox.Good : Paintbox.Muted, Paintbox.Weight.Bold, TextAnchor.MiddleRight);
        Image rule = Paintbox.Image(row.transform, "Rule", null, Paintbox.Line);
        rule.rectTransform.anchorMin = new Vector2(0f, 0f);
        rule.rectTransform.anchorMax = new Vector2(1f, 0f);
        rule.rectTransform.offsetMin = Vector2.zero;
        rule.rectTransform.offsetMax = new Vector2(0f, 2f);
    }

    // ================= UI =================

    private void BuildUI()
    {
        overlayCanvas = OnlineUI.CreateCanvas("OnlineMatchesOverlay", transform, 56);
        // With the other Multiplayer pages (48): under Settings (52) and the Shop (58), which open
        // over it (2026-11-25: they opened behind it), over the round's HUD and menu (47).
        screenCanvas = OnlineUI.CreateCanvas("OnlineMatchesScreens", transform, 48);

        // The coin balance, top right, left of the HUD's Settings column: Home's own pill once Home
        // is loaded (UpdateCoinPill), a plain one until then.
        {
            // Design B's balance pill (2026-11-24): the same on Home, in the shop and here.
            Text coins = Paintbox.CoinPill(overlayCanvas.transform, out RectTransform prt);
            coins.name = "CoinText";
            prt.name = "GlobalCoinPill";
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(1f, 1f);
            prt.anchoredPosition = new Vector2(-160f, -30f);
            prt.sizeDelta = new Vector2(240f, 76f);
            coinPill = prt.gameObject;
        }
        coinPill.SetActive(false);

        // The chip: matches in progress, top centre.
        Button chipButton = OnlineUI.NewButton(overlayCanvas.transform, "", Vector2.zero, new Vector2(560f, 70f), new Color(0.85f, 0.52f, 0.12f, 0.95f), OpenList);
        chipButton.name = "MatchesChip";
        RectTransform crt = (RectTransform)chipButton.transform;
        crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 1f);
        crt.anchoredPosition = new Vector2(0f, -30f);
        chipText = chipButton.GetComponentInChildren<Text>();
        chipText.fontSize = 30;
        chip = chipButton.gameObject;
        chip.SetActive(false);


        // The list: Multiplayer's Matches tab (design B).
        PaintboxPage listPage = PaintboxPage.Build(screenCanvas.transform, "MyMatchesScreen", "Multiplayer", () => { AudioManager.ButtonTap(); CloseList(); },
                                                   PaintboxPage.Top.Balance | PaintboxPage.Top.Shop | PaintboxPage.Top.Settings, HubTab.Matches);
        listScreen = listPage.Root.gameObject;
        listTitle = listPage.Title;
        listTitle.text = "Multiplayer";
        listTitle = Paintbox.Label(listPage.Content, "", 1f, Color.clear);   // the old heading, kept for its state
        RectTransform head = Paintbox.TopBand(Paintbox.NewRect(listPage.Content, "Head"), 0f, 44f);
        listHead = head.gameObject;
        Text kicker = PaintboxPage.Kicker(head, "JOINED MATCHES");
        Paintbox.Fill(kicker.rectTransform);
        RectTransform activeChip = Paintbox.Tag(head, "", Paintbox.Wash, Paintbox.WashInk, "Radio", 22f);
        activeChip.anchorMin = activeChip.anchorMax = activeChip.pivot = new Vector2(1f, 0.5f);
        activeChip.anchoredPosition = Vector2.zero;
        listActive = activeChip.GetComponentInChildren<Text>();

        RectTransform view = Paintbox.Fill(Paintbox.NewRect(listPage.Content, "List"), 0f, 0f, 56f, 0f);
        ScrollRect scroll = view.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.scrollSensitivity = 40f;
        view.gameObject.AddComponent<RectMask2D>();
        view.gameObject.AddComponent<Image>().color = Color.clear;
        listRows = Paintbox.NewRect(view, "Rows");
        listRows.anchorMin = new Vector2(0f, 1f);
        listRows.anchorMax = new Vector2(1f, 1f);
        listRows.pivot = new Vector2(0.5f, 1f);
        listRows.offsetMin = listRows.offsetMax = Vector2.zero;
        VerticalLayoutGroup rows = listRows.gameObject.AddComponent<VerticalLayoutGroup>();
        rows.spacing = 14f;
        rows.padding = new RectOffset(0, 0, 0, 20);
        rows.childControlWidth = rows.childControlHeight = true;
        rows.childForceExpandWidth = true;
        rows.childForceExpandHeight = false;
        listRows.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = listRows;
        scroll.viewport = view;

        // Nothing running: the way to a new match, or to the history.
        RectTransform empty = Paintbox.NewRect(listPage.Content, "Empty");
        Paintbox.Pin(empty, new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(1000f, 300f));
        listEmpty = empty.gameObject;
        Image art = Paintbox.Rounded(empty, "Art", Color.white, 30f);
        Paintbox.Pin(art.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(420f, 280f));
        art.rectTransform.pivot = new Vector2(0f, 0.5f);
        Image artMask = Paintbox.Rounded(art.rectTransform, "Frame", Color.white, 30f);
        Paintbox.Fill(artMask.rectTransform);
        artMask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        Image artPic = Paintbox.Image(artMask.rectTransform, "Picture", Paintbox.Art("aerial"), Color.white);
        Paintbox.Fill(artPic.rectTransform);
        AspectRatioFitter artFit = artPic.gameObject.AddComponent<AspectRatioFitter>();
        artFit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        artFit.aspectRatio = 832f / 466f;
        Text emptyTitle = Paintbox.Label(empty, "No active matches", 54f, Paintbox.Ink, Paintbox.Weight.Title);
        Paintbox.Pin(emptyTitle.rectTransform, new Vector2(0f, 1f), new Vector2(456f, -40f), new Vector2(560f, 70f));
        emptyTitle.rectTransform.pivot = new Vector2(0f, 1f);
        Text emptyText = Paintbox.Label(empty, "Join a room to play. While a match is running, you can return to it here.", 26f, Paintbox.Muted);
        emptyText.verticalOverflow = VerticalWrapMode.Overflow;
        Paintbox.Pin(emptyText.rectTransform, new Vector2(0f, 1f), new Vector2(456f, -114f), new Vector2(540f, 70f));
        emptyText.rectTransform.pivot = new Vector2(0f, 1f);
        Button find = Paintbox.Button(empty, "Find match", Paintbox.Tone.Primary, "Search", () => { AudioManager.ButtonTap(); MultiplayerHub.Go(HubTab.NewMatch); }, 28f);
        find.name = "EmptyFindMatch";
        Paintbox.Pin((RectTransform)find.transform, new Vector2(0f, 1f), new Vector2(456f, -200f), new Vector2(250f, 80f));
        ((RectTransform)find.transform).pivot = new Vector2(0f, 1f);
        Button history = Paintbox.Button(empty, "Open history", Paintbox.Tone.Paper, null, () => { AudioManager.ButtonTap(); MultiplayerHub.Go(HubTab.History); }, 28f);
        history.name = "EmptyHistory";
        Paintbox.Pin((RectTransform)history.transform, new Vector2(0f, 1f), new Vector2(722f, -200f), new Vector2(240f, 80f));
        ((RectTransform)history.transform).pivot = new Vector2(0f, 1f);
        listEmpty.SetActive(false);
        listScreen.SetActive(false);

        // The results: a page of its own, the table and the ways on in the foot.
        PaintboxPage results = PaintboxPage.Build(screenCanvas.transform, "MatchResultsScreen", "Match results", null,
                                                  PaintboxPage.Top.Balance, null, foot: true);
        resultsScreen = results.Root.gameObject;
        resultsHeading = results.Title;
        resultsTitle = results.FootDetail;
        results.FootTitle.text = "";
        resultsFootTitle = results.FootTitle;

        PaintboxFrame table = Paintbox.Panel(results.Content, "Table", 30f);
        Paintbox.Fill(table.Root);
        RectTransform tableHead = PaintboxPage.TableHead(table.Body, new[]
        {
            ("RANK", 0f, 0.09f, TextAnchor.MiddleLeft), ("PLAYER", 0.09f, 0.42f, TextAnchor.MiddleLeft), ("SIDE", 0.42f, 0.55f, TextAnchor.MiddleLeft),
            ("SCORE", 0.55f, 0.86f, TextAnchor.MiddleRight), ("COINS", 0.86f, 1f, TextAnchor.MiddleRight),
        });
        Paintbox.TopBand(tableHead, 4f, 52f, 4f, 4f);
        RectTransform tableView = Paintbox.Fill(Paintbox.NewRect(table.Body, "View"), 8f, 8f, 60f, 8f);
        ScrollRect tableScroll = tableView.gameObject.AddComponent<ScrollRect>();
        tableScroll.horizontal = false;
        tableView.gameObject.AddComponent<RectMask2D>();
        tableView.gameObject.AddComponent<Image>().color = Color.clear;
        resultsRows = Paintbox.NewRect(tableView, "Rows");
        resultsRows.anchorMin = new Vector2(0f, 1f);
        resultsRows.anchorMax = new Vector2(1f, 1f);
        resultsRows.pivot = new Vector2(0.5f, 1f);
        resultsRows.offsetMin = resultsRows.offsetMax = Vector2.zero;
        VerticalLayoutGroup resultRows = resultsRows.gameObject.AddComponent<VerticalLayoutGroup>();
        resultRows.childControlWidth = resultRows.childControlHeight = true;
        resultRows.childForceExpandWidth = true;
        resultRows.childForceExpandHeight = false;
        resultsRows.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        tableScroll.content = resultsRows;
        tableScroll.viewport = tableView;

        resultsClose = results.FootButton("Close", Paintbox.Tone.Paper, "X", () =>
        {
            AudioManager.ButtonTap();
            Action close = onResultsClose;
            CloseResults();
            close?.Invoke();
        }, 200f);
        BackKey.Mark(resultsClose);   // the phone's Back closes the results
        resultsHome = results.FootButton("Home", Paintbox.Tone.Paper, "House", () =>
        {
            Action home = onResultsHome;
            CloseResults();
            home?.Invoke();
        }, 200f);
        // A seeker's Next Player when the hiders won: where each of them hid (OnlineRound.Reveal).
        resultsExtra = results.FootButton("Next Player", Paintbox.Tone.Secondary, "Eye", () =>
        {
            AudioManager.ButtonTap();
            onResultsExtra?.Invoke();
        }, 330f);
        resultsExtra.name = "ResultsNextPlayer";
        resultsNext = results.FootButton("Next", Paintbox.Tone.Primary, "ArrowRight", () =>
        {
            Action next = onResultsNext;
            CloseResults();
            next?.Invoke();
        }, 220f);
        resultsNext.name = "ResultsNext";
        resultsExtraHint = Paintbox.Label(results.Foot, "See where each hider hid with Next Player.", 22f, OnlineUI.Warning);
        resultsExtraHint.rectTransform.anchorMin = new Vector2(0f, 1f);
        resultsExtraHint.rectTransform.anchorMax = new Vector2(1f, 1f);
        resultsExtraHint.rectTransform.offsetMin = new Vector2(0f, 4f);
        resultsExtraHint.rectTransform.offsetMax = new Vector2(0f, 40f);
        resultsExtraHint.alignment = TextAnchor.LowerRight;
        LayoutResultButtons(false);
        resultsScreen.SetActive(false);
    }

    private void CloseResults()
    {
        resultsScreen.SetActive(false);
        onResultsNext = onResultsHome = onResultsClose = onResultsExtra = null;
    }

    // Next, Next Player, Home, Close in the foot; Next Player only when it is offered.
    private void LayoutResultButtons(bool extra)
    {
        resultsExtra.gameObject.SetActive(extra);
        resultsExtraHint.gameObject.SetActive(extra);
    }

    // ================= Text =================

    // These screens open over other screens (the room list, the round's own result): a fully opaque panel
    // keeps the text underneath from showing through (UI blends in linear space, so 3% shows).
    private static void Opaque(RectTransform panel)
    {
        Image image = panel.GetComponent<Image>();
        Color c = image.color;
        c.a = 1f;
        image.color = c;
    }

    /// <summary>
    /// A player's score as the results show it (2026-11-13): a hider's seconds survived of the seek
    /// ("120s / 150s"), with the seeker who caught them ("120s / 150s | name": the seeker who caught them, SDK 0.9.6),
    /// a seeker's hiders caught ("3 Hiders Caught"). An older server sends neither figure (no seek
    /// length): the reward rule's points then.
    /// </summary>
    public static string ScoreText(Role role, int survivedSeconds, int seekSeconds, int catches, int pointsHundredths,
                                   string caughtBy = null)
    {
        if (seekSeconds <= 0) return Points(pointsHundredths) + " pts";
        if (role == Role.Hunter) return catches == 1 ? "1 Hider Caught" : $"{catches} Hiders Caught";

        int survived = Mathf.Clamp(survivedSeconds, 0, seekSeconds);
        return string.IsNullOrEmpty(caughtBy)
            ? $"{survived}s / {seekSeconds}s"
            : $"{survived}s / {seekSeconds}s | {Clip(SafeName(caughtBy), 20)}";
    }

    private static string Points(int hundredths) =>
        (hundredths / 100m).ToString(hundredths % 100 == 0 ? "0" : "0.00", CultureInfo.InvariantCulture);

    private static string RoomTitle(string id)
    {
        if (string.IsNullOrEmpty(id)) return "Room";
        string spaced = id.Replace('-', ' ');
        return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1);
    }

    private static string MapTitle(string map)
    {
        if (string.IsNullOrEmpty(map)) return "";
        int digit = map.IndexOfAny("0123456789".ToCharArray());
        return digit > 0 && map[digit - 1] != ' ' ? map.Insert(digit, " ") : map;
    }

    private static string SafeName(string name) => string.IsNullOrEmpty(name) ? "Player" : name.Replace('<', '(').Replace('>', ')');

    private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + ".";
}
