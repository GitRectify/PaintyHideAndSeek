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
using UnityEngine.EventSystems;
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
                if (Pair) net.AuthToken = "pair-test-" + System.Diagnostics.Process.GetCurrentProcess().Id;   // one PC, two players
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
                    if (Pair)
                    {
                        // Every pose the shop sells: all nine are tried in the round.
                        Meta.Purchase(Enumerable.Range(3, 6).Select(i => new MetaPurchaseLine { ItemId = "pose-" + i, Quantity = 1 }).ToList(), receipt =>
                        {
                            Check(receipt.Status == PurchaseStatus.Completed, $"bought the six poses on sale ({receipt.Status}, {receipt.Error})");
                            posesAnswered = true;
                        });
                    }
                }
                if (!boughtAnswered || (Pair && !posesAnswered)) return;

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
                if (Seeker && !Chase && !Pair && !Kills)
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

    private static bool WallsOpen()
    {
        WallClimber climber = Object.FindAnyObjectByType<WallClimber>(FindObjectsInactive.Include);
        return (bool)typeof(WallClimber).GetProperty("WallsOpen", Any).GetValue(climber);
    }

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
                Check(WallsOpen(), "Single Play: a hider can use the walls");
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
                Check(WallsOpen(), "Single Play: a seeker can use the walls");
                gmm.ShowMenu();
                Check(!WallsOpen(), "and nobody can from the Home screen");
                Go(Step.Done);
                return;
        }
    }

    // -playtestAgain: ask for another match after the first (the path every returning player takes).
    private static bool Again => System.Environment.GetCommandLineArgs().Contains("-playtestAgain");
    private static int matchesFound, matchesAtAgain;

    private static bool signedIn, bought, boughtAnswered, submittedName, reboughtAnswered, posesAnswered;
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
    private static float walkedPath, walkedSince, walkedSeconds, worstServerGap, placementGap;
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
    private static int botWallShapeFrames, botWallStraightFrames;

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

        foreach (NetEntityView v in Object.FindObjectsByType<NetEntityView>().Where(x => x.IsHosted && !x.Eliminated))
        {
            if (!hostedStart.ContainsKey(v.Slot)) { hostedStart[v.Slot] = v.FeetPosition; continue; }
            if (v.Role == Role.Hunter)
            {
                if (Object.FindAnyObjectByType<BotSeekController>().IsClimbing(v.transform))
                {
                    hostedClimbers.Add(v.Slot);
                    hostedClimbRise = Mathf.Max(hostedClimbRise, v.FeetPosition.y - hostedStart[v.Slot].y);

                    Animator a = v.GetComponentInChildren<Animator>();
                    if (a != null && !a.GetBool("Nam"))
                    {
                        bool straight = a.GetInteger("Pose") == 1 || a.GetCurrentAnimatorStateInfo(0).IsName("pose1") || a.GetLayerWeight(a.GetLayerIndex("Gun")) < 0.5f;
                        if (straight) botWallStraightFrames++; else botWallShapeFrames++;
                    }
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
        foreach (NetEntityView v in Object.FindObjectsByType<NetEntityView>().Where(x => x.IsHosted && x.Role == Role.Fugitive))
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
        if (Kills) report.Add($"INFO  a round ended by shooting every hider is too short to judge the bots' climbing ({hostedClimbers.Count} climbed)");
        else if (hostedClimbers.Count == 0) report.Add("INFO  no seeker bot chose to climb in this round (they do it by chance, or when a wall is in their way)");
        else Check(hostedClimbRise > 3f, $"seeker bots climbed walls ({hostedClimbers.Count} bots, up to {hostedClimbRise:F0} units up)");
        if (botWallShapeFrames + botWallStraightFrames > 0)
            Check(botWallStraightFrames == 0, $"and went up as they stand, gun in hand ({botWallShapeFrames} frames, {botWallStraightFrames} straightened)");
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

            // Once it has landed: the snapshot that places the player still says where they were.
            float gap = Vector3.Distance(local.FeetPosition, local.LastServerFeet);
            if (now > walkedSince) worstServerGap = Mathf.Max(worstServerGap, gap);
            else placementGap = Mathf.Max(placementGap, gap);
        }

        // The wire position is the feet: a body standing on the floor must have it at floor level.
        if (feetAboveFloor.Count >= 400) return;
        foreach (NetEntityView v in Object.FindObjectsByType<NetEntityView>())
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

        BotSeekController seek = Object.FindAnyObjectByType<BotSeekController>();
        float now = Time.realtimeSinceStartup;

        if (proneStage == 0)
        {
            proneBody = Object.FindObjectsByType<NetEntityView>()
                .FirstOrDefault(v => v.IsHosted && v.Role == Role.Hunter && v.GetComponent<NetBodyAnimator>() != null && !seek.IsClimbing(v.transform));
            if (proneBody == null) return;
            BodyAnimation.SetProne(proneBody.GetComponentInChildren<Animator>(), true);
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
            BodyAnimation.SetProne(animator, false);
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
        if (me == null || !me.Eliminated || freeLookStage == 2 || Pair) return;

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
        CheckBodies();
        if (Kills || Mixed) WatchKillBullet();
        SampleMovement();
        SampleHostedBots();
        SampleTimer();
        SampleAnimations();
        CheckAnimationSwitching();
        if (Pair)
        {
            PlayPair();
            return;
        }
        if (Kills && Seeker)
        {
            PlayKills();
            return;
        }
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

    // ================= Other players' bodies =================

    private static bool bodiesChecked;
    private static int bodyStage;
    private static float bodyAt;
    private static NetEntityView hostedSeeker;

    private static bool IsBuiltTag(Canvas c) => c.name == "NameTag" && c.gameObject.activeSelf;

    private static bool IsTagShown(NetEntityView v) => v.GetComponentsInChildren<Canvas>(true).Any(IsBuiltTag);

    private static IEnumerable<Transform> GunsOf(NetEntityView v) =>
        v.GetComponentsInChildren<Transform>(true).Where(t => t.name == "NetGun" || t.name == "BotGun" || t.name == "gun");

    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static MaterialPropertyBlock probe;

    // The body's paint canvas, when this renderer has been given one in place of its own texture.
    private static bool WearsCanvas(Renderer r)
    {
        if (!r.HasPropertyBlock()) return false;
        probe ??= new MaterialPropertyBlock();
        r.GetPropertyBlock(probe);
        return probe.GetTexture(BaseMapId) != null;
    }

    // Other seekers' guns as they are drawn here: with the material the gun prefab gives them, and
    // not the body's paint canvas, which is blank on a seeker and made the gun white.
    private static void CheckGunMaterials(List<NetEntityView> views, string when)
    {
        Material gunMaterial = Resources.Load<GameObject>("GameObjects/gun").GetComponentInChildren<Renderer>(true).sharedMaterial;
        int guns = 0, painted = 0, otherMaterial = 0, bodiesWithCanvas = 0, bots = 0;

        foreach (NetEntityView v in views.Where(v => v != null && v.Role == Role.Hunter))
        {
            if (v.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(WearsCanvas)) bodiesWithCanvas++;

            foreach (Renderer r in GunsOf(v).Where(g => g.gameObject.activeInHierarchy).SelectMany(g => g.GetComponentsInChildren<Renderer>(true)))
            {
                guns++;
                if (IsBotSlot(v.Slot)) bots++;
                if (WearsCanvas(r)) painted++;
                if (r.sharedMaterial != gunMaterial || gunMaterial.GetTexture(BaseMapId) == null) otherMaterial++;
            }
        }

        if (guns == 0)
        {
            report.Add($"INFO  no other seeker's gun to look at {when}");
            return;
        }

        Check(painted == 0 && otherMaterial == 0,
              $"other seekers' guns keep their own material {when} ({guns} guns, {bots} on bots; {painted} wear the body's paint canvas, {otherMaterial} another material; {bodiesWithCanvas} of their bodies wear a canvas)");
    }

    private static bool IsBotSlot(byte slot)
    {
        var entries = Get<MecchaNetClient.RosterEntry[]>(OnlineRoundInstance(), "roster");
        return entries != null && entries.Any(e => e.Slot == slot && e.IsBot);
    }

    // Every other body in the round, once it has had time to come up.
    private static void CheckBodies()
    {
        if (bodiesChecked || Net.Phase != MatchPhase.Seek) return;

        float now = Time.realtimeSinceStartup;
        if (bodyStage == 0)
        {
            bodyStage = 1;
            bodyAt = now + 1.5f;
            return;
        }

        if (bodyStage == 1)
        {
            if (now < bodyAt) return;

            var views = Object.FindObjectsByType<NetEntityView>().Where(v => !v.IsLocal && (v.HasPosition || v.IsHosted)).ToList();
            if (views.Count == 0) return;

            int seekers = 0, hiders = 0, tagsOnSeekers = 0, tagsOnHiders = 0, tagsInTheAir = 0, botsTagged = 0;
            int notDrawn = 0, offScreenRule = 0, wrong = 0, holders = 0, outOfRange = 0;
            foreach (NetEntityView v in views)
            {
                bool tagged = IsTagShown(v);
                if (!v.IsShown)
                {
                    outOfRange++;
                    if (tagged) tagsInTheAir++;
                }
                else if (v.Eliminated) { }                      // a hider who is out: judged as they fall (-playtestKills)
                else if (v.Role == Role.Hunter) { seekers++; if (tagged) tagsOnSeekers++; }
                else { hiders++; if (tagged) tagsOnHiders++; }

                if (tagged && IsBotSlot(v.Slot)) botsTagged++;

                foreach (SkinnedMeshRenderer mesh in v.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (v.IsShown && (!mesh.enabled || !mesh.gameObject.activeInHierarchy)) notDrawn++;
                    if (!mesh.updateWhenOffscreen) offScreenRule++;
                }

                bool armed = GunsOf(v).Any(g => g.gameObject.activeInHierarchy);
                if (armed != (v.Role == Role.Hunter)) wrong++;
                if (v.GetComponentInChildren<GunHolder>(true) != null || v.GetComponentInChildren<SeekerCamProfile>(true) != null) holders++;
            }

            // Whose name this player sees is their side's to say.
            if (Seeker)
            {
                Check(tagsOnSeekers == seekers, $"a seeker sees a name over every other seeker ({tagsOnSeekers} of {seekers}, {botsTagged} of them bots)");
                Check(tagsOnHiders == 0, $"and over no hider still in ({tagsOnHiders} of {hiders})");
            }
            else
            {
                Check(tagsOnSeekers == seekers && tagsOnHiders == hiders,
                      $"a hider sees a name over everybody: {tagsOnSeekers} of {seekers} seekers, {tagsOnHiders} of {hiders} other hiders ({botsTagged} of them bots)");
            }
            Check(tagsInTheAir == 0, $"no name hangs where the body is not drawn ({tagsInTheAir})");
            Check(notDrawn == 0, $"every other body's mesh is on ({notDrawn} off, {views.Count} bodies, {outOfRange} out of range)");
            Check(offScreenRule == 0, $"no other body can be culled by its import bounds ({offScreenRule})");
            Check(wrong == 0, $"seekers and only seekers carry a gun ({wrong} wrong)");
            Check(holders == 0, $"other bodies run none of the local player's scripts ({holders})");

            // A gun goes when its body goes: it used to stay, floating where the body had been.
            NetEntityView armedOne = views.FirstOrDefault(v => v.Role == Role.Hunter && GunsOf(v).Any(g => g.gameObject.activeInHierarchy));
            if (armedOne != null)
            {
                armedOne.SetShown(false);
                bool gunHid = GunsOf(armedOne).Where(g => g.gameObject.activeInHierarchy)
                                              .All(g => g.GetComponentsInChildren<Renderer>(true).All(r => !r.enabled));
                bool bodyHid = armedOne.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(r => !r.enabled);
                armedOne.SetShown(true);
                Check(gunHid && bodyHid, $"a seeker's gun is hidden along with their body (gun {gunHid}, body {bodyHid})");
            }
            else report.Add("INFO  no other seeker's body to try hiding");

            // Guns as they are, and then with a paint canvas on every other seeker: the server's
            // paint snapshot of a join or a reconnect gives one to everybody it names.
            CheckGunMaterials(views, "as the round stands");
            MethodInfo paintMaterial = typeof(OnlineRound).GetMethod("HandleRemoteMaterial", Any);
            foreach (NetEntityView v in views.Where(v => v.Role == Role.Hunter))
                paintMaterial.Invoke(OnlineRoundInstance(), new object[] { v.Slot, 0f, 1f });
            bodyViews = views;

            // A body this game runs is not the snapshots' to hide.
            hostedSeeker = views.FirstOrDefault(v => v.IsHosted && v.Role == Role.Hunter);
            if (hostedSeeker == null) report.Add("INFO  this game hosts no seeker bot");
            else
            {
                hostedSeeker.OnLostVisibility();

                // ...and on a wall it keeps its shape, as BotSeekController puts it there.
                Animator a = hostedSeeker.GetComponentInChildren<Animator>();
                forcedClimb = a != null && a.GetInteger("Pose") == 0 && !a.GetBool("Nam");
                if (forcedClimb) BodyAnimation.SetClimbing(a, true);
            }

            bodyStage = 2;
            bodyAt = now + 0.6f;
            return;
        }

        if (now < bodyAt) return;
        bodiesChecked = true;

        int canvases = bodyViews.Count(v => v != null && v.Role == Role.Hunter && v.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(WearsCanvas));
        int otherSeekers = bodyViews.Count(v => v != null && v.Role == Role.Hunter);
        if (otherSeekers > 0) Check(canvases == otherSeekers, $"the paint canvas reaches every other seeker's body ({canvases} of {otherSeekers})");
        CheckGunMaterials(bodyViews, "with a paint canvas on every other seeker's body");

        if (hostedSeeker == null) return;
        Check(hostedSeeker.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(r => r.enabled) && hostedSeeker.IsShown,
              "a bot this game runs stays drawn when the snapshots stop naming it");

        if (!forcedClimb)
        {
            report.Add("INFO  the seeker bot this game runs was not standing, so not tried on a wall");
            return;
        }

        Animator bot = hostedSeeker.GetComponentInChildren<Animator>();
        float gunWeight = bot.GetLayerWeight(bot.GetLayerIndex("Gun"));
        bool straight = bot.GetInteger("Pose") == 1 || bot.GetCurrentAnimatorStateInfo(0).IsName("pose1");
        Check(!straight && gunWeight > 0.99f, $"a seeker bot on a wall keeps its shape and its gun up (wall pose {straight}, gun layer {gunWeight:F2})");
        BodyAnimation.SetClimbing(bot, false);
    }

    private static List<NetEntityView> bodyViews;
    private static Vector3? walkFrom;
    private static bool forcedClimb;

    // ================= Shots at the far ends of bodies, and the cooldown after a kill =================

    private static bool Kills => System.Environment.GetCommandLineArgs().Contains("-playtestKills");

    // What the box used to be, for the report: fractions of a capsule of radius 1, height 8 at hider size.
    private static readonly float[] OldHeight = { 1.00f, 1.01f, 1.01f, 0.36f, 0.26f, 1.01f, 0.75f, 0.58f, 0.71f, 0.88f };
    private static readonly float[] OldRadius = { 1.00f, 0.59f, 0.59f, 1.27f, 1.46f, 0.93f, 0.99f, 0.94f, 0.60f, 0.90f };

    private static int killStage, killsMade, killsTried, oldBoxMisses, liftsSeen, liftsMissing, crosshairMisses, killPose;
    private static float killAt;
    private static bool killLanded, missTried, missHeld, missHeldTried, shotAfterWaitLanded;
    private static byte killSlot = 0xFF;
    private static Vector3 killPoint;
    private static readonly List<string> killNotes = new List<string>();
    private static readonly HashSet<int> posesShot = new HashSet<int>();

    // Each kill: how far the hider was, and how long after the shot they fell.
    private static float killFiredAt, killDistance, killBulletAt;
    private static Vector3 killMuzzle, killDir;
    private static GameObject killBullet;
    private static bool killBulletJudged;       // drawn, with no wall of the room between the gun and the hider
    private static int killsThroughWalls;
    private static readonly List<float> killDistances = new List<float>(), fellAfterFlight = new List<float>(), bulletStoppedOff = new List<float>();
    private static int fellStanding, fellEarly, deadTagged, deadUntagged, aliveTaggedBeforeFalling;
    private static readonly List<(byte Slot, float At)> deadToLookAt = new List<(byte, float)>();

    // Every frame of a kills run: where the bullet drawn for the last shot has got to, and a look
    // at each hider a moment after they fell.
    private static void WatchKillBullet()
    {
        if (killBullet != null) killBulletAt = Vector3.Dot(killBullet.transform.position - killMuzzle, killDir);

        for (int i = deadToLookAt.Count - 1; i >= 0; i--)
        {
            if (Time.unscaledTime < deadToLookAt[i].At) continue;
            NetEntityView dead = NetEntityRegistry.Get(deadToLookAt[i].Slot);
            if (dead != null && dead.gameObject.activeInHierarchy && dead.IsShown)
            {
                if (IsTagShown(dead)) deadTagged++;
                else deadUntagged++;
            }
            deadToLookAt.RemoveAt(i);
        }
    }

    // As OnEliminated is raised for the hider just shot at.
    private static void NoteKill(byte victim)
    {
        float waited = Time.unscaledTime - killFiredAt;
        float flight = killDistance / GameTuning.BulletSpeed;
        killDistances.Add(killDistance);
        fellAfterFlight.Add(waited - flight);
        if (waited < flight - 0.03f) fellEarly++;

        // The bullet drawn for it: gone, having stopped on the body and not flown on through it.
        // (These shots are sent without the room's walls, to try the far ends of bodies wherever
        // they hide; a drawn bullet does stop at a wall, so those are left out.)
        if (!killBulletJudged) killsThroughWalls++;
        else if (killBullet == null) bulletStoppedOff.Add(killBulletAt - killDistance);
        else bulletStoppedOff.Add(float.PositiveInfinity);

        deadToLookAt.Add((victim, Time.unscaledTime + 0.3f));
    }

    // The vertex of a body furthest out from where the server tracks it.
    private static Vector3 FarthestVertex(NetEntityView v)
    {
        Vector3 best = v.transform.position;
        float far = -1f;
        var baked = new Mesh();

        foreach (SkinnedMeshRenderer smr in v.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!smr.gameObject.activeInHierarchy) continue;
            smr.BakeMesh(baked, true);          // scaled by the mesh's own transform, not by the half-size body above it
            Matrix4x4 toWorld = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, smr.transform.parent.lossyScale);
            foreach (Vector3 vertex in baked.vertices)
            {
                Vector3 w = toWorld.MultiplyPoint3x4(vertex);
                float d = new Vector2(w.x - v.transform.position.x, w.z - v.transform.position.z).sqrMagnitude;
                if (d > far) { far = d; best = w; }
            }
        }

        Object.DestroyImmediate(baked);
        return best;
    }

    private static bool killRealPlayersOnly;

    private static NetEntityView NextHider(out MecchaNetClient.RosterEntry entry)
    {
        entry = default;
        var entries = Get<MecchaNetClient.RosterEntry[]>(OnlineRoundInstance(), "roster");
        foreach (MecchaNetClient.RosterEntry e in entries)
        {
            if (killRealPlayersOnly && e.IsBot) continue;
            NetEntityView v = NetEntityRegistry.Get(e.Slot);
            if (e.Role != Role.Fugitive || v == null || v.Eliminated) continue;
            if (!v.HasPosition && !v.IsHosted) continue;
            entry = e;
            return v;
        }
        return null;
    }

    private static void PlayKills()
    {
        if (Net.Phase != MatchPhase.Seek) return;
        float now = Time.realtimeSinceStartup;
        if (now < killAt) return;

        NetEntityView me = NetEntityRegistry.Get(Net.LocalSlot);
        var hiders = Object.FindObjectsByType<NetEntityView>().Where(v => v.Role == Role.Fugitive && !v.IsLocal && !v.Eliminated).ToList();

        Mode1FirstPersonGun gun = Object.FindAnyObjectByType<Mode1FirstPersonGun>(FindObjectsInactive.Include);
        Vector3 muzzle = gun != null && gun.muzzlePoint != null ? gun.muzzlePoint.position : me.transform.position;

        switch (killStage)
        {
            case 0:     // every shape on show: the bots this game runs strike one each
                int shape = 0;
                foreach (NetEntityView v in hiders.Where(x => x.IsHosted).OrderBy(x => x.Slot))
                    BodyAnimation.SetPose(v.GetComponentInChildren<Animator>(), PoseSelectorUI.AnimatorPoseFor(shape++ % 10));
                killAt = now + 2f;      // time to strike it, and for the server to hear
                killStage = 1;
                return;

            case 1:     // turn to the next one
                NetEntityView target = NextHider(out MecchaNetClient.RosterEntry entry);
                if (target == null) { killStage = 9; return; }

                killSlot = target.Slot;
                Animator anim = target.GetComponentInChildren<Animator>();
                killPose = target.IsHosted
                    ? Array.FindIndex(Enumerable.Range(0, 10).ToArray(), id => PoseSelectorUI.AnimatorPoseFor(id) == anim.GetInteger("Pose"))
                    : target.Pose;
                killPoint = FarthestVertex(target);

                Vector3 flat = killPoint - me.transform.position;
                flat.y = 0f;
                if (flat.sqrMagnitude > 0.01f) me.transform.rotation = Quaternion.LookRotation(flat.normalized);
                killAt = now + 0.35f;
                killStage = 2;
                return;

            case 2:     // at that vertex, from the real muzzle
                Vector3 dir = (killPoint - muzzle).normalized;
                NetEntityView shot = NetEntityRegistry.Get(killSlot);
                if (shot == null || shot.Eliminated) { killStage = 1; return; }

                if (!OnlineRound.BodyAlong(muzzle, dir, out float body)) crosshairMisses++;

                // The hider is still standing, with no name over them, as the bullet leaves the gun.
                if (!shot.Eliminated) fellStanding++;
                if (IsTagShown(shot)) aliveTaggedBeforeFalling++;
                killFiredAt = Time.unscaledTime;
                killDistance = body;
                killMuzzle = muzzle;
                killDir = dir;
                killBulletAt = 0f;

                // The bullet the gun draws for a shot, as Fire() does.
                var before = new HashSet<NetBullet>(Object.FindObjectsByType<NetBullet>());
                typeof(Mode1FirstPersonGun).GetMethod("SpawnBullet", Any).Invoke(gun, new object[] { muzzle, dir, 1f, null, 0f });
                NetBullet drawn = Object.FindObjectsByType<NetBullet>().FirstOrDefault(b => !before.Contains(b));
                killBullet = drawn != null ? drawn.gameObject : null;

                float wall = (float)typeof(OnlineRound).GetMethod("WallDistanceAlong", Any).Invoke(null, new object[] { muzzle, dir, 1000f });
                killBulletJudged = drawn != null && (wall < 0f || wall > body + 8f);

                Vector3 local = killPoint - shot.FeetPosition;
                int p = Mathf.Clamp(killPose, 0, 9);
                float wide = 1f * OldRadius[p] + 0.6f, tall = 8f * OldHeight[p] + 0.6f;
                bool oldMiss = Mathf.Abs(local.x) > wide || Mathf.Abs(local.z) > wide || local.y > tall || local.y < -0.6f;
                if (oldMiss) oldBoxMisses++;

                killLanded = false;
                killsTried++;
                posesShot.Add(killPose);
                Net.Fire(muzzle, dir, -1f);
                killAt = now;
                bodyAt = now + 3.5f;        // a bullet's whole life, and the word of it coming back
                killStage = 3;
                return;

            case 3:
                if (killLanded)
                {
                    killsMade++;
                    if ((float)typeof(OnlineRound).GetField("nextFireAt", Any).GetValue(OnlineRoundInstance()) == 0f) liftsSeen++;
                    else liftsMissing++;

                    // After the first: a miss, and then the cooldown must hold again.
                    killStage = !missTried ? 5 : 1;
                    return;
                }

                if (now < bodyAt) return;
                killNotes.Add($"pose {killPose} at {killPoint - NetEntityRegistry.Get(killSlot).FeetPosition:F1} from the feet");
                killAt = now + 3.3f;    // waiting out the cooldown of the miss
                killStage = killRealPlayersOnly && killsTried >= 2 ? 9 : 1;
                return;

            case 5:
                missTried = true;
                Net.Fire(muzzle, Quaternion.Euler(0f, 40f, 0f) * me.transform.forward + Vector3.up * 0.5f, -1f);
                killAt = now + 0.4f;
                killStage = 6;
                return;

            case 6:     // inside the miss's cooldown: this one is refused however true it is
                NetEntityView last = NextHider(out _);
                if (last == null) { killStage = 9; return; }

                killSlot = last.Slot;
                killPoint = FarthestVertex(last);
                Vector3 toLast = killPoint - me.transform.position;
                toLast.y = 0f;
                me.transform.rotation = Quaternion.LookRotation(toLast.normalized);
                killAt = now + 0.35f;
                killStage = 7;
                return;

            case 7:
                killLanded = false;
                missHeldTried = true;
                Net.Fire(muzzle, (killPoint - muzzle).normalized, -1f);
                killAt = now + 1f;
                killStage = 8;
                return;

            case 8:
                killStage = 10;
                killAt = now + 2.4f;    // the rest of the 3 s, and longer than any bullet takes to land
                return;

            case 10:
                missHeld = !killLanded;
                killLanded = false;
                killFiredAt = Time.unscaledTime;
                killMuzzle = muzzle;
                killDir = (killPoint - muzzle).normalized;
                if (!OnlineRound.BodyAlong(muzzle, killDir, out killDistance)) killDistance = Vector3.Distance(muzzle, killPoint);
                killBullet = null;
                killBulletJudged = false;       // no bullet is drawn for this one
                NetEntityView standing = NetEntityRegistry.Get(killSlot);
                if (standing != null && !standing.Eliminated) fellStanding++;
                Net.Fire(muzzle, killDir, -1f);
                bodyAt = now + 3.5f;
                killStage = 11;
                return;

            case 11:
                if (!killLanded && now < bodyAt) return;
                shotAfterWaitLanded = killLanded;
                if (killLanded) { killsMade++; killsTried++; }
                killStage = 1;
                return;
        }
    }

    private static void CheckKills()
    {
        if (killStage == 3 && killLanded) killsMade++;      // the last hider: the match ended on that shot

        Check(killsTried >= 3 && killsMade == killsTried,
              $"every shot at the far end of a body hit ({killsMade} of {killsTried}; shapes {string.Join(",", posesShot.OrderBy(x => x))}; the old box would have missed {oldBoxMisses}{(killNotes.Count > 0 ? "; missed: " + string.Join("; ", killNotes) : "")})");
        Check(crosshairMisses == 0, $"the crosshair's test agreed with every shot ({crosshairMisses} disagreed)");
        CheckBulletTiming();
        Check(liftsMissing == 0 && liftsSeen > 0, $"each kill lifted the cooldown on the client too ({liftsSeen} lifted, {liftsMissing} not)");
        if (missHeldTried)
        {
            Check(missHeld, "a shot just after a miss was refused: the cooldown is back");
            Check(shotAfterWaitLanded, "and once it had passed, the same shot landed");
        }
        else report.Add("INFO  the bots took the last hider first: the cooldown after a miss was not tried here");
    }

    private static void CheckBulletTiming()
    {
        // A hider falls when the bullet gets to them, not as it is fired.
        if (fellAfterFlight.Count > 0)
        {
            Check(fellEarly == 0 && fellStanding >= fellAfterFlight.Count,
                  $"no hider fell before the bullet reached them ({fellAfterFlight.Count} kills at {killDistances.Min():F0} to {killDistances.Max():F0} units, {killDistances.Min() / GameTuning.BulletSpeed * 1000f:F0} to {killDistances.Max() / GameTuning.BulletSpeed * 1000f:F0} ms of flight; they fell {fellAfterFlight.Min() * 1000f:F0} to {fellAfterFlight.Max() * 1000f:F0} ms after it; {fellEarly} early)");
            Check(fellAfterFlight.Max() < 0.6f, $"and none long after ({fellAfterFlight.Max() * 1000f:F0} ms at most)");

            var stops = bulletStoppedOff.Where(x => !float.IsInfinity(x)).ToList();
            if (bulletStoppedOff.Count > 0)
                Check(stops.Count == bulletStoppedOff.Count && stops.All(x => x > -8f && x < 8f),
                      $"the bullet drawn for each stopped on the hider it reached ({stops.Count} of {bulletStoppedOff.Count} gone by then; it stopped {(stops.Count > 0 ? stops.Min() : 0f):F1} to {(stops.Count > 0 ? stops.Max() : 0f):F1} units from where it met the body; {killsThroughWalls} more shots went through a wall and are not judged)");
            else report.Add($"INFO  every hider shot stood behind a wall of the room ({killsThroughWalls}): no drawn bullet to judge");
        }

        // Names: none over a hider still in (to this seeker), one over each who is out.
        Check(aliveTaggedBeforeFalling == 0, $"no hider had a name over them before they fell ({aliveTaggedBeforeFalling})");
        if (deadTagged + deadUntagged > 0)
            Check(deadUntagged == 0, $"a hider who is out shows their name to a seeker ({deadTagged} of {deadTagged + deadUntagged})");
        else report.Add("INFO  no fallen hider's body was left to look at for its name");
    }

    // ---- The same from the other end: this player, a hider, shot ----

    private struct SeenShot { public float At; public Vector3 Origin, Direction; public float Flown; }
    private static readonly Dictionary<byte, SeenShot> lastShotFrom = new Dictionary<byte, SeenShot>();
    private static readonly List<string> ownFalls = new List<string>();
    private static int ownFallsEarly;

    private static void NoteShotSeen(MecchaNetClient.RemoteShot shot)
    {
        Vector3 path = shot.Impact - shot.Origin;
        if (path.sqrMagnitude < 0.0001f) return;
        lastShotFrom[shot.Slot] = new SeenShot
        {
            At = Time.unscaledTime,
            Origin = shot.Origin,
            Direction = path.normalized,
            Flown = GameTuning.BulletSpeed * shot.Age,
        };
    }

    // As OnEliminated is raised for this player: how long since the bullet drawn for that shot set
    // out on this screen, against how long it needed to get here.
    private static void NoteOwnFall(byte by)
    {
        NetEntityView me = NetEntityRegistry.Get(Net.LocalSlot);
        if (me == null || !lastShotFrom.TryGetValue(by, out SeenShot shot))
        {
            ownFalls.Add("no bullet was drawn for the shot");
            ownFallsEarly++;
            return;
        }

        Vector3 body = me.FeetPosition + Vector3.up * (GameTuning.PlayerHeight * me.transform.lossyScale.y * 0.5f);
        float along = Vector3.Dot(body - shot.Origin, shot.Direction);
        float beside = Vector3.Cross(body - shot.Origin, shot.Direction).magnitude;
        float needed = Mathf.Max(0f, along - shot.Flown) / GameTuning.BulletSpeed;
        float waited = Time.unscaledTime - shot.At;

        // The body is some units deep, so the bullet is on it a little before it is level with its middle.
        if (waited < needed - 0.05f) ownFallsEarly++;
        ownFalls.Add($"shot from {along:F0} units off (drawn from {shot.Flown:F0} units along, passing {beside:F1} from the body's middle): {needed * 1000f:F0} ms to get here, fell after {waited * 1000f:F0} ms");
    }

    private static void CheckOwnFalls()
    {
        if (ownFalls.Count == 0)
        {
            if (!Seeker) report.Add("INFO  this hider was not shot in the round");
            return;
        }
        Check(ownFallsEarly == 0, $"this hider fell no sooner than the bullet drawn for the shot got to them ({string.Join("; ", ownFalls)})");
    }

    // ================= The server's clock, as this game shows it =================

    private static readonly StringBuilder timerCsv = new StringBuilder("utc_ms,phase,left,shown,text\n");
    private static float nextTimerSampleAt;

    private static void Mark(string what) =>
        timerCsv.Append($"#{what},{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\n");

    private static void SampleTimer()
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextTimerSampleAt) return;
        nextTimerSampleAt = now + 0.05f;

        GameModeManager gmm = Object.FindAnyObjectByType<GameModeManager>();
        string text = FindByName("TimerText")?.GetComponent<Text>()?.text ?? "";
        string left = Net.PhaseTimeLeft.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        timerCsv.Append($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{(int)Net.Phase},{left},{gmm.NetTimerSeconds},{text}\n");
    }

    // ================= Animations that start over =================

    private sealed class AnimTrack
    {
        public int State, Previous;
        public float Time, ChangedAt;
    }

    private static readonly Dictionary<Animator, AnimTrack> animTracks = new Dictionary<Animator, AnimTrack>();
    private static int animRestarts, animBounces;
    private static readonly List<string> animNotes = new List<string>();

    private static readonly Dictionary<int, string> StateNames = new[] { "idle", "run", "pose_nam", "pose1", "pose2", "pose3", "pose4", "pose5", "pose6", "pose7", "pose8", "pose9" }
        .ToDictionary(n => Animator.StringToHash("Base Layer." + n), n => n);

    private static string StateName(Animator a) => StateNames.GetValueOrDefault(a.GetCurrentAnimatorStateInfo(0).fullPathHash, "?");

    // Every body in the match, each frame. A state whose time goes backwards was restarted; a state
    // left and come back to within a moment is a body flickering between two.
    private static void SampleAnimations()
    {
        float now = Time.realtimeSinceStartup;
        foreach (NetEntityView v in Object.FindObjectsByType<NetEntityView>())
        {
            Animator a = v.GetComponentInChildren<Animator>();
            if (a == null || !a.isActiveAndEnabled || v.Eliminated || a.IsInTransition(0)) continue;

            AnimatorStateInfo s = a.GetCurrentAnimatorStateInfo(0);
            if (!animTracks.TryGetValue(a, out AnimTrack t))
            {
                animTracks[a] = new AnimTrack { State = s.fullPathHash, Time = s.normalizedTime, ChangedAt = now };
                continue;
            }

            if (s.fullPathHash == t.State)
            {
                if (s.normalizedTime < t.Time - 0.05f)
                {
                    animRestarts++;
                    if (animNotes.Count < 8) animNotes.Add($"restart on {v.name}: {t.Time:F2} -> {s.normalizedTime:F2}");
                }
                t.Time = s.normalizedTime;
                continue;
            }

            if (s.fullPathHash == t.Previous && now - t.ChangedAt < 0.3f)
            {
                animBounces++;
                if (animNotes.Count < 8) animNotes.Add($"{(v.IsHosted ? "hosted " : "")}{v.name} back to {StateName(a)} from {StateNames.GetValueOrDefault(t.State, "?")} after {now - t.ChangedAt:F2} s");
            }

            t.Previous = t.State;
            t.State = s.fullPathHash;
            t.Time = s.normalizedTime;
            t.ChangedAt = now;
        }
    }

    // On two spare bodies out of sight: what the controller does with both states on, and what
    // BodyAnimation makes of the same request; then one frame of stillness in the middle of a run.
    private static int switchStage, rawEntries, drivenEntries, switchLast;
    private static GameObject switchBody;
    private static float switchAt;
    private static bool runHeld = true, runFlip;

    private static Animator NewSwitchBody()
    {
        if (switchBody != null) Object.Destroy(switchBody);
        switchBody = Object.Instantiate(Resources.Load<GameObject>("GameObjects/Bot"), Vector3.down * 800f, Quaternion.identity);
        switchBody.name = "SwitchTest";
        switchBody.SetActive(true);
        switchLast = 0;
        return switchBody.GetComponentInChildren<Animator>();
    }

    private static void CountStateEntries(Animator a, ref int entries)
    {
        if (a.IsInTransition(0)) return;
        int state = a.GetCurrentAnimatorStateInfo(0).fullPathHash;
        if (state != switchLast) entries++;
        switchLast = state;
    }

    private static void CheckAnimationSwitching()
    {
        if (switchStage >= 4) return;
        float now = Time.realtimeSinceStartup;
        Animator a = switchBody != null ? switchBody.GetComponentInChildren<Animator>() : null;

        switch (switchStage)
        {
            case 0:
                a = NewSwitchBody();
                a.SetBool("Nam", true);
                a.SetInteger("Pose", 3);
                switchAt = now + 2f;
                switchStage = 1;
                return;

            case 1:
                CountStateEntries(a, ref rawEntries);
                if (now < switchAt) return;
                a = NewSwitchBody();
                BodyAnimation.SetProne(a, true);
                BodyAnimation.SetPose(a, 3);
                switchAt = now + 2f;
                switchStage = 2;
                return;

            case 2:
                CountStateEntries(a, ref drivenEntries);
                if (now < switchAt) return;
                report.Add($"INFO  lying down and a pose asked for together: the bare controller hopped between states {rawEntries} times in 2 s");
                Check(drivenEntries <= 2 && a.GetBool("Nam") && a.GetInteger("Pose") == 0,
                      $"BodyAnimation plays only one of them ({drivenEntries} state entries; the bare controller {rawEntries})");
                a = NewSwitchBody();
                BodyAnimation.SetSpeed(a, 1f);
                switchAt = now + 1.5f;
                switchStage = 3;
                return;

            case 3:
                runFlip = !runFlip;
                BodyAnimation.SetSpeed(a, runFlip ? 0f : 1f);     // stillness for a frame, every other frame
                runHeld &= a.GetFloat("Speed") == 1f;
                if (now < switchAt) return;
                Check(runHeld, "a frame of stillness in the middle of a run does not stop it");
                Object.Destroy(switchBody);
                switchStage = 4;
                return;
        }
    }

    // ================= -playtestPair: two real games in the one match =================

    // Each game runs through all nine shapes with the real buttons, lies down if it is a seeker and
    // climbs a wall, and judges what the OTHER game's body does on its own screen.
    // -playtestMixed: a hider and a seeker, the hider keeping the last shape into the seek.
    private static bool Pair => System.Environment.GetCommandLineArgs().Contains("-playtestPair");
    private static bool Mixed => System.Environment.GetCommandLineArgs().Contains("-playtestMixed");

    private static readonly List<string> climbTrace = new List<string>();
    private static float nextClimbTraceAt;
    private static int pairStage, shapeIndex, placementsBeforeClimb;
    private static float pairAt, walkDeadline, climbFromY, climbHighest;
    private static readonly HashSet<int> partnerShapes = new HashSet<int>();
    private static bool partnerSeen, partnerClimbFlag, partnerClimbAnim, partnerLayFlag, partnerLayAnim, partnerGunOffShape, partnerGunBack;
    private static bool climbButtonsShown, climbedUp, cameDown, climbFlagFromServer, climbShownWhileFrozen;
    private static int partnerClimbFrames, partnerFacingWall, ownGunUpFrames, ownGunDownFrames;
    private static int partnerShapeKeptFrames, partnerShapeLostFrames, ownWallPoseFrames;

    // On the wall a seeker turns a quarter round and walks the way they face.
    private const float WallTurnDegrees = 90f;
    private static float partnerWallYawFirst = float.NaN, partnerWallYawLast, partnerWallYawSwing;
    private static float wallWalkAngle = -1f, wallWalkDistance, wallTurnFrom;
    private static Vector3 wallWalkFrom, wallWalkFacing;
    private static bool wallTurned;
    private static float partnerClimbSeconds;
    private static readonly List<string> facingNotes = new List<string>();
    private static string partnerBodyMesh = "";

    private static void DriveStraight(bool on)
    {
        JoystickMover mover = Object.FindAnyObjectByType<JoystickMover>();
        Joystick stick = mover != null ? Get<Joystick>(mover, "joystick") : null;
        if (stick != null) typeof(Joystick).GetField("input", Any).SetValue(stick, on ? Vector2.up : Vector2.zero);
    }

    private static void Hold(string button, bool held)
    {
        ClimbHold hold = FindByName(button).GetComponent<ClimbHold>();
        if (held) hold.onDown();
        else hold.onUp();
    }

    private static void WatchPartner()
    {
        var entries = Get<MecchaNetClient.RosterEntry[]>(OnlineRoundInstance(), "roster");
        if (entries == null) return;

        foreach (MecchaNetClient.RosterEntry e in entries)
        {
            if (e.IsBot || e.Slot == Net.LocalSlot) continue;
            NetEntityView v = NetEntityRegistry.Get(e.Slot);
            if (v == null || v.IsLocal || !v.HasPosition) continue;
            partnerSeen = true;

            Animator a = v.GetComponentInChildren<Animator>();
            AnimatorStateInfo s = a.GetCurrentAnimatorStateInfo(0);
            bool climbing = (v.Flags & EntityFlags.Climbing) != 0;
            bool prone = (v.Flags & EntityFlags.Prone) != 0;
            int gun = e.Role == Role.Hunter ? a.GetLayerIndex("Gun") : -1;

            if (v.Pose > 0 && !prone && s.IsName("pose" + PoseSelectorUI.AnimatorPoseFor(v.Pose)))
            {
                partnerShapes.Add(v.Pose);
                if (gun >= 0 && a.GetLayerWeight(gun) < 0.01f) partnerGunOffShape = true;
            }

            if (climbing)
            {
                partnerClimbFlag = true;

                // Which way they face on the wall, as it is drawn here.
                float yaw = v.transform.eulerAngles.y;
                if (float.IsNaN(partnerWallYawFirst)) partnerWallYawFirst = yaw;
                partnerWallYawLast = yaw;
                partnerWallYawSwing = Mathf.Max(partnerWallYawSwing, Mathf.Abs(Mathf.DeltaAngle(partnerWallYawFirst, yaw)));

                bool wallPose = s.IsName("pose1") || a.GetInteger("Pose") == 1;
                if (v.Pose == 0 && wallPose) partnerClimbAnim = true;

                // A seeker goes up as they stand, gun in hand; only a hider flattens against the wall.
                if (gun >= 0 && v.Pose == 0 && !prone)
                {
                    if (wallPose || a.GetLayerWeight(gun) < 0.5f) partnerShapeLostFrames++;
                    else partnerShapeKeptFrames++;
                }

                // Facing what they are climbing, whichever way they are aiming.
                bool Solid(Vector3 from, Vector3 along) =>
                    Physics.RaycastAll(from, along, 8f, ~0, QueryTriggerInteraction.Ignore).Any(h => !Bullet.IsCharacter(h.collider));

                float bodyHeight = GameTuning.PlayerHeight * v.transform.lossyScale.y;
                bool wallAround = false, facing = false;
                foreach (float up in new[] { 0.15f, 0.45f, 0.7f, 0.92f })
                {
                    Vector3 from = v.FeetPosition + Vector3.up * (bodyHeight * up);
                    facing |= Solid(from, v.transform.forward);
                    for (int d = 0; d < 8 && !wallAround; d++) wallAround = Solid(from, Quaternion.Euler(0f, d * 45f, 0f) * Vector3.forward);
                }

                partnerClimbSeconds += Time.unscaledDeltaTime;
                if (wallAround)
                {
                    partnerClimbFrames++;
                    if (facing) partnerFacingWall++;
                    else if (Time.frameCount % 15 == 0 && facingNotes.Count < 14)
                    {
                        float nearest = 99f, angle = -1f;
                        foreach (float up in new[] { 0.15f, 0.45f, 0.7f, 0.92f })
                        {
                            for (int d = 0; d < 8; d++)
                            {
                                Vector3 along = Quaternion.Euler(0f, d * 45f, 0f) * Vector3.forward;
                                foreach (RaycastHit h in Physics.RaycastAll(v.FeetPosition + Vector3.up * (bodyHeight * up), along, 8f, ~0, QueryTriggerInteraction.Ignore))
                                {
                                    if (Bullet.IsCharacter(h.collider) || h.distance >= nearest) continue;
                                    nearest = h.distance;
                                    angle = Vector3.Angle(v.transform.forward, -new Vector3(h.normal.x, 0f, h.normal.z));
                                }
                            }
                        }
                        facingNotes.Add($"wall {nearest:F1} away, {angle:F0} deg off, y {v.FeetPosition.y:F1}");
                    }
                }
            }

            if (a.runtimeAnimatorController != null && v.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh != null)
                partnerBodyMesh = v.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh.name;

            if (prone)
            {
                partnerLayFlag = true;
                if (s.IsName("pose_nam")) partnerLayAnim = true;
            }

            if (gun >= 0 && !prone && !climbing && v.Pose == 0 && partnerGunOffShape && a.GetLayerWeight(gun) > 0.99f) partnerGunBack = true;
        }
    }

    private static void CheckClimbButtons()
    {
        foreach (string name in new[] { "ClimbUp", "ClimbDown" })
        {
            var rt = (RectTransform)FindByName(name).transform;
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            var pointer = new PointerEventData(EventSystem.current) { position = (corners[0] + corners[2]) * 0.5f };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);
            GameObject top = hits.Count > 0 ? hits[0].gameObject : null;
            Check(top != null && top.transform.IsChildOf(rt), $"a tap on {name} reaches it (top of the pile: {(top != null ? top.name : "nothing")}) at {Screen.width}x{Screen.height}");
        }
    }

    private static void PlayPair()
    {
        WatchPartner();

        GameObject climbCanvas = FindByName("ClimbButtonsCanvas");
        if (Seeker && Net.Phase == MatchPhase.Hide && climbCanvas != null && climbCanvas.activeSelf) climbShownWhileFrozen = true;

        MatchPhase acting = Seeker ? MatchPhase.Seek : MatchPhase.Hide;
        if (Net.Phase != acting)
        {
            DriveStraight(false);
            return;
        }

        // A hider and a seeker: before anything else the seeker shoots the other real player, who
        // times the bullet coming at them from their own side.
        if (Mixed && Seeker && killStage != 9)
        {
            killRealPlayersOnly = true;
            if (killStage == 0)
            {
                killStage = 1;
                killAt = Time.realtimeSinceStartup + 2f;
            }
            PlayKills();
            return;
        }

        float now = Time.realtimeSinceStartup;
        PoseSelectorUI poses = Object.FindAnyObjectByType<PoseSelectorUI>(FindObjectsInactive.Include);
        WallClimber climber = Object.FindAnyObjectByType<WallClimber>(FindObjectsInactive.Include);
        Mode1FirstPersonGun gun = Object.FindAnyObjectByType<Mode1FirstPersonGun>(FindObjectsInactive.Include);
        NetEntityView me = NetEntityRegistry.Get(Net.LocalSlot);
        if (now < pairAt)
        {
            if (pairStage == 5)
            {
                climbHighest = Mathf.Max(climbHighest, me.FeetPosition.y);
                if (now >= nextClimbTraceAt)
                {
                    nextClimbTraceAt = now + 0.3f;
                    climbTrace.Add($"{me.FeetPosition.y - climbFromY:F1}{(climber.IsClinging ? "" : "(off)")}");
                }

                // A seeker keeps their shape and looks along their gun on the wall; a hider flattens.
                if (climber.IsClinging)
                {
                    Animator own = me.GetComponentInChildren<Animator>();
                    bool wallPose = own.GetInteger("Pose") == 1;
                    if (wallPose) ownWallPoseFrames++;

                    if (Seeker)
                    {
                        if (!wallPose && own.GetLayerWeight(own.GetLayerIndex("Gun")) > 0.99f) ownGunUpFrames++;
                        else ownGunDownFrames++;
                    }
                }
            }
            if (pairStage == 5) climbFlagFromServer |= (me.Flags & EntityFlags.Climbing) != 0;
            return;
        }

        switch (pairStage)
        {
            case 0:
                pairAt = now + 1f;
                pairStage = 1;
                return;

            case 1:     // the nine pose buttons, in turn, as their OnClick calls them
                if (shapeIndex < 9)
                {
                    poses.SlotNoads(shapeIndex++);
                    pairAt = now + 0.8f;
                    return;
                }
                pairStage = Mixed && !Seeker ? 9 : 2;
                if (pairStage == 9) return;     // the hider keeps shape 9 for the seeker to find
                poses.SlotNoads(8);             // tapping the active one again takes it off
                pairAt = now + 1f;
                return;

            case 2:     // a seeker lies down and gets up, with the real button
                pairStage = 4;
                walkDeadline = now + 16f;
                if (!Seeker) return;
                gun.ToggleProne();
                pairAt = now + 1f;
                pairStage = 3;
                return;

            case 3:
                gun.ToggleProne();
                pairAt = now + 0.8f;
                walkDeadline = pairAt + 16f;
                pairStage = 4;
                return;

            case 4:     // to the nearest wall; the buttons come up by themselves
                DriveStraight(true);
                if (climbCanvas != null && climbCanvas.activeSelf)
                {
                    DriveStraight(false);
                    climbButtonsShown = true;
                    CheckClimbButtons();
                    placementsBeforeClimb = Net.PositionPlacements;
                    climbFromY = climbHighest = me.FeetPosition.y;
                    Hold("ClimbUp", true);
                    pairAt = now + 2.5f;
                    pairStage = 5;
                    return;
                }
                if (walkFrom == null) walkFrom = me.FeetPosition;
                if (now > walkDeadline)
                {
                    DriveStraight(false);
                    pairStage = 9;

                    // Why no wall came up: how far the walk got and what stands in front of the body.
                    Transform body = me.transform;
                    Vector3 chest = me.FeetPosition + Vector3.up * (GameTuning.PlayerHeight * body.lossyScale.y * 0.5f);
                    string ahead = string.Join(", ", Physics.RaycastAll(chest, body.forward, 12f, ~0, QueryTriggerInteraction.Ignore)
                        .Where(h => !h.transform.IsChildOf(body)).OrderBy(h => h.distance).Take(3)
                        .Select(h => $"{h.collider.name} [{h.collider.tag}] at {h.distance:F1}, {Vector3.Angle(h.normal, Vector3.up):F0} deg{(Bullet.IsCharacter(h.collider) ? ", a character" : "")}"));
                    report.Add($"INFO  no wall in 16 s of walking: went {Vector3.Distance(walkFrom.Value, me.FeetPosition):F1} units from {walkFrom.Value} to {me.FeetPosition}; ahead: {(ahead.Length > 0 ? ahead : "nothing within 12")}");
                }
                return;

            case 5:
                Hold("ClimbUp", false);
                climbedUp = climber.IsOnWall && climbHighest > climbFromY + 3f;

                // A seeker looks round from the wall, as the aim drag turns them...
                if (Seeker && climber.IsOnWall)
                {
                    wallTurnFrom = me.transform.eulerAngles.y;
                    me.transform.Rotate(0f, WallTurnDegrees, 0f);
                    wallTurned = true;
                    pairAt = now + 1f;          // long enough for the other screen to show it
                    pairStage = 51;
                    return;
                }

                Hold("ClimbDown", true);
                pairAt = now + 0.2f;
                walkDeadline = now + 8f;
                pairStage = 6;
                return;

            case 51:    // ...and walks the way they now face
                wallWalkFrom = me.transform.position;
                wallWalkFacing = me.transform.forward;
                DriveStraight(true);
                pairAt = now + 0.8f;
                pairStage = 52;
                return;

            case 52:
                DriveStraight(false);
                Vector3 walked = me.transform.position - wallWalkFrom;
                walked.y = 0f;
                wallWalkDistance = walked.magnitude;
                wallWalkAngle = wallWalkDistance > 0.01f ? Vector3.Angle(walked, wallWalkFacing) : 180f;

                Hold("ClimbDown", true);
                pairAt = now + 0.2f;
                walkDeadline = now + 8f;
                pairStage = 6;
                return;

            case 6:
                if (climber.IsOnWall && now < walkDeadline) return;
                Hold("ClimbDown", false);
                cameDown = !climber.IsOnWall;
                pairStage = 9;
                return;
        }
    }

    private static void CheckPair()
    {
        DriveStraight(false);
        Check(partnerSeen, "the other real player was on this screen");

        if (Mixed)
        {
            if (Seeker)
            {
                Check(partnerShapes.Contains(9), $"the hider's shape shows on the seeker's screen (seen: {string.Join(",", partnerShapes)})");
                if (fellAfterFlight.Count > 0) CheckBulletTiming();
                else report.Add($"INFO  the seeker's shot at the other player did not land ({killsTried} tried{(killNotes.Count > 0 ? ": " + string.Join("; ", killNotes) : "")})");
            }
            else report.Add($"INFO  shapes of the seeker seen on the hider's screen: {string.Join(",", partnerShapes)}");
            return;
        }

        Check(partnerShapes.Count == 9, $"all nine shapes of the other player show here ({string.Join(",", partnerShapes.OrderBy(x => x))})");
        if (Seeker)
        {
            Check(partnerGunOffShape, "a seeker's shape shows through their gun layer");
            Check(partnerGunBack, "and their gun comes back after it");
            Check(partnerLayFlag && partnerLayAnim, $"the other seeker lying down shows here (flag {partnerLayFlag}, animation {partnerLayAnim})");
            Check(!climbShownWhileFrozen, "no climb buttons while a seeker is frozen");
        }

        Check(climbButtonsShown, "walking to a wall brought up this player's climb buttons");
        Check(climbedUp, $"this player climbed the wall ({climbHighest - climbFromY:F1} units up; every 0.3 s: {string.Join(" ", climbTrace)})");
        Check(climbFlagFromServer, "the server has this player climbing");
        Check(Net.PositionPlacements == placementsBeforeClimb, $"the server accepted the climb ({Net.PositionPlacements - placementsBeforeClimb} corrections)");
        Check(cameDown, "and came back down with the down button");
        // Over the top of something low the climb is over before the pose has been blended in.
        if (partnerClimbSeconds < 1f)
            report.Add($"INFO  the other player was on a wall for {partnerClimbSeconds:F1} s only (flag {partnerClimbFlag}, animation {partnerClimbAnim})");
        else if (Seeker)
            Check(partnerClimbFlag && !partnerClimbAnim && partnerShapeKeptFrames > 0 && partnerShapeLostFrames == 0,
                  $"the other seeker on a wall keeps their shape and gun here (flag {partnerClimbFlag}, {partnerShapeKeptFrames} frames as they stand, {partnerShapeLostFrames} straightened)");
        else
            Check(partnerClimbFlag && partnerClimbAnim, $"the other hider climbing shows here, flat against the wall (flag {partnerClimbFlag}, animation {partnerClimbAnim})");
        if (Seeker)
        {
            // This seeker turned on the wall and walked the way they faced...
            Check(wallTurned && wallWalkDistance > 3f && wallWalkAngle < 8f,
                  $"a seeker on a wall moves the way they face (turned {WallTurnDegrees:F0} degrees from {wallTurnFrom:F0}; then {wallWalkDistance:F1} units in 0.8 s, {wallWalkAngle:F1} degrees off their facing)");

            // ...and the other one's turn, made the same way on their own wall, shows here.
            if (partnerClimbSeconds >= 1f)
                Check(Mathf.Abs(partnerWallYawSwing - WallTurnDegrees) < 12f,
                      $"the other seeker's turn on a wall shows here (facing {partnerWallYawFirst:F0} as they took hold, turned by up to {partnerWallYawSwing:F0} degrees, {partnerWallYawLast:F0} at the end)");
        }
        else report.Add($"INFO  the other hider on a wall is drawn facing as their own game has them ({partnerWallYawFirst:F0} to {partnerWallYawLast:F0} degrees)");
        if (Seeker) Check(ownGunUpFrames > 0 && ownGunDownFrames == 0 && ownWallPoseFrames == 0, $"a seeker's own shape, arms and gun stay as they are on a wall ({ownGunUpFrames} frames, {ownGunDownFrames} not, {ownWallPoseFrames} in the wall pose)");
        else Check(ownWallPoseFrames > 0, $"a hider still flattens against the wall ({ownWallPoseFrames} frames in the wall pose)");
        Check(partnerBodyMesh == Resources.Load<GameObject>("Players/Player").GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh.name,
              $"the other player is drawn as their character from Resources/Players ({partnerBodyMesh})");

        // The other character, as far as this game picks the prefab: the server's roster names it.
        var entries = Get<MecchaNetClient.RosterEntry[]>(OnlineRoundInstance(), "roster");
        int partner = Array.FindIndex(entries, e => !e.IsBot && e.Slot != Net.LocalSlot);
        if (partner >= 0)
        {
            entries[partner].ModelId = 1;
            var prefab = (GameObject)typeof(OnlineRound).GetMethod("BodyPrefabOf", Any).Invoke(OnlineRoundInstance(), new object[] { entries[partner].Slot });
            entries[partner].ModelId = 0;
            Check(prefab != null && prefab.name == "Player2", $"character 1 is drawn with the Player2 prefab ({(prefab != null ? prefab.name : "none")})");
        }
    }

    private static void CheckRound()
    {
        DriveJoystick(false);
        Check(animRestarts == 0, $"no animation started over on any body ({animRestarts}{(animNotes.Count > 0 ? ": " + string.Join("; ", animNotes) : "")})");
        report.Add($"INFO  bodies that went back to a state within 0.3 s: {animBounces}, over {animTracks.Count} bodies");
        if (switchStage < 4) report.Add("INFO  the animation switching check did not finish");
        CheckOwnFalls();
        if (Pair)
        {
            CheckPair();
            return;
        }
        CheckHostedBots();
        if (Kills && Seeker)
        {
            CheckKills();
            return;
        }
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
        Net.OnRemoteShot += shot => { remoteShots++; NoteShotSeen(shot); };
        Net.OnLoadoutChanged += () => loadoutAnswers++;
        Net.OnPhaseChanged += (_, _) => phaseChanges++;
        Net.OnEliminated += (victim, by, cause) =>
        {
            eliminations++;
            if (cause == EliminationCause.Caught) catches++;
            if (by == Net.LocalSlot && cause == EliminationCause.Shot && victim == killSlot)
            {
                if (!killLanded) NoteKill(victim);
                killLanded = true;
            }
            if (victim == Net.LocalSlot && cause == EliminationCause.Shot) NoteOwnFall(by);
        };
        Net.OnMatchEnd += _ => matchEnds++;
        Net.OnSpotAlert += _ => spotAlerts++;
        Net.OnMatchFound += _ => matchesFound++;
        Net.OnMatchStart += _ => Mark("matchstart");
        Net.OnPhaseChanged += (phase, _) => Mark("phase" + (int)phase);
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
        File.WriteAllText(ReportPath + ".timer.csv", timerCsv.ToString());
        Debug.Log("[PlayTest]\n" + text);

        int code = failures == 0 ? 0 : 1;
        EditorApplication.isPlaying = false;
        EditorApplication.delayCall += () => EditorApplication.Exit(code);
    }

    private static void CountErrors(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        if (message.StartsWith("[PlayTest]") || message.StartsWith("connection.state_change") || stack.Contains("UnityEditor.Search")) return;   // the editor's own indexing is not the game
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
