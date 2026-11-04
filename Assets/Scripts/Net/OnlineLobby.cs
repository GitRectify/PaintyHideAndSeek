using System;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Multiple Play after sign-in: Seeker/Hider -> Room Selection (a price tier, not a map) ->
// Matchmaking with Players, shown in the game's own MatchmakingUI with the server's real roster.
// Adapted from the role, room and lobby parts of the SDK sample's OnlineFlow.
//
// Until the online round exists, a match is left as soon as it locks (OnMatchStart), and only
// free rooms can be joined: leaving a free room before the round costs nothing, while leaving a
// paid one counts toward the server's early-leave cooldown.
public class OnlineLobby : MonoBehaviour
{
    private enum Step { Idle, Role, Room, Searching, Lobby, Result }

    private static MecchaNetClient Net => MecchaNetClient.Instance;
    private static MetaClient Meta => MetaClient.Instance;

    private MatchmakingUI matchmaking;
    private GameModeManager gmm;
    private CharacterSelect characters;

    private Step step;
    private bool hooked;
    private Action onClosed;

    // Bumped whenever the player backs out, so an answer to a request made on an abandoned screen
    // (a slow ListRooms, say) cannot pop that screen back up.
    private int flowVersion;

    private MetaProfile profile;
    private RolePreference role = RolePreference.Any;
    private MetaRoom room;
    private string mapName = "";

    // ---- UI ---------------------------------------------------------------------------------
    private GameObject roleScreen, roomScreen, lobbyBar, resultScreen;
    private Text roleSubtitle, roomBalance, roomError, lobbyMessage, resultTitle, resultMessage;
    private RectTransform roomList;

    /// <summary>Opens the role screen for a signed-in player; <paramref name="closed"/> when they go home.</summary>
    public static void Open(MetaProfile profile, Action closed)
    {
        OnlineLobby lobby = FindAnyObjectByType<OnlineLobby>();
        if (lobby == null) lobby = new GameObject("OnlineLobby").AddComponent<OnlineLobby>();
        lobby.Begin(profile, closed);
    }

    // ================= Lifecycle =================

    private void Awake()
    {
        matchmaking = FindAnyObjectByType<MatchmakingUI>();
        gmm = FindAnyObjectByType<GameModeManager>();
        characters = FindAnyObjectByType<CharacterSelect>();
        BuildUI();
        HideScreens();
    }

    private void OnDestroy()
    {
        // The menu scene reloads when the player switches character; don't leave a seat behind.
        if (Net != null && (step == Step.Searching || step == Step.Lobby)) Net.LeaveMatch();
        Unhook();
    }

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
    }

    private void Unhook()
    {
        if (!hooked || Net == null) return;
        hooked = false;

        Net.OnFindMatchFailed -= HandleFindMatchFailed;
        Net.OnMatchFound -= HandleMatchFound;
        Net.OnRosterChanged -= HandleRosterChanged;
        Net.OnMatchStart -= HandleMatchStart;
    }

    private void GoHome()
    {
        AudioManager.ButtonTap();
        flowVersion++;

        if (Net != null && (step == Step.Searching || step == Step.Lobby)) Net.LeaveMatch();

        step = Step.Idle;
        HideScreens();
        if (matchmaking != null) matchmaking.EndOnline();
        onClosed?.Invoke();
    }

    // ================= Seeker / Hider =================

    private void ShowRoleScreen()
    {
        step = Step.Role;
        HideScreens();
        roleScreen.SetActive(true);
        roleSubtitle.text = $"Playing as <b>{SafeName(profile.UserId)}</b>";
    }

    private void PickRole(RolePreference preference)
    {
        AudioManager.ButtonTap();
        role = preference;
        ShowRoomScreen(null);
    }

    // ================= Room (price tier) =================

    private void ShowRoomScreen(string error)
    {
        step = Step.Room;
        HideScreens();
        roomScreen.SetActive(true);
        roomBalance.text = $"Your coins: {OnlineUI.CoinsText(Net.CoinCents)}";
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
                : $"Entry {OnlineUI.CoinsText(r.EntryCents)} - opens with online rounds";

            Button b = OnlineUI.NewButton(roomList, $"{RoomTitle(r.Id)}\n<size=26>{detail}</size>",
                                          new Vector2(0f, 140f - i * 130f), new Vector2(820f, 112f),
                                          free ? OnlineUI.Green : OnlineUI.Gray, () => PickRoom(r));
            b.interactable = free;
        }
    }

    private void PickRoom(MetaRoom picked)
    {
        AudioManager.ButtonTap();
        room = picked;
        StartSearch();
    }

    // ================= Matchmaking =================

    private void StartSearch()
    {
        step = Step.Searching;
        HideScreens();
        mapName = "";

        if (matchmaking != null)
        {
            matchmaking.BeginOnline(role == RolePreference.Seeker, "Searching for players...");
            matchmaking.SetMapLabel("Server picks the map");
            matchmaking.SetCoinText((Net.CoinCents / 100).ToString());
        }

        lobbyBar.SetActive(true);
        lobbyMessage.text = $"Searching in {RoomTitle(room.Id)}...";

        Net.FindMatch(role, room.Id, ModelId());
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

    private void CancelSearch()
    {
        AudioManager.ButtonTap();
        Net.LeaveMatch();
        if (matchmaking != null) matchmaking.EndOnline();
        ShowRoomScreen(null);
    }

    private void HandleFindMatchFailed(FindMatchError error)
    {
        if (step != Step.Searching && step != Step.Lobby) return;

        // A held seat was restored on sign-in before this request landed: MatchFound follows.
        if (error == FindMatchError.AlreadyInMatch) return;

        if (matchmaking != null) matchmaking.EndOnline();
        ShowRoomScreen($"<color=#FFC759>{FindMatchErrorText(error)}</color>");
    }

    private void HandleMatchFound(MecchaNetClient.MatchFoundInfo info)
    {
        // A held seat restored on reconnect while the player is elsewhere (Single Play, or back
        // home): there is no online round to take them into yet, so give the seat up.
        if ((step != Step.Searching && step != Step.Lobby) || (gmm != null && gmm.RoundActive))
        {
            Net.LeaveMatch();
            return;
        }

        step = Step.Lobby;
        mapName = info.RoomName ?? "";

        if (matchmaking != null)
        {
            matchmaking.SetMapLabel(MapTitle(mapName));
            matchmaking.SetOnlineHint(info.YourRole == Role.Hunter
                ? "You are a Seeker. Find the fugitives and catch them before the round ends."
                : "You are a Hider. Hide carefully. Don't let the Seekers find you!");
        }

        lobbyMessage.text = "Waiting for players... bots take the empty seats after a few seconds.";
        ShowRoster(info.Roster);
    }

    private void HandleRosterChanged(MecchaNetClient.RosterEntry[] entries)
    {
        if (step == Step.Lobby || step == Step.Searching) ShowRoster(entries);
    }

    private void ShowRoster(MecchaNetClient.RosterEntry[] entries)
    {
        if (matchmaking == null || entries == null) return;

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

    // The lobby locked and the server is waiting for the map to load. With no online round to
    // play yet, leave now: free rooms charge nothing for leaving before the round starts.
    private void HandleMatchStart(MecchaNetClient.MatchStartInfo info)
    {
        if (step != Step.Searching && step != Step.Lobby) return;

        Net.LeaveMatch();
        if (matchmaking != null) matchmaking.EndOnline();

        int players = info.Roster != null ? info.Roster.Length : 0;
        step = Step.Result;
        HideScreens();
        resultScreen.SetActive(true);
        resultTitle.text = "Match ready";
        resultMessage.text = $"{players} players are in, on {MapTitle(mapName)}.\n" +
                             "Online rounds arrive in the next update, so you have left this match. Nothing was charged.";
    }

    // ================= UI =================

    private void BuildUI()
    {
        Transform canvas = OnlineUI.CreateCanvas("OnlineLobbyCanvas", transform, 50).transform;

        // Seeker / Hider.
        RectTransform p = OnlineUI.Modal(canvas, "RoleScreen", new Vector2(960f, 680f));
        roleScreen = p.parent.gameObject;
        OnlineUI.NewLabel(p, "Choose your side", 52, new Vector2(0f, 260f), new Vector2(900f, 80f), TextAnchor.MiddleCenter, true);
        roleSubtitle = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, 195f), new Vector2(900f, 50f));
        roleSubtitle.color = OnlineUI.Muted;
        OnlineUI.NewButton(p, "HIDER\n<size=26>paint yourself and hide</size>", new Vector2(0f, 85f), new Vector2(640f, 110f), OnlineUI.Blue, () => PickRole(RolePreference.Hider));
        OnlineUI.NewButton(p, "SEEKER\n<size=26>hunt the hiders down</size>", new Vector2(0f, -40f), new Vector2(640f, 110f), OnlineUI.Red, () => PickRole(RolePreference.Seeker));
        OnlineUI.NewButton(p, "EITHER\n<size=26>fastest match</size>", new Vector2(0f, -165f), new Vector2(640f, 110f), OnlineUI.Gray, () => PickRole(RolePreference.Any));
        OnlineUI.NewButton(p, "BACK", new Vector2(0f, -275f), new Vector2(280f, 76f), OnlineUI.Gray, GoHome);

        // Room (price tier).
        p = OnlineUI.Modal(canvas, "RoomScreen", new Vector2(1060f, 820f));
        roomScreen = p.parent.gameObject;
        OnlineUI.NewLabel(p, "Choose a room", 52, new Vector2(0f, 340f), new Vector2(980f, 80f), TextAnchor.MiddleCenter, true);
        roomBalance = OnlineUI.NewLabel(p, "", 32, new Vector2(0f, 275f), new Vector2(980f, 50f));
        roomBalance.color = OnlineUI.Muted;
        roomList = (RectTransform)new GameObject("RoomList", typeof(RectTransform)).transform;
        roomList.SetParent(p, false);
        OnlineUI.Place(roomList, Vector2.zero, new Vector2(1000f, 600f));
        roomError = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, -260f), new Vector2(980f, 70f));
        roomError.color = OnlineUI.Warning;
        OnlineUI.NewButton(p, "BACK", new Vector2(0f, -345f), new Vector2(280f, 80f), OnlineUI.Gray, ShowRoleScreen);

        // Lobby: a bar under MatchmakingUI's panel (this canvas sorts above it).
        Image bar = OnlineUI.NewImage("LobbyBar", canvas, OnlineUI.PanelColor);
        OnlineUI.Place(bar.rectTransform, new Vector2(0f, -462f), new Vector2(1331f, 110f));
        lobbyBar = bar.gameObject;
        lobbyMessage = OnlineUI.NewLabel(bar.transform, "", 30, new Vector2(-170f, 0f), new Vector2(940f, 100f), TextAnchor.MiddleLeft);
        OnlineUI.NewButton(bar.transform, "CANCEL", new Vector2(500f, 0f), new Vector2(260f, 84f), OnlineUI.Red, CancelSearch);

        // Match found (until the online round exists).
        p = OnlineUI.Modal(canvas, "ResultScreen", new Vector2(980f, 520f));
        resultScreen = p.parent.gameObject;
        resultTitle = OnlineUI.NewLabel(p, "", 52, new Vector2(0f, 170f), new Vector2(900f, 80f), TextAnchor.MiddleCenter, true);
        resultMessage = OnlineUI.NewLabel(p, "", 34, new Vector2(0f, 20f), new Vector2(880f, 200f));
        OnlineUI.NewButton(p, "OK", new Vector2(0f, -180f), new Vector2(300f, 100f), OnlineUI.Green, GoHome);
    }

    private void HideScreens()
    {
        roleScreen.SetActive(false);
        roomScreen.SetActive(false);
        lobbyBar.SetActive(false);
        resultScreen.SetActive(false);
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
            case FindMatchError.PaidRoomsUnavailable: return "Paid rooms are closed on this server.";
            default: return "The server refused the request.";
        }
    }
}
