using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// NOTE: Fully runtime-built matchmaking/lobby screen (main panel, progress bar, two columns of
// player slots, top HUD bar) via SceneUI helpers, plus a fake-fill animation that reveals bot
// names into empty slots over `fillSeconds`, then pauses `readyPause` before calling the ready
// callback. Confirms SceneUI.UIRoot() (static, returns Transform) - paired with the
// already-known SceneUI.HUDRoot(). EnsureBuilt/AuthorUI are byte-identical again (same
// exact-duplicate-method pattern as MatchHUD.EnsureBuilt/AuthorUI and
// HideModeResultUI.EnsureBuilt/AuthorUI) - implemented once, AuthorUI forwards.
public class MatchmakingUI : MonoBehaviour
{
    [Serializable]
    public class Slot
    {
        public GameObject go;
        public Image bg;
        public Text label;
    }

    [SerializeField] private Font font;
    [SerializeField] public int hunterSlots = 3;
    [SerializeField] public int totalPlayers = 8;
    [SerializeField] private float fillSeconds = 4.5f;
    [SerializeField] private float readyPause = 0.8f;

    [SerializeField] private Sprite panelSprite;
    [SerializeField] private Sprite titleSprite;
    [SerializeField] private Sprite barTrack;
    [SerializeField] private Sprite barFill;
    [SerializeField] private Sprite dividerV;
    [SerializeField] private Sprite dividerH;
    [SerializeField] private Sprite mapBorder;
    [SerializeField] private Sprite mapSprite;
    [SerializeField] private Sprite columnBoxSprite;
    [SerializeField] private Sprite nameGray;
    [SerializeField] private Sprite coinPanel;
    [SerializeField] private Sprite coinIcon;
    [SerializeField] private Sprite plusIcon;
    [SerializeField] private Sprite settingsIcon;
    [SerializeField] private Sprite adIcon;

    // NOTE: confirmed to exist (read via fixed instance-field offsets in SetSlot/BuildColumn)
    // but real names weren't in the paste - named here by inferred purpose, unconfirmed.
    [SerializeField] private Sprite slotFilledSprite;   // offset 0x58, used when highlight == true
    [SerializeField] private Sprite slotEmptySprite;    // offset 0x60, used when highlight == false
    [SerializeField] private Sprite fugitiveBadgeSprite; // offset 0x48
    [SerializeField] private Sprite hunterBadgeSprite;   // offset 0x50
    [SerializeField] private Sprite fugitiveTitleSprite; // offset 0x38
    [SerializeField] private Sprite hunterTitleSprite;   // offset 0x40

    private readonly List<Slot> fugSlots = new List<Slot>();
    private readonly List<Slot> hunSlots = new List<Slot>();
    private readonly List<Slot> revealOrder = new List<Slot>();

    private GameObject canvasGO;
    private Image barFillImg;
    private Text counterText;
    private Text hintText;
    private Text mapLabelText;
    private Text coinLabelText;

    private bool playerIsHunter;
    private Action onReady;
    private int joined;
    private float fillTimer;
    private float doneTimer;
    private int state; // 0 = idle, 1 = filling, 2 = done/waiting to auto-hide

    // NOTE: plain private static field (no get_/set_ accessors decompiled for it, unlike the
    // singleton Instance properties seen on other classes) - a fixed pool of bot display names.
    private static readonly string[] BotNames =
    {
        "Lieng", "Baconga", "Jack", "Lie", "Bumerang", "Max", "Rex", "Zoe",
        "Kai", "Milo", "Nova", "Pixel", "Echo", "Otto", "Suki", "Bolt", "Wolf", "Coco"
    };

    /// <summary>The waiting page is on screen (design B).</summary>
    public static bool PageOpen
    {
        get
        {
            MatchmakingUI ui = FindAnyObjectByType<MatchmakingUI>();
            return ui != null && ui.canvasGO != null && ui.canvasGO.activeInHierarchy;
        }
    }

    public int FugitiveCount => totalPlayers - hunterSlots;
    public int HunterCount => hunterSlots;

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

        Transform uiRoot = SceneUI.UIRoot();
        if (uiRoot == null)
        {
            throw new NullReferenceException("SceneUI.UIRoot() returned null");
        }

        Transform existing = uiRoot.Find("MatchmakingCanvas");
        if (existing != null)
        {
            Transform panel = existing.Find("Panel");
            if (panel != null)
            {
                BindUI(existing.gameObject);
                return;
            }
        }

        BuildUI();
    }

    public void AuthorUI()
    {
        EnsureBuilt();
    }

    // FIX (confirmed): raw's structure is `if (canvasGO != null) { SetActive(true);
    // if (hintText != null) { ...entire shuffle/fill/interleave/reveal logic... } }
    // goto <throw>;` - there is no else anywhere in that chain, so a null hintText does NOT
    // make raw silently skip the rest of the method. It falls straight through both checks to
    // the function's standard NRE-throw target. A prior draft had a silent `return;` here
    // instead. Fixed to throw. (canvasGO == null needs no separate fix - the SetActive(true)
    // call below already throws NRE naturally on a null reference, matching raw's outcome.)
    //
    // NOTE: fills fugSlots then hunSlots with shuffled bot names (fugitive loop uses index % N,
    // hunter loop CONTINUES the same cycling index rather than restarting at 0 - i.e. hunters
    // draw from later in the shuffled list), overwrites the player's own first slot with "You",
    // then interleaves the remaining slots into revealOrder via a round-robin merge (alternating
    // fug/hun preference each step, falling back to whichever list still has room) before
    // starting the fill animation.
    public void Begin(bool hunter, Action ready)
    {
        EnsureBuilt();
        playerIsHunter = hunter;
        onReady = ready;
        ShowRole(false);

        canvasGO.SetActive(true);

        if (hintText == null)
        {
            throw new NullReferenceException("hintText not available");
        }

        hintText.text = hunter
            ? "Find the fugitives and catch them before the round ends."
            : "Hide carefully. Don't let the Seeker find you!";

        List<string> names = new List<string>(BotNames);
        for (int i = names.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            string tmp = names[i];
            names[i] = names[j];
            names[j] = tmp;
        }

        for (int i = 0; i < fugSlots.Count; i++)
        {
            SetSlot(fugSlots[i], names[i % names.Count], false);
        }

        int idx = fugSlots.Count;
        for (int k = 0; k < hunSlots.Count; k++, idx++)
        {
            SetSlot(hunSlots[k], names[idx % names.Count], false);
        }

        List<Slot> ownList = hunter ? hunSlots : fugSlots;
        Slot ownSlot = (ownList.Count > 0) ? ownList[0] : null;
        if (ownSlot != null)
        {
            SetSlot(ownSlot, "You", true);
        }

        revealOrder.Clear();
        if (ownSlot != null)
        {
            revealOrder.Add(ownSlot);
        }

        int fi = (!hunter && ownSlot != null) ? 1 : 0;
        int hi = (hunter && ownSlot != null) ? 1 : 0;
        bool preferFug = true;

        while (fi < fugSlots.Count || hi < hunSlots.Count)
        {
            if (preferFug)
            {
                if (fi < fugSlots.Count)
                {
                    revealOrder.Add(fugSlots[fi]);
                    fi++;
                }
                else
                {
                    revealOrder.Add(hunSlots[hi]);
                    hi++;
                }
            }
            else
            {
                if (hi < hunSlots.Count)
                {
                    revealOrder.Add(hunSlots[hi]);
                    hi++;
                }
                else
                {
                    revealOrder.Add(fugSlots[fi]);
                    fi++;
                }
            }
            preferFug = !preferFug;
        }

        // Any slots not part of revealOrder's initial reveal stay hidden until Reveal() shows them.
        foreach (Slot s in fugSlots)
        {
            s.go.SetActive(false);
        }
        foreach (Slot s in hunSlots)
        {
            s.go.SetActive(false);
        }

        joined = 0;
        Reveal(1);
        fillTimer = 0f;
        doneTimer = 0f;
        state = 1;
        UpdateBarAndCounter();
    }

    // ---- Online lobby (Multiple Play) --------------------------------------------------------
    // The same screen showing the server's real roster instead of the fake fill. The lobby
    // decides when it closes (EndOnline); there is no ready callback.

    public struct OnlineSeat
    {
        public string Name;
        public bool Hunter;
        public bool IsLocal;
    }

    // ---- Online rooms of 8-16 players ------------------------------------------------------
    // The screen's columns are authored for the standard 3 + 5 and hold 5 rows. A bigger room
    // adds copies of a slot and flows them into side-by-side rows; Single Play and the standard
    // size keep the authored layout exactly (ResetOnlineMatchSize puts it back).

    private const int RowsPerColumn = 5;
    private const float ColumnWidth = 368f, ColumnGap = 8f, FirstRowY = 92f, RowSpacing = 62f, RowHeight = 56f;

    private readonly Dictionary<List<Slot>, int> authoredSlots = new Dictionary<List<Slot>, int>();
    private int standardTotal = -1, standardHunters;

    /// <summary>This match's seats, from MatchFound. Ends with EndOnline, or the next BeginOnline.</summary>
    public void SetOnlineMatchSize(int total, int hunters)
    {
        EnsureBuilt();
        if (standardTotal < 0)
        {
            standardTotal = totalPlayers;
            standardHunters = hunterSlots;
        }

        totalPlayers = total;
        hunterSlots = hunters;
        FitColumn(fugSlots, total - hunters);
        FitColumn(hunSlots, hunters);
        UpdateBarAndCounter();
    }

    private void ResetOnlineMatchSize()
    {
        if (standardTotal < 0) return;

        totalPlayers = standardTotal;
        hunterSlots = standardHunters;
        standardTotal = -1;
        FitColumn(fugSlots, totalPlayers - hunterSlots);
        FitColumn(hunSlots, hunterSlots);
    }

    private void FitColumn(List<Slot> column, int count)
    {
        if (column.Count == 0) return;
        if (!authoredSlots.TryGetValue(column, out int authored)) authoredSlots[column] = authored = column.Count;

        Transform box = column[0].go.transform.parent;
        while (column.Count < count)
        {
            GameObject go = Instantiate(column[0].go, box, false);
            go.name = "Slot" + column.Count;
            go.SetActive(false);
            Transform nameT = go.transform.Find("Name");
            column.Add(new Slot { go = go, bg = go.GetComponent<Image>(), label = nameT != null ? nameT.GetComponent<Text>() : null });
        }

        while (column.Count > Mathf.Max(count, authored))
        {
            Destroy(column[column.Count - 1].go);
            column.RemoveAt(column.Count - 1);
        }

        int columns = Mathf.Max(1, Mathf.CeilToInt(count / (float)RowsPerColumn));
        float width = (ColumnWidth - (columns - 1) * ColumnGap) / columns;

        for (int i = 0; i < column.Count; i++)
        {
            var rt = (RectTransform)column[i].go.transform;
            rt.sizeDelta = new Vector2(width, RowHeight);
            rt.anchoredPosition = new Vector2(-ColumnWidth * 0.5f + width * 0.5f + i / RowsPerColumn * (width + ColumnGap),
                                              FirstRowY - i % RowsPerColumn * RowSpacing);

            Text label = column[i].label;
            if (label == null) continue;
            label.resizeTextForBestFit = columns > 1;   // narrower rows: long names shrink to fit
            label.resizeTextMinSize = 14;
            label.resizeTextMaxSize = label.fontSize;
        }
    }

    public void BeginOnline(bool hunter, string hint)
    {
        EnsureBuilt();
        ResetOnlineMatchSize();
        playerIsHunter = hunter;
        onReady = null;
        state = 0;
        canvasGO.SetActive(true);
        ShowRole(true);
        SetOnlineHint(hint);
        SetOnlineRoster(null);
    }

    public void SetOnlineHint(string hint)
    {
        if (hintText != null) hintText.text = hint;
        if (bannerLine != null) bannerLine.text = hint ?? "";
    }

    // Seconds to the match while a full lobby counts down (0 = not counting).
    private int onlineCountdown;

    /// <summary>
    /// A full online lobby's countdown to the match, on the server's clock (OnlineLobby): the
    /// counter says "Match starts in N" in place of the player count; 0 puts the count back.
    /// </summary>
    public void SetOnlineCountdown(int seconds)
    {
        onlineCountdown = Mathf.Max(0, seconds);
        UpdateBarAndCounter();
    }

    public void SetMapLabel(string text)
    {
        if (mapLabelText != null) mapLabelText.text = text;
        if (roomChip != null)
        {
            roomChip.text = text ?? "";
            roomChip.transform.parent.gameObject.SetActive(!string.IsNullOrEmpty(text) && text != "Map game");
        }
    }

    /// <summary>Shows these seats (null or empty = nobody yet) and the joined count.</summary>
    public void SetOnlineRoster(IList<OnlineSeat> seats)
    {
        foreach (Slot s in fugSlots) s.go.SetActive(false);
        foreach (Slot s in hunSlots) s.go.SetActive(false);

        int fugitives = 0, hunters = 0;
        if (seats != null)
        {
            foreach (OnlineSeat seat in seats)
            {
                List<Slot> column = seat.Hunter ? hunSlots : fugSlots;
                int index = seat.Hunter ? hunters++ : fugitives++;
                if (index >= column.Count) continue;

                SetSlot(column[index], seat.IsLocal ? "You" : seat.Name, seat.IsLocal);
                column[index].go.SetActive(true);
            }
        }

        joined = Mathf.Min(fugitives, fugSlots.Count) + Mathf.Min(hunters, hunSlots.Count);
        UpdateBarAndCounter();
    }

    public void EndOnline()
    {
        ResetOnlineMatchSize();
        onlineCountdown = 0;
        state = 0;
        onReady = null;
        if (canvasGO != null) canvasGO.SetActive(false);

        // Back to what the offline fake fill shows. The coin label is the server's balance either
        // way (CoinBalance): Single Play has no coins of its own.
        SetMapLabel("Map game");
    }

    private void SetSlot(Slot s, string name, bool highlight)
    {
        s.label.text = name;
        Text letter = s.go.transform.Find("Initial/Letter")?.GetComponent<Text>();
        if (letter != null)
        {
            letter.text = string.IsNullOrEmpty(name) ? "" : char.ToUpperInvariant(name[0]).ToString();
            s.bg.color = highlight ? Paintbox.Wash : Paintbox.Paper;
            s.label.color = Paintbox.Ink;
        }
        else PaintboxSkin.Seat(s.bg, s.label, highlight);   // a scene slot (design B's colours)
    }

    private void Reveal(int target)
    {
        int count = revealOrder.Count;
        target = Mathf.Min(target, count);

        for (int i = joined; i < target; i++)
        {
            revealOrder[i].go.SetActive(true);
        }

        joined = target;
    }

    private void UpdateBarAndCounter()
    {
        float frac = (totalPlayers < 1) ? 0f : (float)joined / totalPlayers;

        if (barFillImg != null)
        {
            barFillImg.fillAmount = frac;
        }

        if (counterText == null)
        {
            return;
        }

        counterText.text = onlineCountdown > 0
            ? string.Format("Match starts in {0}", $"<color=#2C684C><size=44>{onlineCountdown}</size></color>")
            : $"<color=#2C684C>{joined}</color>/{totalPlayers} Player";
        RefreshPage();
    }

    private void Update()
    {
        if (state == 2)
        {
            doneTimer += Time.deltaTime;
            if (doneTimer >= readyPause)
            {
                state = 0;
                if (canvasGO != null)
                {
                    canvasGO.SetActive(false);
                }

                Action callback = onReady;
                onReady = null;
                callback?.Invoke();
            }
        }
        else if (state == 1)
        {
            fillTimer += Time.deltaTime;
            float duration = Mathf.Max(0.1f, fillSeconds);
            float progress = Mathf.Clamp01(fillTimer / duration);

            int target = Mathf.Clamp(Mathf.FloorToInt(progress * (totalPlayers - 1) + 0.0001f) + 1, 1, totalPlayers);

            if (joined < target)
            {
                Reveal(target);
            }
            UpdateBarAndCounter();

            if (joined < totalPlayers && joined < revealOrder.Count)
            {
                return;
            }

            doneTimer = 0f;
            state = 2;
        }
    }

    private void BindUI(GameObject canvas)
    {
        canvasGO = canvas;
        Transform canvasT = canvas.transform;

        Transform panel = UiFind.Require(canvasT, "Panel");
        if (panel != null)
        {
            barFillImg = UiFind.Require<Image>(panel, "BarTrack/BarFill");
            counterText = UiFind.Require<Text>(panel, "Counter");
            hintText = UiFind.Require<Text>(panel, "Hint");
            mapLabelText = UiFind.Require<Text>(panel, "MapLabel");

            BindColumn(UiFind.Require(panel, "FugitiveBox"), fugSlots);
            BindColumn(UiFind.Require(panel, "HunterBox"), hunSlots);
        }

        // The coin pill: its text sits inside CoinPanel in the scene, and beside it in the canvas
        // BuildUI makes. Either way it shows the server's balance, never a number of its own.
        List<Text> coinLabels = CoinBalance.BindNamed(canvasT);
        coinLabelText = coinLabels.Count > 0 ? coinLabels[0] : null;

        BuildPage();
        canvasGO.SetActive(false);
    }

    // ================= Design B: the waiting page (2026-11-24) =================
    //
    // "Finding players": a band with how many have joined (a countdown once the lobby is full), a
    // bar filling as they join, the Hiders and Seekers in two cards (each seat a row, the open ones
    // dashed), and a foot saying which side the player is on. The scene's own panel is hidden; its
    // slots are replaced by these rows (the fill, the roster and the countdown drive them as before).

    private PaintboxPage page;
    private Text bannerTitle, bannerLine, countdownText, roomChip, hiderCount, seekerCount;
    private Image bannerIcon, pageBarFill;
    private RectTransform hiderRows, seekerRows;
    private readonly List<GameObject> openHider = new List<GameObject>();
    private readonly List<GameObject> openSeeker = new List<GameObject>();
    private bool onlinePage;

    private void BuildPage()
    {
        if (page != null) return;
        foreach (string old in new[] { "Dim", "Panel", "CoinPanel", "SettingsBtn", "AdBtn", "LobbyShopBtn" })
        {
            Transform t = canvasGO.transform.Find(old);
            if (t != null) t.gameObject.SetActive(false);
        }

        RectTransform units = PaintboxUnits.Create(canvasGO.transform, "QueueDesign");
        page = PaintboxPage.Build(units, "QueuePage", "Finding players", null, PaintboxPage.Top.Balance | PaintboxPage.Top.Shop, null, foot: true);
        page.Right.gameObject.SetActive(false);

        // The band: an icon, the count (or "Lobby ready" and the countdown), a line under it, the room.
        Image band = Paintbox.Rounded(page.Content, "Band", Paintbox.Wash, 28f);
        Paintbox.TopBand(band.rectTransform, 0f, 136f);
        bannerIcon = Paintbox.IconImage(band.rectTransform, "Users", 44f, Paintbox.WashInk);
        Paintbox.Pin(bannerIcon.rectTransform, new Vector2(0f, 0.5f), new Vector2(34f, 0f), new Vector2(44f, 44f));
        bannerIcon.rectTransform.pivot = new Vector2(0f, 0.5f);
        bannerTitle = Paintbox.Label(band.rectTransform, "", 58f, Paintbox.WashInk, Paintbox.Weight.Title);
        bannerTitle.name = "QueueCount";
        bannerTitle.horizontalOverflow = HorizontalWrapMode.Overflow;
        bannerTitle.rectTransform.anchorMin = new Vector2(0f, 0.42f);
        bannerTitle.rectTransform.anchorMax = new Vector2(0.7f, 1f);
        bannerTitle.rectTransform.offsetMin = new Vector2(110f, 0f);
        bannerTitle.rectTransform.offsetMax = new Vector2(0f, -10f);
        bannerTitle.alignment = TextAnchor.LowerLeft;
        bannerLine = Paintbox.Label(band.rectTransform, "", 24f, Paintbox.WashInk);
        Paintbox.Fit(bannerLine, 14);
        bannerLine.rectTransform.anchorMin = new Vector2(0f, 0f);
        bannerLine.rectTransform.anchorMax = new Vector2(0.7f, 0.42f);
        bannerLine.rectTransform.offsetMin = new Vector2(110f, 14f);
        bannerLine.rectTransform.offsetMax = new Vector2(0f, 0f);
        bannerLine.alignment = TextAnchor.UpperLeft;
        countdownText = Paintbox.Label(band.rectTransform, "", 88f, Paintbox.WashInk, Paintbox.Weight.Title, TextAnchor.MiddleRight);
        countdownText.name = "QueueCountdown";
        Paintbox.Fill(countdownText.rectTransform, 0f, 36f, 0f, 0f);
        RectTransform chip = Paintbox.Tag(band.rectTransform, "", Paintbox.Paper, Paintbox.WashInk, "Gamepad2", 24f);
        chip.anchorMin = chip.anchorMax = chip.pivot = new Vector2(1f, 0.5f);
        chip.anchoredPosition = new Vector2(-30f, 0f);
        roomChip = chip.GetComponentInChildren<Text>();
        chip.gameObject.SetActive(false);

        // The bar: paper with an ink edge, filling with yellow.
        PaintboxFrame track = Paintbox.Frame(page.Content, "Bar", Paintbox.Paper, 10f, 3f, 0f, Paintbox.PanelLine);
        Paintbox.TopBand(track.Root, 152f, 20f);
        pageBarFill = Paintbox.Image(track.Fill.rectTransform, "Fill", Paintbox.WhiteSprite, Paintbox.Yellow);
        Paintbox.Fill(pageBarFill.rectTransform);
        pageBarFill.type = Image.Type.Filled;
        pageBarFill.fillMethod = Image.FillMethod.Horizontal;
        pageBarFill.fillAmount = 0f;

        // The two sides.
        hiderRows = TeamCard(page.Content, "Hiders", "PaintRoller", 0f, 0.5f, out hiderCount);
        seekerRows = TeamCard(page.Content, "Seekers", "ScanEye", 0.5f, 1f, out seekerCount);
        ReplaceSlots(fugSlots, hiderRows, FugitiveCount);
        ReplaceSlots(hunSlots, seekerRows, HunterCount);
    }

    private static RectTransform TeamCard(RectTransform content, string title, string icon, float from, float to, out Text count)
    {
        PaintboxFrame card = Paintbox.Panel(content, title + "Card", 30f);
        card.Root.anchorMin = new Vector2(from, 0f);
        card.Root.anchorMax = new Vector2(to, 1f);
        card.Root.offsetMin = new Vector2(from > 0f ? 12f : 0f, 0f);
        card.Root.offsetMax = new Vector2(to < 1f ? -12f : 0f, -190f);

        Image badge = Paintbox.Rounded(card.Body, "Badge", Paintbox.Wash, 16f);
        Paintbox.Pin(badge.rectTransform, new Vector2(0f, 1f), new Vector2(26f, -22f), new Vector2(64f, 64f));
        badge.rectTransform.pivot = new Vector2(0f, 1f);
        Paintbox.IconImage(badge.rectTransform, icon, 36f, Paintbox.WashInk);
        Text name = Paintbox.Label(card.Body, title, 44f, Paintbox.Ink, Paintbox.Weight.Title);
        Paintbox.TopBand(name.rectTransform, 22f, 64f, 108f, 140f);
        RectTransform pill = Paintbox.Tag(card.Body, "", Paintbox.Wash, Paintbox.WashInk, null, 24f);
        pill.anchorMin = pill.anchorMax = pill.pivot = new Vector2(1f, 1f);
        pill.anchoredPosition = new Vector2(-26f, -34f);
        count = pill.GetComponentInChildren<Text>();
        Image rule = Paintbox.Divider(card.Body);
        Paintbox.TopBand(rule.rectTransform, 100f, 3f, 26f, 26f);

        RectTransform rows = Paintbox.Fill(Paintbox.NewRect(card.Body, "Rows"), 26f, 26f, 112f, 16f);
        VerticalLayoutGroup v = rows.gameObject.AddComponent<VerticalLayoutGroup>();
        v.spacing = 8f;
        v.childControlWidth = v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        return rows;
    }

    // The seats become design B rows: an initial on a violet square, the name, and how they stand.
    private void ReplaceSlots(List<Slot> list, RectTransform rows, int count)
    {
        list.Clear();
        for (int i = 0; i < Mathf.Max(1, count); i++) list.Add(NewRow(rows, "Slot" + i));
    }

    private static Slot NewRow(RectTransform rows, string name)
    {
        Image bg = Paintbox.Rounded(rows, name, Paintbox.Paper, 14f);
        bg.gameObject.AddComponent<LayoutElement>().preferredHeight = 62f;
        Image initial = Paintbox.Rounded(bg.rectTransform, "Initial", Paintbox.Violet, 10f);
        Paintbox.Pin(initial.rectTransform, new Vector2(0f, 0.5f), new Vector2(12f, 0f), new Vector2(40f, 40f));
        initial.rectTransform.pivot = new Vector2(0f, 0.5f);
        Text letter = Paintbox.Label(initial.rectTransform, "", 24f, Paintbox.WashInk, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
        Paintbox.Fill(letter.rectTransform);
        letter.name = "Letter";
        Text label = Paintbox.Label(bg.rectTransform, "", 30f, Paintbox.Ink, Paintbox.Weight.Bold);
        label.name = "Name";
        Paintbox.Fit(label, 16);
        Paintbox.Fill(label.rectTransform, 68f, 200f, 0f, 0f);
        Image tick = Paintbox.IconImage(bg.rectTransform, "CircleCheck", 26f, Paintbox.Good);
        Paintbox.Pin(tick.rectTransform, new Vector2(1f, 0.5f), new Vector2(-120f, 0f), new Vector2(26f, 26f));
        Text state = Paintbox.Label(bg.rectTransform, "Joined", 22f, Paintbox.Good, Paintbox.Weight.Regular, TextAnchor.MiddleRight);
        state.name = "State";
        Paintbox.Fill(state.rectTransform, 0f, 14f, 0f, 0f);
        Image rule = Paintbox.Image(bg.rectTransform, "Rule", null, Paintbox.Line);
        rule.rectTransform.anchorMin = new Vector2(0f, 0f);
        rule.rectTransform.anchorMax = new Vector2(1f, 0f);
        rule.rectTransform.offsetMin = Vector2.zero;
        rule.rectTransform.offsetMax = new Vector2(0f, 2f);
        return new Slot { go = bg.gameObject, bg = bg, label = label };
    }

    // A seat nobody has yet: a dashed (faint) row saying so.
    private static GameObject OpenRow(RectTransform rows)
    {
        PaintboxFrame row = Paintbox.Frame(rows, "OpenSlot", Paintbox.Paper, 14f, 2f, 0f, Paintbox.Line);
        row.Root.gameObject.AddComponent<LayoutElement>().preferredHeight = 58f;
        Image icon = Paintbox.IconImage(row.Body, "UserPlus", 26f, Paintbox.Muted);
        Paintbox.Pin(icon.rectTransform, new Vector2(0f, 0.5f), new Vector2(20f, 0f), new Vector2(26f, 26f));
        icon.rectTransform.pivot = new Vector2(0f, 0.5f);
        Text words = Paintbox.Label(row.Body, "Open slot", 24f, Paintbox.Muted);
        Paintbox.Fill(words.rectTransform, 60f, 10f, 0f, 0f);
        return row.Root.gameObject;
    }

    private void ShowRole(bool online)
    {
        onlinePage = online;
        if (page == null) return;
        page.Right.gameObject.SetActive(online);
        page.FootTitle.text = playerIsHunter ? "You are a Seeker" : "You are a Hider";
        page.FootDetail.text = online ? "" : playerIsHunter ? "Find the Hiders before time runs out." : "Paint yourself to blend into the room.";
    }

    // The band, the bar, the counts and the open seats, as the roster and countdown stand.
    private void RefreshPage()
    {
        if (page == null) return;
        bool counting = onlineCountdown > 0;
        page.Title.text = counting ? "Match starting" : "Finding players";
        bannerTitle.text = counting ? L.T("Lobby ready") : L.F("{0} of {1} joined", joined, totalPlayers);
        bannerIcon.sprite = Paintbox.Icon(counting ? "Timer" : "Users");
        countdownText.text = counting ? $"00:{onlineCountdown:00}" : "";
        if (counting) bannerLine.text = L.T("Everyone is in. The match starts shortly.");
        roomChip.transform.parent.gameObject.SetActive(!counting && roomChip.text.Length > 0 && roomChip.text != "Map game");
        pageBarFill.fillAmount = totalPlayers < 1 ? 0f : (float)joined / totalPlayers;

        int hiders = 0, seekers = 0;
        foreach (Slot s in fugSlots) if (s.go.activeSelf) hiders++;
        foreach (Slot s in hunSlots) if (s.go.activeSelf) seekers++;
        hiderCount.text = $"{hiders}/{FugitiveCount}";
        seekerCount.text = $"{seekers}/{HunterCount}";
        Open(openHider, hiderRows, FugitiveCount - hiders);
        Open(openSeeker, seekerRows, HunterCount - seekers);
    }

    private static void Open(List<GameObject> pool, RectTransform rows, int wanted)
    {
        wanted = Mathf.Max(0, wanted);
        while (pool.Count < wanted) pool.Add(OpenRow(rows));
        for (int i = 0; i < pool.Count; i++)
        {
            pool[i].SetActive(i < wanted);
            pool[i].transform.SetAsLastSibling();
        }
    }

    private void BindColumn(Transform box, List<Slot> list)
    {
        list.Clear();

        if (box == null)
        {
            return;
        }

        int i = 0;
        Transform slotT = box.Find("Slot" + i);
        while (slotT != null)
        {
            Transform nameT = slotT.Find("Name");

            Slot slot = new Slot();
            slot.go = slotT.gameObject;
            slot.bg = slotT.GetComponent<Image>();
            slot.label = (nameT != null) ? nameT.GetComponent<Text>() : null;
            list.Add(slot);

            i++;
            slotT = box.Find("Slot" + i);
        }
    }

    private void BuildUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }

        bool canvasCreated;
        canvasGO = SceneUI.GetOrCreate("MatchmakingCanvas", null, out canvasCreated);

        Canvas canvasComp = SceneUI.GetOrAdd<Canvas>(canvasGO);
        canvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasComp.sortingOrder = 38;

        CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);
        scaler.matchWidthOrHeight = 1f;

        Transform canvasT = canvasGO.transform;

        Image dim = NewImage("Dim", canvasT, null, new Color(0f, 0f, 0f, 0.4f));
        Stretch(dim);

        Image panel = NewImage("Panel", canvasT, panelSprite, Color.white);
        Place(panel, 0f, 0f, 1331f, 736f);
        Transform panelT = panel.transform;

        Image title = NewImage("Title", panelT, titleSprite, Color.white);
        title.preserveAspect = true;
        Place(title, 0f, 288f, 520f, 73f);

        Text counter = NewText("Counter", panelT, "", 32, Color.white, TextAnchor.MiddleCenter, true);
        counterText = counter;
        Place(counter, 0f, 230f, 540f, 44f);

        Image barTrackImg = NewImage("BarTrack", panelT, barTrack, Color.white);
        Place(barTrackImg, 0f, 190f, 1150f, 22f);

        Image barFillImgLocal = NewImage("BarFill", barTrackImg.transform, barFill, Color.white);
        barFillImgLocal.type = Image.Type.Filled;
        barFillImgLocal.fillMethod = Image.FillMethod.Horizontal;
        barFillImgLocal.fillOrigin = (int)Image.OriginHorizontal.Left;
        barFillImgLocal.fillAmount = 0f;
        Stretch(barFillImgLocal);
        barFillImg = barFillImgLocal;

        BuildColumn(panelT, false, -368f);
        BuildColumn(panelT, true, 72f);

        Image divV = NewImage("DivV", panelT, dividerV, Color.white);
        Place(divV, 290f, -78f, 4f, 400f);

        Image mapBorderImg = NewImage("MapBorder", panelT, mapBorder, Color.white);
        mapBorderImg.preserveAspect = true;
        Place(mapBorderImg, 470f, -62f, 372f, 386f);

        Image map = NewImage("Map", mapBorderImg.transform, mapSprite, Color.white);
        Place(map, 0f, 0f, 344f, 358f);

        Text mapLabel = NewText("MapLabel", panelT, "Map game", 30, Color.white, TextAnchor.MiddleCenter, true);
        Place(mapLabel, 470f, -268f, 360f, 40f);
        mapLabelText = mapLabel;

        Image divH = NewImage("DivH", panelT, dividerH, Color.white);
        Place(divH, 0f, -300f, 1240f, 4f);

        Text hint = NewText("Hint", panelT, "", 27, new Color(1f, 1f, 1f, 0.6f), TextAnchor.MiddleCenter, false);
        hintText = hint;
        Place(hint, 0f, -332f, 1240f, 44f);

        BuildTopBar();

        canvasGO.SetActive(false);
    }

    private void BuildColumn(Transform panel, bool hunter, float cx)
    {
        string boxName = hunter ? "HunterBox" : "FugitiveBox";
        Image box = NewImage(boxName, panel, columnBoxSprite, Color.white);
        Place(box, cx, -78f, 420f, 405f);
        Transform boxT = box.transform;

        Sprite badgeSprite = hunter ? hunterBadgeSprite : fugitiveBadgeSprite;
        Image badge = NewImage("Icon", boxT, badgeSprite, Color.white);
        badge.preserveAspect = true;
        Place(badge, -120f, 158f, 52f, 48f);

        Sprite titleSpr = hunter ? hunterTitleSprite : fugitiveTitleSprite;
        Image label = NewImage("Label", boxT, titleSpr, Color.white);
        label.preserveAspect = true;

        int slotCount;
        if (hunter)
        {
            Place(label, 0f, 158f, 150f, 40f);
            slotCount = hunterSlots;
        }
        else
        {
            Place(label, 14f, 158f, 172f, 46f);
            slotCount = totalPlayers - hunterSlots;
        }

        List<Slot> list = hunter ? hunSlots : fugSlots;
        list.Clear();

        for (int i = 0; i < slotCount; i++)
        {
            Image slotBg = NewImage("Slot" + i, boxT, nameGray, Color.white);
            Place(slotBg, 0f, i * -62f + 92f, 368f, 56f);

            Text nameLabel = NewText("Name", slotBg.transform, "", 30, new Color(0.25f, 0.25f, 0.25f, 1f), TextAnchor.MiddleCenter, true);
            Stretch(nameLabel);

            slotBg.gameObject.SetActive(false);

            Slot slot = new Slot();
            slot.go = slotBg.gameObject;
            slot.bg = slotBg;
            slot.label = nameLabel;
            list.Add(slot);
        }
    }

    private void BuildTopBar()
    {
        Transform canvasT = canvasGO.transform;

        Image coinPanelImg = NewImage("CoinPanel", canvasT, coinPanel, Color.white);
        PlaceTR(coinPanelImg, -436f, -34f, 250f, 60f, new Vector2(0f, 1f));

        Image coinIconImg = NewImage("CoinIcon", canvasT, coinIcon, Color.white);
        coinIconImg.preserveAspect = true;
        PlaceTR(coinIconImg, -466f, -26f, 74f, 76f, new Vector2(0f, 1f));

        Text coinText = NewText("CoinText", canvasT, CoinBalance.Text, 34, Color.white, TextAnchor.MiddleCenter, true);
        PlaceTR(coinText, -386f, -38f, 200f, 50f, new Vector2(0f, 1f));
        coinLabelText = coinText;
        CoinBalance.Bind(coinLabelText);

        Image plusBtn = NewImage("PlusBtn", canvasT, plusIcon, Color.white);
        plusBtn.preserveAspect = true;
        PlaceTR(plusBtn, -186f, -30f, 56f, 58f, new Vector2(0f, 1f));

        Image settingsBtn = NewImage("SettingsBtn", canvasT, settingsIcon, Color.white);
        settingsBtn.preserveAspect = true;
        PlaceTR(settingsBtn, -30f, -30f, 76f, 78f, new Vector2(1f, 1f));

        Image adBtn = NewImage("AdBtn", canvasT, adIcon, Color.white);
        adBtn.preserveAspect = true;
        PlaceTR(adBtn, -30f, -120f, 76f, 78f, new Vector2(1f, 1f));
    }

    private static Image NewImage(string n, Transform parent, Sprite spr, Color col)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = col;
        if (spr != null)
        {
            img.sprite = spr;
        }
        return img;
    }

    private Text NewText(string n, Transform parent, string content, int size, Color col, TextAnchor anchor, bool bold)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Text text = go.AddComponent<Text>();
        text.font = font;
        text.text = content;
        text.fontSize = size;
        text.color = col;
        text.alignment = anchor;
        text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        text.supportRichText = true;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    // Anchors and pivots to the center of the parent - used for the main panel and its children.
    private static void Place(Component c, float x, float y, float w, float h)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    // Anchors to the top-right corner of the parent, with a caller-supplied pivot.
    private static void PlaceTR(Component c, float x, float y, float w, float h, Vector2 pivot)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMax = new Vector2(1f, 1f);
        rt.anchorMin = new Vector2(1f, 1f);
        rt.pivot = pivot;
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void Stretch(Component c)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}