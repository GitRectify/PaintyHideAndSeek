using System;
using System.Collections.Generic;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Play setup (2026-11-21, redesigned 2026-11-23): every choice before a match on one screen, for
// Single Play and Multiple Play alike, in one panel on the side screen (RoleSelectCanvas, which keeps
// its Back, Settings, Rate and Shop buttons and the home room behind it):
//
//   SINGLE PLAY / MULTIPLAYER   the title: the mode chosen on Home (Play or Multiplayer), kept for
//                               the whole flow; Multiplayer adds its Matches, History, Profile
//   SIDE        Hider / Seeker
//   CHARACTER   the bodies, by their pictures; a tap makes it the player's body at once
//               (CharacterSelect swaps it in place, no scene reload)
//   ROOM        Multiplayer only: the server's rooms (Practice free with bots, Standard paid)
//   MAP         the maps' pictures; online, a lock and price on one not bought yet, and none the
//               server doesn't have
//   YOUR MATCH  what Play starts, and PLAY / FIND MATCH
//
// The look: deep navy rounded panel and cards (UiArt shapes, UiGradient), one way of showing a
// choice everywhere (a gold ring and check badge that pop in), a dark veil with a lock and price on
// what is not owned, a raised green Play button; the panel slides in when it opens, buttons sink
// when pressed (UiPress).
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

    // Sizes, in the 1920 x 1080 reference canvas units.
    private const float PanelWidth = 880f;
    private const float Pad = 32f;
    private const float Inner = PanelWidth - Pad * 2f;
    private const float RightMargin = 120f;            // at least; more when the side screen's button column needs it
    private const float BottomMargin = 36f;
    private const float CardRadius = 20f;
    private const float FooterHeight = 120f;

    // Colours.
    private static readonly Color PanelTop = Hex(0x1E2D52, 0.97f);
    private static readonly Color PanelBottom = Hex(0x0F182E, 0.97f);
    private static readonly Color PanelEdge = Hex(0x4C6CAA, 0.45f);
    private static readonly Color CardTop = Hex(0x1D2A4A);
    private static readonly Color CardBottom = Hex(0x172240);
    private static readonly Color CardEdge = Hex(0x34497A, 0.9f);
    private static readonly Color PickedTop = Hex(0x2C5097);
    private static readonly Color PickedBottom = Hex(0x1D3A72);
    private static readonly Color HiderTop = Hex(0x3FCF7D);
    private static readonly Color HiderBottom = Hex(0x1E9A55);
    private static readonly Color SeekerTop = Hex(0x4D9BFF);
    private static readonly Color SeekerBottom = Hex(0x2A62D8);
    private static readonly Color Gold = Hex(0xFFD24A);
    private static readonly Color Ink = Hex(0x14203A);
    private static readonly Color Muted = Hex(0x8FA4CE);
    private static readonly Color Warning = Hex(0xFFC759);
    private static readonly Color PlayTop = Hex(0x47DE86);
    private static readonly Color PlayBottom = Hex(0x1FA65A);
    private static readonly Color PlayLip = Hex(0x157A40);

    public static PlaySetupUI Instance { get; private set; }

    /// <summary>Whether the setup panel is on screen.</summary>
    public static bool IsOpen => Instance != null && Instance.root != null && Instance.root.activeSelf;

    private sealed class Card
    {
        public Button Button;
        public UiGradient Fill;
        public Image Ring;
        public GameObject Badge;
        public Image Picture;
        public Text Title;
        public Text Detail;
        public GameObject Veil;          // "Lock": a dark veil with a lock, over what isn't owned
        public Text VeilText;
        public GameObject Pill;
        public Image PillImage;
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
    private GameObject root, panel, extras;      // root: the panel and its shadow, shown and hidden together
    private RectTransform panelRect, shadowRect;
    private CanvasGroup panelGroup;
    private float openedAt = -1f;
    private float rightMargin = RightMargin;
    private readonly List<RectTransform> column = new List<RectTransform>();
    private readonly Vector3[] corners = new Vector3[4];
    private Text title, summaryCaption, summary;
    private Card hiderCard, seekerCard;
    private Section sideSection, characterSection, roomSection, mapSection;
    private RectTransform footer;
    private Text roomStatus;
    private Button playButton;
    private Text playLabel, playDetail;
    private readonly List<Card> characterCards = new List<Card>();
    private readonly List<Card> roomCards = new List<Card>();
    private readonly List<Card> mapCards = new List<Card>();
    private readonly List<(RectTransform rt, float at)> pops = new List<(RectTransform, float)>();

    private sealed class Section
    {
        public Text Label;
        public Image Line;
        public RectTransform Row;
    }

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
        if (Instance != null && Instance.root != null) Instance.root.SetActive(false);
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
        if (roleCanvas != null)
        {
            // The side screen's right-hand buttons (Settings, Shop, Rate): the panel stays left of them.
            foreach (string name in new[] { "SettingsBtn", "AdBtn", "RoleShopBtn", "Ratebtn" })
                if (FindUnder(roleCanvas.transform, name) is RectTransform rt) column.Add(rt);
        }
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
        if (!wasOpen) openedAt = Time.unscaledTime;   // slides in
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

    private void Update()
    {
        if (roleCanvas == null || root == null) return;

        // The side screen closed under the panel (its Back, Settings' Home): the panel goes with it,
        // and Multiple Play's flow ends. The side screen shown by someone else: the panel comes too.
        bool screen = roleCanvas.activeInHierarchy;
        if (root.activeSelf && !screen)
        {
            root.SetActive(false);
            if (Online) OnlineLobby.RoleSelectClosed();
        }
        else if (!root.activeSelf && screen && !LoadingScreen.Holding)
        {
            Open(OnlineLobby.InSetup, "");
        }

        if (root.activeSelf)
        {
            KeepClearOfColumn();
            Animate();
        }
    }

    // The side screen scales by another rule than this canvas, so where its button column ends
    // depends on the screen's shape (on a 20:9 phone it reaches further in): measured each frame,
    // the panel standing that much in from the right edge.
    private void KeepClearOfColumn()
    {
        var area = (RectTransform)root.transform;
        Canvas own = area.GetComponentInParent<Canvas>().rootCanvas;
        Camera ownCam = own.renderMode == RenderMode.ScreenSpaceOverlay ? null : own.worldCamera;
        float left = float.MaxValue;
        foreach (RectTransform rt in column)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            Canvas theirs = rt.GetComponentInParent<Canvas>().rootCanvas;
            Camera theirCam = theirs.renderMode == RenderMode.ScreenSpaceOverlay ? null : theirs.worldCamera;
            rt.GetWorldCorners(corners);
            Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(theirCam, corners[0]);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(area, screenPoint, ownCam, out Vector2 local))
                left = Mathf.Min(left, local.x);
        }
        float wanted = left < float.MaxValue ? Mathf.Max(RightMargin, area.rect.width * 0.5f - left + 16f) : RightMargin;
        if (Mathf.Abs(wanted - rightMargin) < 0.5f) return;
        rightMargin = wanted;
        if (openedAt < 0f)
        {
            panelRect.anchoredPosition = new Vector2(-rightMargin, BottomMargin);
            PlaceShadow();
        }
    }

    // The panel slides in from the right as it fades in; a ring just picked pops into place.
    private void Animate()
    {
        if (openedAt >= 0f)
        {
            float t = Mathf.Clamp01((Time.unscaledTime - openedAt) / 0.26f);
            float e = 1f - Mathf.Pow(1f - t, 3f);
            panelGroup.alpha = e;
            float slide = (1f - e) * 60f;
            panelRect.anchoredPosition = new Vector2(-rightMargin + slide, BottomMargin);
            PlaceShadow();
            if (t >= 1f) openedAt = -1f;
        }

        for (int i = pops.Count - 1; i >= 0; i--)
        {
            (RectTransform rt, float at) = pops[i];
            float t = Mathf.Clamp01((Time.unscaledTime - at) / 0.18f);
            if (rt != null) rt.localScale = Vector3.one * Mathf.Lerp(1.08f, 1f, 1f - (1f - t) * (1f - t));
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
        if (root == null) return;

        title.text = Online ? "MULTIPLAYER" : "SINGLE PLAY";
        extras.SetActive(Online);

        SetPicked(hiderCard, side == 2, HiderTop, HiderBottom);
        SetPicked(seekerCard, side == 1, SeekerTop, SeekerBottom);

        int character = characters != null ? characters.CurrentIndex : 0;
        for (int i = 0; i < characterCards.Count; i++)
        {
            Card c = characterCards[i];
            bool locked = Online && lobby.CharacterLocked(i);
            SetPicked(c, i == character, PickedTop, PickedBottom);
            c.Veil.SetActive(locked);
            c.VeilText.text = locked ? "In the Shop" : "";
        }

        int roomIndex = RoomIndex;
        for (int i = 0; i < roomCards.Count; i++) SetPicked(roomCards[i], i == roomIndex, PickedTop, PickedBottom);
        roomStatus.text = roomCards.Count == 0 ? roomsNote : "";

        int shown = MapShown;
        int fixedMap = FixedMap;
        for (int i = 0; i < mapCards.Count; i++)
        {
            Card c = mapCards[i];
            bool open = MapOpen(i);
            long price = Online && open ? lobby.MapPrice(i) : 0;
            SetPicked(c, i == shown && open, PickedTop, PickedBottom);
            c.Button.interactable = open;
            c.Veil.SetActive(!open || price > 0);
            c.VeilText.text = !open ? (fixedMap >= 0 ? "" : "Not online yet") : "";
            c.Pill.SetActive(price > 0);
            c.PillText.text = price > 0 ? OnlineUI.CoinsText(price) : "";
        }
        mapSection.Label.text = fixedMap >= 0 ? "MAP  ·  set by this room" : "MAP";

        Layout();

        // Online on a character this account doesn't own (picked for Single Play, say): said at once,
        // not only when Find Match refuses it.
        string note = message;
        if (string.IsNullOrEmpty(note) && Online && lobby.CharacterLocked(character))
            note = "Not unlocked online. Pick another character, or get it in the Shop.";

        if (string.IsNullOrEmpty(note))
        {
            summaryCaption.text = "YOUR MATCH";
            summaryCaption.color = Muted;
            summary.text = Summary();
            summary.color = Color.white;
        }
        else
        {
            summaryCaption.text = "";
            summary.text = note;
            summary.color = Warning;
        }

        bool paid = Online && HasRoom && Room.EntryCents > 0;
        playLabel.text = Online ? "FIND MATCH" : "PLAY";
        playDetail.text = paid ? $"Entry {OnlineUI.CoinsText(Room.EntryCents)}" : "";
        RectTransform lrt = playLabel.rectTransform;
        lrt.anchorMin = new Vector2(0f, paid ? 0.36f : 0f);
        lrt.offsetMin = new Vector2(84f, paid ? 0f : 4f);
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
        return string.Join("  ·  ", parts);
    }

    // A card picked: the side's colours or the picked blue, a gold ring and a check badge that pop
    // in; not picked, a quiet navy card.
    private void SetPicked(Card c, bool picked, Color top, Color bottom)
    {
        if (picked) c.Fill.Set(top, bottom);
        else c.Fill.Set(CardTop, CardBottom);
        c.Ring.gameObject.SetActive(picked);
        c.Badge.SetActive(picked);
        if (picked && !c.Picked)
        {
            pops.Add((c.Ring.rectTransform, Time.unscaledTime));
            pops.Add(((RectTransform)c.Badge.transform, Time.unscaledTime));
        }
        c.Picked = picked;
    }

    // Top to bottom: the title (and Multiplayer's buttons), Side, Character, Room (online only), Map,
    // then the footer. The panel is as tall as what it shows, standing on the bottom of the screen.
    private void Layout()
    {
        float y = 24f + 54f;
        if (Online)
        {
            Place((RectTransform)extras.transform, Pad, y + 10f, Inner, 44f);
            y += 10f + 44f;
        }
        y += 18f;

        y = PlaceSection(sideSection, y, 104f, true);
        y = PlaceSection(characterSection, y, 140f, true);
        y = PlaceSection(roomSection, y, 84f, Online);
        y = PlaceSection(mapSection, y, 112f, mapCards.Count > 0);

        float height = y + FooterHeight;
        panelRect.sizeDelta = new Vector2(PanelWidth, height);
        PlaceShadow();
        Place(footer, 0f, y, PanelWidth, FooterHeight);
    }

    // The shadow round the panel, a little larger and lower, wherever the panel is.
    private void PlaceShadow()
    {
        shadowRect.sizeDelta = panelRect.sizeDelta + new Vector2(44f, 44f);
        shadowRect.anchoredPosition = panelRect.anchoredPosition + new Vector2(22f, -32f);
    }

    private float PlaceSection(Section s, float y, float rowHeight, bool show)
    {
        s.Label.gameObject.SetActive(show);
        s.Line.gameObject.SetActive(show);
        s.Row.gameObject.SetActive(show);
        if (!show) return y;

        Place(s.Label.rectTransform, Pad, y, Inner, 26f);
        float labelWidth = Mathf.Min(Inner - 40f, s.Label.preferredWidth + 14f);
        Place(s.Line.rectTransform, Pad + labelWidth, y + 13f, Inner - labelWidth, 2f);
        Place(s.Row, Pad, y + 34f, Inner, rowHeight);
        return y + 34f + rowHeight + 18f;
    }

    // ================= Building =================

    private void Build()
    {
        Transform canvas = OnlineUI.CreateCanvas("PlaySetupCanvas", transform, 44).transform;   // over the side screen (40), under the Shop (45)

        // The screen: the panel standing on the bottom right, its soft shadow behind it, the two
        // shown, hidden and faded in together.
        root = new GameObject("PlaySetupScreen", typeof(RectTransform));
        root.transform.SetParent(canvas, false);
        Stretch((RectTransform)root.transform, 0f);
        panelGroup = root.AddComponent<CanvasGroup>();

        Image shadow = Shape("PlaySetupShadow", root.transform, UiArt.Shadow, 30f, new Color(0f, 0f, 0f, 0.55f));
        shadowRect = shadow.rectTransform;
        shadowRect.anchorMin = shadowRect.anchorMax = shadowRect.pivot = new Vector2(1f, 0f);

        Image bg = Shape("PlaySetup", root.transform, UiArt.Rounded, 30f, Color.white);
        bg.raycastTarget = true;
        bg.gameObject.AddComponent<UiGradient>().Set(PanelTop, PanelBottom);
        panel = bg.gameObject;
        panelRect = bg.rectTransform;
        panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(1f, 0f);
        panelRect.anchoredPosition = new Vector2(-RightMargin, BottomMargin);
        Stretch(Shape("Edge", panelRect, UiArt.RingThin, 30f, PanelEdge).rectTransform, 0f);
        Transform p = panel.transform;

        // The mode, as chosen on Home.
        title = OnlineUI.NewLabel(p, "", 46, Vector2.zero, new Vector2(Inner, 54f), TextAnchor.MiddleLeft, true);
        title.name = "SetupTitle";
        Place(title.rectTransform, Pad, 24f, Inner, 54f);
        Shadowed(title, 0.45f);

        // Multiplayer's other places, small, under the title: they open over this screen.
        extras = new GameObject("PlaySetupExtras", typeof(RectTransform));
        extras.transform.SetParent(p, false);
        // No WALLET here (2026-11-24): the coin balance opens the wallet, and its history is
        // HISTORY's first tab.
        float w = (Inner - 2f * 12f) / 3f;
        Extra("MATCHES", 0, w, OnlineMatches.ShowList);
        Extra("HISTORY", 1, w, () => lobby?.OpenHistory());
        Extra("PROFILE", 2, w, () => lobby?.OpenAccount());

        // The side.
        sideSection = NewSection(p, "SIDE", "SetupSides");
        float half = (Inner - 16f) / 2f;
        hiderCard = SideCard(sideSection.Row, "Hider", "Paint yourself and hide", RoleIcon("FugitiveBox"), 0f, half,
                             () => { AudioManager.ButtonTap(); PickSide(2); });
        hiderCard.Button.name = "HiderChoice";
        seekerCard = SideCard(sideSection.Row, "Seeker", "Find and catch the hiders", RoleIcon("HunterBox"), half + 16f, half,
                              () => { AudioManager.ButtonTap(); PickSide(1); });
        seekerCard.Button.name = "SeekerChoice";

        // The character.
        characterSection = NewSection(p, "CHARACTER", "SetupCharacters");
        int count = characters != null && characters.characterPrefabs != null ? characters.characterPrefabs.Length : 0;
        for (int i = 0; i < count; i++)
        {
            int index = i;
            string label = characters.buttonLabels != null && i < characters.buttonLabels.Length ? characters.buttonLabels[i] : "Player " + (i + 1);
            Card c = CharacterCard(characterSection.Row, label, Portrait(i), i * (150f + 14f), () => PickCharacter(index));
            c.Button.name = "Character_" + i;
            characterCards.Add(c);
        }

        // The room (Multiple Play).
        roomSection = NewSection(p, "ROOM", "SetupRooms");
        roomStatus = OnlineUI.NewLabel(roomSection.Row, "", 26, Vector2.zero, new Vector2(Inner, 84f), TextAnchor.MiddleLeft);
        roomStatus.color = Muted;
        OnlineUI.Stretch(roomStatus.rectTransform);

        // The map.
        mapSection = NewSection(p, "MAP", "SetupMaps");
        MapSelectPopup picker = MapSelectPopup.Instance;
        float mapWidth = MapCount > 0 ? (Inner - (MapCount - 1) * 12f) / MapCount : 0f;
        for (int i = 0; i < MapCount; i++)
        {
            int index = i;
            Card c = MapCard(mapSection.Row, levels.DisplayNameAt(i), picker != null ? picker.ThumbnailAt(i) : null, i * (mapWidth + 12f), mapWidth,
                             () => PickMap(index));
            c.Button.name = "Map_" + levels.RoomNameAt(i);
            mapCards.Add(c);
        }

        BuildFooter(p);
    }

    private Section NewSection(Transform parent, string label, string rowName)
    {
        var s = new Section();
        s.Label = OnlineUI.NewLabel(parent, label, 22, Vector2.zero, new Vector2(Inner, 26f), TextAnchor.MiddleLeft, true);
        s.Label.color = Muted;
        s.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
        s.Line = Shape("Line", parent, null, 0f, new Color(Muted.r, Muted.g, Muted.b, 0.22f));
        var row = new GameObject(rowName, typeof(RectTransform));
        row.transform.SetParent(parent, false);
        s.Row = (RectTransform)row.transform;
        return s;
    }

    // "YOUR MATCH" and what was chosen on the left, the raised Play button on the right.
    private void BuildFooter(Transform p)
    {
        footer = (RectTransform)new GameObject("SetupFooter", typeof(RectTransform)).transform;
        footer.SetParent(p, false);
        Image line = Shape("Line", footer, null, 0f, new Color(Muted.r, Muted.g, Muted.b, 0.22f));
        Place(line.rectTransform, Pad, 0f, Inner, 2f);

        summaryCaption = OnlineUI.NewLabel(footer, "YOUR MATCH", 20, Vector2.zero, new Vector2(420f, 26f), TextAnchor.MiddleLeft, true);
        Place(summaryCaption.rectTransform, Pad, 24f, 430f, 26f);
        summary = OnlineUI.NewLabel(footer, "", 28, Vector2.zero, new Vector2(430f, 62f), TextAnchor.UpperLeft, true);
        summary.name = "SetupSummary";
        Place(summary.rectTransform, Pad, 52f, 430f, 60f);
        summary.resizeTextForBestFit = true;
        summary.resizeTextMinSize = 18;
        summary.resizeTextMaxSize = 28;
        summary.verticalOverflow = VerticalWrapMode.Truncate;

        // Play: green, lit from the top, standing on a darker lip, a play sign before its name.
        Image face = Shape("SetupPlay", footer, UiArt.Rounded, 24f, Color.white);
        face.raycastTarget = true;
        face.gameObject.AddComponent<UiGradient>().Set(PlayTop, PlayBottom);
        Shadow lip = face.gameObject.AddComponent<Shadow>();
        lip.effectColor = PlayLip;
        lip.effectDistance = new Vector2(0f, -6f);
        RectTransform frt = face.rectTransform;
        frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(1f, 1f);
        frt.anchoredPosition = new Vector2(-Pad, -18f);
        frt.sizeDelta = new Vector2(330f, 88f);
        playButton = face.gameObject.AddComponent<Button>();
        playButton.targetGraphic = face;
        playButton.onClick.AddListener(Play);
        face.gameObject.AddComponent<UiPress>();

        Image icon = Shape("Icon", frt, UiArt.Play, 0f, Color.white);
        icon.type = Image.Type.Simple;
        icon.preserveAspect = true;
        RectTransform irt = icon.rectTransform;
        irt.anchorMin = irt.anchorMax = irt.pivot = new Vector2(0f, 0.5f);
        irt.anchoredPosition = new Vector2(26f, 0f);
        irt.sizeDelta = new Vector2(46f, 46f);

        playLabel = OnlineUI.NewLabel(frt, "PLAY", 38, Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft, true);
        RectTransform lrt = playLabel.rectTransform;
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = new Vector2(84f, 4f);
        lrt.offsetMax = new Vector2(-16f, 0f);
        playLabel.resizeTextForBestFit = true;
        playLabel.resizeTextMinSize = 22;
        playLabel.resizeTextMaxSize = 38;
        Shadowed(playLabel, 0.3f);

        playDetail = OnlineUI.NewLabel(frt, "", 20, Vector2.zero, Vector2.zero, TextAnchor.UpperLeft, true);
        RectTransform drt = playDetail.rectTransform;
        drt.anchorMin = new Vector2(0f, 0f);
        drt.anchorMax = new Vector2(1f, 0.38f);
        drt.offsetMin = new Vector2(86f, 6f);
        drt.offsetMax = new Vector2(-16f, 0f);
        playDetail.color = new Color(1f, 1f, 1f, 0.9f);
    }

    private void Extra(string text, int column, float width, Action tap)
    {
        Image face = Shape("Extra_" + text, extras.transform, UiArt.Rounded, 22f, Hex(0x22355E, 0.95f));
        face.raycastTarget = true;
        RectTransform rt = face.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(column * (width + 12f), 0f);
        rt.sizeDelta = new Vector2(width, 44f);
        Stretch(Shape("Edge", rt, UiArt.RingThin, 22f, PanelEdge).rectTransform, 0f);
        Button b = face.gameObject.AddComponent<Button>();
        b.targetGraphic = face;
        b.onClick.AddListener(() => tap());
        face.gameObject.AddComponent<UiPress>();
        Text label = OnlineUI.NewLabel(rt, text, 22, Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, true);
        OnlineUI.Stretch(label.rectTransform);
        label.color = new Color(1f, 1f, 1f, 0.92f);
        label.resizeTextForBestFit = true;
        label.resizeTextMinSize = 14;
        label.resizeTextMaxSize = 22;
    }

    // ---- Cards ----

    // A card: a rounded face (its gradient set by SetPicked) with a thin edge, a gold ring and a
    // check badge for when it is picked, and a veil for when it is locked.
    private Card NewCard(Transform parent, string name, float x, float width, float height, Action tap)
    {
        Image face = Shape(name, parent, UiArt.Rounded, CardRadius, Color.white);
        face.raycastTarget = true;
        RectTransform rt = face.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, 0f);
        rt.sizeDelta = new Vector2(width, height);
        var c = new Card { Fill = face.gameObject.AddComponent<UiGradient>() };
        c.Fill.Set(CardTop, CardBottom);
        Stretch(Shape("Edge", rt, UiArt.RingThin, CardRadius, CardEdge).rectTransform, 0f);

        c.Button = face.gameObject.AddComponent<Button>();
        c.Button.targetGraphic = face;
        c.Button.onClick.AddListener(() => tap());
        face.gameObject.AddComponent<UiPress>();
        return c;
    }

    // The parts every card has over its content: the veil (locked), the ring and badge (picked).
    private void FinishCard(Card c, bool withPill)
    {
        Transform t = c.Button.transform;

        Image veil = Shape("Lock", t, UiArt.Rounded, CardRadius, new Color(0.03f, 0.05f, 0.11f, 0.62f));
        Stretch(veil.rectTransform, 0f);
        c.Veil = veil.gameObject;
        Image lockIcon = Shape("Icon", veil.transform, null, 0f, Color.white);
        lockIcon.sprite = OnlineUI.LockSprite();
        lockIcon.preserveAspect = true;
        // Stacked from the bottom: the card's name stays readable, then the price (or a word on
        // why), then the lock.
        RectTransform lrt = lockIcon.rectTransform;
        lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(0.5f, 0f);
        lrt.anchoredPosition = new Vector2(0f, 64f);
        lrt.sizeDelta = new Vector2(34f, 34f);
        c.VeilText = OnlineUI.NewLabel(veil.transform, "", 18, Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, true);
        RectTransform vrt = c.VeilText.rectTransform;
        vrt.anchorMin = new Vector2(0f, 0f);
        vrt.anchorMax = new Vector2(1f, 0f);
        vrt.pivot = new Vector2(0.5f, 0f);
        vrt.anchoredPosition = new Vector2(0f, 36f);
        vrt.sizeDelta = new Vector2(-12f, 24f);
        c.VeilText.color = new Color(1f, 1f, 1f, 0.9f);
        c.VeilText.resizeTextForBestFit = true;
        c.VeilText.resizeTextMinSize = 12;
        c.VeilText.resizeTextMaxSize = 18;
        c.Veil.SetActive(false);

        if (withPill)
        {
            // The price of what is locked, on a gold pill at the bottom.
            Image pill = Shape("Price", veil.transform, UiArt.Rounded, 12f, Hex(0x3B2D0A, 0.95f));
            RectTransform prt = pill.rectTransform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0f);
            prt.anchoredPosition = new Vector2(0f, 34f);
            prt.sizeDelta = new Vector2(132f, 26f);
            Stretch(Shape("Edge", prt, UiArt.RingThin, 12f, new Color(Gold.r, Gold.g, Gold.b, 0.7f)).rectTransform, 0f);
            c.PillText = OnlineUI.NewLabel(prt, "", 17, Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, true);
            OnlineUI.Stretch(c.PillText.rectTransform);
            c.PillText.color = Gold;
            c.PillText.resizeTextForBestFit = true;
            c.PillText.resizeTextMinSize = 11;
            c.PillText.resizeTextMaxSize = 17;
            c.Pill = pill.gameObject;
            c.PillImage = pill;
            c.Pill.SetActive(false);
        }

        c.Ring = Shape("Ring", t, UiArt.RingBold, CardRadius + 3f, Gold);
        Stretch(c.Ring.rectTransform, -4f);
        c.Ring.gameObject.SetActive(false);

        Image badge = Shape("Badge", t, UiArt.Circle, 0f, Gold);
        badge.type = Image.Type.Simple;
        RectTransform brt = badge.rectTransform;
        brt.anchorMin = brt.anchorMax = new Vector2(1f, 1f);
        brt.pivot = new Vector2(0.5f, 0.5f);
        brt.anchoredPosition = new Vector2(-8f, -8f);
        brt.sizeDelta = new Vector2(30f, 30f);
        Image tick = Shape("Check", brt, UiArt.Check, 0f, Ink);
        tick.type = Image.Type.Simple;
        Stretch(tick.rectTransform, 3f);
        c.Badge = badge.gameObject;
        c.Badge.SetActive(false);
    }

    private Card SideCard(Transform row, string label, string line, Sprite icon, float x, float width, Action tap)
    {
        Card c = NewCard(row, "Side", x, width, 104f, tap);
        Transform t = c.Button.transform;

        float textLeft = 24f;
        if (icon != null)
        {
            Image disc = Shape("IconDisc", t, UiArt.Circle, 0f, new Color(0f, 0f, 0f, 0.22f));
            disc.type = Image.Type.Simple;
            RectTransform drt = disc.rectTransform;
            drt.anchorMin = drt.anchorMax = drt.pivot = new Vector2(0f, 0.5f);
            drt.anchoredPosition = new Vector2(18f, 0f);
            drt.sizeDelta = new Vector2(72f, 72f);
            Image pic = Shape("Icon", disc.transform, null, 0f, Color.white);
            pic.sprite = icon;
            pic.preserveAspect = true;
            Stretch(pic.rectTransform, 8f);
            c.Picture = pic;
            textLeft = 106f;
        }

        c.Title = OnlineUI.NewLabel(t, label, 36, Vector2.zero, Vector2.zero, TextAnchor.LowerLeft, true);
        Band(c.Title.rectTransform, textLeft, 0.46f, 1f, -2f);
        Shadowed(c.Title, 0.3f);
        c.Detail = OnlineUI.NewLabel(t, line, 20, Vector2.zero, Vector2.zero, TextAnchor.UpperLeft, false);
        Band(c.Detail.rectTransform, textLeft, 0f, 0.46f, 2f);
        c.Detail.color = new Color(1f, 1f, 1f, 0.82f);
        c.Detail.resizeTextForBestFit = true;
        c.Detail.resizeTextMinSize = 14;
        c.Detail.resizeTextMaxSize = 20;
        FinishCard(c, false);
        return c;
    }

    private Card CharacterCard(Transform row, string label, Sprite portrait, float x, Action tap)
    {
        Card c = NewCard(row, "Character", x, 150f, 140f, tap);
        Transform t = c.Button.transform;

        Image glow = Shape("Stage", t, UiArt.Circle, 0f, new Color(0.45f, 0.62f, 1f, 0.16f));
        glow.type = Image.Type.Simple;
        RectTransform grt = glow.rectTransform;
        grt.anchorMin = grt.anchorMax = grt.pivot = new Vector2(0.5f, 1f);
        grt.anchoredPosition = new Vector2(0f, -10f);
        grt.sizeDelta = new Vector2(96f, 96f);

        Image pic = Shape("Picture", t, null, 0f, Color.white);
        pic.sprite = portrait;
        pic.preserveAspect = true;
        pic.enabled = portrait != null;
        RectTransform prt = pic.rectTransform;
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(10f, 34f);
        prt.offsetMax = new Vector2(-10f, -8f);
        c.Picture = pic;

        c.Title = OnlineUI.NewLabel(t, label, 22, Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, true);
        RectTransform trt = c.Title.rectTransform;
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(1f, 0f);
        trt.pivot = new Vector2(0.5f, 0f);
        trt.anchoredPosition = new Vector2(0f, 6f);
        trt.sizeDelta = new Vector2(-12f, 28f);
        c.Title.resizeTextForBestFit = true;
        c.Title.resizeTextMinSize = 14;
        c.Title.resizeTextMaxSize = 22;
        FinishCard(c, false);
        return c;
    }

    private Card MapCard(Transform row, string label, Sprite picture, float x, float width, Action tap)
    {
        Card c = NewCard(row, "Map", x, width, 112f, tap);
        Transform t = c.Button.transform;

        // The picture fills the card, cut to its rounded corners.
        Image mask = Shape("Frame", t, UiArt.Rounded, CardRadius - 4f, Color.white);
        Stretch(mask.rectTransform, 3f);
        mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        Image pic = Shape("Picture", mask.transform, null, 0f, Color.white);
        pic.sprite = picture;
        pic.enabled = picture != null;
        pic.preserveAspect = false;
        Stretch(pic.rectTransform, 0f);
        if (picture != null)
        {
            // Cover the frame without stretching the picture: the longer side runs past the edges.
            float aspect = picture.rect.width / Mathf.Max(1f, picture.rect.height);
            float frameAspect = (width - 6f) / 106f;
            RectTransform prt = pic.rectTransform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = aspect > frameAspect ? new Vector2(106f * aspect, 106f) : new Vector2(width - 6f, (width - 6f) / aspect);
        }
        c.Picture = pic;

        // The name on a shade at the bottom of the picture.
        Image shade = Shape("Shade", mask.transform, null, 0f, Color.white);
        shade.gameObject.AddComponent<UiGradient>().Set(new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0.8f));
        RectTransform srt = shade.rectTransform;
        srt.anchorMin = new Vector2(0f, 0f);
        srt.anchorMax = new Vector2(1f, 0f);
        srt.pivot = new Vector2(0.5f, 0f);
        srt.anchoredPosition = Vector2.zero;
        srt.sizeDelta = new Vector2(0f, 48f);
        c.Title = OnlineUI.NewLabel(mask.transform, label, 20, Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, true);
        RectTransform trt = c.Title.rectTransform;
        trt.anchorMin = new Vector2(0f, 0f);
        trt.anchorMax = new Vector2(1f, 0f);
        trt.pivot = new Vector2(0.5f, 0f);
        trt.anchoredPosition = new Vector2(0f, 4f);
        trt.sizeDelta = new Vector2(-10f, 26f);
        c.Title.resizeTextForBestFit = true;
        c.Title.resizeTextMinSize = 13;
        c.Title.resizeTextMaxSize = 20;
        Shadowed(c.Title, 0.5f);

        FinishCard(c, true);
        return c;
    }

    // Room cards: the room's name and what it is for, and its price on a pill (FREE, or coins).
    private void BuildRoomCards()
    {
        foreach (Card c in roomCards) Destroy(c.Button.gameObject);
        roomCards.Clear();
        if (rooms == null) return;

        int n = rooms.Length;
        float width = n <= 2 ? (Inner - 16f) / 2f : (Inner - (n - 1) * 12f) / n;
        for (int i = 0; i < n; i++)
        {
            int index = i;
            MetaRoom r = rooms[i];
            bool free = r.EntryCents == 0;
            Card c = NewCard(roomSection.Row, "Room_" + r.Id, i * (width + (n <= 2 ? 16f : 12f)), width, 84f, () => PickRoom(index));
            Transform t = c.Button.transform;

            c.Title = OnlineUI.NewLabel(t, RoomTitle(r.Id), 30, Vector2.zero, Vector2.zero, TextAnchor.LowerLeft, true);
            Band(c.Title.rectTransform, 22f, 0.46f, 1f, -2f);
            c.Title.rectTransform.offsetMax = new Vector2(-130f, -2f);
            Shadowed(c.Title, 0.3f);
            c.Detail = OnlineUI.NewLabel(t, free ? "Bots fill empty seats" : "Real players only", 19, Vector2.zero, Vector2.zero, TextAnchor.UpperLeft);
            Band(c.Detail.rectTransform, 22f, 0f, 0.46f, 2f);
            c.Detail.rectTransform.offsetMax = new Vector2(-130f, 2f);
            c.Detail.color = new Color(1f, 1f, 1f, 0.8f);

            Image pill = Shape("Price", t, UiArt.Rounded, 14f, free ? Hex(0x2FB86B) : Hex(0x3B2D0A, 0.95f));
            RectTransform prt = pill.rectTransform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(1f, 0.5f);
            prt.anchoredPosition = new Vector2(-18f, 0f);
            prt.sizeDelta = new Vector2(104f, 34f);
            if (!free) Stretch(Shape("Edge", prt, UiArt.RingThin, 14f, new Color(Gold.r, Gold.g, Gold.b, 0.7f)).rectTransform, 0f);
            Text price = OnlineUI.NewLabel(prt, free ? "FREE" : OnlineUI.CoinsText(r.EntryCents), 19, Vector2.zero, Vector2.zero, TextAnchor.MiddleCenter, true);
            OnlineUI.Stretch(price.rectTransform);
            price.color = free ? Color.white : Gold;
            price.resizeTextForBestFit = true;
            price.resizeTextMinSize = 11;
            price.resizeTextMaxSize = 19;

            FinishCard(c, false);
            roomCards.Add(c);
        }
    }

    // ================= Small helpers =================

    // An Image child: a rounded shape at a corner radius, or a plain rectangle (no sprite).
    private static Image Shape(string name, Transform parent, Sprite sprite, float radius, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        Image image = go.GetComponent<Image>();
        if (sprite != null && radius > 0f) UiArt.Apply(image, sprite, radius);
        else if (sprite != null) image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static void Stretch(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    // A child of the panel (or a row), its top-left x across and y down from its parent's top-left.
    private static void Place(RectTransform rt, float x, float yTop, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop);
        rt.sizeDelta = new Vector2(w, h);
    }

    // A text across a card between two heights (fractions of it), inset at the left.
    private static void Band(RectTransform rt, float left, float bottom01, float top01, float nudge)
    {
        rt.anchorMin = new Vector2(0f, bottom01);
        rt.anchorMax = new Vector2(1f, top01);
        rt.offsetMin = new Vector2(left, nudge);
        rt.offsetMax = new Vector2(-14f, nudge);
    }

    private static void Shadowed(Text text, float alpha)
    {
        Shadow s = text.gameObject.AddComponent<Shadow>();
        s.effectColor = new Color(0f, 0f, 0f, alpha);
        s.effectDistance = new Vector2(0f, -2f);
    }

    private static Color Hex(int rgb, float alpha = 1f) =>
        new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

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
