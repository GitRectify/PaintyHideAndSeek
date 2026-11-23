using System;
using System.Collections.Generic;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Play setup (2026-11-21): every choice before a match on one screen, for Single Play and Multiple
// Play alike. It replaces the walk through four screens (side, character, room, map) with one panel
// on the side screen (RoleSelectCanvas, which keeps its Back, Settings, Rate and Shop buttons and the
// home room behind it):
//
//   SINGLE PLAY | MULTIPLAYER     the mode; switching to Multiplayer signs in first
//   SIDE        Hider / Seeker
//   CHARACTER   the bodies, by their pictures; a tap makes it the player's body at once
//               (CharacterSelect swaps it in place, no scene reload)
//   ROOM        Multiplayer only: the server's rooms (Practice free with bots, Standard paid)
//   MAP         the maps' pictures; online, a lock and price on one not bought yet, and none the
//               server doesn't have
//   PLAY / FIND MATCH, with a line saying what was chosen
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
    private const string MapKey = "PlaySetup.Map";     // the map last picked (LevelManager's index): Home resets
                                                       // LevelManager to the first map (GameModeManager.ShowMenu)

    private const float PanelWidth = 860f;
    private const float Inner = 800f;
    private static readonly Color CardColor = new Color(0.19f, 0.22f, 0.30f, 1f);
    private static readonly Color CardDim = new Color(0.13f, 0.15f, 0.21f, 1f);
    private static readonly Color Highlight = new Color(1f, 0.84f, 0.32f, 1f);
    private static readonly Color Warning = new Color(1f, 0.78f, 0.35f, 1f);

    public static PlaySetupUI Instance { get; private set; }

    /// <summary>Whether the setup panel is on screen.</summary>
    public static bool IsOpen => Instance != null && Instance.panel != null && Instance.panel.activeSelf;

    private sealed class Card
    {
        public Button Button;
        public Image Background;
        public Image Picture;
        public Text Title;
        public Text Detail;
        public Image Lock;
        public Outline Frame;
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
    private GameObject panel, extras;
    private RectTransform panelRect;
    private Button tabSingle, tabOnline, playButton;
    private Card hiderCard, seekerCard;
    private Text sideCaption, characterCaption, roomCaption, mapCaption, roomStatus, summary;
    private RectTransform characterRow, roomRow, mapRow;
    private readonly List<Card> characterCards = new List<Card>();
    private readonly List<Card> roomCards = new List<Card>();
    private readonly List<Card> mapCards = new List<Card>();

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

    /// <summary>Puts the panel away (a match is starting, the player went home).</summary>
    public static void CloseIfOpen()
    {
        if (Instance != null && Instance.panel != null)
        {
            Instance.panel.SetActive(false);
            Instance.extras.SetActive(false);
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
        panel.SetActive(false);
        extras.SetActive(false);
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
        panel.SetActive(true);
        extras.SetActive(Online);
        Refresh();
    }

    // The side screen's own Hider / Seeker buttons: this panel is where the side is chosen now.
    private void HideOldRoleButtons()
    {
        if (roleCanvas == null) return;
        foreach (string name in new[] { "PlayNowBtn", "SeekerBtn" })
        {
            Transform t = FindUnder(roleCanvas.transform, name);
            if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
        }
    }

    // The side screen closed under the panel (its Back, Settings' Home): the panel goes with it, and
    // Multiple Play's flow ends. The side screen shown by someone else: the panel comes with it.
    private void Update()
    {
        if (roleCanvas == null || panel == null) return;
        bool screen = roleCanvas.activeInHierarchy;
        if (panel.activeSelf && !screen)
        {
            panel.SetActive(false);
            extras.SetActive(false);
            if (Online) OnlineLobby.RoleSelectClosed();
        }
        else if (!panel.activeSelf && screen && !LoadingScreen.Holding)
        {
            Open(OnlineLobby.InSetup, "");
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

    private void PickMode(bool online)
    {
        AudioManager.ButtonTap();
        if (online == Online) return;
        if (online)
        {
            // Signed in (at once when already), Multiple Play's flow opens this same panel online.
            OnlineSignIn.Open(profile => OnlineLobby.Open(profile, null), null);
            return;
        }
        OnlineLobby.LeaveForSinglePlay();
        Open(null, "");
    }

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

    // The map the panel shows as chosen: the saved one, or the room's own, or the first open one.
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
        if (panel == null) return;

        SetTab(tabSingle, !Online);
        SetTab(tabOnline, Online);

        SetChoice(hiderCard, side == 2, OnlineUI.Green);
        SetChoice(seekerCard, side == 1, OnlineUI.Blue);

        int character = characters != null ? characters.CurrentIndex : 0;
        for (int i = 0; i < characterCards.Count; i++)
        {
            Card c = characterCards[i];
            bool locked = Online && lobby.CharacterLocked(i);
            SetChoice(c, i == character, CardColor);
            c.Lock.gameObject.SetActive(locked);
            c.Picture.color = locked ? new Color(0.55f, 0.55f, 0.55f, 1f) : Color.white;
            c.Detail.text = locked ? "Locked" : "";
        }

        int roomIndex = RoomIndex;
        for (int i = 0; i < roomCards.Count; i++)
        {
            bool free = rooms != null && i < rooms.Length && rooms[i].EntryCents == 0;
            SetChoice(roomCards[i], i == roomIndex, free ? OnlineUI.Green : OnlineUI.Blue);
        }
        roomStatus.text = roomCards.Count == 0 ? roomsNote : "";

        int shown = MapShown;
        int fixedMap = FixedMap;
        for (int i = 0; i < mapCards.Count; i++)
        {
            Card c = mapCards[i];
            bool open = MapOpen(i);
            long price = Online ? lobby.MapPrice(i) : 0;
            SetChoice(c, i == shown && open, CardColor);
            c.Button.interactable = open;
            c.Picture.color = !open ? new Color(0.35f, 0.35f, 0.35f, 1f) : price > 0 ? new Color(0.62f, 0.62f, 0.62f, 1f) : Color.white;
            c.Lock.gameObject.SetActive(open && price > 0);
            c.Detail.text = !open ? (fixedMap >= 0 ? "" : "Not online yet") : price > 0 ? OnlineUI.CoinsText(price) : "";
        }
        mapCaption.text = fixedMap >= 0 ? "MAP  ·  set by this room" : "MAP";

        Layout();
        summary.text = string.IsNullOrEmpty(message) ? Summary() : $"<color=#FFC759>{message}</color>";

        if (!Online) OnlineUI.SetButtonText(playButton, "PLAY");
        else if (HasRoom && Room.EntryCents > 0) OnlineUI.SetButtonText(playButton, $"FIND MATCH\n<size=24>Entry {OnlineUI.CoinsText(Room.EntryCents)}</size>");
        else OnlineUI.SetButtonText(playButton, "FIND MATCH");
    }

    // "Hider · Player · Room 2 · Practice": what Play starts, each part in the player's language
    // (the line as a whole is no translation's key).
    private string Summary()
    {
        var parts = new List<string> { L.T(side == 1 ? "Seeker" : "Hider") };
        int character = characters != null ? characters.CurrentIndex : 0;
        if (characters != null && characters.buttonLabels != null && character < characters.buttonLabels.Length)
            parts.Add(L.T(characters.buttonLabels[character]));
        if (MapCount > 0) parts.Add(L.T(levels.DisplayNameAt(MapShown)));
        if (Online && HasRoom) parts.Add(L.T(RoomTitle(Room.Id)));
        return "<b>" + string.Join("  ·  ", parts) + "</b>";
    }

    // Sections from the top; Room only online, so Map moves up in Single Play and the panel is that
    // much shorter (its top comes down; Play stays at the bottom).
    private void Layout()
    {
        panelRect.offsetMax = new Vector2(-130f, Online ? -110f : -230f);
        float y = 112f;
        Place(sideCaption.rectTransform, 0f, y, Inner, 34f);
        y += 38f;
        PlaceRow(hiderCard, seekerCard, y);
        y += 108f + 16f;

        Place(characterCaption.rectTransform, 0f, y, Inner, 34f);
        y += 38f;
        Place(characterRow, 0f, y, Inner, 160f);
        y += 160f + 16f;

        roomCaption.gameObject.SetActive(Online);
        roomRow.gameObject.SetActive(Online);
        if (Online)
        {
            Place(roomCaption.rectTransform, 0f, y, Inner, 34f);
            y += 38f;
            Place(roomRow, 0f, y, Inner, 96f);
            y += 96f + 16f;
        }

        bool maps = mapCards.Count > 0;
        mapCaption.gameObject.SetActive(maps);
        mapRow.gameObject.SetActive(maps);
        if (maps)
        {
            Place(mapCaption.rectTransform, 0f, y, Inner, 34f);
            y += 38f;
            Place(mapRow, 0f, y, Inner, 118f);
        }
    }

    private static void SetTab(Button tab, bool on)
    {
        tab.targetGraphic.color = on ? OnlineUI.Blue : CardDim;
        Text t = tab.GetComponentInChildren<Text>();
        if (t != null) t.color = on ? Color.white : new Color(1f, 1f, 1f, 0.6f);
    }

    // A chosen card is lit and framed; the others sit back.
    private static void SetChoice(Card c, bool chosen, Color litColor)
    {
        c.Background.color = chosen ? litColor : CardDim;
        c.Frame.enabled = chosen;
    }

    // ================= Building =================

    private void Build()
    {
        Transform canvas = OnlineUI.CreateCanvas("PlaySetupCanvas", transform, 44).transform;   // over the side screen (40), under the Shop (45)

        Image bg = OnlineUI.NewImage("PlaySetup", canvas, OnlineUI.PanelColor);
        RectTransform rt = bg.rectTransform;
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.sizeDelta = new Vector2(PanelWidth, 0f);
        rt.offsetMin = new Vector2(-PanelWidth - 130f, 40f);   // clear of the side screen's button column on the right
        rt.offsetMax = new Vector2(-130f, -110f);              // and of the coin balance at the top
        Outline edge = bg.gameObject.AddComponent<Outline>();
        edge.effectColor = new Color(1f, 1f, 1f, 0.18f);
        edge.effectDistance = new Vector2(3f, -3f);
        panel = bg.gameObject;
        panelRect = rt;
        Transform p = panel.transform;

        // The mode.
        tabSingle = OnlineUI.NewButton(p, "SINGLE PLAY", Vector2.zero, new Vector2(395f, 74f), CardDim, () => PickMode(false));
        tabSingle.name = "SinglePlayTab";
        Place((RectTransform)tabSingle.transform, -202.5f, 22f, 395f, 74f);
        tabOnline = OnlineUI.NewButton(p, "MULTIPLAYER", Vector2.zero, new Vector2(395f, 74f), CardDim, () => PickMode(true));
        tabOnline.name = "MultiplayerTab";
        Place((RectTransform)tabOnline.transform, 202.5f, 22f, 395f, 74f);

        // The side.
        sideCaption = Caption(p, "SIDE");
        hiderCard = SideCard(p, "Hider", "Paint yourself and hide", RoleIcon("FugitiveBox"), () => { AudioManager.ButtonTap(); PickSide(2); });
        hiderCard.Button.name = "HiderChoice";
        seekerCard = SideCard(p, "Seeker", "Find and catch the hiders", RoleIcon("HunterBox"), () => { AudioManager.ButtonTap(); PickSide(1); });
        seekerCard.Button.name = "SeekerChoice";

        // The character.
        characterCaption = Caption(p, "CHARACTER");
        characterRow = Row(p, "SetupCharacters");
        int count = characters != null && characters.characterPrefabs != null ? characters.characterPrefabs.Length : 0;
        for (int i = 0; i < count; i++)
        {
            int index = i;
            string label = characters.buttonLabels != null && i < characters.buttonLabels.Length ? characters.buttonLabels[i] : "Player " + (i + 1);
            Card c = PictureCard(characterRow, label, Portrait(i), new Vector2(190f, 160f), () => PickCharacter(index));
            c.Button.name = "Character_" + i;
            characterCards.Add(c);
        }
        LineUp(characterCards, 190f, 16f);

        // The room (Multiple Play).
        roomCaption = Caption(p, "ROOM");
        roomRow = Row(p, "SetupRooms");
        roomStatus = OnlineUI.NewLabel(roomRow, "", 28, Vector2.zero, new Vector2(Inner, 90f), TextAnchor.MiddleLeft);
        roomStatus.color = OnlineUI.Muted;
        OnlineUI.Stretch(roomStatus.rectTransform);

        // The map.
        mapCaption = Caption(p, "MAP");
        mapRow = Row(p, "SetupMaps");
        MapSelectPopup picker = MapSelectPopup.Instance;
        for (int i = 0; i < MapCount; i++)
        {
            int index = i;
            Card c = PictureCard(mapRow, levels.DisplayNameAt(i), picker != null ? picker.ThumbnailAt(i) : null, new Vector2(150f, 118f), () => PickMap(index));
            c.Button.name = "Map_" + levels.RoomNameAt(i);
            mapCards.Add(c);
        }
        LineUp(mapCards, 150f, 12f);

        // What Play starts, and Play.
        summary = OnlineUI.NewLabel(p, "", 28, Vector2.zero, new Vector2(440f, 96f), TextAnchor.MiddleLeft);
        summary.name = "SetupSummary";
        RectTransform srt = summary.rectTransform;
        srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0f, 0f);
        srt.anchoredPosition = new Vector2(30f, 26f);
        summary.resizeTextForBestFit = true;
        summary.resizeTextMinSize = 18;
        summary.resizeTextMaxSize = 28;
        summary.verticalOverflow = VerticalWrapMode.Truncate;

        playButton = OnlineUI.NewButton(p, "PLAY", Vector2.zero, new Vector2(340f, 100f), OnlineUI.Green, Play);
        playButton.name = "SetupPlay";
        RectTransform prt = (RectTransform)playButton.transform;
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(1f, 0f);
        prt.anchoredPosition = new Vector2(-30f, 24f);
        playButton.GetComponentInChildren<Text>().fontSize = 40;

        // Multiple Play's other places, bottom left over the room: they open over this screen.
        extras = new GameObject("PlaySetupExtras", typeof(RectTransform));
        extras.transform.SetParent(canvas, false);
        RectTransform ert = (RectTransform)extras.transform;
        ert.anchorMin = ert.anchorMax = ert.pivot = Vector2.zero;
        ert.anchoredPosition = new Vector2(40f, 40f);
        ert.sizeDelta = new Vector2(220f, 336f);
        Extra("MATCHES", 3, new Color(0.85f, 0.52f, 0.12f, 0.97f), OnlineMatches.ShowList);
        Extra("WALLET", 2, OnlineUI.Green, () => lobby?.OpenWallet());
        Extra("HISTORY", 1, OnlineUI.Blue, () => lobby?.OpenHistory());
        Extra("PROFILE", 0, OnlineUI.Gray, () => lobby?.OpenAccount());
    }

    private void Extra(string text, int row, Color color, Action tap)
    {
        Button b = OnlineUI.NewButton(extras.transform, text, Vector2.zero, new Vector2(220f, 72f), color, () => tap());
        b.name = "Extra_" + text;
        b.GetComponentInChildren<Text>().fontSize = 28;
        RectTransform rt = (RectTransform)b.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
        rt.anchoredPosition = new Vector2(0f, row * 88f);
    }

    private static Text Caption(Transform parent, string text)
    {
        Text t = OnlineUI.NewLabel(parent, text, 26, Vector2.zero, new Vector2(Inner, 34f), TextAnchor.MiddleLeft, true);
        t.color = OnlineUI.Muted;
        return t;
    }

    private static RectTransform Row(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    // A child of the panel, centred at x, its top yTop down from the panel's top.
    private static void Place(RectTransform rt, float x, float yTop, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void PlaceRow(Card left, Card right, float yTop)
    {
        Place((RectTransform)left.Button.transform, -202.5f, yTop, 395f, 108f);
        Place((RectTransform)right.Button.transform, 202.5f, yTop, 395f, 108f);
    }

    // Cards side by side, centred in their row.
    private static void LineUp(List<Card> cards, float width, float gap)
    {
        float total = cards.Count * width + Mathf.Max(0, cards.Count - 1) * gap;
        float x = -total / 2f + width / 2f;
        foreach (Card c in cards)
        {
            RectTransform rt = (RectTransform)c.Button.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            x += width + gap;
        }
    }

    private Card SideCard(Transform parent, string title, string line, Sprite icon, Action tap)
    {
        Button b = OnlineUI.NewButton(parent, "", Vector2.zero, new Vector2(395f, 108f), CardDim, () => tap());
        var c = new Card { Button = b, Background = (Image)b.targetGraphic };
        c.Frame = Frame(b.gameObject);

        float textLeft = 28f;
        if (icon != null)
        {
            Image pic = OnlineUI.NewImage("Icon", b.transform, Color.white);
            pic.sprite = icon;
            pic.preserveAspect = true;
            pic.raycastTarget = false;
            RectTransform irt = pic.rectTransform;
            irt.anchorMin = irt.anchorMax = irt.pivot = new Vector2(0f, 0.5f);
            irt.anchoredPosition = new Vector2(20f, 0f);
            irt.sizeDelta = new Vector2(72f, 72f);
            c.Picture = pic;
            textLeft = 108f;
        }

        c.Title = OnlineUI.NewLabel(b.transform, title, 40, Vector2.zero, Vector2.zero, TextAnchor.LowerLeft, true);
        Band(c.Title.rectTransform, textLeft, 0.48f, 1f, -6f);
        c.Detail = OnlineUI.NewLabel(b.transform, line, 22, Vector2.zero, Vector2.zero, TextAnchor.UpperLeft);
        Band(c.Detail.rectTransform, textLeft, 0f, 0.48f, 2f);
        c.Detail.color = new Color(1f, 1f, 1f, 0.85f);
        c.Lock = LockBadge(b.transform);
        return c;
    }

    // A card with a picture (a character, a map), its name on a strip at the bottom, and a lock
    // with a line (the price) for one not owned online.
    private Card PictureCard(Transform parent, string title, Sprite picture, Vector2 size, Action tap)
    {
        Button b = OnlineUI.NewButton(parent, "", Vector2.zero, size, CardDim, () => tap());
        var c = new Card { Button = b, Background = (Image)b.targetGraphic };
        c.Frame = Frame(b.gameObject);

        Image pic = OnlineUI.NewImage("Picture", b.transform, Color.white);
        pic.sprite = picture;
        pic.preserveAspect = true;
        pic.raycastTarget = false;
        pic.enabled = picture != null;
        RectTransform prt = pic.rectTransform;
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(6f, 34f);
        prt.offsetMax = new Vector2(-6f, -6f);
        c.Picture = pic;

        Image strip = OnlineUI.NewImage("NameStrip", b.transform, new Color(0f, 0f, 0f, 0.45f));
        strip.raycastTarget = false;
        RectTransform srt = strip.rectTransform;
        srt.anchorMin = new Vector2(0f, 0f);
        srt.anchorMax = new Vector2(1f, 0f);
        srt.pivot = new Vector2(0.5f, 0f);
        srt.sizeDelta = new Vector2(0f, 30f);
        srt.anchoredPosition = Vector2.zero;
        c.Title = OnlineUI.NewLabel(strip.transform, title, 22, Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, true);
        OnlineUI.Stretch(c.Title.rectTransform);
        c.Title.horizontalOverflow = HorizontalWrapMode.Wrap;
        c.Title.resizeTextForBestFit = true;
        c.Title.resizeTextMinSize = 14;
        c.Title.resizeTextMaxSize = 22;

        c.Detail = OnlineUI.NewLabel(b.transform, "", 20, Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, true);
        RectTransform drt = c.Detail.rectTransform;
        drt.anchorMin = new Vector2(0f, 0f);
        drt.anchorMax = new Vector2(1f, 0f);
        drt.pivot = new Vector2(0.5f, 0f);
        drt.sizeDelta = new Vector2(0f, 26f);
        drt.anchoredPosition = new Vector2(0f, 32f);
        c.Detail.color = Highlight;
        Outline o = c.Detail.gameObject.AddComponent<Outline>();
        o.effectColor = new Color(0f, 0f, 0f, 0.85f);
        o.effectDistance = new Vector2(2f, -2f);

        c.Lock = LockBadge(b.transform);
        return c;
    }

    // Room cards: the room's name and what it costs, one per room the server lists.
    private void BuildRoomCards()
    {
        foreach (Card c in roomCards) Destroy(c.Button.gameObject);
        roomCards.Clear();
        if (rooms == null) return;

        // Two side by side at full size (Practice, Standard); more share the row.
        int shown = rooms.Length;
        float width = shown <= 2 ? 395f : (Inner - (shown - 1) * 10f) / shown;
        for (int i = 0; i < shown; i++)
        {
            int index = i;
            MetaRoom r = rooms[i];
            bool free = r.EntryCents == 0;
            Button b = OnlineUI.NewButton(roomRow, "", Vector2.zero, new Vector2(width, 96f), CardDim, () => PickRoom(index));
            b.name = "Room_" + r.Id;
            var c = new Card { Button = b, Background = (Image)b.targetGraphic };
            c.Frame = Frame(b.gameObject);
            c.Title = OnlineUI.NewLabel(b.transform, RoomTitle(r.Id), 34, Vector2.zero, Vector2.zero, TextAnchor.LowerCenter, true);
            Band(c.Title.rectTransform, 12f, 0.45f, 1f, -4f);
            c.Detail = OnlineUI.NewLabel(b.transform, free ? "Free · bots fill empty seats" : $"Entry {OnlineUI.CoinsText(r.EntryCents)} · real players",
                                         21, Vector2.zero, Vector2.zero, TextAnchor.UpperCenter);
            Band(c.Detail.rectTransform, 12f, 0f, 0.45f, 2f);
            c.Detail.color = new Color(1f, 1f, 1f, 0.85f);
            c.Detail.resizeTextForBestFit = true;
            c.Detail.resizeTextMinSize = 14;
            c.Detail.resizeTextMaxSize = 21;
            c.Lock = LockBadge(b.transform);
            c.Lock.gameObject.SetActive(false);
            roomCards.Add(c);
        }
        LineUp(roomCards, width, 10f);
    }

    // A text across a card between two heights (fractions of it), inset at the left.
    private static void Band(RectTransform rt, float left, float bottom01, float top01, float nudge)
    {
        rt.anchorMin = new Vector2(0f, bottom01);
        rt.anchorMax = new Vector2(1f, top01);
        rt.offsetMin = new Vector2(left, nudge);
        rt.offsetMax = new Vector2(-12f, nudge);
    }

    // The frame round a chosen card (an Outline effect, so nothing is added to the card).
    private static Outline Frame(GameObject card)
    {
        Outline o = card.AddComponent<Outline>();
        o.effectColor = Highlight;
        o.effectDistance = new Vector2(4f, -4f);
        o.enabled = false;
        return o;
    }

    private static Image LockBadge(Transform card)
    {
        Image lockIcon = OnlineUI.NewImage("Lock", card, Color.white);
        lockIcon.sprite = OnlineUI.LockSprite();
        lockIcon.preserveAspect = true;
        lockIcon.raycastTarget = false;
        RectTransform lrt = lockIcon.rectTransform;
        lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(1f, 1f);
        lrt.anchoredPosition = new Vector2(-6f, -6f);
        lrt.sizeDelta = new Vector2(40f, 40f);
        lockIcon.gameObject.SetActive(false);
        return lockIcon;
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

    // The side's icon from the matchmaking screen's columns (FugitiveBox = hiders, HunterBox = seekers).
    private static Sprite RoleIcon(string box)
    {
        GameObject canvas = SceneObject("MatchmakingCanvas");
        Transform column = canvas != null ? FindUnder(canvas.transform, box) : null;
        Image icon = column != null ? column.Find("Icon")?.GetComponent<Image>() : null;
        return icon != null ? icon.sprite : null;
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
