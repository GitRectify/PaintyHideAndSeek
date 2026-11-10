using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Meccha online play-test: plays one Multiple Play match in the real game, in Play mode, through
// the game's own screens and scripts, against a running server, and writes a PASS/FAIL report.
// It is a check of the integration, not a unit test: everything the server sends goes through the
// SDK and the game exactly as on a phone.
//
//   Unity.exe -batchmode -projectPath <project> -executeMethod OnlinePlayTest.Run
//             -playtestHost 127.0.0.1 -playtestPort 17777 -playtestMetaPort 17779
//             -playtestRole seeker|hider -playtestSeats 16 -playtestReport <file>
//
// The flow: Multiplayer sign-in (first-run name if the server asks) -> special bullets bought in
// the shop -> Single Play's role screen (HomeUI.BeginMatch) -> the room screen's Practice button
// -> Single Play's map picker -> matchmaking -> the round (seeker: shoots and switches bullet
// type; hider: presses Start and paints) -> the result popup -> Home (wallet refreshed) -> match
// history -> the shop's bullet cards. Bots or headless clients fill the lobby.
//
// Play mode reloads scripts, so the driver keeps its state in SessionState and re-attaches itself
// on load ([InitializeOnLoad]).
[InitializeOnLoad]
public static class OnlinePlayTest
{
    private const string ActiveKey = "MecchaPlayTest.Active";
    private const string ArgsKey = "MecchaPlayTest.Args";
    private const float TimeoutSeconds = 240f;

    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static OnlinePlayTest()
    {
        if (!SessionState.GetBool(ActiveKey, false)) return;
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    // ================= Start =================

    public static void Run()
    {
        string[] args = Environment.GetCommandLineArgs();
        string Arg(string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        var settings = string.Join("\n", new[]
        {
            Arg("-playtestHost", "127.0.0.1"),
            Arg("-playtestPort", "7777"),
            Arg("-playtestMetaPort", "7779"),
            Arg("-playtestRole", "seeker"),
            Arg("-playtestReport", Path.Combine(Directory.GetCurrentDirectory(), "Logs", "OnlinePlayTest.txt")),
            Arg("-playtestSeats", "8"),
        });
        SessionState.SetString(ArgsKey, settings);
        SessionState.SetBool(ActiveKey, true);

        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        EditorApplication.isPlaying = true;
    }

    // ================= State =================

    private enum Step { WaitPlay, SignIn, FirstRun, Shop, Role, Room, Map, Match, Round, Result, Home, History, ShopCards, Again, SinglePlay, Done }

    private static Step step;
    private static float stepStarted, startedAt, nextAction;
    private static bool hooked;
    private static readonly List<string> report = new List<string>();
    private static int failures;
    private static int errorsLogged;
    private static readonly List<string> errors = new List<string>();

    // Evidence gathered during the round.
    private static int remoteShots, loadoutAnswers, phaseChanges, shotsFired, eliminations;
    private static int sawPhaseSeek;
    private static bool chosenSpecial, specialSpent, pressedStart, painted, sawStartButton;
    private static int remoteViews, remoteAnimators, matchEnds;
    private static BulletStock stockBeforeRound;

    private static string[] Settings => SessionState.GetString(ArgsKey, "").Split('\n');
    private static string Host => Settings[0];
    private static int Port => int.Parse(Settings[1]);
    private static int MetaPort => int.Parse(Settings[2]);
    private static bool Seeker => Settings[3] != "hider";
    private static string ReportPath => Settings[4];
    private static int Seats => int.Parse(Settings[5]);

    private static MecchaNetClient Net => MecchaNetClient.Instance;
    private static MetaClient Meta => MetaClient.Instance;

    private static void Check(bool ok, string what)
    {
        report.Add((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) failures++;
        Debug.Log("[PlayTest] " + (ok ? "PASS  " : "FAIL  ") + what);
    }

    private static void Go(Step next)
    {
        Debug.Log($"[PlayTest] {step} -> {next} at {Time.realtimeSinceStartup - startedAt:F1}s");
        step = next;
        stepStarted = Time.realtimeSinceStartup;
        nextAction = 0f;
    }

    private static float InStep => Time.realtimeSinceStartup - stepStarted;

    // ================= Driver =================

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) return;

        try
        {
            if (step == Step.WaitPlay && startedAt == 0f)
            {
                startedAt = Time.realtimeSinceStartup;
                stepStarted = startedAt;
                Application.logMessageReceived += CountErrors;
            }

            if (Time.realtimeSinceStartup - startedAt > TimeoutSeconds)
            {
                Check(false, $"finished within {TimeoutSeconds:F0} s (stuck at {step})");
                Finish();
                return;
            }

            Advance();
        }
        catch (Exception ex)
        {
            Check(false, $"driver error at {step}: {ex.Message}");
            Debug.LogException(ex);
            Finish();
        }
    }

    private static void Advance()
    {
        switch (step)
        {
            case Step.WaitPlay:
                if (InStep < 3f || Object.FindAnyObjectByType<HomeMenuUI>() == null) return;

                // Point the game at the test server (NetBootstrap reads NetConfig.json).
                MecchaNetClient net = NetBootstrap.Ensure();
                net.Configure(Host, Port, 0, OnlineConfig.Load().connectionKey);
                Meta.Configure(MetaPort, false, "");
                Hook();
                OnlineSignIn.Open(p => { signedIn = true; profile = p; }, null);
                Go(Step.SignIn);
                return;

            case Step.SignIn:
                if (signedIn)
                {
                    Check(true, "signed in (Multiplayer -> OnlineSignIn)");
                    Go(Step.Shop);
                    return;
                }
                OnlineSignIn signIn = Object.FindAnyObjectByType<OnlineSignIn>();
                if (!submittedName && signIn != null && Get<GameObject>(signIn, "profileScreen") is { activeSelf: true })
                {
                    submittedName = true;
                    Get<InputField>(signIn, "userIdInput").text = "PlayTest" + UnityEngine.Random.Range(1000, 9999);
                    Call(signIn, "SubmitProfile");
                    Check(true, "first-run profile screen shown and submitted");
                }
                return;

            case Step.Shop:
                if (nextAction == 0f)
                {
                    nextAction = 1f;
                    // A few special bullets of type 2, through the same purchase the shop makes.
                    Meta.Purchase(new List<MetaPurchaseLine> { new MetaPurchaseLine { ItemId = "bullets-2", Quantity = 3 } }, receipt =>
                    {
                        bought = receipt.Status == PurchaseStatus.Completed;
                        Check(bought, $"bought 3 x special bullet 2 ({receipt.Status}, {receipt.Error}); stock {receipt.Bullets}");
                        stockBeforeRound = receipt.Bullets;
                        boughtAnswered = true;
                    });
                }
                if (!boughtAnswered) return;

                OnlineLobby.Open(profile, null);
                Go(Step.Role);
                return;

            case Step.Role:
                if (InStep < 1f) return;
                GameObject roleCanvas = FindByName("RoleSelectCanvas");
                Check(roleCanvas != null && roleCanvas.activeInHierarchy, "online side choice is Single Play's RoleSelectCanvas");
                Object.FindAnyObjectByType<HomeUI>().BeginMatch(Seeker ? 1 : 2);   // its Seeker / Hider button
                Go(Step.Room);
                return;

            case Step.Room:
                Button practice = FindButton("RoomScreen", "Practice");
                if (practice == null) return;
                Check(FindButton("RoomScreen", "HISTORY") != null, "room screen has the HISTORY button");
                practice.onClick.Invoke();
                Go(Step.Map);
                return;

            case Step.Map:
                if (InStep < 0.5f) return;
                MapSelectPopup popup = MapSelectPopup.Instance;
                if (popup != null && popup.Ready)
                {
                    popup.SelectMap(0);
                    popup.Confirm();
                    Check(true, "map chosen in Single Play's map picker");
                }
                else Check(true, "no map picker in the scene: the server picks the map");
                Go(Step.Match);
                return;

            case Step.Match:
                if (!lobbyLaidOut) { lobbyLaidOut = true; CheckLobbyLayout(); }
                if (!homeChecked && FindByName("MatchmakingCanvas") is { activeSelf: true })
                {
                    homeChecked = true;
                    Check(FindByName("HomeCanvas") is { activeSelf: false }, "the Home screen is hidden during matchmaking");
                }
                if (!OnlineRound.Active) return;
                Check(Net.MatchSeats == Seats, $"the match has the room's {Seats} seats ({Net.MatchSeats})");
                Check(Net.LocalRole == (Seeker ? Role.Hunter : Role.Fugitive), $"plays the side picked ({Net.LocalRole})");
                Go(Step.Round);
                return;

            case Step.Round:
                PlayRound();
                if (matchEnds > 0 && InStep > 2f) { CheckRound(); Go(Step.Result); }
                return;

            case Step.Result:
                if (InStep < 2f) return;
                OnlineRound round = OnlineRoundInstance();
                bool shown = round != null && (bool)typeof(OnlineRound).GetField("resultShown", Any).GetValue(round);
                bool hasPopup = typeof(OnlineRound).GetField("results", Any).GetValue(round) != null;
                Check(shown && hasPopup, "Single Play's result popup (HideModeResultUI) shows the online result");
                Object.FindAnyObjectByType<OnlineLobby>().GoHomeFromRound();
                Go(Step.Home);
                return;

            case Step.Home:
                if (InStep < 3f) return;   // ShopInventory.Refresh after the round
                Check(!OnlineRound.Active, "back Home after the round");
                MatchmakingUI lobbyUi = Object.FindAnyObjectByType<MatchmakingUI>(FindObjectsInactive.Include);
                int Authored(string box) => FindByName(box)?.GetComponentsInChildren<Transform>(true).Count(t => t.name.StartsWith("Slot")) ?? -1;
                Check(lobbyUi != null && lobbyUi.totalPlayers == GameTuning.TotalPlayers && lobbyUi.hunterSlots == GameTuning.HunterSlots
                      && Authored("FugitiveBox") == GameTuning.FugitiveSlots && Authored("HunterBox") == GameTuning.HunterSlots,
                      $"lobby screen is back to Single Play's 3 + 5 ({Authored("HunterBox")} + {Authored("FugitiveBox")})");
                Check(ShopInventory.Loaded, "wallet refreshed from the server after the round");
                if (Seeker && !Chase)
                    Check(ShopInventory.SpecialBullets(BulletType.Special2) < stockBeforeRound.Special2,
                          $"special bullets spent in the round are gone from the wallet ({stockBeforeRound.Special2} -> {ShopInventory.SpecialBullets(BulletType.Special2)})");
                OnlineHistory.Show(Object.FindAnyObjectByType<OnlineLobby>().transform, null);
                Go(Step.History);
                return;

            case Step.History:
                if (InStep < 2f) return;
                Text status = FindByName("HistoryScreen")?.GetComponentsInChildren<Text>(true)
                    .FirstOrDefault(t => t.text.Contains("No matches") || t.text.Contains("did not answer") || t.text.Contains("could not"));
                bool rows = FindByName("HistoryScreen")?.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Record") == true;
                Check(rows || (status != null && status.text.StartsWith("No matches")),
                      "match history loads from the server" + (status != null ? $" (\"{status.text}\")" : rows ? " (records listed)" : ""));
                Go(Step.ShopCards);
                return;

            case Step.ShopCards:
                ShopUI shop = Object.FindAnyObjectByType<ShopUI>(FindObjectsInactive.Include);
                if (nextAction == 0f)
                {
                    // Two more of type 2, so its cards have a count to show.
                    nextAction = 1f;
                    Meta.Purchase(new List<MetaPurchaseLine> { new MetaPurchaseLine { ItemId = "bullets-2", Quantity = 2 } },
                                  _ => reboughtAnswered = true);
                    return;
                }
                if (!reboughtAnswered) return;
                if (nextAction == 1f && shop != null)
                {
                    shop.Open();
                    shop.SelectTab((int)ShopCategory.Bullets);   // the Bullets tab, as a player taps it
                    nextAction = 2f;
                    stepStarted = Time.realtimeSinceStartup;
                    return;
                }
                if (InStep < 3f) return;
                ShopCatalog catalog = Resources.FindObjectsOfTypeAll<ShopCatalog>().FirstOrDefault();
                int owned = ShopInventory.SpecialBullets(BulletType.Special2);
                var badges = Object.FindObjectsByType<ShopItemCard>(FindObjectsInactive.Include)
                    .Where(c => c.Item != null && c.Item.category == ShopCategory.Bullets && c.Item.gameId == 2)
                    .Select(c => c.GetComponentsInChildren<Text>(true).Select(t => t.text).FirstOrDefault(t => t.StartsWith("Own ")))
                    .ToList();
                Check(owned > 0 && badges.Any(b => b == $"Own {owned}"),
                      $"shop's special bullet 2 cards show the owned count (owned {owned}; ribbons: {string.Join(", ", badges.Select(b => b ?? "-"))}; catalog {(catalog != null ? "loaded" : "missing")})");
                Go(Again ? Step.Again : SinglePlay ? Step.SinglePlay : Step.Done);
                return;

            case Step.Again:
                // Against a cluster: the join ticket is long stale, so this goes back to the
                // matchmaker and on to a game process again, with no game code involved.
                if (nextAction == 0f)
                {
                    nextAction = 1f;
                    matchesAtAgain = matchesFound;
                    Net.FindMatch(Seeker ? RolePreference.Seeker : RolePreference.Hider, "practice", 0, "Room");
                    return;
                }
                if (matchesFound == matchesAtAgain) return;
                // The lobby screen is closed by now, so it gives the seat up (OnlineLobby.HandleMatchFound).
                Check(Net.IsConnected, $"a second match found after the first, {InStep:F1} s after asking");
                Net.LeaveMatch();
                Go(SinglePlay ? Step.SinglePlay : Step.Done);
                return;

            case Step.SinglePlay:
                PlaySinglePlay();
                return;

            case Step.Done:
                Check(errorsLogged == 0, $"no errors or exceptions logged by the game ({errorsLogged}{(errors.Count > 0 ? ": " + string.Join(" | ", errors.Take(3)) : "")})");
                Finish();
                return;
        }
    }

    // -playtestSinglePlay: after the online match, a Single Play round on each side, offline, through
    // the same entry points the mode buttons use. Online code left behind would show here.
    private static bool SinglePlay => System.Environment.GetCommandLineArgs().Contains("-playtestSinglePlay");
    private static int singlePlayStage;
    private static List<Vector3> seekerStarts;
    private static float singleWalked;
    private static List<Transform> singleBots;

    private static void PlaySinglePlay()
    {
        GameModeManager gmm = Object.FindAnyObjectByType<GameModeManager>();
        BotManager manager = Object.FindAnyObjectByType<BotManager>();
        BotSeekController seek = Object.FindAnyObjectByType<BotSeekController>();
        BotHideController hide = Object.FindAnyObjectByType<BotHideController>();

        switch (singlePlayStage)
        {
            case 0:
                Check(!OnlineRound.Active, "no online round is left running before Single Play");
                gmm.ShowMenu();
                gmm.BeginRound(false);      // Play Now: the player hides, the bots hunt
                seek.SkipGrace();
                singleBots = manager.Bots.ToList();
                singlePlayStage = 1;
                stepStarted = Time.realtimeSinceStartup;
                return;

            case 1:
                if (InStep < 0.3f) return;
                seekerStarts = singleBots.Skip(singleBots.Count - GameTuning.HunterSlots).Select(b => b.position).ToList();
                Check(seek.chasePlayer, "Single Play's seekers hunt the player again after an online round");
                singlePlayStage = 2;
                return;

            case 2:
                // The player stands in the open here, so they may be spotted within a couple of seconds.
                singleWalked = Mathf.Max(singleWalked, singleBots.Skip(singleBots.Count - GameTuning.HunterSlots)
                    .Select((b, i) => Vector3.Distance(b.position, seekerStarts[i])).Max());
                bool spotted = (bool)typeof(BotSeekController).GetField("_spotting", Any).GetValue(seek) || gmm.PlayerFrozen;
                if (InStep < 12f && !spotted) return;
                Check(singleWalked > 3f || spotted, $"Single Play: the seeker bots hunted (furthest {singleWalked:F0} units, spotted the player: {spotted})");
                Check(Net.HostedBots.Length == 0, "Single Play sends nothing to the server about its bots");
                gmm.ShowMenu();
                gmm.BeginRound(true);       // the player seeks, the bots hide
                singlePlayStage = 3;
                stepStarted = Time.realtimeSinceStartup;
                return;

            case 3:
                if (InStep < 3f) return;
                Check(hide.BotCount > 0 && hide.Active, $"Single Play: the hider bots took their places ({hide.BotCount})");
                gmm.ShowMenu();
                Go(Step.Done);
                return;
        }
    }

    // -playtestAgain: ask for another match after the first (the path every returning player takes).
    private static bool Again => System.Environment.GetCommandLineArgs().Contains("-playtestAgain");
    private static int matchesFound, matchesAtAgain;

    private static bool signedIn, bought, boughtAnswered, submittedName, reboughtAnswered;
    private static bool lobbyLaidOut, homeChecked;

    // The lobby screen with every seat taken: bots fill a lobby in the same tick the round starts,
    // so this is laid out directly. Each seat must show, inside its column, overlapping no other.
    private static void CheckLobbyLayout()
    {
        MatchmakingUI ui = Object.FindAnyObjectByType<MatchmakingUI>(FindObjectsInactive.Include);
        int seekers = GameTuning.SeekersFor(Seats);
        ui.SetOnlineMatchSize(Seats, seekers);
        ui.SetOnlineRoster(Enumerable.Range(0, Seats).Select(i => new MatchmakingUI.OnlineSeat { Name = "Seat" + i, Hunter = i < seekers }).ToList());

        int shown = 0, outside = 0, overlaps = 0;
        foreach (string boxName in new[] { "FugitiveBox", "HunterBox" })
        {
            var box = (RectTransform)FindByName(boxName).transform;
            Rect Bounds(RectTransform rt)
            {
                var c = new Vector3[4];
                rt.GetWorldCorners(c);
                return new Rect(c[0].x, c[0].y, c[2].x - c[0].x, c[2].y - c[0].y);
            }

            Rect boxRect = Bounds(box);
            List<Rect> rows = box.GetComponentsInChildren<RectTransform>(false).Where(t => t.name.StartsWith("Slot")).Select(Bounds).ToList();
            shown += rows.Count;
            outside += rows.Count(r => r.xMin < boxRect.xMin || r.xMax > boxRect.xMax || r.yMin < boxRect.yMin || r.yMax > boxRect.yMax);
            for (int a = 0; a < rows.Count; a++)
                for (int b = a + 1; b < rows.Count; b++)
                    if (rows[a].Overlaps(rows[b])) overlaps++;
        }

        Check(shown == Seats && outside == 0 && overlaps == 0,
              $"the lobby screen lays out all {Seats} seats ({shown} shown, {outside} outside their column, {overlaps} overlapping)");
        ui.SetOnlineRoster(null);
    }
    private static MetaProfile profile;

    // ================= The round =================

    // Client-authoritative movement, measured.
    private static Vector3 lastFeet;
    private static bool haveLastFeet;
    private static float walkedPath, walkedSince, walkedSeconds, worstServerGap;
    private static readonly List<float> feetAboveFloor = new List<float>();

    // Pushes the real on-screen joystick, so JoystickMover and the CharacterController do the walking.
    private static void DriveJoystick(bool on)
    {
        JoystickMover mover = Object.FindAnyObjectByType<JoystickMover>();
        Joystick stick = mover != null ? Get<Joystick>(mover, "joystick") : null;
        if (stick == null) return;

        float t = Time.realtimeSinceStartup * 0.7f;
        typeof(Joystick).GetField("input", Any).SetValue(stick, on ? new Vector2(Mathf.Sin(t), Mathf.Cos(t)) : Vector2.zero);
    }

    private static float spawnGap = float.MaxValue;

    // What could be holding the character, every couple of seconds; reported only if it did not walk.
    private static readonly List<string> moveTrace = new List<string>();
    private static float nextTraceAt;

    private static void TraceMovement(NetEntityView local, float now)
    {
        if (now < nextTraceAt) return;
        nextTraceAt = now + 2f;

        JoystickMover mover = Object.FindAnyObjectByType<JoystickMover>();
        Joystick stick = mover != null ? Get<Joystick>(mover, "joystick") : null;
        WallClimber climber = local.GetComponentInChildren<WallClimber>();
        CameraController cam = Object.FindAnyObjectByType<CameraController>();
        GameModeManager gmm = Object.FindAnyObjectByType<GameModeManager>();
        CharacterController cc = local.GetComponent<CharacterController>();
        Vector3 f = local.FeetPosition;
        Vector2 d = stick != null ? stick.Direction : Vector2.zero;
        moveTrace.Add($"  feet ({f.x:F1},{f.y:F1},{f.z:F1}) stick ({d.x:F2},{d.y:F2}) activeStick={stick != null && stick.gameObject.activeInHierarchy} " +
                      $"moverOn={mover != null && mover.isActiveAndEnabled} onWall={climber != null && climber.IsOnWall} freeLook={cam != null && cam.FreeLook} " +
                      $"firstPerson={cam != null && cam.IsFirstPerson} frozen={gmm != null && gmm.PlayerFrozen} countdown={gmm != null && gmm.SeekerHideCountdown} " +
                      $"cc={cc != null && cc.enabled} grounded={cc != null && cc.isGrounded} timeScale={Time.timeScale}");

        if (cc == null) return;
        float scale = local.transform.lossyScale.y;
        float radius = cc.radius * scale + 1.5f;
        Vector3 low = f + Vector3.up * radius, high = f + Vector3.up * (cc.height * scale - radius);
        IEnumerable<string> around = Physics.OverlapCapsule(low, high, radius, ~0, QueryTriggerInteraction.Collide)
            .Where(c => !c.transform.IsChildOf(local.transform))
            .Select(c => $"{c.name}[{c.GetType().Name}{(c.isTrigger ? ",trigger" : "")}]")
            .Distinct();
        moveTrace.Add("    touching: " + string.Join(", ", around));
    }

    // The server names spawns by number; the character must end up on one of this room's markers.
    private static float DistanceToNearestMarker(Vector3 feet)
    {
        GameObject room = Object.FindAnyObjectByType<LevelManager>().CurrentRoomInstance;
        string prefix = Seeker ? "HunterSpawn" : "HiderSpawn";
        return room.GetComponentsInChildren<Transform>(true)
            .Where(t => t.name.StartsWith(prefix))
            .Select(t => Vector3.Distance(feet, t.position))
            .DefaultIfEmpty(float.MaxValue)
            .Min();
    }

    // Client-hosted bots, measured on the bodies this game drives.
    private static int hostAssignments, botsHostedMost, spotAlerts;
    private static bool hostingBeganLate;
    private static bool Handover => System.Environment.GetCommandLineArgs().Contains("-playtestHandover");
    private static readonly Dictionary<byte, Vector3> hostedStart = new Dictionary<byte, Vector3>();
    private static float hostedSeekerWalked, worstHostedGap, hostedClimbRise;
    private static readonly HashSet<byte> hostedClimbers = new HashSet<byte>();

    // Single Play's last-resort rescue hops a stranded seeker across the room. The server lets the
    // body catch up at running speed, so it is left out of the gap until it has.
    private static readonly Dictionary<byte, Vector3> hostedLast = new Dictionary<byte, Vector3>();
    private static readonly Dictionary<byte, float> hopExemptUntil = new Dictionary<byte, float>();
    private static int hostedHops;
    private static int hiderBotsMoved, hiderBotsPainted;
    private static bool hiderBotsMeasured;

    private static void SampleHostedBots()
    {
        botsHostedMost = Math.Max(botsHostedMost, OnlineRound.HostedBotCount);
        if (Net.Phase != MatchPhase.Seek) return;

        foreach (NetEntityView v in Object.FindObjectsByType<NetEntityView>(FindObjectsSortMode.None).Where(x => x.IsHosted && !x.Eliminated))
        {
            if (!hostedStart.ContainsKey(v.Slot)) { hostedStart[v.Slot] = v.FeetPosition; continue; }
            if (v.Role == Role.Hunter)
            {
                if (Object.FindAnyObjectByType<BotSeekController>().IsClimbing(v.transform))
                {
                    hostedClimbers.Add(v.Slot);
                    hostedClimbRise = Mathf.Max(hostedClimbRise, v.FeetPosition.y - hostedStart[v.Slot].y);
                }

                hostedSeekerWalked = Mathf.Max(hostedSeekerWalked, Vector3.Distance(hostedStart[v.Slot], v.FeetPosition));

                float now = Time.realtimeSinceStartup;
                if (hostedLast.TryGetValue(v.Slot, out Vector3 last) && Vector3.Distance(last, v.FeetPosition) > 15f)
                {
                    hostedHops++;
                    hopExemptUntil[v.Slot] = now + 15f;
                }
                hostedLast[v.Slot] = v.FeetPosition;

                if (!hopExemptUntil.TryGetValue(v.Slot, out float until) || now > until)
                    worstHostedGap = Mathf.Max(worstHostedGap, Vector3.Distance(v.FeetPosition, v.LastServerFeet));
            }
        }

        if (hiderBotsMeasured) return;
        hiderBotsMeasured = true;

        GameObject room = Object.FindAnyObjectByType<LevelManager>().CurrentRoomInstance;
        List<Vector3> markers = room.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("HiderSpawn")).Select(t => t.position).ToList();
        foreach (NetEntityView v in Object.FindObjectsByType<NetEntityView>(FindObjectsSortMode.None).Where(x => x.IsHosted && x.Role == Role.Fugitive))
        {
            if (markers.Count > 0 && markers.Min(m => Vector3.Distance(m, v.FeetPosition)) > 4f) hiderBotsMoved++;
            if (v.GetComponentInChildren<SkinnedMeshRenderer>().material.GetColor("_BaseColor") != Color.white) hiderBotsPainted++;
        }
    }

    private static void CheckHostedBots()
    {
        if (hostAssignments == 0)
        {
            Check(!Handover, "this game was given the bots to run");
            report.Add("INFO  another player's game hosted the bots");
            return;
        }

        Check(botsHostedMost > 0, $"this game ran the empty seats' bots (up to {botsHostedMost})");
        if (Handover) Check(hostingBeganLate, "the bots came to this game when their host left mid-round");
        else
        {
            Check(hiderBotsMoved > 0, $"Single Play's hide logic put the hider bots in hiding places ({hiderBotsMoved} off their spawns)");
            Check(hiderBotsPainted > 0, $"hider bots camouflaged themselves ({hiderBotsPainted})");
        }
        Check(hostedSeekerWalked > 15f, $"seeker bots walked the room under Single Play's seek logic (furthest {hostedSeekerWalked:F0} units from the start)");
        Check(hostedClimbers.Count > 0 && hostedClimbRise > 3f, $"seeker bots climbed walls ({hostedClimbers.Count} bots, up to {hostedClimbRise:F0} units up)");
        Check(worstHostedGap < 15f, $"the server followed the hosted bots (worst gap {worstHostedGap:F1} units)");
        if (!Seeker) report.Add($"INFO  seeker bots spotted this hider {spotAlerts} time(s)");
        if (hostedHops > 0) report.Add($"INFO  a stranded seeker bot used Single Play's rescue hop {hostedHops} time(s)");
    }

    private static void SampleMovement()
    {
        NetEntityView local = NetEntityRegistry.Get(Net.LocalSlot);
        if (local == null || local.Eliminated || Net.PositionEpoch == PlayerInput.NoPositionEpoch) return;

        if (Net.Phase == MatchPhase.Seek || (!Seeker && Net.Phase == MatchPhase.Hide))
        {
            float now = Time.realtimeSinceStartup;
            if (!haveLastFeet)
            {
                haveLastFeet = true;
                walkedSince = now + 1f;   // from when it has landed and got up to speed
                spawnGap = DistanceToNearestMarker(local.FeetPosition);
            }
            else if (now > walkedSince)
            {
                walkedPath += Vector3.Distance(lastFeet, local.FeetPosition);
                walkedSeconds += Time.unscaledDeltaTime;
            }
            lastFeet = local.FeetPosition;
            TraceMovement(local, now);
            worstServerGap = Mathf.Max(worstServerGap, Vector3.Distance(local.FeetPosition, local.LastServerFeet));
        }

        // The wire position is the feet: a body standing on the floor must have it at floor level.
        if (feetAboveFloor.Count >= 400) return;
        foreach (NetEntityView v in Object.FindObjectsByType<NetEntityView>(FindObjectsSortMode.None))
        {
            if (v.IsLocal || v.Eliminated || (v.Flags & EntityFlags.Grounded) == 0) continue;
            Vector3 feet = v.FeetPosition;
            foreach (RaycastHit hit in Physics.RaycastAll(feet + Vector3.up * 6f, Vector3.down, 14f).OrderBy(h => h.distance))
            {
                if (hit.collider.GetComponentInParent<NetEntityView>() != null) continue;
                feetAboveFloor.Add(Mathf.Abs(feet.y - hit.point.y));
                break;
            }
        }
    }

    // -playtestChase: the seeker walks up to the nearest hider instead of shooting, at walking speed
    // and straight through anything, to prove that touching a hider eliminates nobody.
    private static bool Chase => System.Environment.GetCommandLineArgs().Contains("-playtestChase");

    // Single Play's hider bots tuck in behind furniture, where a touch has no clear line: after a
    // moment beside one, the walker moves on to the next.
    private static readonly HashSet<byte> chaseSkipped = new HashSet<byte>();
    private static byte chaseTarget = 0xFF;
    private static byte chaseProgressSlot = 0xFF;
    private static float chaseBestDistance = float.MaxValue, chaseProgressAt;
    private static float chaseArrivedAt = -1f;

    private static void WalkToNearestHider()
    {
        NetEntityView me = NetEntityRegistry.Get(Net.LocalSlot);
        if (me == null) return;

        NetEntityView nearest = Object.FindObjectsByType<NetEntityView>()
            .Where(v => !v.IsLocal && !v.Eliminated && v.Role == Role.Fugitive && !chaseSkipped.Contains(v.Slot))
            .OrderBy(v => Vector3.Distance(v.transform.position, me.transform.position))
            .FirstOrDefault();
        if (nearest == null) return;

        Vector3 to = nearest.transform.position - me.transform.position;
        if (nearest.Slot != chaseTarget) { chaseTarget = nearest.Slot; chaseArrivedAt = -1f; }
        if (to.magnitude < 5f)
        {
            if (chaseArrivedAt < 0f) chaseArrivedAt = Time.realtimeSinceStartup;
            else if (Time.realtimeSinceStartup > chaseArrivedAt + 1.5f) chaseSkipped.Add(nearest.Slot);
        }

        float step = Mathf.Min(to.magnitude, GameTuning.MoveSpeed * 0.9f * Time.deltaTime);

        // The body's collider stops it at a wall, and a hider is often behind one. Once a second
        // without progress it hops a wall's thickness, inside the server's burst allowance.
        if (to.magnitude < chaseBestDistance - 1f || nearest.Slot != chaseProgressSlot)
        {
            chaseBestDistance = to.magnitude;
            chaseProgressSlot = nearest.Slot;
            chaseProgressAt = Time.realtimeSinceStartup;
        }
        else if (Time.realtimeSinceStartup > chaseProgressAt + 1f)
        {
            step = Mathf.Min(to.magnitude, GameTuning.MoveBurst * 0.9f);
            chaseProgressAt = Time.realtimeSinceStartup;
        }

        CharacterController cc = me.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        me.transform.position += to.normalized * step;
        if (cc != null) cc.enabled = true;
    }

    // ================= What the 2026-11-10 fixes promise =================

    private static bool spawnsChecked, playerPrefabChecked, gunChecked;

    // Every seat of the match resolves to a place of its own, whichever number the match starts from.
    private static void CheckSpawnsApart()
    {
        if (spawnsChecked || Net.Phase != MatchPhase.Hide) return;
        spawnsChecked = true;

        OnlineRound round = OnlineRoundInstance();
        MethodInfo resolve = typeof(OnlineRound).GetMethod("ResolveSpawn", Any, null, new[] { typeof(int), typeof(bool) }, null);
        int seekers = GameTuning.SeekersFor(Seats);
        float closest = float.MaxValue;

        foreach (int first in new[] { 0, 7, 199 })
        {
            var spots = new List<Vector3>();
            for (int i = 0; i < seekers; i++) spots.Add((Vector3)resolve.Invoke(round, new object[] { first + i, true }));
            for (int i = 0; i < Seats - seekers; i++) spots.Add((Vector3)resolve.Invoke(round, new object[] { first + i, false }));

            for (int a = 0; a < spots.Count; a++)
                for (int b = a + 1; b < spots.Count; b++)
                    closest = Mathf.Min(closest, Vector2.Distance(new Vector2(spots[a].x, spots[a].z), new Vector2(spots[b].x, spots[b].z)));
        }

        Check(closest >= 9f, $"all {Seats} seats start apart (closest two {closest:F1} units)");
    }

    private static void CheckPlayerPrefab()
    {
        if (playerPrefabChecked) return;
        playerPrefabChecked = true;

        Transform player = PlayerRef.Active;
        int bodies = Object.FindObjectsByType<SeekerCamProfile>(FindObjectsInactive.Include).Length;
        Check(player != null && player.parent != null && player.parent.name == "Characters" && bodies == 1,
              $"the player is one body spawned from Resources/Players ({(player != null ? player.name : "none")}, {bodies} in the scene)");
    }

    private static void CheckSeekerBotGun()
    {
        if (gunChecked) return;

        foreach (NetEntityView v in Object.FindObjectsByType<NetEntityView>().Where(x => !x.IsLocal && x.Role == Role.Hunter))
        {
            Transform gun = v.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "NetGun" || t.name == "BotGun");
            if (gun == null) continue;

            gunChecked = true;
            GameObject prefab = Resources.Load<GameObject>("GameObjects/gun");
            Check(prefab != null && gun.GetComponent<MeshFilter>().sharedMesh == prefab.GetComponent<MeshFilter>().sharedMesh
                  && gun.localScale == prefab.transform.localScale && gun.Find("MuzzlePoint") != null,
                  "seeker bodies carry the gun from Resources/GameObjects");
            return;
        }
    }

    // A seeker's body lying down shows it: the "Gun" layer used to cover the pose.
    private static int proneStage;
    private static float proneAt;
    private static NetEntityView proneBody;

    private static void CheckSeekerBodyLiesDown()
    {
        if (proneStage >= 3 || Net.Phase != MatchPhase.Seek) return;

        PropertyInfo flags = typeof(NetEntityView).GetProperty("Flags", Any);
        BotSeekController seek = Object.FindAnyObjectByType<BotSeekController>();
        float now = Time.realtimeSinceStartup;

        if (proneStage == 0)
        {
            proneBody = Object.FindObjectsByType<NetEntityView>()
                .FirstOrDefault(v => v.IsHosted && v.Role == Role.Hunter && v.GetComponent<NetBodyAnimator>() != null && !seek.IsClimbing(v.transform));
            if (proneBody == null) return;
            flags.SetValue(proneBody, EntityFlags.Prone);
            proneStage = 1;
            proneAt = now;
            return;
        }

        if (now < proneAt + 0.3f) return;

        Animator animator = proneBody.GetComponentInChildren<Animator>();
        int layer = animator.GetLayerIndex("Gun");

        if (proneStage == 1)
        {
            Check(animator.GetBool("Nam") && animator.GetLayerWeight(layer) == 0f, "a seeker's body lying down is drawn lying down (Gun layer off)");
            flags.SetValue(proneBody, EntityFlags.None);
            proneStage = 2;
            proneAt = now;
            return;
        }

        Check(!animator.GetBool("Nam") && animator.GetLayerWeight(layer) == 1f, "and it gets up again with its gun");
        proneStage = 3;
    }

    // The free camera, once this hider is out.
    private static int freeLookStage;
    private static float freeLookAt;
    private static Vector3 freeLookFrom;

    private static void CheckFreeLookWhenOut()
    {
        NetEntityView me = NetEntityRegistry.Get(Net.LocalSlot);
        if (me == null || !me.Eliminated || freeLookStage == 2) return;

        CameraController cam = Object.FindAnyObjectByType<CameraController>();
        FieldInfo pivot = typeof(CameraController).GetField("_freePivot", Any);

        if (freeLookStage == 0)
        {
            cam.SetFreeLook(true);
            freeLookFrom = (Vector3)pivot.GetValue(cam);
            freeLookAt = Time.realtimeSinceStartup;
            freeLookStage = 1;
            return;
        }

        if (Time.realtimeSinceStartup < freeLookAt + 1.5f) return;

        float moved = Vector3.Distance((Vector3)pivot.GetValue(cam), freeLookFrom);
        Check(cam.FreeLook && moved > 5f, $"the free camera flies once the player is out ({moved:F0} units in 1.5 s)");
        cam.SetFreeLook(false);
        freeLookStage = 2;
    }

    private static void PlayRound()
    {
        CheckPlayerPrefab();
        CheckSpawnsApart();
        CheckSeekerBotGun();
        CheckSeekerBodyLiesDown();
        CheckFreeLookWhenOut();
        SampleMovement();
        SampleHostedBots();
        DriveJoystick(!Chase && (Net.Phase == MatchPhase.Seek || (!Seeker && Net.Phase == MatchPhase.Hide)));

        if (Chase && Seeker && Net.Phase == MatchPhase.Seek)
        {
            WalkToNearestHider();
            return;
        }

        // Other bodies, as the server places them.
        foreach (NetEntityView v in Object.FindObjectsByType<NetEntityView>())
        {
            if (v.IsLocal) continue;
            remoteViews = Math.Max(remoteViews, Object.FindObjectsByType<NetEntityView>().Count(x => !x.IsLocal));
            if (v.GetComponent<NetBodyAnimator>() != null) remoteAnimators = Math.Max(remoteAnimators, 1);
        }

        if (Net.Phase == MatchPhase.Seek) sawPhaseSeek = 1;

        if (Seeker)
        {
            if (Net.Phase != MatchPhase.Seek || Time.realtimeSinceStartup < nextAction) return;

            // Switch to special bullet 2 once (the bullet label's tap), then keep shooting.
            if (!chosenSpecial && Net.SpecialBullets.Special2 > 0)
            {
                chosenSpecial = true;
                Call(OnlineRoundInstance(), "CycleBullet");   // Default -> the next type owned: special bullet 2
                nextAction = Time.realtimeSinceStartup + 0.6f;
                return;
            }

            Mode1FirstPersonGun gun = Object.FindAnyObjectByType<Mode1FirstPersonGun>();
            if (gun != null)
            {
                gun.Fire();   // the fire button; the gun waits out the bullet's cooldown itself
                shotsFired++;
            }
            nextAction = Time.realtimeSinceStartup + 0.5f;
            if (Net.SpecialBullets.Special2 < stockBeforeRound.Special2) specialSpent = true;
        }
        else
        {
            if (Net.Phase == MatchPhase.Hide && hideSeenAt < 0f) hideSeenAt = Time.realtimeSinceStartup;
            if (Net.Phase == MatchPhase.Hide && !pressedStart && Time.realtimeSinceStartup > hideSeenAt + 5f)
            {
                sawStartButton |= OnlineRound.WantsStartButton;
                Object.FindAnyObjectByType<BotSeekController>()?.SkipGrace();   // the Start button
                pressedStart = true;
            }
            if (Net.Phase == MatchPhase.Hide && !painted)
            {
                OnlineRound.SendPaintMaterial(0.6f, 0.3f);   // ChameleonPaint's sliders
                painted = true;
            }
        }
    }

    private static int catches;
    private static float hideSeenAt = -1f;

    private static void CheckRound()
    {
        DriveJoystick(false);
        CheckHostedBots();
        Check(spawnGap < 12f, $"the character started on one of the room's spawn markers ({spawnGap:F1} units away)");
        if (Chase)
        {
            Check(catches == 0, $"walking into the hiders eliminated nobody ({catches} touch eliminations)");
            return;
        }
        // How long there is to walk depends on the bots (a short round, or being caught), so this
        // judges the speed reached rather than the distance covered.
        if (walkedSeconds >= 1f)
        {
            bool walked = walkedPath / walkedSeconds > 5f;
            Check(walked, $"the joystick walked the character ({walkedPath:F0} units in {walkedSeconds:F1} s)");
            if (!walked) report.AddRange(moveTrace);
        }
        else report.Add($"INFO  the round left too little time to judge walking ({walkedSeconds:F1} s)");
        Check(worstServerGap < (Handover ? 14f : 8f), $"the server's position kept up with the client's (worst gap {worstServerGap:F1} units)");
        Check(Net.PositionPlacements <= 2, $"the server never had to correct the walking ({Net.PositionPlacements} placements: the spawns only)");

        if (feetAboveFloor.Count > 0)
        {
            feetAboveFloor.Sort();
            float median = feetAboveFloor[feetAboveFloor.Count / 2];
            Check(median < 3f, $"other bodies stand on the floor (feet within {median:F1} units, {feetAboveFloor.Count} samples)");
        }
        else report.Add("INFO  no body was over a collider to measure");

        Check(phaseChanges >= 2 && sawPhaseSeek == 1, $"phases came from the server (hide -> seek; {phaseChanges} changes)");
        Check(remoteViews > 0, $"other players' bodies placed from snapshots ({remoteViews})");
        Check(remoteAnimators > 0, "other bodies carry NetBodyAnimator (prone / climbing / gunfire)");
        if (Seeker)
        {
            Check(shotsFired > 0 && loadoutAnswers > 0, $"shots fired through the gun were answered by the server ({shotsFired} fired, {loadoutAnswers} loadout answers)");
            Check(chosenSpecial && specialSpent, "switched to special bullet 2 by the bullet label and spent it");
        }
        else
        {
            Check(pressedStart && sawStartButton, "the hider's Start button showed and was pressed (SkipHide)");
            Check(painted, "metallic / roughness sent from the paint sliders");
        }
        // Whether another seeker fires where this player can see them depends on the lobby and
        // the map, so none arriving is reported, not failed (the server tests cover delivery).
        if (remoteShots > 0) Check(true, $"other players' shots arrived as OnRemoteShot ({remoteShots}) and were drawn");
        else report.Add("INFO  no other player's shot was in view this match (OnRemoteShot 0)");
        report.Add($"INFO  other players are drawn {Net.InterpolationDelaySeconds * 1000f:F0} ms behind the newest snapshot; ping {Net.PingMs} ms");
        if (freeLookStage == 0) report.Add("INFO  this player was not eliminated, so the free camera was not tried out of the round");
        Check(matchEnds > 0, "the match ended with the server's result");
    }

    private static void Hook()
    {
        if (hooked) return;
        hooked = true;
        Net.OnRemoteShot += _ => remoteShots++;
        Net.OnLoadoutChanged += () => loadoutAnswers++;
        Net.OnPhaseChanged += (_, _) => phaseChanges++;
        Net.OnEliminated += (_, _, cause) => { eliminations++; if (cause == EliminationCause.Caught) catches++; };
        Net.OnMatchEnd += _ => matchEnds++;
        Net.OnSpotAlert += _ => spotAlerts++;
        Net.OnMatchFound += _ => matchesFound++;
        Net.OnBotHost += bots =>
        {
            if (bots.Length == 0) return;
            hostAssignments++;
            hostingBeganLate |= bots.All(b => b.SpawnIndex == Snapshot.NoSpawn);
        };
    }

    // ================= Finish =================

    private static void Finish()
    {
        step = Step.Done;
        SessionState.SetBool(ActiveKey, false);
        EditorApplication.update -= Tick;
        Application.logMessageReceived -= CountErrors;
        ShopInventory.PreferredBullet = BulletType.Default;   // leave the editor's preference as found

        var text = new StringBuilder();
        text.AppendLine($"Online play-test as {(Seeker ? "seeker" : "hider")} against {Host}:{Port} (meta {MetaPort}), {Time.realtimeSinceStartup - startedAt:F0} s");
        foreach (string line in report) text.AppendLine(line);
        text.AppendLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, text.ToString());
        Debug.Log("[PlayTest]\n" + text);

        int code = failures == 0 ? 0 : 1;
        EditorApplication.isPlaying = false;
        EditorApplication.delayCall += () => EditorApplication.Exit(code);
    }

    private static void CountErrors(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        if (message.StartsWith("[PlayTest]")) return;
        errorsLogged++;
        if (errors.Count < 5) errors.Add(message.Length > 160 ? message.Substring(0, 160) : message);
    }

    // ================= Helpers =================

    private static OnlineRound OnlineRoundInstance() =>
        (OnlineRound)typeof(OnlineRound).GetField("_instance", Any).GetValue(null);

    private static T Get<T>(object target, string field) where T : class =>
        target.GetType().GetField(field, Any)?.GetValue(target) as T;

    private static void Call(object target, string method) =>
        target.GetType().GetMethod(method, Any, null, Type.EmptyTypes, null).Invoke(target, null);

    private static GameObject FindByName(string name) =>
        Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.name == name && g.scene.IsValid());

    private static Button FindButton(string screen, string label)
    {
        GameObject root = FindByName(screen);
        if (root == null || !root.activeInHierarchy) return null;
        return root.GetComponentsInChildren<Button>(false)
            .FirstOrDefault(b => b.GetComponentsInChildren<Text>(true).Any(t => t.text.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0));
    }
}
