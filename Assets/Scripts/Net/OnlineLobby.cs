using System;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Multiple Play after sign-in: Seeker/Hider, on Single Play's own role screen (RoleSelectCanvas:
// its Hider/Seeker buttons reach HomeUI.BeginMatch, which hands the choice here while this flow
// is on that step) -> Room Selection (a price tier) -> the map, in Single Play's own map picker (MapSelectPopup; the server matches players only with others on the same
// map) -> Matchmaking with Players, shown in the game's own MatchmakingUI with the server's real
// roster -> Game Start: from MatchStart the round itself belongs to OnlineRound. Adapted from the
// role, room and lobby parts of the SDK sample's OnlineFlow.
//
// Rooms: Practice is free and bots take the empty seats. Standard costs an entry (20 coins on the
// default server): real players only, no bots, so its lobby waits until every seat is taken, and
// the server refunds the entry if it does not fill in time. The entry is confirmed before every
// search (PayScreen). After a match, Next and the round's Exit open the room list: the player
// picks any room again (the server no longer queues anyone by itself). Leaving a lobby is never
// held against anyone: there is no cooldown.
//
// Drops: a MatchFound for the round on screen is the server giving a held seat back (the round
// resumes); a search lost with the connection is sent again once signed in; "signed in on another
// device" ends whatever was going on with a notice. After an app restart, Home offers to rejoin a
// round that was running (OfferRejoin): signing in then lands straight back in the held seat.
public class OnlineLobby : MonoBehaviour
{
    private enum Step { Idle, Role, Body, Room, Map, Searching, Lobby, Match, Rejoin }

    [Tooltip("Rejoining after a restart: how long after signing in to wait for the held seat.")]
    [SerializeField] private float rejoinWaitSeconds = 3f;

    private static MecchaNetClient Net => MecchaNetClient.Instance;
    private static MetaClient Meta => MetaClient.Instance;

    private MatchmakingUI matchmaking;
    private GameModeManager gmm;
    private CharacterSelect characters;
    private HomeUI home;
    private OnlineRound round;

    // A free room puts everyone back in the queue when a match closes, so the next MatchFound can
    // arrive while this player is still reading the result. It waits here until they tap Next.
    private MecchaNetClient.MatchFoundInfo? pendingLobby;

    private Step step;
    private bool hooked;
    private Action onClosed;

    // Bumped whenever the player backs out, so an answer to a request made on an abandoned screen
    // (a slow ListRooms, say) cannot pop that screen back up.
    private int flowVersion;

    private MetaProfile profile;
    private RolePreference role = RolePreference.Any;
    private MetaRoom room;
    private string mapName = "";          // the match's map, as MatchFound names it
    private string chosenMap = "";        // the map picked for FindMatch; "" = any
    private string[] serverMaps;          // the maps the server has baked (ListRooms)

    // ---- UI ---------------------------------------------------------------------------------
    private GameObject roomScreen, lobbyBar, noticeScreen, rejoinScreen, payScreen;
    private Text payTitle, payBody, payStatus;
    private Button payButton;
    private Action afterPay;
    private Action payBack;                 // the pay screen's BACK; the rooms when null
    private Text roomBalance, roomError, lobbyMessage, noticeText;
    private RectTransform roomList;

    private string lobbyText = "";   // the lobby bar's message while connected
    private float rejoinDeadline;

    /// <summary>Opens the role screen for a signed-in player; <paramref name="closed"/> when they go home.</summary>
    public static void Open(MetaProfile profile, Action closed)
    {
        OnlineLobby lobby = FindAnyObjectByType<OnlineLobby>();
        if (lobby == null) lobby = new GameObject("OnlineLobby").AddComponent<OnlineLobby>();
        lobby.Begin(profile, closed);
    }

    /// <summary>
    /// Back into one of the player's matches in progress (OnlineMatches' list): signs in if need
    /// be, leaves the round on screen (the server keeps that seat too) and asks for the seat.
    /// MatchFound then brings the round back as after a reconnect.
    /// </summary>
    public static void RejoinMatch(uint matchId)
    {
        OnlineLobby lobby = FindAnyObjectByType<OnlineLobby>();
        if (lobby == null) lobby = new GameObject("OnlineLobby").AddComponent<OnlineLobby>();
        lobby.StartRejoinTo(matchId);
    }

    private void StartRejoinTo(uint matchId)
    {
        if (OnlineRound.Active) round.End();
        flowVersion++;
        onClosed = null;
        pendingLobby = null;

        NetBootstrap.Ensure();
        Hook();
        step = Step.Rejoin;
        rejoinDeadline = 0f;
        HideScreens();
        if (matchmaking != null) matchmaking.EndOnline();
        if (home != null) home.GoHome();

        int version = flowVersion;
        OnlineSignIn.Open(signedIn =>
        {
            profile = signedIn;
            if (version != flowVersion || step != Step.Rejoin) return;
            rejoinDeadline = Time.unscaledTime + 8f;
            Net.RejoinMatch(matchId);
        }, () =>
        {
            if (version == flowVersion && step == Step.Rejoin) GoHome();
        });
    }

    /// <summary>
    /// Called once when Home first shows: if an online round was running moments ago (the app was
    /// closed or crashed), asks whether to rejoin it while the server still holds the seat.
    /// </summary>
    public static void OfferRejoin()
    {
        if (!OnlineRejoin.Pending) return;

        OnlineLobby lobby = FindAnyObjectByType<OnlineLobby>();
        if (lobby == null) lobby = new GameObject("OnlineLobby").AddComponent<OnlineLobby>();
        lobby.ShowRejoinPrompt();
    }

    // ================= Lifecycle =================

    private static OnlineLobby current;

    /// <summary>
    /// True while the player is in Multiple Play: choosing a side, body, room or map, searching, in a
    /// lobby or an online round, rejoining. False at Home and in Single Play. What needs the server
    /// (the coin balance, Shop buttons, match notices) shows only then (2026-11-13: Single Play runs
    /// without the server and shows none of it).
    /// </summary>
    public static bool InMultiplayer => OnlineRound.Active || (current != null && current.step != Step.Idle);

    private void Awake()
    {
        current = this;
        matchmaking = FindAnyObjectByType<MatchmakingUI>();
        gmm = FindAnyObjectByType<GameModeManager>();
        characters = FindAnyObjectByType<CharacterSelect>();
        home = FindAnyObjectByType<HomeUI>();
        round = gameObject.AddComponent<OnlineRound>();
        BuildUI();
        HideScreens();
    }

    private void OnDestroy()
    {
        // The scene goes (the app is closing); don't leave a seat behind.
        if (Net != null && InQueueOrMatch) Net.LeaveMatch();
        Unhook();
    }

    private bool InQueueOrMatch => step == Step.Searching || step == Step.Lobby || step == Step.Match || step == Step.Rejoin || pendingLobby != null;

    private void Begin(MetaProfile signedIn, Action closed)
    {
        flowVersion++;
        profile = signedIn;
        onClosed = closed;
        Hook();
        ShowRoleScreen();
    }

    private void Hook()
    {
        if (hooked || Net == null) return;
        hooked = true;

        Net.OnFindMatchFailed += HandleFindMatchFailed;
        Net.OnMatchFound += HandleMatchFound;
        Net.OnRosterChanged += HandleRosterChanged;
        Net.OnMatchStart += HandleMatchStart;
        Net.OnAuthenticated += HandleAuthenticated;
        Net.OnRejected += HandleRejected;
    }

    private void Unhook()
    {
        if (!hooked || Net == null) return;
        hooked = false;

        Net.OnFindMatchFailed -= HandleFindMatchFailed;
        Net.OnMatchFound -= HandleMatchFound;
        Net.OnRosterChanged -= HandleRosterChanged;
        Net.OnMatchStart -= HandleMatchStart;
        Net.OnAuthenticated -= HandleAuthenticated;
        Net.OnRejected -= HandleRejected;
    }

    private void Update()
    {
        // The lobby bar says when the connection is gone; the SDK is already reconnecting.
        if (lobbyBar.activeSelf && Net != null)
            lobbyMessage.text = Net.IsConnected || Net.IsChangingServer ? lobbyText : "Connection lost. Reconnecting...";

        if (step == Step.Rejoin && rejoinDeadline > 0f && Time.unscaledTime > rejoinDeadline)
        {
            // Signed in, but no seat came back: the match ended, or the seat ran out.
            rejoinDeadline = 0f;
            OnlineRejoin.Clear();
            EndWithNotice("That match has already ended.");
        }
    }

    private void SetLobbyMessage(string text)
    {
        lobbyText = text;
        lobbyMessage.text = text;
    }

    // Every handshake, reconnects included. The server forgets a queued search when the
    // connection drops (a held seat only exists once a match was found), so ask again.
    private void HandleAuthenticated()
    {
        if (step == Step.Searching && room.Id != null) Net.FindMatch(role, room.Id, ModelId(), chosenMap);
    }

    // The SDK does not reconnect after this one: another device has the account now.
    private void HandleRejected(string reason)
    {
        if (reason != NetTuning.SignedInElsewhereReason) return;
        if (step == Step.Idle && !OnlineRound.Active) return;

        OnlineRejoin.Clear();
        EndWithNotice("You signed in on another device, so you left the match here.");
    }

    /// <summary>Ends the round (when one is on screen), goes home and says why.</summary>
    public void EndRoundWithNotice(string message) => EndWithNotice(message);

    private void EndWithNotice(string message)
    {
        if (OnlineRound.Active) round.End();
        GoHome();
        ShowNotice(message);
    }

    private void ShowNotice(string message)
    {
        noticeText.text = message;
        noticeScreen.SetActive(true);
    }

    // ================= Rejoin after a restart =================

    private void ShowRejoinPrompt()
    {
        HideScreens();
        rejoinScreen.SetActive(true);
    }

    private void DeclineRejoin()
    {
        AudioManager.ButtonTap();
        OnlineRejoin.Clear();
        rejoinScreen.SetActive(false);   // the held seat simply runs out
    }

    // Hooked before signing in: the server sends the held seat's MatchFound straight after the
    // handshake, before the profile has even loaded.
    private void StartRejoin()
    {
        AudioManager.ButtonTap();
        flowVersion++;
        onClosed = null;
        pendingLobby = null;

        NetBootstrap.Ensure();
        Hook();
        step = Step.Rejoin;
        rejoinDeadline = 0f;
        HideScreens();

        int version = flowVersion;
        OnlineSignIn.Open(signedIn =>
        {
            profile = signedIn;
            if (version == flowVersion && step == Step.Rejoin) rejoinDeadline = Time.unscaledTime + rejoinWaitSeconds;
        }, () =>
        {
            if (version != flowVersion || step != Step.Rejoin) return;
            OnlineRejoin.Clear();
            GoHome();
        });
    }

    // The held seat, back: into the round at whatever point it has reached. A match still in its
    // lobby shows the map frozen until MatchStart, which begins the round again as usual.
    private void EnterRejoinedMatch(MecchaNetClient.MatchFoundInfo info)
    {
        rejoinDeadline = 0f;
        step = Step.Match;
        mapName = info.RoomName ?? "";
        HideScreens();
        if (matchmaking != null) matchmaking.EndOnline();
        round.Begin(this, mapName, info.Roster, info.MatchId);
    }

    private void GoHome()
    {
        AudioManager.ButtonTap();
        flowVersion++;

        // Leaves the queue or match, which also cancels the automatic re-queue a free room makes
        // when a match ends.
        if (Net != null && InQueueOrMatch) Net.LeaveMatch();

        bool fromRound = step == Step.Match;
        pendingLobby = null;
        step = Step.Idle;
        HideScreens();
        if (matchmaking != null) matchmaking.EndOnline();
        if (home != null) home.SetHomeVisible(true);
        if (fromRound && home != null) home.GoHome();
        onClosed?.Invoke();
    }

    // ================= Seeker / Hider =================

    // Single Play's role screen. Its buttons go to HomeUI.BeginMatch (TryPickRole below) and its
    // Back to HomeMenuUI.ShowHome (RoleSelectClosed). Strict roles: the player always gets the
    // side they pick, as in Single Play.
    private void ShowRoleScreen()
    {
        step = Step.Role;
        HideScreens();
        if (home == null)
        {
            // No role screen in this scene: let the server pick the side.
            PickRole(RolePreference.Any);
            return;
        }
        home.ShowRoleSelect();
    }

    // The side, then the body as in Single Play (SelectPlayerCanvas), then the room.
    private void PickRole(RolePreference preference)
    {
        role = preference;
        step = Step.Body;

        int version = flowVersion;
        int mode = preference == RolePreference.Seeker ? 1 : 2;
        bool shown = home != null && home.ShowSelectPlayerOnline(mode,
            () => { if (version == flowVersion && step == Step.Body) BodyChosen(); },
            () => { if (version == flowVersion && step == Step.Body) ShowRoleScreen(); },
            NotOwnedOnline);

        if (!shown) BodyChosen();
    }

    private void BodyChosen()
    {
        if (home != null) home.HideRoleSelect();   // back to Home, under the room screen
        ShowRoomScreen(null);
    }

    // The characters this account cannot play online: those the server does not list as owned.
    private bool NotOwnedOnline(int character) =>
        profile.OwnedAvatars != null && profile.OwnedAvatars.Length > 0 && Array.IndexOf(profile.OwnedAvatars, character) < 0;

    // Choosing the side, or the character after it: either way the side screen's buttons are
    // this online match's. A side screen shown while the flow was still on the character step
    // (a Back that did not go through SelectPlayerUI) must never start Single Play.
    private static OnlineLobby ChoosingRole()
    {
        OnlineLobby lobby = FindAnyObjectByType<OnlineLobby>();
        return lobby != null && (lobby.step == Step.Role || lobby.step == Step.Body) ? lobby : null;
    }

    /// <summary>
    /// RoleSelectCanvas's Hider (2) / Seeker (1) button, from HomeUI.BeginMatch: true when it
    /// picked the side of an online match, false when it is Single Play's own choice.
    /// </summary>
    public static bool TryPickRole(int mode)
    {
        OnlineLobby lobby = ChoosingRole();
        if (lobby == null) return false;
        lobby.PickRole(mode == 1 ? RolePreference.Seeker : RolePreference.Hider);
        return true;
    }

    /// <summary>RoleSelectCanvas's Back (HomeMenuUI.ShowHome): leaves the online flow when it was choosing a side.</summary>
    public static void RoleSelectClosed()
    {
        OnlineLobby lobby = ChoosingRole();
        if (lobby != null) lobby.GoHome();
    }

    // ================= Room (price tier) =================

    private void ShowRoomScreen(string error)
    {
        step = Step.Room;
        HideScreens();
        if (home != null) home.SetHomeVisible(true);
        roomScreen.SetActive(true);
        roomError.text = error ?? "Loading rooms...";

        for (int i = roomList.childCount - 1; i >= 0; i--) Destroy(roomList.GetChild(i).gameObject);

        int version = flowVersion;
        Meta.ListRooms(reply =>
        {
            if (version != flowVersion || step != Step.Room) return;

            if (reply.TimedOut || reply.Error != MetaError.None)
            {
                roomError.text = reply.TimedOut
                    ? "The server's room list did not answer. Try again."
                    : $"The server could not list its rooms ({reply.Error}).";
                return;
            }

            if (error == null) roomError.text = "";
            serverMaps = reply.Maps;
            BuildRoomButtons(reply.Rooms);
        });
    }

    private void BuildRoomButtons(MetaRoom[] rooms)
    {
        if (rooms == null || rooms.Length == 0)
        {
            roomError.text = "The server has no rooms configured.";
            return;
        }

        // Free rooms first: they start fastest, because bots fill the empty seats.
        var sorted = new List<MetaRoom>(rooms);
        sorted.Sort((a, b) => a.EntryCents.CompareTo(b.EntryCents));

        for (int i = 0; i < sorted.Count; i++)
        {
            MetaRoom r = sorted[i];
            bool free = r.EntryCents == 0;

            string detail = free
                ? "Free - bots fill empty seats"
                : $"Entry {OnlineUI.CoinsText(r.EntryCents)} - real players only";

            OnlineUI.NewButton(roomList, $"{RoomTitle(r.Id)}\n<size=26>{detail}</size>",
                               new Vector2(0f, 140f - i * 130f), new Vector2(820f, 112f),
                               free ? OnlineUI.Green : OnlineUI.Blue, () => PickRoom(r));
        }
    }

    private void PickRoom(MetaRoom picked)
    {
        AudioManager.ButtonTap();
        room = picked;
        if (picked.EntryCents > 0) ConfirmEntry(PickMap);
        else PickMap();
    }

    // ================= Paid entry =================

    // A paid room: what the entry buys (real players only, refunded if the lobby does not fill),
    // then onPay. A balance short of the entry is said here, before the server refuses it.
    private void ConfirmEntry(Action onPay)
    {
        string fee = OnlineUI.CoinsText(room.EntryCents);
        if (CoinBalance.Known && CoinBalance.Cents < room.EntryCents)
        {
            ShowRoomScreen($"<color=#FFC759>Not enough coins for this room. The entry is {fee}; top up in WALLET.</color>");
            return;
        }

        step = Step.Room;
        HideScreens();
        if (home != null) home.SetHomeVisible(true);
        afterPay = onPay;
        payBack = null;
        payTitle.text = RoomTitle(room.Id);
        payBody.text = $"Entry {fee}. Real players only, no bots: the match starts once all {GameTuning.TotalPlayers} seats are taken " +
                       $"({GameTuning.HunterSlots} seekers, {GameTuning.FugitiveSlots} hiders).\nIf it does not fill in time, your entry is refunded.";
        OnlineUI.SetButtonText(payButton, $"PAY {fee}");
        payScreen.SetActive(true);
    }

    private void Pay()
    {
        AudioManager.ButtonTap();
        payScreen.SetActive(false);
        Action next = afterPay;
        afterPay = null;
        next?.Invoke();   // the entry is held when the server seats this player, not before
    }

    // ================= Map (Single Play's picker) =================

    // The same map picker as Single Play. A room that fixes its map skips it, and so does a server
    // that has none of this build's maps (it then picks one itself).
    private void PickMap()
    {
        if (!string.IsNullOrEmpty(room.Map))
        {
            chosenMap = room.Map;
            StartSearch();
            return;
        }

        MapSelectPopup popup = MapSelectPopup.Instance;
        LevelManager levels = LevelManager.Instance;
        if (popup == null || !popup.Ready || levels == null || !ServerHasAnyMapOf(levels))
        {
            chosenMap = "";
            StartSearch();
            return;
        }

        step = Step.Map;
        HideScreens();

        int version = flowVersion;
        popup.Show(() =>
        {
            if (version != flowVersion || step != Step.Map) return;

            string map = levels.CurrentRoomName;
            if (!ServerHasMap(map))
            {
                ShowRoomScreen($"<color=#FFC759>{MapTitle(map)} isn't available online yet. Pick another map.</color>");
                return;
            }

            // Every map but the first is bought once before it is played online (2026-11-13).
            if (ShopInventory.MapLocked(MapNumber(map)))
            {
                ConfirmMapPurchase(map);
                return;
            }

            chosenMap = map;
            StartSearch();
        },
        () =>
        {
            if (version != flowVersion || step != Step.Map) return;
            ShowRoomScreen(null);
        },
        index => ShopInventory.MapLocked(index + 1) ? ShopInventory.PriceCents(ShopInventory.MapItem(index + 1)) : 0);

        // The locks as the server has them now (bought on another device, say).
        ShopInventory.Refresh(_ =>
        {
            if (version == flowVersion && step == Step.Map) popup.RefreshLocks();
        });
    }

    // The game's map number: Room = 1, Room2 = 2 ... (the shop's GameId for a map).
    private static int MapNumber(string map)
    {
        LevelManager levels = LevelManager.Instance;
        if (levels == null) return 0;
        for (int i = 0; i < levels.Count; i++)
            if (levels.RoomNameAt(i) == map) return i + 1;
        return 0;
    }

    // A map this account has not bought: its price, then BUY buys it in the shop and the search
    // starts; BACK goes back to the map picker.
    private void ConfirmMapPurchase(string map)
    {
        ShopItem item = ShopInventory.MapItem(MapNumber(map));
        if (item == null)
        {
            ShowRoomScreen($"<color=#FFC759>{FindMatchErrorText(FindMatchError.MapLocked)}</color>");
            return;
        }

        string price = OnlineUI.CoinsText(ShopInventory.PriceCents(item));
        step = Step.Room;
        HideScreens();
        if (home != null) home.SetHomeVisible(true);
        payTitle.text = MapTitle(map);
        payBody.text = $"{MapTitle(map)} is locked. Buy it once for {price} and play it online whenever you like.";
        OnlineUI.SetButtonText(payButton, $"BUY {price}");
        afterPay = () => BuyMap(item, map);
        payBack = PickMap;
        payScreen.SetActive(true);
    }

    private void BuyMap(ShopItem item, string map)
    {
        if (CoinBalance.Known && CoinBalance.Cents < ShopInventory.PriceCents(item))
        {
            ShowRoomScreen($"<color=#FFC759>Not enough coins for {MapTitle(map)}. Top up in WALLET.</color>");
            return;
        }

        int version = flowVersion;
        ShopInventory.Purchase(item, (bought, message) =>
        {
            if (version != flowVersion) return;
            if (!bought)
            {
                ShowRoomScreen($"<color=#FFC759>{message}</color>");
                return;
            }

            chosenMap = map;
            StartSearch();
        });
    }

    // An older server's room list carries no maps: then every map is offered, and the server says
    // (FindMatchError.UnknownMap) if one is not baked.
    private bool ServerHasMap(string map) =>
        serverMaps == null || serverMaps.Length == 0 || Array.IndexOf(serverMaps, map) >= 0;

    private bool ServerHasAnyMapOf(LevelManager levels)
    {
        if (serverMaps == null || serverMaps.Length == 0) return true;

        for (int i = 0; i < levels.Count; i++)
        {
            if (Array.IndexOf(serverMaps, levels.RoomNameAt(i)) >= 0) return true;
        }

        return false;
    }

    // ================= Matchmaking =================

    private void StartSearch()
    {
        step = Step.Searching;
        HideScreens();
        if (home != null) home.SetHomeVisible(false);
        mapName = chosenMap;

        if (matchmaking != null)
        {
            matchmaking.BeginOnline(role == RolePreference.Seeker, "Searching for players...");
            matchmaking.SetMapLabel(string.IsNullOrEmpty(chosenMap) ? "Server picks the map" : MapTitle(chosenMap));
        }

        lobbyBar.SetActive(true);
        string message = $"Searching in {RoomTitle(room.Id)}...";

        // The Home character is played only when the server says it is owned; say so otherwise.
        uint model = ModelId();
        int picked = characters != null ? characters.CurrentIndex : 0;
        if (picked != model)
            message += $"\n{AvatarName(picked)} isn't unlocked online: playing as {AvatarName((int)model)}.";
        SetLobbyMessage(message);

        Net.FindMatch(role, room.Id, model, chosenMap);
    }

    /// <summary>
    /// The avatar to play. The server refuses one the player does not own, so prefer the
    /// character picked on the Home screen only when the server says it is owned.
    /// </summary>
    private uint ModelId()
    {
        int current = characters != null ? characters.CurrentIndex : 0;
        bool owned = profile.OwnedAvatars != null && Array.IndexOf(profile.OwnedAvatars, current) >= 0;
        return (uint)Mathf.Max(0, owned ? current : profile.AvatarId);
    }

    // Avatar id = CharacterSelect index (Player = 0, Player2 = 1).
    private string AvatarName(int id)
    {
        if (characters != null && characters.buttonLabels != null && id >= 0 && id < characters.buttonLabels.Length)
            return characters.buttonLabels[id];
        return "Avatar " + (id + 1);
    }

    private void CancelSearch()
    {
        AudioManager.ButtonTap();
        Net.LeaveMatch();
        if (matchmaking != null) matchmaking.EndOnline();

        // Back where the search started: the map picker, Play there searches (and rejoins this
        // lobby while it has a seat) again. A paid room goes back to the rooms, so its entry is
        // confirmed again before it is paid again.
        if (room.EntryCents == 0 && string.IsNullOrEmpty(room.Map) && MapSelectPopup.Instance is { Ready: true } &&
            LevelManager.Instance != null && ServerHasAnyMapOf(LevelManager.Instance))
            PickMap();
        else
            ShowRoomScreen(null);
    }

    private void HandleFindMatchFailed(FindMatchError error)
    {
        if (step == Step.Rejoin && error == FindMatchError.MatchEnded)
        {
            rejoinDeadline = 0f;
            EndWithNotice("That match has already ended. Its result is in your matches.");
            return;
        }

        if (step != Step.Searching && step != Step.Lobby) return;

        // A held seat was restored on sign-in before this request landed: MatchFound follows.
        if (error == FindMatchError.AlreadyInMatch) return;

        if (matchmaking != null) matchmaking.EndOnline();

        // A map the shop listing had not yet shown as locked: offer it to buy, with the listing fresh.
        if (error == FindMatchError.MapLocked && !string.IsNullOrEmpty(chosenMap))
        {
            lobbyBar.SetActive(false);
            string map = chosenMap;
            int version = flowVersion;
            ShopInventory.Refresh(_ =>
            {
                if (version == flowVersion) ConfirmMapPurchase(map);
            });
            return;
        }

        ShowRoomScreen($"<color=#FFC759>{FindMatchErrorText(error)}</color>");
    }

    private void HandleMatchFound(MecchaNetClient.MatchFoundInfo info)
    {
        // The seat of the round on screen, given back after a dropped connection.
        if (OnlineRound.IsCurrentMatch(info.MatchId))
        {
            round.HandleResumed();
            return;
        }

        // Rejoining after a restart: this is the held seat.
        if (step == Step.Rejoin)
        {
            OnlineRejoin.Clear();
            EnterRejoinedMatch(info);
            return;
        }

        // The next free-room match, found while the result of the last one is still up.
        if (OnlineRound.Active)
        {
            pendingLobby = info;
            return;
        }

        // A held seat restored on reconnect while the player is elsewhere (Single Play, or back
        // home after leaving): they did not ask for it, so give the seat up.
        if ((step != Step.Searching && step != Step.Lobby) || (gmm != null && gmm.RoundActive))
        {
            Net.LeaveMatch();
            return;
        }

        ShowLobby(info);
    }

    private void ShowLobby(MecchaNetClient.MatchFoundInfo info)
    {
        if (step != Step.Searching && step != Step.Lobby)
        {
            HideScreens();
            if (home != null) home.SetHomeVisible(false);
            if (matchmaking != null) matchmaking.BeginOnline(info.YourRole == Role.Hunter, "");
            lobbyBar.SetActive(true);
        }

        step = Step.Lobby;
        mapName = info.RoomName ?? "";

        if (matchmaking != null)
        {
            matchmaking.SetOnlineMatchSize(info.TotalPlayers, info.HunterSlots);
            matchmaking.SetMapLabel(MapTitle(mapName));
            matchmaking.SetOnlineHint(info.YourRole == Role.Hunter
                ? "You are a Seeker. Find the fugitives and catch them before the round ends."
                : "You are a Hider. Hide carefully. Don't let the Seekers find you!");
        }

        SetLobbyMessage(room.EntryCents > 0
            ? "Waiting for real players... the match starts once every seat is taken. Entry refunded if it does not fill."
            : "Waiting for players... bots take the empty seats after a few seconds.");
        ShowRoster(info.Roster);
    }

    private void HandleRosterChanged(MecchaNetClient.RosterEntry[] entries)
    {
        if (pendingLobby is { } pending)
        {
            pending.Roster = entries;
            pendingLobby = pending;
            return;
        }

        if (step == Step.Lobby || step == Step.Searching) ShowRoster(entries);
    }

    private void ShowRoster(MecchaNetClient.RosterEntry[] entries)
    {
        if (matchmaking == null || entries == null) return;

        // Real players joining can add seats.
        matchmaking.SetOnlineMatchSize(Net.MatchSeats, Net.MatchHunterSeats);

        var seats = new List<MatchmakingUI.OnlineSeat>(entries.Length);
        foreach (MecchaNetClient.RosterEntry e in entries)
        {
            seats.Add(new MatchmakingUI.OnlineSeat
            {
                Name = string.IsNullOrEmpty(e.Name) ? (e.IsBot ? "Bot" : "Player") : SafeName(e.Name),
                Hunter = e.Role == Role.Hunter,
                IsLocal = e.Slot == Net.LocalSlot,
            });
        }

        matchmaking.SetOnlineRoster(seats);
    }

    // ================= The round =================

    // The lobby locked: load the map the server named and tell it this player is ready.
    private void HandleMatchStart(MecchaNetClient.MatchStartInfo info)
    {
        if (gmm != null && gmm.RoundActive && !OnlineRound.Active) return;   // Single Play
        if (step == Step.Idle) return;
        ShopUI.CloseOpen();   // opened from the lobby's Shop button: the match is starting

        if (pendingLobby is { } pending)
        {
            // The re-queued match locked while the result was still up: go straight into it.
            mapName = pending.RoomName ?? "";
            pendingLobby = null;
        }

        bool fromLobby = step == Step.Lobby || step == Step.Searching;
        step = Step.Match;
        HideScreens();
        if (matchmaking != null) matchmaking.EndOnline();
        round.Begin(this, mapName, info.Roster, info.MatchId);

        // Sent on every MatchStart: a reconnect during the ready gate gets MatchStart again and
        // must answer again. The room is loaded synchronously by Begin, so it is ready by now.
        Net.SendPlayerReady();

        // The loading screen, between the lobby and the match (2026-11-13: there and at launch only).
        if (fromLobby) LoadingScreen.Run(() => { });
    }

    /// <summary>The result popup's Home, or leaving mid-round (OnlineRound confirms first).</summary>
    public void GoHomeFromRound()
    {
        round.End();
        GoHome();
    }

    /// <summary>The result popup's Next: the room list, to pick any room for the next match.</summary>
    public void PlayAgainFromRound() => ExitToRoomsFromRound();

    /// <summary>
    /// Out of the match (the result's Next, or the round's Exit) and back to the room list, signed
    /// in and on the same side and character. Leaves the match on the server first, and any lobby
    /// an older server queued this player into meanwhile.
    /// </summary>
    public void ExitToRoomsFromRound()
    {
        AudioManager.ButtonTap();
        round.End();
        if (Net != null && InQueueOrMatch) Net.LeaveMatch();
        pendingLobby = null;
        if (matchmaking != null) matchmaking.EndOnline();
        if (home != null) home.GoHome();   // back to the menu scene state; the room list covers it
        ShowRoomScreen(null);
    }

    /// <summary>The room of the match being played costs an entry (Standard).</summary>
    public bool PaidRoom => room.EntryCents > 0;

    // ================= Match history =================

    // The room screen's HISTORY: the player's own records (OnlineHistory), then back to the rooms.
    private void ShowHistory()
    {
        AudioManager.ButtonTap();
        int version = flowVersion;
        roomScreen.SetActive(false);
        OnlineHistory.Show(transform, () =>
        {
            if (version == flowVersion && step == Step.Room) roomScreen.SetActive(true);
        });
    }

    // The room screen's BACK: the character screen, as it was (the side stays chosen).
    private void BackToCharacters()
    {
        AudioManager.ButtonTap();
        HideScreens();
        PickRole(role);
    }

    // The shop, from the rooms: the room screen comes back when it closes.
    private void ShowShop()
    {
        ShopUI shop = FindAnyObjectByType<ShopUI>(FindObjectsInactive.Include);
        if (shop == null) return;
        int version = flowVersion;
        roomScreen.SetActive(false);
        shop.OpenFrom(() =>
        {
            if (version == flowVersion && step == Step.Room) roomScreen.SetActive(true);
        });
    }

    // Deposits, withdrawals and the coin ledger (OnlineWallet), over the room screen.
    private void ShowWallet()
    {
        AudioManager.ButtonTap();
        int version = flowVersion;
        roomScreen.SetActive(false);
        OnlineWallet.Show(() =>
        {
            if (version == flowVersion && step == Step.Room) roomScreen.SetActive(true);
        });
    }

    // ================= UI =================

    private void BuildUI()
    {
        Transform canvas = OnlineUI.CreateCanvas("OnlineLobbyCanvas", transform, 50).transform;

        // Seeker / Hider is Single Play's RoleSelectCanvas (ShowRoleScreen).

        // Room (price tier).
        RectTransform p = OnlineUI.Modal(canvas, "RoomScreen", new Vector2(1060f, 820f));
        roomScreen = p.parent.gameObject;
        OnlineUI.NewLabel(p, "Choose a room", 52, new Vector2(0f, 340f), new Vector2(980f, 80f), TextAnchor.MiddleCenter, true);
        roomBalance = OnlineUI.NewLabel(p, "", 32, new Vector2(0f, 275f), new Vector2(980f, 50f));
        roomBalance.color = OnlineUI.Muted;
        CoinBalance.Bind(roomBalance, "Your coins: {0}");   // the server's balance, kept up to date
        roomList = (RectTransform)new GameObject("RoomList", typeof(RectTransform)).transform;
        roomList.SetParent(p, false);
        OnlineUI.Place(roomList, Vector2.zero, new Vector2(1000f, 600f));
        roomError = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, -260f), new Vector2(980f, 70f));
        roomError.color = OnlineUI.Warning;
        // Back is the screen before this one: the character choice.
        OnlineUI.NewButton(p, "BACK", new Vector2(-416f, -345f), new Vector2(196f, 80f), OnlineUI.Gray, BackToCharacters);
        OnlineUI.NewButton(p, "MATCHES", new Vector2(-208f, -345f), new Vector2(196f, 80f), new Color(0.85f, 0.52f, 0.12f, 0.97f), OnlineMatches.ShowList);
        OnlineUI.NewButton(p, "WALLET", new Vector2(0f, -345f), new Vector2(196f, 80f), OnlineUI.Green, ShowWallet);
        OnlineUI.NewButton(p, "HISTORY", new Vector2(208f, -345f), new Vector2(196f, 80f), OnlineUI.Blue, ShowHistory);
        OnlineUI.NewButton(p, "SHOP", new Vector2(416f, -345f), new Vector2(196f, 80f), new Color(0.52f, 0.30f, 0.80f, 0.97f), ShowShop);

        // Lobby: a bar under MatchmakingUI's panel (this canvas sorts above it).
        Image bar = OnlineUI.NewImage("LobbyBar", canvas, OnlineUI.PanelColor);
        OnlineUI.Place(bar.rectTransform, new Vector2(0f, -462f), new Vector2(1331f, 110f));
        lobbyBar = bar.gameObject;
        lobbyMessage = OnlineUI.NewLabel(bar.transform, "", 30, new Vector2(-170f, 0f), new Vector2(940f, 100f), TextAnchor.MiddleLeft);
        OnlineUI.NewButton(bar.transform, "CANCEL", new Vector2(500f, 0f), new Vector2(260f, 84f), OnlineUI.Red, CancelSearch);

        // A paid room's entry, confirmed before each search (ConfirmEntry).
        p = OnlineUI.Modal(canvas, "PayScreen", new Vector2(980f, 600f));
        payScreen = p.parent.gameObject;
        payTitle = OnlineUI.NewLabel(p, "", 52, new Vector2(0f, 220f), new Vector2(900f, 80f), TextAnchor.MiddleCenter, true);
        payBody = OnlineUI.NewLabel(p, "", 32, new Vector2(0f, 60f), new Vector2(880f, 220f));
        payStatus = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, -85f), new Vector2(880f, 50f));
        payStatus.color = OnlineUI.Muted;
        CoinBalance.Bind(payStatus, "Your coins: {0}");
        payButton = OnlineUI.NewButton(p, "PAY", new Vector2(-210f, -200f), new Vector2(380f, 100f), OnlineUI.Green, Pay);
        OnlineUI.NewButton(p, "BACK", new Vector2(210f, -200f), new Vector2(380f, 100f), OnlineUI.Gray, () =>
        {
            AudioManager.ButtonTap();
            afterPay = null;
            Action back = payBack;
            payBack = null;
            if (back != null) back();
            else ShowRoomScreen(null);
        });

        // A notice after a round ended on its own (seat ran out, another device).
        p = OnlineUI.Modal(canvas, "NoticeScreen", new Vector2(900f, 440f));
        noticeScreen = p.parent.gameObject;
        noticeText = OnlineUI.NewLabel(p, "", 36, new Vector2(0f, 60f), new Vector2(820f, 220f));
        OnlineUI.NewButton(p, "OK", new Vector2(0f, -140f), new Vector2(280f, 90f), OnlineUI.Green, () =>
        {
            AudioManager.ButtonTap();
            noticeScreen.SetActive(false);
        });

        // Rejoin after a restart.
        p = OnlineUI.Modal(canvas, "RejoinScreen", new Vector2(900f, 480f));
        rejoinScreen = p.parent.gameObject;
        OnlineUI.NewLabel(p, "Rejoin your match?", 50, new Vector2(0f, 150f), new Vector2(820f, 80f), TextAnchor.MiddleCenter, true);
        OnlineUI.NewLabel(p, "Your online round is still going.", 34, new Vector2(0f, 50f), new Vector2(820f, 90f));
        OnlineUI.NewButton(p, "REJOIN", new Vector2(-170f, -130f), new Vector2(280f, 90f), OnlineUI.Green, StartRejoin);
        OnlineUI.NewButton(p, "NO", new Vector2(170f, -130f), new Vector2(280f, 90f), OnlineUI.Gray, DeclineRejoin);
    }

    private void HideScreens()
    {
        roomScreen.SetActive(false);
        payScreen.SetActive(false);
        lobbyBar.SetActive(false);
        noticeScreen.SetActive(false);
        rejoinScreen.SetActive(false);
    }

    // ================= Text =================

    private static string RoomTitle(string id)
    {
        if (string.IsNullOrEmpty(id)) return "Room";
        string spaced = id.Replace('-', ' ');
        return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1);
    }

    // Map names are the room prefab names ("Room3"); LevelManager shows them as "Room 3".
    private static string MapTitle(string map)
    {
        if (string.IsNullOrEmpty(map)) return "Online match";
        int digit = map.IndexOfAny("0123456789".ToCharArray());
        return digit > 0 && map[digit - 1] != ' ' ? map.Insert(digit, " ") : map;
    }

    // Names are drawn by Text with rich text on; a name carrying <size=...> must not restyle the
    // screen. Chosen user ids cannot contain these, but a handshake name from before one is set can.
    private static string SafeName(string name) => name.Replace('<', '(').Replace('>', ')');

    // Player-facing texts from the SDK reference (MecchaNetClient.OnFindMatchFailed).
    private static string FindMatchErrorText(FindMatchError error)
    {
        switch (error)
        {
            case FindMatchError.UnknownRoom: return "That room no longer exists.";
            case FindMatchError.ModelNotOwned: return "You don't own that avatar.";
            case FindMatchError.LobbyNotFilled: return "Not enough players joined. Entry refunded.";
            case FindMatchError.NotEnoughCoins: return "Not enough coins for this room.";
            case FindMatchError.LeaveCooldown: return "You left too many matches early. Try again in a few minutes.";
            case FindMatchError.MatchEnded: return "That match has already ended.";
            case FindMatchError.MapLocked: return "That map is locked. Buy it to play it online.";
            case FindMatchError.PaidRoomsUnavailable: return "Paid rooms are closed on this server.";
            case FindMatchError.UnknownMap: return "That map isn't available online yet. Pick another map.";
            case FindMatchError.NoServerAvailable: return "All servers are busy. Try again in a moment.";
            case FindMatchError.TicketRefused: return "That took too long. Please try again.";
            default: return "The server refused the request.";
        }
    }
}
