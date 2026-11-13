using System;
using System.Collections.Generic;
using System.Globalization;
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
    private const int RowsPerPage = 5;
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
    private Text chipText, listTitle, listPage, resultsTitle;
    private RectTransform listRows, toastStack, resultsRows;
    private Button resultsNext, resultsHome;
    private Action onResultsNext, onResultsHome;
    private int page;
    private readonly List<(GameObject Go, CanvasGroup Group, float Until, uint MatchId)> toasts = new List<(GameObject, CanvasGroup, float, uint)>();

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

    /// <summary>Opens the list of the player's matches (in progress and ended).</summary>
    public static void ShowList()
    {
        if (instance == null) Create();
        instance.OpenList();
    }

    /// <summary>
    /// A match's full results. At the end of a match OnlineRound passes Next and Home, which the
    /// screen offers next to Close (Close shows the match's own result popup underneath).
    /// </summary>
    public static void ShowResults(MatchResultTable table, Action onNext = null, Action onHome = null)
    {
        if (instance == null) Create();
        instance.OpenResults(table, onNext, onHome);
    }

    /// <summary>Closes the results screen (the round ending, the player leaving).</summary>
    public static void HideResults()
    {
        if (instance != null && instance.resultsScreen != null) instance.resultsScreen.SetActive(false);
    }

    // ================= Server =================

    private void Update()
    {
        Hook();

        if (Net != null && Net.IsConnected && !string.IsNullOrEmpty(Net.SessionToken) && Time.unscaledTime >= nextPollAt)
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
        UpdateToasts();
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
            string coins = m.CoinCents > 0 ? $" <color=#FFD24A>+{OnlineUI.CoinsText(m.CoinCents)}</color>" : "";
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
            OpenResults(table, null, null);
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
    }

    // ================= Coin balance everywhere =================

    // Shown when nothing else on screen shows the balance (and never over the loading screen).
    private void UpdateCoinPill()
    {
        bool other = false;
        foreach (CoinLabel label in FindObjectsByType<CoinLabel>(FindObjectsInactive.Exclude))
        {
            if (label.transform.IsChildOf(coinPill.transform)) continue;
            Canvas c = label.GetComponentInParent<Canvas>();
            if (c != null && c.isActiveAndEnabled) { other = true; break; }
        }

        GameObject loading = GameObject.Find("LoadingCanvas");
        bool show = !other && (loading == null || !loading.activeInHierarchy);
        if (coinPill.activeSelf != show) coinPill.SetActive(show);
    }

    // ================= The chip: matches in progress =================

    private void UpdateChip()
    {
        int away = AwayCount;
        GameObject loading = GameObject.Find("LoadingCanvas");
        bool show = away > 0 && !OnlineRound.Active && !listScreen.activeSelf && !resultsScreen.activeSelf &&
                    (loading == null || !loading.activeInHierarchy);
        if (chip.activeSelf != show) chip.SetActive(show);
        if (show) chipText.text = away == 1 ? "1 match in progress: VIEW" : $"{away} matches in progress: VIEW";
    }

    // ================= Toasts =================

    // Top centre, under the round's banner during a round; a tap opens the results (or the list).
    private void Toast(string text, uint matchId, bool openList)
    {
        // Two at most: the oldest goes.
        while (toasts.Count >= 2)
        {
            Destroy(toasts[0].Go);
            toasts.RemoveAt(0);
        }

        // Opaque with an edge, so it reads as a banner over whatever screen is under it.
        Button b = OnlineUI.NewButton(toastStack, text, Vector2.zero, new Vector2(900f, 84f), new Color(0.10f, 0.12f, 0.18f, 1f), null);
        b.name = "Toast";
        Outline edge = b.gameObject.AddComponent<Outline>();
        edge.effectColor = new Color(0.85f, 0.52f, 0.12f, 1f);
        edge.effectDistance = new Vector2(3f, -3f);
        Text label = b.GetComponentInChildren<Text>();
        label.fontSize = 28;
        label.resizeTextForBestFit = true;
        label.resizeTextMinSize = 20;
        label.resizeTextMaxSize = 28;
        CanvasGroup group = b.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        GameObject go = b.gameObject;
        b.onClick.AddListener(() =>
        {
            AudioManager.ButtonTap();
            RemoveToast(go);
            if (matchId != 0) AskResults(matchId);
            else if (openList) OpenList();
        });
        toasts.Add((go, group, Time.unscaledTime + ToastSeconds, matchId));
        LayoutToasts();
    }

    private void RemoveToast(GameObject go)
    {
        int i = toasts.FindIndex(t => t.Go == go);
        if (i < 0) return;
        Destroy(toasts[i].Go);
        toasts.RemoveAt(i);
        LayoutToasts();
    }

    private void LayoutToasts()
    {
        // In a round, under the round's banner; elsewhere, under the chip.
        float top = OnlineRound.Active ? -250f : -120f;
        for (int i = 0; i < toasts.Count; i++)
        {
            var rt = (RectTransform)toasts[i].Go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, top - i * 96f);
        }
    }

    private void UpdateToasts()
    {
        float now = Time.unscaledTime;
        for (int i = toasts.Count - 1; i >= 0; i--)
        {
            var t = toasts[i];
            if (t.Go == null) { toasts.RemoveAt(i); continue; }
            float left = t.Until - now;
            if (left <= 0f) { RemoveToast(t.Go); continue; }
            // A quick fade in and out: nothing jumps on or off the screen.
            float age = ToastSeconds - left;
            t.Group.alpha = Mathf.Clamp01(Mathf.Min(age / 0.25f, left / 0.4f));
        }
    }

    // ================= The list =================

    private void OpenList()
    {
        AudioManager.ButtonTap();
        page = 0;
        listScreen.SetActive(true);
        listScreen.transform.SetAsLastSibling();
        if (Net != null && Net.IsConnected) Net.RequestMyMatches();
        RefreshList();
    }

    private void RefreshList()
    {
        for (int i = listRows.childCount - 1; i >= 0; i--) Destroy(listRows.GetChild(i).gameObject);

        var shown = new List<MyMatchEntry>();
        foreach (MyMatchEntry m in matches) if (m.State != MyMatchState.Playing) shown.Add(m);

        int pages = Mathf.Max(1, (shown.Count + RowsPerPage - 1) / RowsPerPage);
        page = Mathf.Clamp(page, 0, pages - 1);
        listPage.text = pages > 1 ? $"{page + 1}/{pages}" : "";
        listTitle.text = shown.Count == 0 ? "No matches in progress" : "Your matches";

        for (int i = 0; i < RowsPerPage && page * RowsPerPage + i < shown.Count; i++)
        {
            MyMatchEntry m = shown[page * RowsPerPage + i];
            float y = 190f - i * 116f;

            Image row = OnlineUI.NewImage("Match_" + m.MatchId, listRows, new Color(1f, 1f, 1f, 0.07f));
            OnlineUI.Place(row.rectTransform, new Vector2(0f, y), new Vector2(1240f, 104f));

            Text label = OnlineUI.NewLabel(row.transform, "", 30, new Vector2(-170f, 0f), new Vector2(860f, 100f), TextAnchor.MiddleLeft);
            label.name = "Info";
            label.text = Describe(m);

            if (m.State == MyMatchState.Away)
            {
                uint id = m.MatchId;
                Button rejoin = OnlineUI.NewButton(row.transform, "REJOIN", new Vector2(480f, 0f), new Vector2(240f, 80f), OnlineUI.Green, () =>
                {
                    AudioManager.ButtonTap();
                    listScreen.SetActive(false);
                    OnlineLobby.RejoinMatch(id);
                });
                rejoin.name = "Rejoin";
            }
            else
            {
                uint id = m.MatchId;
                OnlineUI.NewButton(row.transform, "RESULTS", new Vector2(480f, 0f), new Vector2(240f, 80f), OnlineUI.Blue, () =>
                {
                    AudioManager.ButtonTap();
                    AskResults(id);
                }).name = "Results";
            }
        }
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
                Text info = row.Find("Info")?.GetComponent<Text>();
                if (info != null) info.text = Describe(m);
            }
        }
    }

    private string Describe(MyMatchEntry m)
    {
        string side = m.Role == Role.Hunter ? "Seeker" : "Hider";
        string where = $"<b>{RoomTitle(m.RoomId)}</b>  {MapTitle(m.Map)}  ·  {side}";
        if (m.State == MyMatchState.Ended)
        {
            string coins = m.CoinCents > 0 ? $"  <color=#FFD24A>+{OnlineUI.CoinsText(m.CoinCents)}</color>" : "";
            return $"{where}\n<size=26>{(m.Won ? "Ended: you won" : "Ended: you lost")}  ·  #{m.Rank}  ·  {Points(m.PointsHundredths)} pts</size>{coins}";
        }

        int left = Mathf.Max(0, m.SecondsLeft - Mathf.FloorToInt(Time.unscaledTime - matchesAt));
        string phase = m.Phase == MatchPhase.Hide ? "Hiding" : m.Phase == MatchPhase.Seek ? "Seeking" : "Starting";
        return $"{where}\n<size=26>In progress: {phase}, {left / 60}:{left % 60:00} left</size>";
    }

    // ================= Results =================

    private void OpenResults(MatchResultTable table, Action onNext, Action onHome)
    {
        onResultsNext = onNext;
        onResultsHome = onHome;
        resultsNext.gameObject.SetActive(onNext != null);
        resultsHome.gameObject.SetActive(onHome != null);

        string outcome = table.Outcome == MatchOutcome.HidersWin ? "Hiders win"
                       : table.Outcome == MatchOutcome.SeekersWin ? "Seekers win" : "No winner";
        resultsTitle.text = $"{RoomTitle(table.RoomId)}  ·  {MapTitle(table.Map)}  ·  {outcome}";

        // One row per player, grouped by side, best first; this player's row in green. Columns are
        // labels of their own, so any name (Latin, Cyrillic, Chinese) lines up.
        for (int i = resultsRows.childCount - 1; i >= 0; i--) Destroy(resultsRows.GetChild(i).gameObject);
        byte me = Net != null && Net.InMatch ? Net.LocalSlot : (byte)0xFE;
        string myName = Net != null ? Net.UserId : null;

        float y = 290f;
        ResultRow(y, "Rank", "Player", "Side", "Score", "Coins", OnlineUI.Muted, false);
        Role? side = null;
        foreach (MatchResultEntry p in table.Players ?? Array.Empty<MatchResultEntry>())
        {
            if (side != p.Role)
            {
                side = p.Role;
                y -= 52f;
                ResultRow(y, "", p.Role == Role.Hunter ? "Seekers" : "Hiders", "", "", "", new Color(1f, 0.85f, 0.45f, 1f), true);
            }

            y -= 46f;
            bool mine = (me != 0xFE && p.Slot == me) || (!p.IsBot && !string.IsNullOrEmpty(myName) && p.Name == myName);
            string name = SafeName(p.Name) + (p.IsBot ? " (bot)" : "") + (p.Away ? " (away)" : "") + (p.Slot == 0xFF ? " (left)" : "");
            ResultRow(y, p.Rank > 0 ? "#" + p.Rank : "-", Clip(name, 28), p.Role == Role.Hunter ? "Seeker" : "Hider",
                      Points(p.PointsHundredths), p.CoinCents > 0 ? "+" + CoinBalance.Format(p.CoinCents) : "0",
                      mine ? new Color(0.49f, 0.95f, 0.6f, 1f) : Color.white, mine);
        }

        resultsScreen.SetActive(true);
        resultsScreen.transform.SetAsLastSibling();
    }

    private void ResultRow(float y, string rank, string player, string side, string score, string coins, Color color, bool bold)
    {
        void Cell(string text, float x, float width, TextAnchor anchor)
        {
            Text t = OnlineUI.NewLabel(resultsRows, text, 30, new Vector2(x, y), new Vector2(width, 46f), anchor, bold);
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
        }

        Cell(rank, -590f, 110f, TextAnchor.MiddleLeft);
        Cell(player, -230f, 600f, TextAnchor.MiddleLeft);
        Cell(side, 200f, 180f, TextAnchor.MiddleLeft);
        Cell(score, 400f, 160f, TextAnchor.MiddleRight);
        Cell(coins, 590f, 180f, TextAnchor.MiddleRight);
    }

    // ================= UI =================

    private void BuildUI()
    {
        overlayCanvas = OnlineUI.CreateCanvas("OnlineMatchesOverlay", transform, 56);
        screenCanvas = OnlineUI.CreateCanvas("OnlineMatchesScreens", transform, 57);

        // The coin balance, top right, left of the HUD's Settings column.
        Image pill = OnlineUI.NewImage("GlobalCoinPill", overlayCanvas.transform, new Color(0.08f, 0.09f, 0.13f, 0.82f));
        RectTransform prt = pill.rectTransform;
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(1f, 1f);
        prt.anchoredPosition = new Vector2(-170f, -46f);
        prt.sizeDelta = new Vector2(240f, 64f);
        pill.raycastTarget = false;
        Text coins = OnlineUI.NewLabel(pill.transform, "", 32, Vector2.zero, new Vector2(220f, 60f), TextAnchor.MiddleCenter, true);
        coins.name = "CoinText";
        CoinBalance.Bind(coins, "Coins: {0}");
        coinPill = pill.gameObject;

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

        toastStack = (RectTransform)new GameObject("Toasts", typeof(RectTransform)).transform;
        toastStack.SetParent(overlayCanvas.transform, false);
        OnlineUI.Stretch(toastStack);

        // The list.
        RectTransform p = OnlineUI.Modal(screenCanvas.transform, "MyMatchesScreen", new Vector2(1320f, 860f));
        Opaque(p);
        listScreen = p.parent.gameObject;
        listTitle = OnlineUI.NewLabel(p, "Your matches", 50, new Vector2(0f, 350f), new Vector2(1200f, 80f), TextAnchor.MiddleCenter, true);
        Text hint = OnlineUI.NewLabel(p, "A match you left goes on without you: rejoin it while it runs.", 28, new Vector2(0f, 290f), new Vector2(1200f, 50f));
        hint.color = OnlineUI.Muted;
        listRows = (RectTransform)new GameObject("Rows", typeof(RectTransform)).transform;
        listRows.SetParent(p, false);
        OnlineUI.Place(listRows, Vector2.zero, new Vector2(1280f, 620f));
        OnlineUI.NewButton(p, "<", new Vector2(-330f, -360f), new Vector2(110f, 76f), OnlineUI.Gray, () => { page--; RefreshList(); });
        listPage = OnlineUI.NewLabel(p, "", 30, new Vector2(-200f, -360f), new Vector2(120f, 70f));
        OnlineUI.NewButton(p, ">", new Vector2(-70f, -360f), new Vector2(110f, 76f), OnlineUI.Gray, () => { page++; RefreshList(); });
        OnlineUI.NewButton(p, "CLOSE", new Vector2(330f, -360f), new Vector2(300f, 80f), OnlineUI.Gray, () =>
        {
            AudioManager.ButtonTap();
            listScreen.SetActive(false);
        });
        listScreen.SetActive(false);

        // The results.
        p = OnlineUI.Modal(screenCanvas.transform, "MatchResultsScreen", new Vector2(1480f, 960f));
        Opaque(p);
        resultsScreen = p.parent.gameObject;
        OnlineUI.NewLabel(p, "Match results", 50, new Vector2(0f, 410f), new Vector2(1400f, 70f), TextAnchor.MiddleCenter, true);
        resultsTitle = OnlineUI.NewLabel(p, "", 32, new Vector2(0f, 350f), new Vector2(1400f, 50f));
        resultsTitle.color = OnlineUI.Muted;
        resultsRows = (RectTransform)new GameObject("Rows", typeof(RectTransform)).transform;
        resultsRows.SetParent(p, false);
        OnlineUI.Place(resultsRows, Vector2.zero, new Vector2(1400f, 700f));
        resultsNext = OnlineUI.NewButton(p, "NEXT", new Vector2(-360f, -410f), new Vector2(300f, 84f), OnlineUI.Green, () =>
        {
            resultsScreen.SetActive(false);
            Action next = onResultsNext;
            onResultsNext = onResultsHome = null;
            next?.Invoke();
        });
        resultsHome = OnlineUI.NewButton(p, "HOME", new Vector2(0f, -410f), new Vector2(300f, 84f), OnlineUI.Blue, () =>
        {
            resultsScreen.SetActive(false);
            Action home = onResultsHome;
            onResultsNext = onResultsHome = null;
            home?.Invoke();
        });
        OnlineUI.NewButton(p, "CLOSE", new Vector2(360f, -410f), new Vector2(300f, 84f), OnlineUI.Gray, () =>
        {
            AudioManager.ButtonTap();
            resultsScreen.SetActive(false);
        });
        resultsScreen.SetActive(false);
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
