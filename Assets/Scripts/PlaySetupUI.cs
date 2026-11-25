using System;
using System.Collections.Generic;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Play setup (2026-11-21; design B's full page since 2026-11-24): every choice before a match on one
// page, for Single Play and Multiple Play alike, over the side screen (RoleSelectCanvas, whose Back,
// Settings, Rate and Shop it takes the place of):
//
//   Single Player / Multiplayer   the title: the mode chosen on Home, kept for the whole flow.
//                                  Multiplayer is a hub: New Match (this page), Matches, History,
//                                  Profile and Rate are tabs under the bar (MultiplayerHub)
//   ROLE        Hider / Seeker
//   CHARACTER   the bodies; a tap makes it the player's body at once (CharacterSelect swaps it in place)
//   ROOM TYPE   Multiplayer only: the server's rooms (Practice free with bots, Standard paid)
//   MAP         the maps' pictures; online, a lock and price on one not bought yet
//   the map     a large picture of the map chosen, on the right
//   the foot    what Play starts, and Play / Find match
//
// Nothing about a match changes: Single Play goes on as it did after the map picker
// (HomeUI.PlaySingle), Multiple Play through the same checks as before (OnlineLobby.FindFromSetup:
// a paid room's entry and a locked map's purchase are still confirmed before anything is spent).
// The choices are remembered: the side, room and map here, the character where it always was
// (CharacterSelect). The map is set on LevelManager when picked and again on Play, since going Home
// puts LevelManager back on the first map.
public class PlaySetupUI : MonoBehaviour
{
    private const string SideKey = "PlaySetup.Side";   // HomeUI's modes: 1 seeker, 2 hider
    private const string RoomKey = "PlaySetup.Room";   // the room id last picked online
    private const string MapKey = "PlaySetup.Map";     // the map last picked (LevelManager's index)

    private const float CardRadius = 22f;
    private const float ChoiceHeight = 96f;
    private const float MapHeight = 118f;

    public static PlaySetupUI Instance { get; private set; }

    /// <summary>Whether the setup page is on screen.</summary>
    public static bool IsOpen => Instance != null && Instance.root != null && Instance.root.activeSelf;

    /// <summary>Multiple Play's flow while it is the one shown (null in Single Play).</summary>
    public OnlineLobby Lobby => lobby;

    private sealed class Card
    {
        public Button Button;
        public PaintboxFrame Frame;
        public Image Check;          // the tick of a picked choice
        public Image Ring;           // a map's picked ring
        public GameObject Badge;     // a map's picked badge
        public Image Picture;
        public Text Title;
        public Text Detail;
        public Image Icon;
        public GameObject Veil;      // "Lock": over what isn't owned
        public Text VeilText;
        public RectTransform VeilIcon;   // a choice's padlock, just before its words
        public GameObject Pill;
        public Text PillText;
        public bool Picked;
    }

    private HomeUI home;
    private CharacterSelect characters;
    private LevelManager levels;
    private GameObject roleCanvas;

    private OnlineLobby lobby;              // Multiple Play's flow; null in Single Play
    private bool Online => lobby != null;
    private int side;                       // 1 seeker, 2 hider
    private MetaRoom[] rooms;
    private string roomsNote = "";
    private string message = "";

    // UI
    private GameObject root;
    private CanvasGroup group;
    private PaintboxPage page;
    private float openedAt = -1f;
    private Text title;
    private GameObject tabsRow, offlineChip, balance, shopButton, rateButton;
    private Card hiderCard, seekerCard;
    private RectTransform roomRow;
    private GameObject roomSection;
    private Text roomStatus;
    private RectTransform characterRow, mapRow;
    private Button playButton;
    private Image heroPicture;
    private Text heroKicker, heroTitle, heroLine;
    private Image heroLineIcon;
    private Text summary;
    private readonly List<Card> characterCards = new List<Card>();
    private readonly List<Card> roomCards = new List<Card>();
    private readonly List<Card> mapCards = new List<Card>();
    private readonly List<(RectTransform rt, float at)> pops = new List<(RectTransform, float)>();

    // ================= Opening =================

    /// <summary>The setup, made the first time it is needed.</summary>
    public static PlaySetupUI Get()
    {
        if (Instance == null) Instance = new GameObject("PlaySetup").AddComponent<PlaySetupUI>();
        return Instance;
    }

    /// <summary>Home's Play: the setup in Single Play.</summary>
    public static void OpenSingle() => Get().Open(null, "");

    /// <summary>Multiple Play's choices (OnlineLobby, signed in): rooms follow with SetRooms.</summary>
    public void ShowOnline(OnlineLobby flow, string note)
    {
        Open(flow, note);
    }

    /// <summary>Puts the page away (a match is starting, the player went home).</summary>
    public static void CloseIfOpen()
    {
        if (Instance != null && Instance.root != null && Instance.root.activeSelf)
        {
            Instance.root.SetActive(false);
            MultiplayerHub.CloseAll();
        }
    }

    /// <summary>A side picked elsewhere (the side screen's own buttons, hidden while this is up).</summary>
    public static bool TrySelectRole(int mode)
    {
        if (!IsOpen) return false;
        Instance.PickSide(mode);
        return true;
    }

    private void Awake()
    {
        Instance = this;
        home = FindAnyObjectByType<HomeUI>();
        characters = FindAnyObjectByType<CharacterSelect>();
        levels = LevelManager.Instance != null ? LevelManager.Instance : FindAnyObjectByType<LevelManager>();
        roleCanvas = SceneObject("RoleSelectCanvas");
        side = PlayerPrefs.GetInt(SideKey, 2) == 1 ? 1 : 2;
        Build();
        root.SetActive(false);
        ShopInventory.Changed += HandleShopChanged;
    }

    private void OnDestroy()
    {
        ShopInventory.Changed -= HandleShopChanged;
        if (Instance == this) Instance = null;
    }

    private void Open(OnlineLobby flow, string note)
    {
        if (flow != lobby)
        {
            rooms = null;                   // another flow's rooms are not this one's
            BuildRoomCards();
        }
        lobby = flow;
        message = note ?? "";
        roomsNote = Online && rooms == null ? "Loading rooms..." : roomsNote;
        if (home != null) home.ShowPlaySetup();
        HideOldRoleButtons();
        bool wasOpen = root.activeSelf;
        root.SetActive(true);
        if (!wasOpen) openedAt = Time.unscaledTime;   // fades in
        Refresh();
    }

    // The side screen's own Hider / Seeker buttons: this page is where the side is chosen now.
    private void HideOldRoleButtons()
    {
        if (roleCanvas == null) return;
        foreach (string name in new[] { "PlayNowBtn", "SeekerBtn" })
        {
            Transform t = FindUnder(roleCanvas.transform, name);
            if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
        }
    }

    private void Update()
    {
        if (roleCanvas == null || root == null) return;

        // The side screen closed under the page (its Back, Settings' Home): the page goes with it,
        // and Multiple Play's flow ends. The side screen shown by someone else: the page comes too.
        bool screen = roleCanvas.activeInHierarchy;
        if (root.activeSelf && !screen)
        {
            root.SetActive(false);
            MultiplayerHub.CloseAll();
            if (Online) OnlineLobby.RoleSelectClosed();
        }
        else if (!root.activeSelf && screen && !LoadingScreen.Holding)
        {
            Open(OnlineLobby.InSetup, "");
        }

        if (root.activeSelf) Animate();
    }

    // The page fades in; a choice just picked pops into place.
    private void Animate()
    {
        if (openedAt >= 0f)
        {
            float t = Mathf.Clamp01((Time.unscaledTime - openedAt) / 0.22f);
            group.alpha = 1f - Mathf.Pow(1f - t, 3f);
            if (t >= 1f) openedAt = -1f;
        }

        for (int i = pops.Count - 1; i >= 0; i--)
        {
            (RectTransform rt, float at) = pops[i];
            float t = Mathf.Clamp01((Time.unscaledTime - at) / 0.18f);
            if (rt != null) rt.localScale = Vector3.one * Mathf.Lerp(1.15f, 1f, 1f - (1f - t) * (1f - t));
            if (t >= 1f || rt == null) pops.RemoveAt(i);
        }
    }

    private void HandleShopChanged()
    {
        if (IsOpen) Refresh();   // a character or map bought from the Shop button unlocks here at once
    }

    // ================= Multiple Play's data =================

    /// <summary>The server's rooms (cheapest first), or none with a note on why.</summary>
    public void SetRooms(MetaRoom[] list, string note)
    {
        rooms = list;
        roomsNote = note ?? "";
        if (rooms != null)
        {
            Array.Sort(rooms, (a, b) => a.EntryCents.CompareTo(b.EntryCents));
            if (rooms.Length == 0) roomsNote = "The server has no rooms configured.";
        }
        BuildRoomCards();
        Refresh();
    }

    /// <summary>A line over Play (a refusal, an error); empty clears it.</summary>
    public void SetMessage(string text)
    {
        message = text ?? "";
        Refresh();
    }

    private int RoomIndex
    {
        get
        {
            if (rooms == null || rooms.Length == 0) return -1;
            string saved = PlayerPrefs.GetString(RoomKey, "");
            for (int i = 0; i < rooms.Length; i++) if (rooms[i].Id == saved) return i;
            return 0;   // the cheapest: Practice, free, the quickest to start
        }
    }

    private bool HasRoom => RoomIndex >= 0;
    private MetaRoom Room => rooms[RoomIndex];

    // ================= Choosing =================

    private void PickSide(int mode)
    {
        side = mode == 1 ? 1 : 2;
        PlayerPrefs.SetInt(SideKey, side);
        Refresh();
    }

    private void PickCharacter(int index)
    {
        AudioManager.ButtonTap();
        if (Online && lobby.CharacterLocked(index))
        {
            message = "Not unlocked online. Get it in the Shop.";
            Refresh();
            return;
        }
        message = "";
        if (characters != null) characters.Select(index);   // the player's body changes at once (swapped in place)
        Refresh();
    }

    private void PickRoom(int index)
    {
        AudioManager.ButtonTap();
        if (rooms == null || index < 0 || index >= rooms.Length) return;
        PlayerPrefs.SetString(RoomKey, rooms[index].Id);
        message = "";
        Refresh();
    }

    private void PickMap(int index)
    {
        AudioManager.ButtonTap();
        if (!MapOpen(index)) return;
        message = "";
        PlayerPrefs.SetInt(MapKey, index);
        levels.SetLevel(index);
        Refresh();
    }

    private void Play()
    {
        AudioManager.ButtonTap();
        if (!Online)
        {
            CloseIfOpen();
            if (MapCount > 0) levels.SetLevel(MapShown);   // Home put LevelManager back on the first map
            if (home != null) home.PlaySingle(side);
            return;
        }

        if (!HasRoom)
        {
            message = string.IsNullOrEmpty(roomsNote) ? "Loading rooms..." : roomsNote;
            Refresh();
            return;
        }
        int character = characters != null ? characters.CurrentIndex : 0;
        if (lobby.CharacterLocked(character))
        {
            message = "Not unlocked online. Get it in the Shop.";
            Refresh();
            return;
        }

        message = "";
        lobby.FindFromSetup(side == 1 ? RolePreference.Seeker : RolePreference.Hider, Room, MapShown);
    }

    // The bar's Back: the side screen's own Back (Home, and out of Multiple Play), as before.
    private void Back()
    {
        Button back = roleCanvas != null ? FindUnder(roleCanvas.transform, "BackBtn")?.GetComponent<Button>() : null;
        if (back != null) { back.onClick.Invoke(); return; }
        AudioManager.ButtonTap();
        FindAnyObjectByType<HomeMenuUI>()?.ShowHome();
    }

    // ================= Maps =================

    private int MapCount => levels != null ? levels.Count : 0;

    // The map a room is always played on, or -1 when the player picks.
    private int FixedMap
    {
        get
        {
            if (!Online || !HasRoom || string.IsNullOrEmpty(Room.Map) || levels == null) return -1;
            for (int i = 0; i < levels.Count; i++) if (levels.RoomNameAt(i) == Room.Map) return i;
            return -1;
        }
    }

    // Whether the map can be picked now: any in Single Play; online, one the server has, and only
    // the room's own map in a room that fixes it.
    private bool MapOpen(int index)
    {
        if (index < 0 || index >= MapCount) return false;
        if (!Online) return true;
        int fixedMap = FixedMap;
        if (fixedMap >= 0) return index == fixedMap;
        return lobby.MapOnline(index);
    }

    // The map the page shows as chosen: the saved one, or the room's own, or the first open one.
    private int MapShown
    {
        get
        {
            int fixedMap = FixedMap;
            if (fixedMap >= 0) return fixedMap;
            int current = Mathf.Clamp(PlayerPrefs.GetInt(MapKey, levels != null ? levels.Current : 0), 0, Mathf.Max(0, MapCount - 1));
            if (MapOpen(current)) return current;
            for (int i = 0; i < MapCount; i++) if (MapOpen(i)) return i;
            return current;
        }
    }

    // ================= Showing =================

    private void Refresh()
    {
        if (root == null) return;

        bool online = Online;
        title.text = online ? "Multiplayer" : "Single Player";
        if (tabsRow != null) tabsRow.SetActive(online);
        if (offlineChip != null) offlineChip.SetActive(!online);
        if (balance != null) balance.SetActive(online);
        if (shopButton != null) shopButton.SetActive(online);
        if (rateButton != null) rateButton.SetActive(!online);
        page.Content.offsetMax = new Vector2(-Paintbox.Gutter, -(online ? PaintboxPage.TabsTop + PaintboxPage.TabsHeight + 14f : PaintboxPage.TabsTop));

        SetPicked(hiderCard, side == 2);
        SetPicked(seekerCard, side == 1);

        int character = characters != null ? characters.CurrentIndex : 0;
        for (int i = 0; i < characterCards.Count; i++)
        {
            Card c = characterCards[i];
            bool locked = online && lobby.CharacterLocked(i);
            SetPicked(c, i == character);
            c.Veil.SetActive(locked);
            c.VeilText.text = locked ? "In the Shop" : "";
            if (locked)
            {
                c.Check.gameObject.SetActive(false);   // the veil's words are where the tick goes
                if (c.VeilIcon != null) c.VeilIcon.anchoredPosition = new Vector2(-(22f + c.VeilText.preferredWidth + 10f), 0f);
            }
        }

        int roomIndex = RoomIndex;
        roomSection.SetActive(online);
        for (int i = 0; i < roomCards.Count; i++) SetPicked(roomCards[i], i == roomIndex);
        roomStatus.text = roomCards.Count == 0 ? roomsNote : "";
        roomStatus.gameObject.SetActive(roomCards.Count == 0);

        int shown = MapShown;
        int fixedMap = FixedMap;
        for (int i = 0; i < mapCards.Count; i++)
        {
            Card c = mapCards[i];
            bool open = MapOpen(i);
            long price = online && open ? lobby.MapPrice(i) : 0;
            SetPicked(c, i == shown && open);
            c.Button.interactable = open;
            c.Veil.SetActive(!open || price > 0);
            c.VeilText.text = !open ? (fixedMap >= 0 ? "" : "Not online yet") : "";
            c.Pill.SetActive(price > 0);
            c.PillText.text = price > 0 ? OnlineUI.CoinsText(price) : "";
        }

        RefreshHero(shown, online && MapOpen(shown) ? lobby.MapPrice(shown) : 0, fixedMap >= 0);
        RefreshFoot(online, character);
    }

    // The large picture of the map chosen, with its name.
    private void RefreshHero(int map, long price, bool setByRoom)
    {
        MapSelectPopup picker = MapSelectPopup.Instance;
        Sprite picture = picker != null && map < MapCount ? picker.ThumbnailAt(map) : null;
        heroPicture.sprite = picture != null ? picture : Paintbox.Art("studio");
        CoverFit(heroPicture);
        heroKicker.text = L.F("MAP {0}", map + 1);
        heroTitle.text = MapCount > 0 ? L.T(levels.DisplayNameAt(map)) : "";
        heroLine.text = price > 0 ? L.F("Locked · {0}", OnlineUI.CoinsText(price))
                      : setByRoom ? "Set by this room" : "Selected map";
        heroLineIcon.sprite = Paintbox.Icon(price > 0 ? "Lock" : "MapPin");
    }

    // The foot: what Play starts (or why it can't), and Play / Find match.
    private void RefreshFoot(bool online, int character)
    {
        var who = new List<string> { L.T(side == 1 ? "Seeker" : "Hider") };
        if (characters != null && characters.buttonLabels != null && character < characters.buttonLabels.Length)
            who.Add(L.T(characters.buttonLabels[character]));
        page.FootTitle.text = string.Join(" · ", who);

        // Online on a character this account doesn't own (picked for Single Play, say): said at once,
        // not only when Find Match refuses it.
        string note = message;
        if (string.IsNullOrEmpty(note) && online && lobby.CharacterLocked(character))
            note = "Not unlocked online. Pick another character, or get it in the Shop.";

        if (string.IsNullOrEmpty(note))
        {
            summary.text = Summary();
            summary.color = Paintbox.Muted;
        }
        else
        {
            summary.text = note;
            summary.color = Paintbox.Bad;
        }

        Paintbox.SetText(playButton, online ? "Find match" : "Play");
        Image icon = playButton.GetComponentInChildren<Image>(true);
        foreach (Image i in playButton.GetComponentsInChildren<Image>(true))
            if (i.name == "Icon") i.sprite = Paintbox.Icon(online ? "Search" : "Play");
    }

    // "Room 2 · Practice · Entry 20 coins" / "Room · Local round": what Play starts, each part in the
    // player's language (the line as a whole is no translation's key).
    private string Summary()
    {
        var parts = new List<string>();
        if (MapCount > 0) parts.Add(L.T(levels.DisplayNameAt(MapShown)));
        if (Online && HasRoom)
        {
            parts.Add(L.T(RoomTitle(Room.Id)));
            if (Room.EntryCents > 0) parts.Add(L.F("Entry {0}", OnlineUI.CoinsText(Room.EntryCents)));
        }
        else if (!Online) parts.Add(L.T("Local round"));
        return string.Join(" · ", parts);
    }

    // Picked: violet with an ink outline and a tick (a map: a yellow ring and a badge), popping in.
    private void SetPicked(Card c, bool picked)
    {
        if (c == null) return;
        if (c.Ring != null)
        {
            c.Ring.gameObject.SetActive(picked);
            c.Badge.SetActive(picked);
            if (picked && !c.Picked) pops.Add(((RectTransform)c.Badge.transform, Time.unscaledTime));
        }
        else
        {
            c.Frame.Fill.color = picked ? Paintbox.Violet : Paintbox.Paper;
            c.Frame.Edge.color = picked ? Paintbox.Ink : Paintbox.PanelLine;
            c.Check.gameObject.SetActive(picked);
            if (picked && !c.Picked) pops.Add((c.Check.rectTransform, Time.unscaledTime));
        }
        c.Picked = picked;
    }

    // ================= Building =================

    private void Build()
    {
        Transform canvas = OnlineUI.CreateCanvas("PlaySetupCanvas", transform, 44).transform;   // over the side screen (40), under the shop

        RectTransform holder = Paintbox.Fill(Paintbox.NewRect(canvas, "PlaySetupScreen"));
        root = holder.gameObject;
        group = root.AddComponent<CanvasGroup>();

        page = PaintboxPage.Build(holder, "PlaySetup", "Single Player", Back,
                                  PaintboxPage.Top.Offline | PaintboxPage.Top.Balance | PaintboxPage.Top.Shop | PaintboxPage.Top.Settings,
                                  HubTab.NewMatch, foot: true);
        title = page.Title;
        title.name = "SetupTitle";
        tabsRow = page.Safe.Find("HubTabs")?.gameObject;
        offlineChip = page.Right.Find("OfflineChip")?.gameObject;
        balance = page.Right.Find("PageBalance")?.gameObject;
        shopButton = page.Right.Find("PageShop")?.gameObject;
        // No Rate here (2026-11-25): Single Play shows nothing of the server's, and the rating is.
        // Online, Rate is one of Multiplayer's tabs.
        rateButton = null;

        summary = page.FootDetail;
        summary.name = "SetupSummary";
        playButton = page.FootButton("Play", Paintbox.Tone.Primary, "Play", Play, 300f);
        playButton.name = "SetupPlay";

        // The choices on the left, the map's picture on the right.
        RectTransform choices = Paintbox.NewRect(page.Content, "Choices");
        choices.anchorMin = new Vector2(0f, 0f);
        choices.anchorMax = new Vector2(0.64f, 1f);
        choices.offsetMin = Vector2.zero;
        choices.offsetMax = new Vector2(-12f, 0f);
        VerticalLayoutGroup column = choices.gameObject.AddComponent<VerticalLayoutGroup>();
        column.childControlWidth = column.childControlHeight = true;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;
        column.spacing = 8f;

        // The side.
        RectTransform sides = Section(choices, "ROLE", "SetupSides", ChoiceHeight);
        hiderCard = ChoiceCard(sides, "HiderChoice", "Hider", "Paint and blend in", "PaintRoller", null,
                               () => { AudioManager.ButtonTap(); PickSide(2); });
        seekerCard = ChoiceCard(sides, "SeekerChoice", "Seeker", "Find the Hiders", "ScanEye", null,
                                () => { AudioManager.ButtonTap(); PickSide(1); });

        // The character.
        characterRow = Section(choices, "CHARACTER", "SetupCharacters", ChoiceHeight);
        int count = characters != null && characters.characterPrefabs != null ? characters.characterPrefabs.Length : 0;
        for (int i = 0; i < count; i++)
        {
            int index = i;
            string label = characters.buttonLabels != null && i < characters.buttonLabels.Length ? characters.buttonLabels[i] : "Player " + (i + 1);
            Card c = ChoiceCard(characterRow, "Character_" + i, label, null, null, Portrait(i) ?? Paintbox.Art(i == 0 ? "avatar" : "avatar2"),
                                () => PickCharacter(index));
            characterCards.Add(c);
        }

        // The room (Multiple Play).
        roomRow = Section(choices, "ROOM TYPE", "SetupRooms", ChoiceHeight);
        roomSection = roomRow.parent.gameObject;
        roomStatus = Paintbox.Label(roomRow, "", 26f, Paintbox.Muted);
        roomStatus.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        // The map.
        mapRow = Section(choices, "MAP", "SetupMaps", MapHeight);
        mapRow.GetComponent<HorizontalLayoutGroup>().spacing = 12f;
        MapSelectPopup picker = MapSelectPopup.Instance;
        for (int i = 0; i < MapCount; i++)
        {
            int index = i;
            Card c = MapCard(mapRow, "Map_" + levels.RoomNameAt(i), levels.DisplayNameAt(i), picker != null ? picker.ThumbnailAt(i) : null,
                             () => PickMap(index));
            mapCards.Add(c);
        }
        Spacer(choices, 1f);

        BuildHero(page.Content);
    }

    // A group of choices: its name in small capitals, then its row. Space spreads between groups.
    private RectTransform Section(RectTransform column, string label, string rowName, float rowHeight)
    {
        Spacer(column, 1f);
        RectTransform section = Paintbox.NewRect(column, "Section_" + rowName);
        VerticalLayoutGroup v = section.gameObject.AddComponent<VerticalLayoutGroup>();
        v.childControlWidth = v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        v.spacing = 8f;

        Text kicker = PaintboxPage.Kicker(section, label);
        kicker.gameObject.AddComponent<LayoutElement>().preferredHeight = 30f;

        RectTransform row = Paintbox.NewRect(section, rowName);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = rowHeight;
        HorizontalLayoutGroup h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 16f;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = true;
        h.childForceExpandHeight = true;
        return row;
    }

    private static void Spacer(Transform parent, float weight)
    {
        RectTransform s = Paintbox.NewRect(parent, "Space");
        LayoutElement le = s.gameObject.AddComponent<LayoutElement>();
        le.flexibleHeight = weight;
        le.minHeight = 6f;
    }

    // A choice: paper with a soft outline, its icon or picture, its name (and a line under it), and
    // a tick when picked (then violet with an ink outline).
    private Card ChoiceCard(Transform row, string name, string label, string line, string icon, Sprite picture, Action tap)
    {
        PaintboxFrame frame = Paintbox.Frame(row, name, Paintbox.Paper, CardRadius, Paintbox.Border, 0f, Paintbox.PanelLine);
        Button button = frame.Root.gameObject.AddComponent<Button>();
        button.targetGraphic = frame.Fill;
        button.transition = Selectable.Transition.None;
        Navigation none = button.navigation;
        none.mode = Navigation.Mode.None;
        button.navigation = none;
        button.onClick.AddListener(() => tap());
        frame.Root.gameObject.AddComponent<UiPress>();
        var c = new Card { Button = button, Frame = frame };
        Transform body = frame.Body;

        float textLeft = 28f;
        if (picture != null)
        {
            Image well = Paintbox.Rounded(body, "Thumb", Paintbox.Card, 16f);
            Paintbox.Pin(well.rectTransform, new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(68f, 68f));
            well.rectTransform.pivot = new Vector2(0f, 0.5f);
            c.Picture = Paintbox.Image(well.rectTransform, "Picture", picture, Color.white);
            c.Picture.preserveAspect = true;
            Paintbox.Fill(c.Picture.rectTransform, 4f, 4f, 4f, 4f);
            textLeft = 100f;
        }
        else if (icon != null)
        {
            c.Icon = Paintbox.IconImage(body, icon, 40f, Paintbox.Ink);
            Paintbox.Pin(c.Icon.rectTransform, new Vector2(0f, 0.5f), new Vector2(24f, 0f), new Vector2(40f, 40f));
            c.Icon.rectTransform.pivot = new Vector2(0f, 0.5f);
            textLeft = 82f;
        }

        c.Title = Paintbox.Label(body, label, 32f, Paintbox.Ink, Paintbox.Weight.Bold);
        Paintbox.Fit(c.Title, 18);
        RectTransform t = c.Title.rectTransform;
        t.anchorMin = new Vector2(0f, line != null ? 0.48f : 0f);
        t.anchorMax = new Vector2(1f, 1f);
        t.offsetMin = new Vector2(textLeft, 0f);
        t.offsetMax = new Vector2(-64f, line != null ? -8f : 0f);
        c.Title.alignment = line != null ? TextAnchor.LowerLeft : TextAnchor.MiddleLeft;

        if (line != null)
        {
            c.Detail = Paintbox.Label(body, line, 22f, Paintbox.Muted);
            Paintbox.Fit(c.Detail, 14);
            RectTransform d = c.Detail.rectTransform;
            d.anchorMin = new Vector2(0f, 0f);
            d.anchorMax = new Vector2(1f, 0.48f);
            d.offsetMin = new Vector2(textLeft, 8f);
            d.offsetMax = new Vector2(-64f, -2f);
            c.Detail.alignment = TextAnchor.UpperLeft;
        }

        c.Check = Paintbox.IconImage(body, "Check", 36f, Paintbox.Ink);
        c.Check.name = "Tick";
        Paintbox.Pin(c.Check.rectTransform, new Vector2(1f, 0.5f), new Vector2(-22f, 0f), new Vector2(36f, 36f));
        c.Check.rectTransform.pivot = new Vector2(1f, 0.5f);
        c.Check.gameObject.SetActive(false);

        // Not owned online: a veil with a lock and a word.
        Image veil = Paintbox.Rounded(frame.Root, "Lock", new Color(Paintbox.Ink.r, Paintbox.Ink.g, Paintbox.Ink.b, 0.62f), CardRadius);
        Paintbox.Fill(veil.rectTransform);
        Image padlock = Paintbox.IconImage(veil.rectTransform, "Lock", 32f, Color.white);
        Paintbox.Pin(padlock.rectTransform, new Vector2(1f, 0.5f), new Vector2(-150f, 0f), new Vector2(32f, 32f));
        c.VeilIcon = padlock.rectTransform;
        c.VeilText = Paintbox.Label(veil.rectTransform, "", 24f, Color.white, Paintbox.Weight.Bold, TextAnchor.MiddleRight);
        Paintbox.Fill(c.VeilText.rectTransform, 0f, 22f, 0f, 0f);
        c.Veil = veil.gameObject;
        c.Veil.SetActive(false);
        return c;
    }

    // A map: its picture filling the card, its name on a shade at the bottom; picked: a yellow ring
    // and a tick badge. Locked: a veil with a lock and its price.
    private Card MapCard(Transform row, string name, string label, Sprite picture, Action tap)
    {
        PaintboxFrame frame = Paintbox.Frame(row, name, Paintbox.Ink, 18f, 0f, 4f);
        Button button = frame.Root.gameObject.AddComponent<Button>();
        button.targetGraphic = frame.Fill;
        Navigation none = button.navigation;
        none.mode = Navigation.Mode.None;
        button.navigation = none;
        ColorBlock colors = button.colors;
        colors.disabledColor = Color.white;
        button.colors = colors;
        button.onClick.AddListener(() => tap());
        frame.Root.gameObject.AddComponent<UiPress>();
        var c = new Card { Button = button, Frame = frame };

        Image mask = Paintbox.Rounded(frame.Body, "Frame", Color.white, 18f);
        Paintbox.Fill(mask.rectTransform);
        mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        c.Picture = Paintbox.Image(mask.rectTransform, "Picture", picture, Color.white);
        Paintbox.Fill(c.Picture.rectTransform);
        c.Picture.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        if (picture != null) c.Picture.GetComponent<AspectRatioFitter>().aspectRatio = picture.rect.width / Mathf.Max(1f, picture.rect.height);

        Image shade = Paintbox.Image(mask.rectTransform, "Shade", null, Color.white);
        shade.gameObject.AddComponent<UiGradient>().Set(new Color(0f, 0f, 0f, 0f), new Color(0.13f, 0.08f, 0.16f, 0.85f));
        shade.rectTransform.anchorMin = new Vector2(0f, 0f);
        shade.rectTransform.anchorMax = new Vector2(1f, 0.55f);
        shade.rectTransform.offsetMin = shade.rectTransform.offsetMax = Vector2.zero;
        c.Title = Paintbox.Label(mask.rectTransform, label, 22f, Color.white, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
        Paintbox.Fit(c.Title, 13);
        c.Title.rectTransform.anchorMin = new Vector2(0f, 0f);
        c.Title.rectTransform.anchorMax = new Vector2(1f, 0f);
        c.Title.rectTransform.pivot = new Vector2(0.5f, 0f);
        c.Title.rectTransform.offsetMin = new Vector2(6f, 6f);
        c.Title.rectTransform.offsetMax = new Vector2(-6f, 36f);

        Image veil = Paintbox.Rounded(frame.Root, "Lock", new Color(Paintbox.Ink.r, Paintbox.Ink.g, Paintbox.Ink.b, 0.6f), 18f);
        Paintbox.Fill(veil.rectTransform);
        Image padlock = Paintbox.IconImage(veil.rectTransform, "Lock", 30f, Color.white);
        Paintbox.Pin(padlock.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(30f, 30f));
        padlock.rectTransform.pivot = new Vector2(0.5f, 1f);
        c.VeilText = Paintbox.Label(veil.rectTransform, "", 18f, Color.white, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
        Paintbox.Fit(c.VeilText, 12);
        Paintbox.Fill(c.VeilText.rectTransform, 6f, 6f, 46f, 40f);
        Image pill = Paintbox.Rounded(veil.rectTransform, "Price", Paintbox.Hot, 14f);
        pill.rectTransform.anchorMin = new Vector2(0.08f, 0f);
        pill.rectTransform.anchorMax = new Vector2(0.92f, 0f);
        pill.rectTransform.pivot = new Vector2(0.5f, 0f);
        pill.rectTransform.offsetMin = new Vector2(0f, 40f);
        pill.rectTransform.offsetMax = new Vector2(0f, 70f);
        c.PillText = Paintbox.Label(pill.rectTransform, "", 19f, Paintbox.HotInk, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
        Paintbox.Fit(c.PillText, 11);
        Paintbox.Fill(c.PillText.rectTransform, 4f, 4f, 0f, 0f);
        c.Pill = pill.gameObject;
        c.Pill.SetActive(false);
        c.Veil = veil.gameObject;
        c.Veil.SetActive(false);

        // Picked: a yellow ring and a tick on a yellow badge.
        c.Ring = Paintbox.Image(frame.Root, "Ring", UiArt.RingBold, Paintbox.Yellow);
        UiArt.Apply(c.Ring, UiArt.RingBold, 22f);
        Paintbox.Fill(c.Ring.rectTransform, -5f, -5f, -5f, -5f);
        c.Ring.gameObject.SetActive(false);
        Image badge = Paintbox.Image(frame.Root, "Badge", UiArt.Circle, Paintbox.Yellow);
        badge.rectTransform.anchorMin = badge.rectTransform.anchorMax = new Vector2(1f, 1f);
        badge.rectTransform.anchoredPosition = new Vector2(-14f, -14f);
        badge.rectTransform.sizeDelta = new Vector2(30f, 30f);
        Paintbox.IconImage(badge.rectTransform, "Check", 20f, Paintbox.Ink);
        c.Badge = badge.gameObject;
        c.Badge.SetActive(false);
        return c;
    }

    // The map chosen, large on the right: its picture, its number and name, and whether it is set.
    private void BuildHero(RectTransform content)
    {
        PaintboxFrame hero = Paintbox.Frame(content, "MapHero", Paintbox.Ink, 34f, Paintbox.Border, Paintbox.Shadow);
        hero.Root.anchorMin = new Vector2(0.655f, 0f);
        hero.Root.anchorMax = new Vector2(1f, 1f);
        hero.Root.offsetMin = new Vector2(0f, Paintbox.Shadow);
        hero.Root.offsetMax = Vector2.zero;

        Image mask = Paintbox.Rounded(hero.Fill.rectTransform, "Frame", Color.white, 30f);
        Paintbox.Fill(mask.rectTransform);
        mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        heroPicture = Paintbox.Image(mask.rectTransform, "Picture", null, Color.white);
        Paintbox.Fill(heroPicture.rectTransform);
        heroPicture.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;

        Image shade = Paintbox.Image(mask.rectTransform, "Shade", null, Color.white);
        shade.gameObject.AddComponent<UiGradient>().Set(new Color(0.13f, 0.08f, 0.16f, 0f), new Color(0.13f, 0.08f, 0.16f, 0.9f));
        shade.rectTransform.anchorMin = new Vector2(0f, 0f);
        shade.rectTransform.anchorMax = new Vector2(1f, 0.5f);
        shade.rectTransform.offsetMin = shade.rectTransform.offsetMax = Vector2.zero;

        heroKicker = Paintbox.Label(mask.rectTransform, "", Paintbox.TinySize, new Color(1f, 1f, 1f, 0.85f), Paintbox.Weight.Bold);
        Paintbox.Pin(heroKicker.rectTransform, new Vector2(0f, 0f), new Vector2(28f, 150f), new Vector2(500f, 30f));
        heroKicker.rectTransform.pivot = new Vector2(0f, 0f);
        heroTitle = Paintbox.Label(mask.rectTransform, "", 68f, Color.white, Paintbox.Weight.Title);
        Paintbox.Fit(heroTitle, 30);
        heroTitle.rectTransform.anchorMin = new Vector2(0f, 0f);
        heroTitle.rectTransform.anchorMax = new Vector2(1f, 0f);
        heroTitle.rectTransform.pivot = new Vector2(0.5f, 0f);
        heroTitle.rectTransform.offsetMin = new Vector2(28f, 62f);
        heroTitle.rectTransform.offsetMax = new Vector2(-28f, 148f);
        heroTitle.alignment = TextAnchor.LowerLeft;

        heroLineIcon = Paintbox.IconImage(mask.rectTransform, "MapPin", 28f, Color.white);
        Paintbox.Pin(heroLineIcon.rectTransform, new Vector2(0f, 0f), new Vector2(28f, 26f), new Vector2(28f, 28f));
        heroLineIcon.rectTransform.pivot = new Vector2(0f, 0f);
        heroLine = Paintbox.Label(mask.rectTransform, "", 25f, Color.white);
        Paintbox.Pin(heroLine.rectTransform, new Vector2(0f, 0f), new Vector2(66f, 22f), new Vector2(560f, 36f));
        heroLine.rectTransform.pivot = new Vector2(0f, 0f);
    }

    // A picture filling its frame without stretching: the longer side runs past the edges.
    private static void CoverFit(Image image)
    {
        AspectRatioFitter fit = image.GetComponent<AspectRatioFitter>();
        if (fit != null && image.sprite != null) fit.aspectRatio = image.sprite.rect.width / Mathf.Max(1f, image.sprite.rect.height);
    }

    // Room cards: the room's name and what it is for (free with bots, or its entry and real players).
    private void BuildRoomCards()
    {
        foreach (Card c in roomCards) Destroy(c.Button.gameObject);
        roomCards.Clear();
        if (rooms == null || roomRow == null) return;

        for (int i = 0; i < rooms.Length; i++)
        {
            int index = i;
            MetaRoom r = rooms[i];
            bool free = r.EntryCents == 0;
            Card c = ChoiceCard(roomRow, "Room_" + r.Id, RoomTitle(r.Id),
                                free ? L.T("Free · Bots fill empty seats") : L.F("{0} · Players only", OnlineUI.CoinsText(r.EntryCents)),
                                free ? "Gamepad2" : "Users", null, () => PickRoom(index));
            roomCards.Add(c);
        }
    }

    // ================= Pictures from the old screens =================

    // The character's picture on the old character screen (SelectPlayerCanvas), which stays in the
    // scene as the source of these pictures.
    private static Sprite Portrait(int index)
    {
        GameObject canvas = SceneObject("SelectPlayerCanvas");
        string name = index == 0 ? "BtnPlayer" : "BtnPlayer" + (index + 1);
        Transform button = canvas != null ? FindUnder(canvas.transform, name) : null;
        Image picture = button != null ? button.Find("Map")?.GetComponent<Image>() : null;
        return picture != null ? picture.sprite : null;
    }

    private static string RoomTitle(string id)
    {
        if (string.IsNullOrEmpty(id)) return "Room";
        string spaced = id.Replace('-', ' ');
        return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1);
    }

    private static GameObject SceneObject(string name)
    {
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t.name == name && t.gameObject.scene.IsValid()) return t.gameObject;
        return null;
    }

    private static Transform FindUnder(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }
}
