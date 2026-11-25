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
//             -playtestHost 192.168.15.150 -playtestPort 17777 -playtestMetaPort 17779
//             -playtestRole seeker|hider -playtestSeats 8 -playtestReport <file>
//             [-playtestMap <0-4>]
//
// The flow: Multiplayer sign-in (first-run name if the server asks) -> a weapon and its ammo bought
// in the shop -> the play setup (PlaySetupUI: side, character, room and map on one screen, in the
// mode chosen on Home, then FIND MATCH) -> matchmaking -> the round (seeker: switches weapon with
// the weapon dock and shoots its rounds; hider: presses Start and paints) -> the result popup ->
// Home (wallet refreshed) -> match history -> the shop's weapon cards. Bots or headless clients
// fill the lobby.
//
// Play mode reloads scripts, so the driver keeps its state in SessionState and re-attaches itself
// on load ([InitializeOnLoad]).
[InitializeOnLoad]
public static class OnlinePlayTest
{
    private const string ActiveKey = "MecchaPlayTest.Active";
    private const string ArgsKey = "MecchaPlayTest.Args";
    private const float TimeoutSeconds = 240f;

    private const string CharacterKey = "MecchaPlayTest.Character";
    private const string CharacterPrefsKey = "SelectedCharacter";      // CharacterSelect.prefsKey

    // -playtestSwitchBody: the account buys the other character, picks it on the play setup (the
    // body is swapped in place, no scene reload) and plays the match with it.
    private static bool SwitchBody => System.Environment.GetCommandLineArgs().Contains("-playtestSwitchBody");
    private static int switchedTo = -1;
    private static bool switchChecked, avatarAnswered, profileRefreshed;
    private static HomeUI sceneMarker;   // HomeUI before the switch: still the same (not destroyed) after it means no reload

    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static OnlinePlayTest()
    {
        if (!SessionState.GetBool(ActiveKey, false)) return;

        // The test signs in when it means to. Only -playtestQuiet lets the game do it by itself at
        // launch, against the test server and as a player who has been online before.
        NetBootstrap.QuietSignInEnabled = Quiet;
        if (Quiet)
        {
            OnlineConfig config = OnlineConfig.Load();
            config.host = Host;
            config.port = QuietOffline ? Port + 50 : Port;      // -playtestQuietOffline: nothing listens there
            config.webSocketPort = 0;
            config.metaPort = MetaPort;
            NetBootstrap.TestConfig = config;
            NetBootstrap.TestSignedInBefore = true;
        }

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
            Arg("-playtestHost", "192.168.15.150"),
            Arg("-playtestPort", "7777"),
            Arg("-playtestMetaPort", "7779"),
            Arg("-playtestRole", "seeker"),
            Arg("-playtestReport", Path.Combine(Directory.GetCurrentDirectory(), "Logs", "OnlinePlayTest.txt")),
            Arg("-playtestSeats", "8"),
        });
        SessionState.SetString(ArgsKey, settings);
        SessionState.SetBool(ActiveKey, true);
        // The character the editor had: -playtestSwitchBody changes it, and it is put back at the end.
        SessionState.SetInt(CharacterKey, PlayerPrefs.GetInt(CharacterPrefsKey, 0));

        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        EditorApplication.isPlaying = true;
    }

    // ================= State =================

    private enum Step { WaitPlay, SignIn, FirstRun, Shop, Role, Room, Map, Match, Round, Result, Home, History, ShopCards, Again, SinglePlay, Wallet, Quiet, Done, Body, Language, Pay, NextRooms, Exited, Away, BuyMap, ShopTabs, Climb, Hub }

    // -playtestShopTabs: the shop as a player taps it, from Home: tab after tab (Avatars, then Coins
    // and Bullets, buying and equipping in between). Every card must be of the tab tapped, and a
    // purchase must say so, and show on its card, as soon as the server has answered.
    private static bool ShopTabsTest => System.Environment.GetCommandLineArgs().Contains("-playtestShopTabs");

    // -playtestMapsSold: the server sells Room 2-5 (play.py with PLAY_MAPS=1); with -playtestMap N
    // (N >= 1) the test buys that map when it picks it.
    private static bool MapsSold => System.Environment.GetCommandLineArgs().Contains("-playtestMapsSold");

    // -playtestNext: the result's Next. -playtestExit: the round's Exit, Stay, then Leave.
    // -playtestReady: two hiders see each other press Start.
    private static bool NextTest => System.Environment.GetCommandLineArgs().Contains("-playtestNext");
    private static bool ExitTest => System.Environment.GetCommandLineArgs().Contains("-playtestExit");
    private static bool ReadyTest => System.Environment.GetCommandLineArgs().Contains("-playtestReady");
    private static int matchesAtNext;

    // -playtestAway: the room list's Back, the round's Shop button and coin balance, the paint panel
    // on top, Exit and Rejoin from the matches list, everyone's results at the end, the Shoot
    // button's countdown. -playtestAwayEnd: Exit, and the match ends while away (notification, results).
    private static bool AwayTest => System.Environment.GetCommandLineArgs().Contains("-playtestAway") || SwitchMapsTest;

    // -playtestSwitchMaps: match A on the first map, Exit; match B on Room2, Exit; REJOIN A. The
    // player must stand on A's floor where the server has them, and stay there.
    private static bool SwitchMapsTest => System.Environment.GetCommandLineArgs().Contains("-playtestSwitchMaps");

    // -playtestOutRejoin: a hider who is out leaves the match and rejoins it; their body must lie on
    // the floor where it fell, and stay there (2026-11-25: a dead hider who rejoined floated or fell).
    private static bool OutRejoinTest => System.Environment.GetCommandLineArgs().Contains("-playtestOutRejoin");
    private static bool AwayEndTest => System.Environment.GetCommandLineArgs().Contains("-playtestAwayEnd");
    private static bool roomBackChecked, splashAtStart, splashChecked, cooldownSeen;
    private static int awayStage;
    private static float awayAt;
    private static int awayMatch = -1;

    // -playtestStandard: the paid room (20 coins, real players only). -playtestLobbyMark <file>:
    // written once this game sits in a lobby, so a runner can send the other players in after it.
    private static bool Standard => System.Environment.GetCommandLineArgs().Contains("-playtestStandard");
    private static string LobbyMark => System.Environment.GetCommandLineArgs().SkipWhile(a => a != "-playtestLobbyMark").Skip(1).FirstOrDefault();

    // -playtestRemote: other real players join this match (the load tester's clients, started when
    // the lobby mark appears), so how they are drawn from the network is judged.
    private static bool RemotePlayers => System.Environment.GetCommandLineArgs().Contains("-playtestRemote");
    private static long standardCoinsBefore;
    private static bool lobbyMarked, standardLobbyChecked;

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
    private static float weaponTapAt = -1f;
    private const int TestWeapon = 2;            // the roller: 2 s, bullets half as big again

    // A finger's tap on this button: the topmost UI under its centre gets the click, as on a
    // phone. Returns what was hit (null for nothing).
    private static GameObject TapAt(Button button)
    {
        if (button == null || EventSystem.current == null) return null;
        var rect = (RectTransform)button.transform;
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));   // its middle, wherever its pivot is
        var pointer = new PointerEventData(EventSystem.current) { position = screen, button = PointerEventData.InputButton.Left };
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(pointer, hits);
        if (hits.Count == 0) return null;
        GameObject top = hits[0].gameObject;
        pointer.pointerPress = top;
        ExecuteEvents.ExecuteHierarchy(top, pointer, ExecuteEvents.pointerClickHandler);
        return top;
    }
    // The round menu (design B): the HUD's menu button, top right.
    private static Button MenuButton() => FindByName("Btn_Setting")?.GetComponent<Button>();
    private static GameObject OpenRoundMenu() => TapAt(MenuButton());
    private static Button MenuItem(string name) => FindByName("RoundMenu") is { activeInHierarchy: true } menu
        ? menu.GetComponentsInChildren<Button>(false).FirstOrDefault(b => b.name == name) : null;

    private static int remoteViews, remoteAnimators, matchEnds;
    private static int ammoBeforeRound = -1;

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

    // The first frames (2026-11-25: the original UI showed for a moment after the launch splash):
    // until Home has its new look, its canvas is not drawn.
    private static bool startupWatched, startupOldSeen;

    private static void WatchStartup()
    {
        if (startupWatched) return;
        GameObject home = FindByName("HomeCanvas") ?? FindByName("MainHomeCanvas");
        if (home == null) return;
        Canvas canvas = home.GetComponent<Canvas>();
        bool restyled = home.transform.Find("PbSkinned") != null;
        if (!restyled && canvas != null && canvas.enabled && home.activeInHierarchy) startupOldSeen = true;
        if (!restyled) return;
        startupWatched = true;
        Check(!startupOldSeen, "the original Home never shows at startup: its canvas is hidden until it has its new look");
    }

    // -playtestRemote: when each stall began, whose it was, and where the match was.
    private static int stallsSeen;
    private static readonly List<string> stallLog = new List<string>();

    private static void WatchStalls()
    {
        if (!RemotePlayers || Net == null || NetEntityView.Stalls <= stallsSeen) return;
        stallsSeen = NetEntityView.Stalls;
        NetEntityView v = NetEntityRegistry.Get(NetEntityView.LastStallSlot);
        string who = v == null ? "gone" : v.Eliminated ? "out" : "in play";
        stallLog.Add($"#{stallsSeen} slot {NetEntityView.LastStallSlot} ({who}) at {Time.realtimeSinceStartup - startedAt:F1} s, {Net.Phase} with {Net.PhaseTimeLeft:F1} s left, ping {Net.PingMs} ms");
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        WatchStartup();
        WatchStalls();

        try
        {
            if (step == Step.WaitPlay && startedAt == 0f)
            {
                startedAt = Time.realtimeSinceStartup;
                stepStarted = startedAt;
                Application.logMessageReceived += CountErrors;
            }

            float limit = ClimbTest ? 1500f : TimeoutSeconds;   // the climbing test goes round every map
            if (Time.realtimeSinceStartup - startedAt > limit)
            {
                Check(false, $"finished within {limit:F0} s (stuck at {step})");
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
        WatchRejoin();
        switch (step)
        {
            case Step.WaitPlay:
                if (Quiet)
                {
                    // Nothing is asked for here: the game signs in by itself, or does not.
                    if (Object.FindAnyObjectByType<HomeMenuUI>() == null) return;
                    Go(Step.Quiet);
                    return;
                }

                if (InStep < 3f || Object.FindAnyObjectByType<HomeMenuUI>() == null) return;

                // Point the game at the test server (NetBootstrap reads NetConfig.json). With
                // -playtestLan at an address where nothing answers: the game must find the server
                // on this network by itself (LanSearch) and sign in there.
                MecchaNetClient net = NetBootstrap.Ensure();
                net.Configure(Lan ? NoServerHere : Host, Port, 0, OnlineConfig.Load().connectionKey);
                if (Pair) net.AuthToken = "pair-test-" + System.Diagnostics.Process.GetCurrentProcess().Id;   // one PC, two players
                Meta.Configure(MetaPort, false, "");
                Hook();
                walletHomeBefore = HomeCoinLabel()?.text;      // what Home shows before anyone has signed in
                OnlineSignIn.Open(p => { signedIn = true; profile = p; }, null);
                Go(Step.SignIn);
                return;

            case Step.SignIn:
                if (signedIn)
                {
                    Check(true, "signed in (Multiplayer -> OnlineSignIn)");
                    if (Lan && !lanChecked)
                    {
                        lanChecked = true;
                        Check(Net.Host != NoServerHere && Net.IsConnected,
                              $"given an address with no server ({NoServerHere}), the game found the server on this network and signed in there ({Net.Host}, after {InStep:F1} s)");
                    }
                    Go(Wallet ? Step.Wallet : Step.Shop);
                    return;
                }
                OnlineSignIn signIn = Object.FindAnyObjectByType<OnlineSignIn>();
                if (!submittedName && signIn != null && Get<GameObject>(signIn, "profileScreen") is { activeSelf: true })
                {
                    submittedName = true;
                    Get<InputField>(signIn, "userIdInput").text = "PlayTest" + UnityEngine.Random.Range(1000, 9999);
                    Shot("profile_first");
                    Call(signIn, "SubmitProfile");
                    Check(true, "first-run profile screen shown and submitted");
                }
                return;

            case Step.Wallet:
                PlayWallet();
                return;

            case Step.Quiet:
                PlayQuiet();
                return;

            case Step.Shop:
                if (nextAction == 0f)
                {
                    nextAction = 1f;
                    // The roller (kept from an earlier run) and a pack of its ammo, through the same
                    // purchase the shop makes: a weapon comes with 10 rounds, a pack adds 10.
                    Meta.Purchase(new List<MetaPurchaseLine> { new MetaPurchaseLine { ItemId = "weapon-roller", Quantity = 1 } }, weapon =>
                    {
                        Check(weapon.Status == PurchaseStatus.Completed || weapon.Status == PurchaseStatus.AlreadyOwned,
                              $"bought the roller, or had it ({weapon.Status}, {weapon.Error})");
                        Meta.Purchase(new List<MetaPurchaseLine> { new MetaPurchaseLine { ItemId = "ammo-roller", Quantity = 1 } }, ammo =>
                        {
                            bought = ammo.Status == PurchaseStatus.Completed || ammo.Status == PurchaseStatus.AmmoFull;
                            Check(bought, $"bought a pack of its ammo, or it is full ({ammo.Status}, {ammo.Error})");
                            boughtAnswered = true;   // its rounds are read from the match's loadout (no extra request: the meta service limits them)
                        });
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
                if (SwitchBody && nextAction == 1f)
                {
                    nextAction = 2f;
                    int other = 1 - Mathf.Clamp(PlayerPrefs.GetInt(CharacterPrefsKey, 0), 0, 1);
                    Meta.Purchase(new List<MetaPurchaseLine> { new MetaPurchaseLine { ItemId = "avatar-" + other, Quantity = 1 } }, receipt =>
                    {
                        Check(receipt.Status == PurchaseStatus.Completed || receipt.Status == PurchaseStatus.AlreadyOwned,
                              $"bought character {other} ({receipt.Status}, {receipt.Error})");
                        Meta.GetProfile(reply =>
                        {
                            if (reply.Error == MetaError.None) profile = reply.Profile;
                            profileRefreshed = true;
                        });
                        avatarAnswered = true;
                    });
                }
                if (ShopTabsTest) { shoesAsked = true; shoesAnswered = true; }   // it buys them on the Shoes tab, with a tap
                if (!shoesAsked)
                {
                    // Sneakers (2026-11-24), the cheapest pair: bought (kept from an earlier run), then
                    // worn, the way the Shoes tab does it.
                    shoesAsked = true;
                    Meta.Purchase(new List<MetaPurchaseLine> { new MetaPurchaseLine { ItemId = "shoes-sneakers", Quantity = 1 } }, receipt =>
                    {
                        Check(receipt.Status == PurchaseStatus.Completed || receipt.Status == PurchaseStatus.AlreadyOwned,
                              $"bought sneakers ({receipt.Status}, {receipt.Error})");
                        Shoes.Equip(1, (worn, message) =>
                        {
                            Check(worn && Shoes.Equipped == 1, $"and put them on: saved with the account ('{message}', worn {Shoes.Equipped})");
                            Meta.GetProfile(reply =>
                            {
                                Check(reply.Error == MetaError.None && reply.Profile.EquippedShoes == 1,
                                      $"the profile says so when asked again ({reply.Error}, {reply.Profile.EquippedShoes})");
                                shoesAnswered = true;
                            });
                        });
                    });
                }
                if (!boughtAnswered || !shoesAnswered || (Pair && !posesAnswered) || (SwitchBody && (!avatarAnswered || !profileRefreshed))) return;

                if (ShopTabsTest)
                {
                    Go(Step.ShopTabs);
                    return;
                }

                OnlineLobby.Open(profile, null);
                Go(Step.Role);
                return;

            case Step.ShopTabs:
                PlayShopTabs();
                return;

            case Step.Role:
                if (InStep < 1f) return;
                {
                    GameObject roleCanvas = FindByName("RoleSelectCanvas");
                    Check(roleCanvas != null && roleCanvas.activeInHierarchy && FindByName("PlaySetup") is { activeInHierarchy: true },
                          "Multiplayer opens the play setup, over Single Play's side screen");
                    Check(FindByName("PageShop") is { activeInHierarchy: true } && roleCanvas.transform.Find("AdBtn") == null,
                          "the setup's top bar has the Shop in Multiplayer (the side screen's Ad button is gone)");
                    Check(FindByName("PageBalance") is { activeInHierarchy: true } rolePill && rolePill.GetComponentInChildren<CoinLabel>(true) != null
                          && FindByName("GlobalCoinPill") is not { activeInHierarchy: true },
                          "and the coin balance is in the top bar (design B), the shared pill not over it");
                    Check(FindByName("PlayNowBtn") is not { activeInHierarchy: true } && FindByName("SeekerBtn") is not { activeInHierarchy: true },
                          "the side screen's old Hider / Seeker buttons give way to the setup's");
                    Shot("role_online");
                    Button sideChoice = SetupButton(Seeker ? "SeekerChoice" : "HiderChoice");
                    Check(sideChoice != null, "the setup offers the side");
                    sideChoice?.onClick.Invoke();
                }
                Go(Step.Body);
                return;

            case Step.Body:
                if (InStep < 0.5f) return;
                GameModeManager modes = Object.FindAnyObjectByType<GameModeManager>();
                Text setupTitle = FindByName("SetupTitle")?.GetComponent<Text>();
                Check(modes != null && !modes.RoundActive && OnlineLobby.InSetup != null && FindByName("HubTabs") is { activeInHierarchy: true }
                      && setupTitle != null && setupTitle.text == L.T("Multiplayer") && SetupButton("SinglePlayTab") == null && SetupButton("MultiplayerTab") == null,
                      $"the setup keeps the mode chosen on Home: Multiplayer, with no mode buttons of its own ('{setupTitle?.text}')");
                Check(FindByName("LoadingCanvas") is not { activeInHierarchy: true }, "and picking the side started no Single Play round");
                SetupButton(Seeker ? "SeekerChoice" : "HiderChoice")?.onClick.Invoke();
                PickBodyOnline();
                Go(Step.Room);
                return;

            case Step.Room:
                Button practice = FindButton("PlaySetup", "Practice");
                if (practice == null) return;
                if (switchedTo >= 0 && !switchChecked)
                {
                    // The new character, chosen on the setup, in the same scene.
                    switchChecked = true;
                    Transform body = PlayerRef.Active;
                    string expected = switchedTo == 0 ? "Player" : "Player" + (switchedTo + 1);
                    int bodies = Object.FindObjectsByType<SeekerCamProfile>(FindObjectsInactive.Include).Length;
                    Check(body != null && body.name == expected && bodies == 1,
                          $"after the switch the room shows {expected} ({(body != null ? body.name : "none")}, {bodies} player bodies)");
                    HomeUI homeNow = Object.FindAnyObjectByType<HomeUI>(FindObjectsInactive.Include);
                    Check(homeNow != null && sceneMarker != null && homeNow == sceneMarker, "without reloading the scene");
                    Check(FindByName("PlaySetup") is { activeInHierarchy: true } && FindByName("SelectPlayerCanvas") is not { activeInHierarchy: true },
                          "with the setup still open and no character screen");
                    Check(Net != null && Net.IsConnected, "still connected to the server");

                    // Everything that moves, films, paints, climbs, poses or arms the player has the new body.
                    var holders = new (System.Type type, string field)[]
                    {
                        (typeof(JoystickMover), "character"), (typeof(CameraController), "character"),
                        (typeof(WallClimber), "character"), (typeof(ChameleonPaint), "characterTf"),
                        (typeof(Mode1FirstPersonGun), "playerTf"), (typeof(GameModeManager), "playerTf"),
                    };
                    var stale = new List<string>();
                    foreach (var (type, field) in holders)
                    {
                        Object holder = Object.FindAnyObjectByType(type, FindObjectsInactive.Include);
                        if (holder != null && Get<Transform>(holder, field) != body) stale.Add(type.Name);
                    }
                    Check(stale.Count == 0, "every script follows the new body" + (stale.Count > 0 ? " (stale: " + string.Join(", ", stale) + ")" : ""));
                    Animator poseAnim = Object.FindAnyObjectByType<PoseSelectorUI>(FindObjectsInactive.Include) is PoseSelectorUI ps ? Get<Animator>(ps, "anim") : null;
                    Check(poseAnim != null && poseAnim.transform.IsChildOf(body), "the pose picker drives the new body's animator");
                }
                Check(FindButton("PlaySetupCanvas", "HISTORY") != null && FindButton("PlaySetupCanvas", "MATCHES") != null && FindByName("Extra_WALLET") == null,
                      "the setup has HISTORY and MATCHES, and no WALLET (2026-11-24: the coin balance opens the wallet)");
                if ((AwayTest || AwayEndTest) && !roomBackChecked)
                {
                    roomBackChecked = true;
                    Check(FindByName("RoleSelectCanvas")?.GetComponentsInChildren<Button>(true).Any(b => b.name == "BackBtn") == true,
                          "the setup has a way back Home (the side screen's Back)");
                }
                if (Standard)
                {
                    // The paid room: open now, real players only, its entry confirmed before the search.
                    Button standard = FindButton("PlaySetup", "Standard");
                    string standardText = standard != null ? string.Join(" / ", standard.GetComponentsInChildren<Text>(true).Select(t => t.text)) : "none";
                    Check(standard != null && standard.interactable && standardText.Contains("20 coins") && standardText.Contains("real players"),
                          $"the Standard room can be picked: '{standardText}'");
                    if (standard == null) { Finish(); return; }
                    standardCoinsBefore = CoinBalance.Cents;
                    Shot("rooms");
                    standard.onClick.Invoke();
                    Go(Step.Map);
                    return;
                }
                practice.onClick.Invoke();
                Go(Step.Map);
                return;

            case Step.Map:
                if (InStep < 0.5f) return;
                {
                    LevelManager levels = Object.FindAnyObjectByType<LevelManager>();
                    Button mapCard = levels != null ? SetupButton("Map_" + levels.RoomNameAt(MapIndex)) : null;
                    if (mapCard == null)
                    {
                        Check(levels == null, "the setup shows the maps");
                        SetupButton("SetupPlay")?.onClick.Invoke();
                        Go(Standard ? Step.Pay : Step.Match);
                        return;
                    }
                    if (MapsSold)
                    {
                        int locked = Enumerable.Range(2, 4).Count(n => ShopInventory.MapLocked(n));
                        int priced = FindByName("SetupMaps").GetComponentsInChildren<Text>(true).Count(t => t.text.EndsWith("coins"));
                        Check(locked == 4 && priced == 4 && !ShopInventory.MapLocked(1),
                              $"Room is free and Room 2-5 show a lock and their price ({locked} locked, {priced} prices)");
                        Shot("map_locks");
                    }
                    mapCard.onClick.Invoke();
                    Text summary = FindByName("SetupSummary")?.GetComponent<Text>();
                    Check(levels.Current == MapIndex && summary != null && summary.text.Contains(L.T(levels.CurrentDisplayName)),
                          $"the map card picks the map, and the line over Play says so ('{summary?.text}')");
                    mapMustBuy = MapsSold && MapIndex > 0 && ShopInventory.MapLocked(MapIndex + 1);
                    SetupButton("SetupPlay")?.onClick.Invoke();
                    Check(true, "FIND MATCH from the setup");
                }
                if (Standard) { Go(Step.Pay); return; }
                Go(mapMustBuy ? Step.BuyMap : Step.Match);
                return;

            case Step.Pay:
                if (InStep < 0.5f) return;
                GameObject pay = FindByName("PayScreen");
                string payText = pay != null ? string.Join(" | ", pay.GetComponentsInChildren<Text>(true).Select(t => t.text.Replace("\n", " "))) : "none";
                Check(pay != null && pay.activeInHierarchy && payText.Contains("Real players only") && payText.Contains("refunded"),
                      $"the entry is confirmed first, saying what it buys ({payText})");
                Check(CoinBalance.Cents == standardCoinsBefore, "nothing is taken before PAY");
                Shot("pay");
                Button payButton = FindButton("PayScreen", "PAY 20 coins");
                Check(payButton != null, "its button says PAY 20 coins");
                if (payButton == null) { Finish(); return; }
                payButton.onClick.Invoke();
                Go(mapMustBuy ? Step.BuyMap : Step.Match);
                return;

            case Step.BuyMap:
                if (InStep < 0.5f) return;
                {
                    GameObject buyScreen = FindByName("PayScreen");
                    Button buy = buyScreen != null ? buyScreen.GetComponentsInChildren<Button>(false).FirstOrDefault(b => b.GetComponentInChildren<Text>().text.StartsWith("BUY ")) : null;
                    Check(buyScreen != null && buyScreen.activeInHierarchy && buy != null, $"a locked map asks to be bought first ('{buy?.GetComponentInChildren<Text>().text}')");
                    Shot("buy_map");
                    if (buy == null) { Go(Step.Done); return; }
                    long before = Net.CoinCents;
                    mapCoinsBefore = before;
                    buy.onClick.Invoke();
                }
                Go(Step.Match);
                return;

            case Step.Match:
                if (!lobbyLaidOut) { lobbyLaidOut = true; CheckLobbyLayout(); }
                if (!OnlineRound.Active) SampleLobby();
                if (OnlineRound.Active && !lobbyJudged) JudgeLobby();
                if (OnlineRound.Active && !splashChecked)
                {
                    splashChecked = true;
                    splashAtStart = FindByName("LoadingCanvas") is { activeInHierarchy: true };
                    Check(splashAtStart, "the loading screen shows between the lobby and the match");
                }
                if (!OnlineRound.Active && Net.LocalSlot != 0xFF && Net.MatchSeats > 0)
                {
                    if (!lobbyMarked && !string.IsNullOrEmpty(LobbyMark)) { lobbyMarked = true; File.WriteAllText(LobbyMark, "lobby"); }
                    if (Standard && !standardLobbyChecked)
                    {
                        standardLobbyChecked = true;
                        string waiting = FindByName("LobbyBar")?.GetComponentsInChildren<Text>(true).FirstOrDefault()?.text ?? "";
                        Check(waiting.Contains("real players"), $"the paid lobby waits for real players ('{waiting.Replace("\n", " ")}')");
                    }
                }
                if (!homeChecked && FindByName("MatchmakingCanvas") is { activeSelf: true })
                {
                    homeChecked = true;
                    Check(FindByName("HomeCanvas") is { activeSelf: false }, "the Home screen is hidden during matchmaking");
                }
                if (!OnlineRound.Active) return;
                Check(Net.MatchSeats == Seats, $"the match has the room's {Seats} seats ({Net.MatchSeats})");
                if (switchedTo >= 0)
                {
                    var everyone = Get<MecchaNetClient.RosterEntry[]>(OnlineRoundInstance(), "roster");
                    uint model = everyone != null ? everyone.FirstOrDefault(e => e.Slot == Net.LocalSlot).ModelId : 999u;
                    Check(model == (uint)switchedTo, $"the server has this player as character {switchedTo} ({model})");
                }
                Check(Net.MatchHunterSeats == GameTuning.SeekersFor(Seats),
                      $"{GameTuning.SeekersFor(Seats)} of them seek and {Seats - GameTuning.SeekersFor(Seats)} hide ({Net.MatchHunterSeats} seekers)");
                Check(Net.LocalRole == (Seeker ? Role.Hunter : Role.Fugitive), $"plays the side picked ({Net.LocalRole})");
                if (Standard)
                {
                    var seated = Get<MecchaNetClient.RosterEntry[]>(OnlineRoundInstance(), "roster") ?? new MecchaNetClient.RosterEntry[0];
                    Check(seated.Length == 8 && seated.All(e => !e.IsBot), $"the Standard match is 8 real players, no bots ({seated.Count(e => !e.IsBot)} real, {seated.Count(e => e.IsBot)} bots)");
                    Check(Net.HostedBots == null || Net.HostedBots.Length == 0, "and this game is given no bots to run");
                    long held = standardCoinsBefore - CoinBalance.Cents;
                    Check(held == 20 * GameTuning.CentsPerCoin, $"the 20-coin entry is held from the balance ({held / (double)GameTuning.CentsPerCoin} coins)");
                }
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
                bool oldPopup = FindByName("VictoryPopup") is { activeInHierarchy: true } || FindByName("DefeatPopup") is { activeInHierarchy: true };
                Check(shown && FindByName("MatchResultsScreen") is { activeInHierarchy: true } && !oldPopup,
                      "the results screen is the one result shown (no Single Play popup under it)");
                if (AwayTest) CheckResultsScreen("at the end the results screen shows");
                if (NextTest)
                {
                    // Next is the room list now, not the same room again.
                    matchesAtNext = matchesFound;
                    Button next = FindByName("ResultsNext") is { activeInHierarchy: true } nextGo ? nextGo.GetComponent<Button>() : null;
                    Check(next != null, "the results screen offers Next");
                    if (next != null) next.onClick.Invoke();
                    else Object.FindAnyObjectByType<OnlineLobby>().PlayAgainFromRound();
                    Go(Step.NextRooms);
                    return;
                }
                Object.FindAnyObjectByType<OnlineLobby>().GoHomeFromRound();
                Go(Step.Home);
                return;

            case Step.Exited:
                if (InStep < 1f) return;
                Go(Step.Done);
                return;

            case Step.Away:
                PlayAway();
                return;

            case Step.NextRooms:
                if (InStep < 4f) return;     // long enough for a lobby to turn up if one were coming
                Check(!OnlineRound.Active && FindByName("PlaySetup") is { activeInHierarchy: true } && FindButton("PlaySetup", "Practice") != null,
                      "Next after the match opens the play setup, with every room the server has to pick from");
                Shot("next_rooms");
                Check(matchesFound == matchesAtNext && !Net.InMatch, "and nobody put this player into another match by themselves");
                // Out the way a player goes: the side screen's Back, under the setup.
                FindByName("RoleSelectCanvas")?.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == "BackBtn")?.onClick.Invoke();
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
                if (mapCoinsBefore >= 0)
                    Check(!ShopInventory.MapLocked(MapIndex + 1) && mapCoinsBefore - Net.CoinCents >= 50000,
                          $"the map bought before the match is owned now, paid once ({mapCoinsBefore} -> {Net.CoinCents} cents)");
                if (Seeker && !Chase && !Pair && !Kills)
                    Check(ShopInventory.AmmoOf(TestWeapon) < ammoBeforeRound,
                          $"the roller's rounds shot in the round are gone from the account ({ammoBeforeRound} -> {ShopInventory.AmmoOf(TestWeapon)})");
                Go(Step.Language);
                return;

            case Step.Language:
                if (!CheckLanguages()) return;
                OnlineHistory.Show(Object.FindAnyObjectByType<OnlineLobby>().transform, OnlineHistory.Tab.Matches, null);
                Go(Step.History);
                return;

            case Step.History:
            {
                if (InStep < 2f) return;
                GameObject historyScreen = FindByName("HistoryScreen");
                if (nextAction == 0f)
                {
                    Text status = historyScreen?.GetComponentsInChildren<Text>(false)
                        .FirstOrDefault(t => t.text.Contains("No matches") || t.text.Contains("did not answer") || t.text.Contains("could not"));
                    bool rows = historyScreen?.GetComponentsInChildren<Transform>(false).Any(t => t.name == "Record") == true;
                    Check(rows || (status != null && status.text.StartsWith("No matches")),
                          "MATCH HISTORY loads from the server" + (status != null ? $" (\"{status.text}\")" : rows ? " (records listed)" : ""));
                    Check(FindByName("Extra_WALLET") == null && FindByName("Extra_HISTORY") != null,
                          "the Multiplayer setup has HISTORY and no WALLET button (the coin balance opens the Shop's Coins)");
                    string listed = string.Join(" | ", historyScreen.GetComponentsInChildren<Text>(false).Select(t => t.text));
                    Check(!listed.Contains("Cancelled") && !listed.Contains("Refunded"), "no cancelled or refunded match is listed");
                    Shot("history_matches");

                    // A played match's Results: its full table, as it ended (2026-11-25).
                    Button results = historyScreen.GetComponentsInChildren<Button>(false).FirstOrDefault(b => b.name == "RecordResults");
                    Check(!rows || results != null, "each match listed has its Results");
                    if (results != null) results.onClick.Invoke();
                    nextAction = results != null ? 0.5f : 0.75f;
                    stepStarted = Time.realtimeSinceStartup;
                    return;
                }

                if (nextAction == 0.5f)
                {
                    if (InStep < 3f) return;
                    GameObject page = FindByName("MatchResultsScreen");
                    string table = page != null && page.activeInHierarchy ? string.Join(" | ", page.GetComponentsInChildren<Text>(false).Select(t => t.text)) : "";
                    Check(OnlineMatches.ResultsOpen && table.Contains("RANK") && page.GetComponentsInChildren<Transform>(false).Count(t => t.name.StartsWith("Row")) >= 2,
                          $"a record's Results open the match's whole table ({(table.Length > 160 ? table.Substring(0, 160) + "..." : table)})");
                    Shot("history_results");
                    BackKey.Press();   // the phone's Back closes them
                    nextAction = 0.6f;
                    stepStarted = Time.realtimeSinceStartup;
                    return;
                }

                if (nextAction == 0.6f)
                {
                    if (InStep < 1f) return;
                    Check(!OnlineMatches.ResultsOpen && historyScreen != null && historyScreen.activeInHierarchy, "Back closes them, and the history is there again");
                    nextAction = 0.75f;
                    return;
                }

                if (nextAction == 0.75f)
                {
                    Button walletTab = FindButton("HistoryScreen", "WALLET HISTORY");
                    Check(walletTab != null, "the history has a WALLET HISTORY tab");
                    if (walletTab != null) walletTab.onClick.Invoke();
                    nextAction = 1f;
                    stepStarted = Time.realtimeSinceStartup - 2f;
                    return;
                }

                if (InStep < 4f) return;
                // The coin ledger: what this account bought, with the balance after each.
                string ledger = historyScreen != null ? string.Join(" | ", historyScreen.GetComponentsInChildren<Text>(false).Select(t => t.text)) : "";
                bool lines = historyScreen?.GetComponentsInChildren<Transform>(false).Any(t => t.name == "Line") == true;
                Check(lines ? ledger.Contains("BALANCE") && (ledger.Contains("Shop spend") || ledger.Contains("Purchased coins") || ledger.Contains("Match reward"))
                            : ledger.Contains("Nothing has moved your coins yet."),
                      $"WALLET HISTORY lists the coin ledger ({(ledger.Length > 200 ? ledger.Substring(0, 200) + "..." : ledger)})");
                Check(historyScreen?.GetComponentsInChildren<Transform>(false).Any(t => t.name == "Record") != true, "and only the ledger while that tab is picked");
                Shot("history_wallet");
                Go(Step.ShopCards);
                return;
            }

            case Step.ShopCards:
                if (nextAction == 0f)
                {
                    // The shop on its Weapons tab, as the Shop button opens it.
                    nextAction = 1f;
                    ShopScreen.Open(null, "weapons");
                    stepStarted = Time.realtimeSinceStartup;
                    return;
                }
                if (InStep < 3f) return;
                if (nextAction == 1f)
                {
                    nextAction = 2f;
                    GameObject card = FindByName("Item_weapon-roller");
                    string cardText = card != null ? string.Join(" | ", card.GetComponentsInChildren<Text>(false).Select(t => t.text)) : "none";
                    int left = ShopInventory.AmmoOf(TestWeapon);
                    Check(ShopScreen.IsOpen && ShopScreen.CurrentCategory == "weapons" && card != null && cardText.Contains("Roller") && cardText.Contains($"{left} rounds"),
                          $"the shop's Weapons tab shows the roller with its rounds left ({cardText})");
                    Check(ShownItemCards().All(c => ShopInventory.TryItem(c.name.Substring(5), out MetaItem k) && k.Kind != ItemKind.Ammo)
                          && FindByName("Filter_Ammo") is not { activeInHierarchy: true },
                          "and lists weapons, not their ammo packs (rounds are bought on the weapon's page)");
                    Shot("shop_weapons");
                    ShopScreen.ShowItem("weapon-roller");
                    return;
                }
                if (nextAction == 2f && InStep > 4f)
                {
                    nextAction = 3f;
                    GameObject detail = FindByName("Detail");
                    string all = detail != null ? string.Join(" | ", detail.GetComponentsInChildren<Text>(false).Select(t => t.text)) : "none";
                    Check(detail is { activeInHierarchy: true } && all.Contains("Cooldown") && all.Contains("2 s") && all.Contains("\u00d71.5"),
                          $"its page shows the roller's own cooldown and bullet size ({(all.Length > 240 ? all.Substring(0, 240) + "..." : all)})");
                    Check(all.Contains("AMMO") && FindByName("AmmoPack_ammo-roller") is { activeInHierarchy: true } && ShopButton("Action_Buy rounds") != null,
                          "and sells its rounds right there: its ammo pack and Buy rounds");
                    Shot("shop_weapon_detail");
                    if (NextPlayerTest)
                    {
                        ShopScreen.CloseIfOpen();
                        ShopScreen.Open(null, "extras");
                    }
                    return;
                }
                if (InStep < 5f) return;
                if (NextPlayerTest)
                {
                    // The More tab sells Next Player: owned by now for a player who bought it when knocked out.
                    GameObject card = FindByName("Item_next-player");
                    string cardText = card != null ? string.Join(" | ", card.GetComponentsInChildren<Text>(false).Select(t => t.text)) : "none";
                    bool ownsIt = ShopInventory.OwnsFeature(GameFeatures.NextPlayer);
                    Check(card != null && cardText.Contains("Next Player") && cardText.Contains(ownsIt ? "Owned" : "100"),
                          $"its Next Player card shows it {(ownsIt ? "owned" : "for sale at 100 coins")} ({cardText})");
                    Shot("shop_extras");
                }
                ShopScreen.CloseIfOpen();
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

            case Step.Climb:
                PlayClimb();
                return;

            case Step.Hub:
                PlayHub();
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

    // ================= Multiplayer's tabs and the coin balance (2026-11-25) =================
    //
    // After the shop test: Multiplayer's setup, then each tab tapped where a finger taps it. The
    // tab's own page has to come up on top, with that tab picked in its row; the coin balance has to
    // fit its pill with the coin and number centred; and the Shop, opened from a tab's page, has to
    // come up over it. Pictures at 16:9 and at a phone's 20:9.
    private static int hubStage;
    private static float hubAt;
    private static readonly (string tab, string page)[] HubPages =
    {
        ("Extra_MATCHES", "MyMatchesScreen"), ("Extra_HISTORY", "HistoryScreen"), ("Extra_PROFILE", "ProfileScreen"),
        ("Extra_RATE", "RateScreen"), ("Tab_NEW", "PlaySetup"),
    };

    private static GameObject ShownByName(string name) =>
        Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.name == name && g.scene.IsValid() && g.activeInHierarchy);

    // A page as it is drawn: on a canvas. (The setup stays up under the other hub pages with tabs of
    // its own, and a page's host object can carry the page's name.)
    private static GameObject ShownPage(string name) =>
        Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.name == name && g.scene.IsValid() && g.activeInHierarchy
                                                                         && g.transform is RectTransform);

    private static GameObject InPage(GameObject page, string name) =>
        page == null ? null : page.GetComponentsInChildren<Transform>(false).FirstOrDefault(x => x.name == name)?.gameObject;

    private static void PlayHub()
    {
        float now = Time.realtimeSinceStartup;
        if (now < hubAt) return;

        if (hubStage == 0)
        {
            OnlineLobby.Open(profile, null);
            hubAt = now + 2.5f;
            hubStage = 1;
            return;
        }

        if (hubStage == 1)
        {
            GameObject setup = ShownPage("PlaySetup");
            Check(setup != null, "Multiplayer's setup is up (New Match)");
            HubLook("newmatch", "Tab_NEW", setup);
            TapAt(InPage(setup, HubPages[0].tab)?.GetComponent<Button>());
            hubAt = now + 1.8f;
            hubStage = 2;
            return;
        }

        int index = hubStage - 2;
        if (index < HubPages.Length)
        {
            (string tab, string page) = HubPages[index];
            GameObject shown = ShownPage(page);
            string others = string.Join(", ", HubPages.Where(h => h.page != page && h.page != "PlaySetup" && ShownPage(h.page) != null).Select(h => h.page));
            Check(shown != null && others.Length == 0, $"the {tab} tab brings up its page ({page}{(others.Length > 0 ? "; also up: " + others : "")})");
            HubLook(page.ToLowerInvariant(), tab, shown);

            if (page == "HistoryScreen")
            {
                // The Shop from here comes up over the page, not behind it.
                InPage(shown, "PageShop")?.GetComponent<Button>()?.onClick.Invoke();
                hubAt = now + 2f;
                hubStage = 20;
                return;
            }

            if (index + 1 < HubPages.Length) TapAt(InPage(shown, HubPages[index + 1].tab)?.GetComponent<Button>());
            hubAt = now + 1.8f;
            hubStage++;
            if (index + 1 >= HubPages.Length) { Go(Step.Done); }
            return;
        }

        if (hubStage == 20)
        {
            Canvas shop = FindByName("ShopScreenCanvas")?.GetComponent<Canvas>();
            Canvas history = FindByName("OnlineHistoryCanvas")?.GetComponent<Canvas>();
            Check(ShopScreen.IsOpen && shop != null && history != null && shop.sortingOrder > history.sortingOrder,
                  $"the Shop opened from the History page comes up over it ({(shop != null ? shop.sortingOrder : -1)} over {(history != null ? history.sortingOrder : -1)})");
            Shot("hub_shop_over_history");
            ShopScreen.CloseIfOpen();
            hubAt = now + 1f;
            hubStage = 21;
            return;
        }

        if (hubStage == 21)
        {
            GameObject history = ShownPage("HistoryScreen");
            Check(history != null, "closing it leaves the History page there");
            TapAt(InPage(history, "Extra_PROFILE")?.GetComponent<Button>());
            hubAt = now + 1.8f;
            hubStage = 2 + 2;   // the Profile page next
        }
    }

    // A tab page as it looks: its tab picked, the tabs' words centred, the coin balance in its pill.
    private static void HubLook(string name, string tab, GameObject page)
    {
        GameObject picked = InPage(page, tab);
        PaintboxFrame frame = picked != null ? picked.GetComponent<PaintboxFrame>() : null;
        Check(frame != null && Near(frame.Fill.color, Paintbox.Violet), $"its tab ({tab}) is picked: violet ({(frame != null ? frame.Fill.color.ToString() : "none")})");

        // The tabs' icon and word, centred in each tab.
        var off = new List<string>();
        foreach (var t in HubPages)
        {
            GameObject b = InPage(page, t.tab);
            RectTransform content = b != null ? b.transform.Find("Body/Content") as RectTransform ?? b.GetComponentsInChildren<RectTransform>().FirstOrDefault(r => r.name == "Content") : null;
            if (content == null) continue;
            var parts = content.GetComponentsInChildren<Graphic>(false).Where(g => g.transform.parent == content).Select(g => ScreenRect(g.rectTransform)).ToList();
            if (parts.Count == 0) continue;
            float left = parts.Min(r => r.xMin), right = parts.Max(r => r.xMax);
            Rect whole = ScreenRect((RectTransform)b.transform);
            float shift = (left + right) * 0.5f - whole.center.x;
            if (Mathf.Abs(shift) > 4f) off.Add($"{t.tab} {shift:F0} px ({string.Join("+", content.Cast<Transform>().Where(c => c.gameObject.activeSelf).Select(c => c.name))})");
        }
        Check(off.Count == 0, $"each tab's icon and word are centred in it{(off.Count > 0 ? ": off by " + string.Join(", ", off) : "")}");

        // The coin balance: coin and number inside the pill, centred.
        GameObject pill = InPage(page, "PageBalance");
        Text number = pill != null ? pill.GetComponentsInChildren<Text>(false).FirstOrDefault(x => x.GetComponent<CoinLabel>() != null) : null;
        if (pill != null && number != null)
        {
            Rect box = ScreenRect((RectTransform)pill.transform);
            Rect row = ScreenRect((RectTransform)number.transform.parent);
            Check(row.xMin >= box.xMin - 1f && row.xMax <= box.xMax + 1f && Mathf.Abs(row.center.x - box.center.x) < 8f,
                  $"the coin balance sits centred inside its pill ('{number.text}': coin and number {row}, pill {box})");
        }
        Shot("hub_" + name);
        Shot("hub_" + name + "_phone", 1560, 720);
    }

    // ================= -playtestClimb: climbing everything climbable, on every map =================
    //
    // Offline (with -playtestQuietOffline), a Single Play hider round on each map with the bots
    // stopped: the climbable walls and furniture around the room are found, and at each the player
    // holds Climb and pushes the stick at it as a player would, for a few seconds. A climb that
    // stops moving while both are held is a freeze (2026-11-25: players froze climbing).
    private static bool ClimbTest => System.Environment.GetCommandLineArgs().Contains("-playtestClimb");

    private struct ClimbSpot
    {
        public Vector3 Feet, Normal;
        public float Height;   // how high the climbable thing goes above the floor, in body heights
    }

    private static int climbStage, climbMap, climbIndex, climbFreezes, climbTopped, climbTried, climbNeverStarted;
    private static int climbTrial;   // 0: Climb held alone; 1: Climb held and the stick pushed at the wall
    private static List<ClimbSpot> climbSpots;
    private static float climbAt, climbStartY, climbBodyHeight, climbFeetOffset, climbBest;
    private static readonly List<(float t, Vector3 p, bool onWall)> climbPath = new List<(float t, Vector3 p, bool onWall)>();
    private static readonly List<string> climbProblems = new List<string>();
    private static bool climbWasOnWall;

    private static void PlayClimb()
    {
        GameModeManager gmm = Object.FindAnyObjectByType<GameModeManager>();
        WallClimber climber = Object.FindAnyObjectByType<WallClimber>(FindObjectsInactive.Include);
        float now = Time.realtimeSinceStartup;
        if (now < climbAt) return;
        int maps = LevelManager.Instance != null ? LevelManager.Instance.Count : 1;

        switch (climbStage)
        {
            case 0:     // a hider round on this map, the bots stopped
            {
                if (climbMap >= maps)
                {
                    Check(climbTried > 0, $"climbing was tried ({climbTried} spots on {maps} maps; {climbTopped} reached a top, {climbNeverStarted} never took hold)");
                    Check(climbFreezes == 0, $"no climb froze ({climbFreezes} froze{(climbProblems.Count > 0 ? ": " + string.Join(" | ", climbProblems.Take(40)) : "")})");
                    Go(Step.Done);
                    return;
                }
                gmm.ShowMenu();
                LevelManager.Instance.SetLevel(climbMap);
                gmm.BeginRound(false);
                Object.FindAnyObjectByType<HomeUI>()?.SetGameplayUIVisible(true);
                if (FindByName("HomeCanvas") is { } homeCanvas) homeCanvas.SetActive(false);   // as the game's Play hides it
                BotSeekController seek = Object.FindAnyObjectByType<BotSeekController>();
                if (seek != null) seek.enabled = false;
                climbAt = now + 1.5f;
                climbStage = 1;
                return;
            }

            case 1:     // where the climbable things are
            {
                Transform body = PlayerRef.Active;
                CharacterController cc = body != null ? body.GetComponent<CharacterController>() : null;
                if (body == null || cc == null) { Check(false, "the player's body is there to climb with"); Go(Step.Done); return; }
                climbBodyHeight = cc.height * body.lossyScale.y;
                climbFeetOffset = body.position.y - cc.bounds.min.y;
                climbSpots = FindClimbSpots(climber, cc.bounds.min.y, climbBodyHeight, cc.radius * body.lossyScale.x);
                report.Add($"INFO  map {climbMap + 1} ({LevelManager.Instance.CurrentRoomName}): {climbSpots.Count} climbable spots found (body {climbBodyHeight:F1} tall)");
                climbIndex = 0;
                climbStage = 2;
                return;
            }

            case 2:     // to the next spot, facing it
            {
                if (climbIndex >= climbSpots.Count)
                {
                    climbMap++;
                    climbStage = 0;
                    return;
                }
                ClimbSpot spot = climbSpots[climbIndex];
                Transform body = PlayerRef.Active;
                CharacterController cc = body.GetComponent<CharacterController>();
                climber.DropToGround();
                SetClimb(climber, 1, false);
                SetClimb(climber, -1, false);
                StickInput(Vector2.zero);
                cc.enabled = false;
                body.position = spot.Feet + Vector3.up * climbFeetOffset;
                body.rotation = Quaternion.LookRotation(-new Vector3(spot.Normal.x, 0f, spot.Normal.z).normalized, Vector3.up);
                cc.enabled = true;
                climbAt = now + 0.4f;
                climbStage = 3;
                return;
            }

            case 3:     // hold Climb and push at the wall
            {
                Transform body = PlayerRef.Active;
                climbStartY = body.position.y;
                climbBest = climbStartY;
                climbPath.Clear();
                climbWasOnWall = false;
                SetClimb(climber, 1, true);
                climbTried++;
                climbAt = 0f;
                climbStage = 4;
                stepStarted = now;
                return;
            }

            case 4:
            {
                Transform body = PlayerRef.Active;
                ClimbSpot spot = climbSpots[climbIndex];
                Vector3 toWall = -new Vector3(spot.Normal.x, 0f, spot.Normal.z).normalized;
                StickInput(climbTrial == 1 ? StickToward(toWall) : Vector2.zero);
                SetClimb(climber, 1, true);
                climbPath.Add((InStep, body.position, climber.IsOnWall));
                climbBest = Mathf.Max(climbBest, body.position.y);
                if (climber.IsOnWall) climbWasOnWall = true;

                bool toppedOut = climbWasOnWall && !climber.IsOnWall && body.position.y > climbStartY + climbBodyHeight * 0.5f;
                // Still on the wall, holding both, and nowhere new for 1.5 s: frozen.
                bool frozen = false;
                if (climber.IsOnWall && InStep > 2f)
                {
                    var window = climbPath.Where(s => s.t >= InStep - 1.5f).ToList();
                    float moved = 0f;
                    for (int i = 1; i < window.Count; i++) moved += Vector3.Distance(window[i].p, window[i - 1].p);
                    frozen = window.Count > 5 && moved < 0.25f;
                }

                if (!toppedOut && !frozen && InStep < 8f) return;

                string where = $"map {climbMap + 1} spot {climbIndex + 1} ({spot.Feet.x:F0}, {spot.Feet.z:F0}), {spot.Height:F1} bodies tall, {(climbTrial == 0 ? "Climb alone" : "Climb and the stick")}";
                if (toppedOut) climbTopped++;
                else if (!climbWasOnWall) { climbNeverStarted++; report.Add($"INFO  {where}: never took hold"); }
                else if (frozen && BlockedAbove(body))
                {
                    // Under a ceiling or an overhang: the way up ends there; down and sideways still go.
                    report.Add($"INFO  {where}: stopped under something {(body.position.y - climbStartY) / climbBodyHeight:F2} bodies up");
                }
                else if (frozen)
                {
                    climbFreezes++;
                    float up = (body.position.y - climbStartY) / climbBodyHeight;
                    climbProblems.Add($"{where}: froze {up:F2} bodies up");
                    if (climbFreezes <= 4) Shot($"climb_freeze_{climbFreezes}");
                }
                else report.Add($"INFO  {where}: still climbing after 8 s ({(climbBest - climbStartY) / climbBodyHeight:F2} bodies up)");

                SetClimb(climber, 1, false);
                StickInput(Vector2.zero);
                if (climbTrial == 0) climbTrial = 1;
                else { climbTrial = 0; climbIndex++; }
                climbStage = 2;
                climbAt = now + 0.2f;
                return;
            }
        }
    }

    // Something just over the head: the ceiling, or an overhang the climb has met.
    private static bool BlockedAbove(Transform body)
    {
        CharacterController cc = body.GetComponent<CharacterController>();
        if (cc == null) return false;
        Bounds b = cc.bounds;
        foreach (RaycastHit hit in Physics.SphereCastAll(new Vector3(b.center.x, b.max.y - cc.radius * body.lossyScale.x, b.center.z),
                                                          cc.radius * body.lossyScale.x * 0.9f, Vector3.up, 1.5f, ~0, QueryTriggerInteraction.Ignore))
            if (!hit.transform.IsChildOf(body)) return true;
        return false;
    }

    private static void SetClimb(WallClimber climber, int dir, bool held) =>
        typeof(WallClimber).GetMethod("SetClimb", Any).Invoke(climber, new object[] { dir, held });

    private static void StickInput(Vector2 input)
    {
        JoystickMover mover = Object.FindAnyObjectByType<JoystickMover>();
        Joystick stick = mover != null ? Get<Joystick>(mover, "joystick") : null;
        if (stick != null) typeof(Joystick).GetField("input", Any).SetValue(stick, input);
    }

    // The stick's push that moves the body along this world direction, as the camera is now.
    private static Vector2 StickToward(Vector3 direction)
    {
        Camera cam = Camera.main;
        if (cam == null) return Vector2.up;
        Vector3 f = cam.transform.forward; f.y = 0f; f.Normalize();
        Vector3 r = cam.transform.right; r.y = 0f; r.Normalize();
        return new Vector2(Vector3.Dot(direction, r), Vector3.Dot(direction, f)).normalized;
    }

    // Steep climbable surfaces around the room, seen from a grid of open floor points, each with
    // how high it goes; no two within a few bodies of each other.
    private static List<ClimbSpot> FindClimbSpots(WallClimber climber, float floorY, float bodyHeight, float radius)
    {
        var spots = new List<ClimbSpot>();
        GameObject room = LevelManager.Instance != null ? LevelManager.Instance.CurrentRoomInstance : null;
        if (room == null) return spots;
        Bounds area = default;
        bool any = false;
        foreach (Renderer r in room.GetComponentsInChildren<Renderer>())
        {
            if (!r.enabled || r.GetComponentInParent<Animator>() != null) continue;
            if (!any) { area = r.bounds; any = true; } else area.Encapsulate(r.bounds);
        }
        if (!any) return spots;

        MethodInfo climbable = typeof(WallClimber).GetMethod("IsClimbable", Any);
        var rng = new System.Random(7 + (int)(area.center.x * 13));
        Vector3[] dirs = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
        for (int attempt = 0; attempt < 400 && spots.Count < 14; attempt++)
        {
            float x = Mathf.Lerp(area.min.x, area.max.x, (float)rng.NextDouble());
            float z = Mathf.Lerp(area.min.z, area.max.z, (float)rng.NextDouble());
            // Open floor at about the spawn's height.
            if (!Physics.Raycast(new Vector3(x, floorY + bodyHeight * 1.2f, z), Vector3.down, out RaycastHit floor, bodyHeight * 2f)) continue;
            if (Mathf.Abs(floor.point.y - floorY) > bodyHeight * 0.3f || Vector3.Angle(floor.normal, Vector3.up) > 20f) continue;
            Vector3 mid = floor.point + Vector3.up * (bodyHeight * 0.5f);
            if (Physics.CheckSphere(mid, radius * 1.1f, ~0, QueryTriggerInteraction.Ignore)) continue;

            Vector3 dir = dirs[rng.Next(dirs.Length)];
            if (!Physics.Raycast(mid, dir, out RaycastHit wall, bodyHeight * 4f, ~0, QueryTriggerInteraction.Ignore)) continue;
            if (Vector3.Angle(wall.normal, Vector3.up) < climber.minWallSteepness) continue;
            if (!(bool)climbable.Invoke(climber, new object[] { wall.collider })) continue;

            Vector3 n = new Vector3(wall.normal.x, 0f, wall.normal.z).normalized;
            Vector3 feet = new Vector3(wall.point.x, floor.point.y, wall.point.z) + n * (radius + 0.6f);
            if (Physics.CheckSphere(feet + Vector3.up * (bodyHeight * 0.5f), radius * 1.05f, ~0, QueryTriggerInteraction.Ignore)) continue;
            if (spots.Any(s => Vector3.Distance(s.Feet, feet) < bodyHeight * 1.5f)) continue;

            // How high it goes: the first height a ray at it no longer meets it.
            float height = 0f;
            for (float h = 0.25f; h <= 6f; h += 0.25f)
            {
                if (!Physics.Raycast(feet + Vector3.up * (bodyHeight * h), -n, radius + 1.6f, ~0, QueryTriggerInteraction.Ignore)) break;
                height = h;
            }
            if (height < 0.75f) continue;   // too low to climb: stepped or jumped onto
            spots.Add(new ClimbSpot { Feet = feet, Normal = n, Height = height });
        }
        return spots;
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
                // Screens the online checks left open (the shop, the match history) are closed first.
                ShopUI.CloseOpen();
                if (FindByName("HistoryScreen") is { activeSelf: true } history) history.SetActive(false);
                {
                    // Single Play's setup: every map free, no lock, no price, no rooms, no online buttons.
                    PlaySetupUI.OpenSingle();
                    GameObject setup = FindByName("PlaySetup");
                    List<Button> mapCards = setup != null ? setup.GetComponentsInChildren<Button>(true).Where(b => b.name.StartsWith("Map_")).ToList() : new List<Button>();
                    int locks = mapCards.Count(b => b.transform.Find("Lock") is { gameObject: { activeSelf: true } });
                    int prices = mapCards.Count(b => b.GetComponentsInChildren<Text>(true).Any(t => t.text.EndsWith("coins")));
                    Check(mapCards.Count > 0 && mapCards.All(b => b.interactable) && locks == 0 && prices == 0
                          && FindByName("SetupRooms") is not { activeInHierarchy: true } && FindByName("PlaySetupExtras") is not { activeInHierarchy: true },
                          $"Single Play's setup: every map free, no lock or price, no rooms or online buttons ({mapCards.Count} maps, {locks} locks, {prices} prices)");
                    Check(FindByName("PageRate") is not { activeInHierarchy: true } && FindByName("Ratebtn") is not { activeInHierarchy: true },
                          "Single Play's setup has no Rate button (no server rating in Single Play)");
                    Shot("single_setup");
                    PlaySetupUI.CloseIfOpen();
                    Object.FindAnyObjectByType<HomeUI>()?.HidePlaySetup(true);
                }
                gmm.ShowMenu();
                gmm.BeginRound(false);      // Play Now: the player hides, the bots hunt
                singleBots = manager.Bots.ToList();
                {
                    string off = OffMarkers(singleBots.Skip(singleBots.Count - GameTuning.HunterSlots), true);
                    Check(off == "", $"Single Play: the seeker bots start on the room's HunterSpawn markers, as online ({off})");
                    off = OffMarkers(new[] { PlayerRef.Active }, false);
                    Check(off == "", $"Single Play: the hiding player starts on a HiderSpawn marker ({off})");
                }
                seek.SkipGrace();
                singlePlayStage = 1;
                stepStarted = Time.realtimeSinceStartup;
                return;

            case 1:
                if (InStep < 0.3f) return;
                seekerStarts = singleBots.Skip(singleBots.Count - GameTuning.HunterSlots).Select(b => b.position).ToList();
                Check(seek.chasePlayer, "Single Play's seekers hunt the player again after an online round");
                {
                    // This test starts the round straight from GameModeManager; with Home still up
                    // underneath, notices may show (Home is the hub), so they are judged only without it.
                    bool homeUp = FindByName("HomeCanvas") is { activeInHierarchy: true };
                    Check(FindByName("Btn_Shop") is not { activeInHierarchy: true } && FindByName("GlobalCoinPill") is not { activeInHierarchy: true }
                          && (homeUp || (FindByName("MatchesChip") is not { activeInHierarchy: true } && FindByName("Toasts") is not { activeInHierarchy: true })),
                          $"Single Play shows no Shop button, no coin balance and no match notices (Home up: {homeUp})");
                }
                {
                    GameObject caught = FindByName("CaughtPopup"), timeUp = FindByName("TimeUpPopup");
                    bool adFree = caught != null && timeUp != null
                                  && caught.transform.Find("Panel/ActionBtn/Ads") is not { gameObject: { activeSelf: true } }
                                  && timeUp.transform.Find("Panel/ActionBtn/Ads") is not { gameObject: { activeSelf: true } }
                                  && caught.transform.Find("Panel/Body")?.GetComponent<Text>().text == "Get one more chance."
                                  && timeUp.transform.Find("Panel/Body")?.GetComponent<Text>().text == "Get 30 extra seconds.";
                    Check(adFree, "Revive and +30s carry no ad sign and say they are free");
                    Check(FindByName("VictoryPopup")?.transform.Find("GoldBtn") is not { gameObject: { activeSelf: true } }
                          && FindByName("DefeatPopup")?.transform.Find("GoldBtn") is not { gameObject: { activeSelf: true } },
                          "Single Play's result popup has no X2 Gold or Shop button");
                }
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
                Check(Net == null || Net.HostedBots.Length == 0, "Single Play sends nothing to the server about its bots");
                {
                    // Revive (the Caught popup's button) costs nothing now: the round goes back to the grace.
                    typeof(BotSeekController).GetMethod("RevivePlayer", Any).Invoke(seek, null);
                    object state = typeof(BotSeekController).GetField("state", Any).GetValue(seek);
                    Check(state.ToString() == "Grace" && FindByName("CaughtPopup") is not { activeInHierarchy: true },
                          $"Revive is free: the round starts again from the grace ({state})");
                }
                // End round from the round menu, then the seeker round from Single Player's setup.
                singlePlayStage = 10;
                stepStarted = Time.realtimeSinceStartup;
                return;

            case 4:     // the free camera, panning down from the high place
                if (!FreeCameraFromHigh(stage: 1)) return;
                gmm.ShowMenu();
                Check(!WallsOpen(), "and nobody can from the Home screen");
                Go(Step.Done);
                return;

            // End round, from the round menu: back to Single Player's setup, not Home (2026-11-25).
            case 10:
                // This test starts its rounds with BeginRound itself; the game's Play shows the HUD first.
                Object.FindAnyObjectByType<HomeUI>()?.SetGameplayUIVisible(true);
                if (InStep < 0.5f) return;
                Check(PaintboxHud.ToggleMenu() && PaintboxHud.MenuOpen, "the round menu opens in Single Play");
                singlePlayStage = 11;
                stepStarted = Time.realtimeSinceStartup;
                return;

            case 11:
            {
                if (InStep < 0.5f) return;
                Button leave = MenuItem("MenuLeave");
                string label = leave != null ? leave.GetComponentInChildren<Text>(true).text : "none";
                Check(label == "End round" && MenuItem("MenuShop") == null, $"with End round and no Shop ('{label}')");
                leave?.onClick.Invoke();
                singlePlayStage = 12;
                stepStarted = Time.realtimeSinceStartup;
                return;
            }

            case 12:
            {
                if (InStep < 0.5f) return;
                GameObject ask = FindByName("EndRoundConfirm");
                string words = ask != null ? string.Join(" | ", ask.GetComponentsInChildren<Text>(false).Select(t => t.text)) : "";
                Check(ask != null && ask.activeInHierarchy && words.Contains("Single Player"), $"it asks first, saying it goes back to Single Player ('{words}')");
                FindByName("EndRoundYes")?.GetComponent<Button>()?.onClick.Invoke();
                singlePlayStage = 13;
                stepStarted = Time.realtimeSinceStartup;
                return;
            }

            case 13:
            {
                if (InStep < 1f) return;
                Text title = FindByName("SetupTitle")?.GetComponent<Text>();
                Check(!gmm.RoundActive && PlaySetupUI.IsOpen && title != null && title.text == "Single Player" && FindByName("HomeCanvas") is not { activeInHierarchy: true },
                      $"End round goes back to Single Player's setup, not Home (title '{title?.text}', setup open {PlaySetupUI.IsOpen}, round on {gmm.RoundActive})");
                Shot("single_after_end_round");

                // The phone's Back from there: Home.
                BackKey.Press();
                singlePlayStage = 14;
                stepStarted = Time.realtimeSinceStartup;
                return;
            }

            case 14:
                if (InStep < 1f) return;
                Check(!PlaySetupUI.IsOpen && FindByName("HomeCanvas") is { activeInHierarchy: true }, "and Back from Single Player's setup goes Home");

                // On to Single Play's seeker round.
                gmm.ShowMenu();
                OpenPanelsBeforeRound();
                gmm.BeginRound(true);       // the player seeks, the bots hide
                Object.FindAnyObjectByType<HomeUI>()?.SetGameplayUIVisible(true);   // as the game's Play does
                {
                    // They go straight on to their hiding places, some in this same frame: where each started is kept.
                    IReadOnlyList<Vector3> starts = hide.RoundStarts;
                    string off = OffMarkers(starts.Select((p, i) => ("hider bot " + (i + 1), p)), false);
                    Check(off == "" && starts.Count == GameTuning.FugitiveSlots,
                          $"Single Play: the hider bots start on the room's HiderSpawn markers, as online ({starts.Count} placed{(off == "" ? "" : "; " + off)})");
                    off = OffMarkers(new[] { PlayerRef.Active }, true);
                    Check(off == "", $"Single Play: the seeking player starts on a HunterSpawn marker ({off})");
                    CheckPanelsClosedAtRound();
                }
                singlePlayStage = 3;
                stepStarted = Time.realtimeSinceStartup;
                return;



            case 3:
                if (InStep < 3f) return;
                Check(hide.BotCount > 0 && hide.Active, $"Single Play: the hider bots took their places ({hide.BotCount})");
                Check(Mathf.Approximately(seek.walkSpeed, 9f * 1.25f) && Mathf.Approximately(hide.walkSpeed, 12f * 1.25f),
                      $"bots move a quarter faster (seekers {seek.walkSpeed}, hiders {hide.walkSpeed}; were 9 and 12)");
                {
                    // +30s (the Time's Up popup's button) costs nothing now.
                    FieldInfo seekTimer = typeof(BotHideController).GetField("seekTimer", Any);
                    float before = (float)seekTimer.GetValue(hide);
                    typeof(BotHideController).GetMethod("AddSeekTime", Any).Invoke(hide, null);
                    float after = (float)seekTimer.GetValue(hide);
                    Check(after - before > 29f, $"+30s is free: 30 more seconds to seek ({before:F0} -> {after:F0})");
                }
                Check(WallsOpen(), "Single Play: a seeker can use the walls");
                Check(PaintboxHud.InRound, "Single Play's seeker round has the match HUD (the menu button, the minimap)");
                CheckMinimap("Single Play seeker");
                Check(FallGuard.Rescues == 0, $"Single Play: nobody fell out of the room ({FallGuard.Rescues} times put back)");
                if (!FreeCameraFromHigh(stage: 0)) { singlePlayStage = 4; stepStarted = Time.realtimeSinceStartup; return; }
                gmm.ShowMenu();
                Check(!WallsOpen(), "and nobody can from the Home screen");
                Go(Step.Done);
                return;
        }
    }

    // The free camera with the player up high (on the tallest thing in the room): it must be able to
    // go down to near the floor. Stage 0 puts the player up there and starts panning down; later
    // calls keep panning, then judge. False while it is still going.
    private static float freeCamFloor, freeCamPerch;

    private static bool FreeCameraFromHigh(int stage)
    {
        CameraController cam = Object.FindAnyObjectByType<CameraController>();
        Transform body = PlayerRef.Active;
        LevelManager levels = LevelManager.Instance;
        if (cam == null || body == null || levels == null || levels.CurrentRoomInstance == null) return true;

        if (stage == 0)
        {
            // The highest surface a player could stand on, at least 4 above the floor under the player.
            if (!Physics.Raycast(body.position + Vector3.up, Vector3.down, out RaycastHit floorHit, 50f)) return true;
            freeCamFloor = floorHit.point.y;
            Collider perch = levels.CurrentRoomInstance.GetComponentsInChildren<Collider>()
                .Where(c => !c.isTrigger && c.bounds.max.y > freeCamFloor + 4f && c.bounds.max.y < freeCamFloor + 25f
                            && c.bounds.size.x > 1.5f && c.bounds.size.z > 1.5f && c.bounds.size.y < 25f)
                .OrderByDescending(c => c.bounds.max.y).FirstOrDefault();
            if (perch == null) { report.Add("INFO  no high place in this room for the free camera test"); return true; }

            freeCamPerch = perch.bounds.max.y;
            CharacterController cc = body.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            body.position = new Vector3(perch.bounds.center.x, freeCamPerch + 0.1f, perch.bounds.center.z);
            if (cc != null) cc.enabled = true;

            cam.SetFreeLook(false);
            cam.SetFreeLook(true);
            SetPitch(cam, 80f);          // looking down
            freeCamPhase = 0;
            freeCamMinY = float.MaxValue;
            freeCamMaxY = float.MinValue;
            return false;
        }

        // The real joystick flies it (JoystickMover feeds PanFree every frame).
        Vector3 at = cam.FreePosition;
        float floorY = (float)typeof(CameraController).GetField("_scoutFloorY", Any).GetValue(cam) + cam.scoutFloorMargin;
        float roofY = (float)typeof(CameraController).GetField("_scoutRoofY", Any).GetValue(cam) - cam.scoutCeilMargin;
        // A map with camera walls (Room's CameraBounds) holds it by its floor and ceiling walls instead.
        if (CameraBounds.HasFloor) floorY = CameraBounds.Move(at, at + Vector3.down * 500f, cam.freeWallDistance).y;
        if (CameraBounds.HasCeiling) roofY = CameraBounds.Move(at, at + Vector3.up * 500f, cam.freeWallDistance).y;
        freeCamMinY = Mathf.Min(freeCamMinY, at.y);
        freeCamMaxY = Mathf.Max(freeCamMaxY, at.y);
        float pitchNow = (float)typeof(CameraController).GetField("pitch", Any).GetValue(cam);
        Vector3 viewForward = Quaternion.Euler(pitchNow, cam.yaw, 0f) * Vector3.forward;
        Vector3 levelRight = Quaternion.Euler(0f, cam.yaw, 0f) * Vector3.right;

        switch (freeCamPhase)
        {
            case 0:     // looking down, stick forward: down from the perch to the floor
                PushStick(Vector2.up);
                if (InStep < 4f) return false;
                float floorSeen = (float)typeof(CameraController).GetField("_scoutFloorY", Any).GetValue(cam);
                Check(floorSeen < freeCamPerch - 3f && at.y < freeCamPerch - 3f,
                      $"the free camera goes down from a high place (perch {freeCamPerch:F1}, floor {freeCamFloor:F1}; it took the floor as {floorSeen:F1} and got down to {at.y:F1})");
                Shot("free_cam_down");
                // Then level, a little above the floor, for the direction checks.
                SetPitch(cam, 0f);
                typeof(CameraController).GetField("_freePivot", Any).SetValue(cam, new Vector3(at.x, Mathf.Min(floorY + 8f, roofY - 1f), at.z));
                PushStick(Vector2.zero);
                freeCamPhase = 1;
                freeCamAt = InStep + 0.6f;
                return false;

            case 1:     // settle, then forward while looking level
                if (InStep < freeCamAt) return false;
                freeCamFrom = at;
                freeCamOpen = at;
                PushStick(Vector2.up);
                freeCamPhase = 2;
                freeCamAt = InStep + 1.2f;
                return false;

            case 2:
                if (InStep < freeCamAt) return false;
                {
                    Vector3 moved = at - freeCamFrom;
                    Vector3 flatForward = Vector3.ProjectOnPlane(viewForward, Vector3.up).normalized;
                    Check(moved.magnitude > 5f && Vector3.Dot(moved.normalized, flatForward) > 0.95f && Mathf.Abs(moved.y) < 0.1f * moved.magnitude,
                          $"looking level, forward flies straight ahead, not up or down ({moved.magnitude:F1} units, {Vector3.Dot(moved.normalized, flatForward):F2} along the view, rise {moved.y:F2})");
                }
                PushStick(Vector2.zero);
                freeCamPhase = 3;
                freeCamAt = InStep + 0.6f;
                return false;

            case 3:     // sideways
                if (InStep < freeCamAt) return false;
                freeCamFrom = at;
                PushStick(Vector2.right);
                freeCamPhase = 4;
                freeCamAt = InStep + 1.2f;
                return false;

            case 4:
                if (InStep < freeCamAt) return false;
                {
                    Vector3 moved = at - freeCamFrom;
                    Check(moved.magnitude > 5f && Vector3.Dot(moved.normalized, levelRight) > 0.95f,
                          $"right flies sideways to the view ({moved.magnitude:F1} units, {Vector3.Dot(moved.normalized, levelRight):F2} along its right)");
                }
                PushStick(Vector2.zero);
                // Back to where it started (open space: forward and right flew freely from there),
                // so a camera wall the last two flights ended near does not turn this one aside.
                typeof(CameraController).GetField("_freePivot", Any).SetValue(cam, freeCamOpen);
                SetPitch(cam, -40f);     // looking up
                freeCamPhase = 5;
                freeCamAt = InStep + 0.6f;
                return false;

            case 5:
                if (InStep < freeCamAt) return false;
                freeCamFrom = at;
                PushStick(Vector2.up);
                freeCamPhase = 6;
                freeCamAt = InStep + 0.8f;
                return false;

            case 6:
                if (InStep < freeCamAt) return false;
                {
                    Vector3 moved = at - freeCamFrom;
                    Check(moved.y > 3f && Vector3.Dot(moved.normalized, viewForward) > 0.9f,
                          $"looking up, forward flies up along the view ({moved.magnitude:F1} units, rise {moved.y:F1}, {Vector3.Dot(moved.normalized, viewForward):F2} along the view)");
                }
                freeCamPhase = 7;
                freeCamAt = InStep + 4f;     // on up, into the roof
                return false;

            case 7:
                if (InStep < freeCamAt) return false;
                PushStick(Vector2.zero);
                Check(freeCamMinY >= floorY - 0.01f && freeCamMaxY <= roofY + 0.01f && freeCamMaxY > roofY - 1f,
                      $"and it never leaves the room: between the floor and the roof ({freeCamMinY:F1} to {freeCamMaxY:F1}; allowed {floorY:F1} to {roofY:F1})");
                Shot("free_cam_up");
                cam.SetFreeLook(false);
                return true;
        }
        return true;
    }

    private static int freeCamPhase;
    private static float freeCamAt, freeCamMinY, freeCamMaxY;
    private static Vector3 freeCamFrom, freeCamOpen;

    private static void SetPitch(CameraController cam, float degrees) =>
        typeof(CameraController).GetField("pitch", Any)?.SetValue(cam, degrees);

    // The on-screen joystick held in one direction, as a thumb holds it.
    private static void PushStick(Vector2 direction)
    {
        JoystickMover mover = Object.FindAnyObjectByType<JoystickMover>();
        Joystick stick = mover != null ? Get<Joystick>(mover, "joystick") : null;
        if (stick != null) typeof(Joystick).GetField("input", Any).SetValue(stick, direction);
    }

    // -playtestAgain: ask for another match after the first (the path every returning player takes).
    private static bool Again => System.Environment.GetCommandLineArgs().Contains("-playtestAgain");
    private static int matchesFound, matchesAtAgain;

    private static bool signedIn, bought, boughtAnswered, submittedName, reboughtAnswered, posesAnswered;
    private static long mapCoinsBefore = -1;
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
        foreach (string boxName in new[] { "HidersCard", "SeekersCard" })   // design B's team cards
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
    private static string worstGapWhen = "";
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

        if (!hiderBotsMeasured) MeasureHiderBots();
    }

    // Where Single Play's hide logic put the hider bots this game runs, and whether they took on paint.
    private static void MeasureHiderBots()
    {
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
            CheckShoesInRound();
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
            if (now > walkedSince && gap > worstServerGap)
            {
                // What was going on at the worst moment, to tell a slow frame from a climb or a lag.
                WallClimber climber = local.GetComponentInChildren<WallClimber>();
                CharacterController body = local.GetComponent<CharacterController>();
                Vector3 d = local.FeetPosition - local.LastServerFeet;
                worstGapWhen = $"{Net.Phase}, frame {Time.unscaledDeltaTime * 1000f:F0} ms, across {new Vector2(d.x, d.z).magnitude:F1}, up {d.y:F1}, " +
                               $"on a wall {climber != null && climber.IsOnWall}, grounded {body != null && body.isGrounded}, ping {Net.PingMs} ms";
            }
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
                if (hit.point.y > feet.y + 1f) continue;    // a tabletop over a hider, not the floor under them
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

    // -playtestMap: which of the map picker's rooms to play (0 = the first, Room).
    // -playtestLan: the game is given an address with no server (TEST-NET-1, never routed).
    private static bool Lan => System.Environment.GetCommandLineArgs().Contains("-playtestLan");
    private const string NoServerHere = "192.0.2.1";
    private static bool lanChecked;

    private static int MapIndex => int.Parse(System.Environment.GetCommandLineArgs().SkipWhile(a => a != "-playtestMap").Skip(1).FirstOrDefault() ?? "0");

    private static bool playAreaChecked;

    // The map is played as it is built: its prefab's own scale, nothing fenced off.
    private static void CheckPlayArea()
    {
        if (playAreaChecked || Net.Phase != MatchPhase.Hide || PlayerRef.Active == null) return;
        playAreaChecked = true;

        GameObject room = RoomSetup.Room;
        GameObject prefab = room != null ? Resources.Load<GameObject>("Rooms/" + room.name) : null;
        bool fenced = room != null && room.GetComponentsInChildren<Transform>(true).Any(t => t.name == "PlayAreaFence");
        Check(room != null && prefab != null && room.transform.localScale == prefab.transform.localScale && !fenced,
              $"the map is played as built ({(room != null ? room.name : "none")}: scale {(room != null ? room.transform.localScale.x : 0f):F2}, prefab {(prefab != null ? prefab.transform.localScale.x : 0f):F2}, fenced {fenced})");
    }

    // Each body stands on a spawn marker of that side, as online places them: "" when they all do,
    // else which ones are how far from the nearest.
    private static string OffMarkers(IEnumerable<Transform> bodies, bool seeker) =>
        OffMarkers(bodies.Select(b => b == null ? ("a missing body", new Vector3(float.NaN, 0f, float.NaN)) : (b.name, b.position)), seeker);

    private static string OffMarkers(IEnumerable<(string Name, Vector3 At)> bodies, bool seeker)
    {
        GameObject room = RoomSetup.Room;
        string prefix = seeker ? "HunterSpawn" : "HiderSpawn";
        List<Vector3> markers = room == null ? new List<Vector3>()
            : room.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith(prefix, StringComparison.Ordinal)).Select(t => t.position).ToList();
        if (markers.Count == 0) return "no " + prefix + " markers in the room";

        var far = new List<string>();
        foreach ((string name, Vector3 position) in bodies)
        {
            if (float.IsNaN(position.x)) { far.Add(name); continue; }
            Vector2 at = new Vector2(position.x, position.z);
            float nearest = markers.Min(m => Vector2.Distance(new Vector2(m.x, m.z), at));
            if (nearest > 2f) far.Add($"{name} {nearest:F1} units off");
        }
        return string.Join(", ", far);
    }

    // Multiple Play's character choice: the play setup's character cards. A character this account
    // does not own online is refused there (the setup says so); the one in use stays chosen.
    private static void PickBodyOnline()
    {
        GameObject setup = FindByName("PlaySetup");
        CharacterSelect characters = Object.FindAnyObjectByType<CharacterSelect>();
        if (setup == null || characters == null)
        {
            report.Add("INFO  no setup or characters in the scene: straight to the rooms");
            return;
        }
        Check(setup.activeInHierarchy, "the online character choice is on the play setup");
        Button Card(int i) => SetupButton("Character_" + i);

        int inUse = characters.CurrentIndex;
        int count = characters.characterPrefabs.Length;
        int locked = -1;
        if (profile.OwnedAvatars != null && profile.OwnedAvatars.Length > 0)
            for (int i = 0; i < count && locked < 0; i++)
                if (Array.IndexOf(profile.OwnedAvatars, i) < 0) locked = i;

        if (SwitchBody && switchedTo < 0)
        {
            // The other character, which this account now owns: a tap swaps the body in place.
            switchedTo = 1 - inUse;
            sceneMarker = Object.FindAnyObjectByType<HomeUI>(FindObjectsInactive.Include);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Card(switchedTo)?.onClick.Invoke();
            report.Add($"INFO  picked character {switchedTo} (was {inUse}): body swapped in {watch.Elapsed.TotalMilliseconds:0.0} ms");
            return;
        }

        if (locked >= 0)
        {
            Card(locked)?.onClick.Invoke();
            Text summary = FindByName("SetupSummary")?.GetComponent<Text>();
            Check(characters.CurrentIndex == inUse && summary != null && summary.text.Contains(L.T("Not unlocked online. Get it in the Shop.")),
                  $"a character not owned online is refused ('{summary?.text}')");
        }
        else report.Add("INFO  this account owns every character online: no locked one to try");

        bool inUseOwned = profile.OwnedAvatars == null || profile.OwnedAvatars.Length == 0 || Array.IndexOf(profile.OwnedAvatars, inUse) >= 0;
        if (inUseOwned)
        {
            Card(inUse)?.onClick.Invoke();   // the character already in use: nothing to swap
            Check(characters.CurrentIndex == inUse, $"the character in use ({inUse}) stays chosen");
        }
        else
        {
            // Picked earlier for Single Play, not owned online: the setup says so, and an owned one is picked.
            string note = FindByName("SetupSummary")?.GetComponent<Text>()?.text ?? "";
            Check(note.Contains(L.T("Not unlocked online. Pick another character, or get it in the Shop.")) || note.Contains(L.T("Not unlocked online. Get it in the Shop.")),
                  $"the setup says the character in use ({inUse}) isn't unlocked online ('{note}')");
            int owned = profile.OwnedAvatars[0];
            Card(owned)?.onClick.Invoke();
            Check(characters.CurrentIndex == owned, $"an owned character ({owned}) is picked instead");
        }
    }

    private static bool mapMustBuy;

    // A button of the play setup (its panel, tabs, cards or Play), by object name.
    private static Button SetupButton(string name)
    {
        GameObject canvas = FindByName("PlaySetupCanvas");
        return canvas != null ? canvas.GetComponentsInChildren<Button>(true).FirstOrDefault(b => b.name == name) : null;
    }

    // The coin balance over Multiple Play's screens (OnlineMatches' copy of Home's pill).
    // Design B's balance pill (2026-11-24): its label is the one bound to the server's balance.
    // The balance in the play setup's own top bar (design B: the shared pill is not over the setup).
    private static Text SetupCoinLabel() =>
        FindByName("PlaySetup") is GameObject setup ? setup.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.GetComponent<CoinLabel>() != null) : null;

    private static Text GlobalCoinLabel() =>
        FindByName("GlobalCoinPill") is GameObject pill ? pill.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.GetComponent<CoinLabel>() != null) : null;

    private static int languageStage;
    private static Text languageLabel;

    // Settings' language row: the screen in Chinese, then Russian, each in a font that has its
    // letters, then English again. True when done.
    private static bool CheckLanguages()
    {
        SettingsUI settings = Object.FindAnyObjectByType<SettingsUI>(FindObjectsInactive.Include);
        MethodInfo step = typeof(SettingsUI).GetMethod("StepLanguage", Any);
        if (settings == null || step == null)
        {
            report.Add("INFO  no Settings screen in the scene: languages not tried");
            return true;
        }

        switch (languageStage)
        {
            case 0:
                L.Set(GameLanguage.English);
                settings.Show();
                languageLabel = FindByName("LblLang")?.GetComponent<Text>();
                Check(languageLabel != null && languageLabel.gameObject.activeInHierarchy, "Settings has a Language row");
                if (languageLabel == null) return true;
                step.Invoke(settings, new object[] { 1 });     // its right arrow
                languageStage = 1;
                stepStarted = Time.realtimeSinceStartup;
                return false;

            case 1:
            case 2:
            {
                if (InStep < 1f) return false;
                GameLanguage expected = languageStage == 1 ? GameLanguage.Chinese : GameLanguage.Russian;
                string text = languageLabel.text;
                bool script = expected == GameLanguage.Chinese ? text.Any(c => c >= 0x4E00 && c <= 0x9FFF) : text.Any(c => c >= 0x0400 && c <= 0x04FF);
                bool drawable = languageLabel.font != null && (languageLabel.font == UiTranslator.WideFont
                                || text.All(c => char.IsWhiteSpace(c) || languageLabel.font.HasCharacter(c)));
                Check(L.Current == expected && script && drawable,
                      $"the right arrow switches to {expected}: Settings reads '{text}' in {languageLabel.font?.name}");
                Text other = FindByName("SettingsCanvas")?.GetComponentsInChildren<Text>(true)
                    .FirstOrDefault(t => t != languageLabel && t.gameObject.activeInHierarchy && t.text.Length > 0
                                         && (expected == GameLanguage.Chinese ? t.text.Any(c => c >= 0x4E00 && c <= 0x9FFF) : t.text.Any(c => c >= 0x0400 && c <= 0x04FF)));
                if (other != null) report.Add($"INFO  another label in {expected}: '{other.text}'");
                step.Invoke(settings, new object[] { 1 });
                languageStage++;
                stepStarted = Time.realtimeSinceStartup;
                return false;
            }

            case 3:
                if (InStep < 1f) return false;
                Check(L.Current == GameLanguage.English && languageLabel.text == "Language",
                      $"and on round to English again ('{languageLabel.text}')");
                settings.Hide();
                return true;
        }
        return true;
    }

    private static bool panelsOpened;

    // A panel left open in one round must not be open in the next (RoundUiReset).
    private static void OpenPanelsBeforeRound()
    {
        PoseSelectorUI poses = Object.FindAnyObjectByType<PoseSelectorUI>(FindObjectsInactive.Include);
        Mode1FirstPersonGun gun = Object.FindAnyObjectByType<Mode1FirstPersonGun>(FindObjectsInactive.Include);
        if (poses != null) { poses.Open(); panelsOpened = true; }
        if (gun != null && !gun.IsProne) { gun.ToggleProne(); panelsOpened = true; }
    }

    private static void CheckPanelsClosedAtRound()
    {
        if (!panelsOpened) { report.Add("INFO  no pose panel or lie-down button to leave open"); return; }
        PoseSelectorUI poses = Object.FindAnyObjectByType<PoseSelectorUI>(FindObjectsInactive.Include);
        Mode1FirstPersonGun gun = Object.FindAnyObjectByType<Mode1FirstPersonGun>(FindObjectsInactive.Include);
        GameObject panel = poses != null ? typeof(PoseSelectorUI).GetField("panelRoot", Any)?.GetValue(poses) as GameObject : null;
        bool poseOpen = panel != null && panel.activeSelf;
        Check(!poseOpen && (gun == null || !gun.IsProne), $"a new round starts with the pose panel shut and standing (pose panel open: {poseOpen}, lying: {gun != null && gun.IsProne})");
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
        if (freeLookStage == 0 && OnlineRoundInstance() is not { IsSettled: true }) return;   // a rejoin still loading

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

    // ================= Exit: out of the match, to the room list =================

    private static int exitStage;
    private static float exitAt;

    private static void CheckExit()
    {
        if (!ExitTest || exitStage >= 9) return;
        if (exitStage == 0 && Net.Phase != MatchPhase.Hide) return;   // begun in the hide; then to the end whatever the phase
        float now = Time.realtimeSinceStartup;
        if (exitStage == 0) { exitAt = now + 3f; exitStage = 1; return; }
        if (now < exitAt) return;

        Button exit = FindByName("ExitMatch")?.GetComponent<Button>();
        GameObject confirm = FindByName("ExitMatchConfirm");
        switch (exitStage)
        {
            case 1:
                Check(MenuButton() is { } menuButton && menuButton.gameObject.activeInHierarchy && exit is not { gameObject: { activeInHierarchy: true } },
                      "the round has its menu button (top right), and no Leave button of its own while it is played");
                Shot("exit_button");
                Check(OpenRoundMenu() != null, "a tap reaches it");
                exitAt = now + 0.5f;
                exitStage = 6;
                return;
            case 6:
            {
                GameObject menu = FindByName("RoundMenu");
                string items = menu != null ? string.Join(" | ", menu.GetComponentsInChildren<Text>(false).Select(t => t.text)) : "none";
                Text balance = FindByName("MenuBalance")?.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.GetComponent<CoinLabel>() != null);
                Check(menu != null && menu.activeInHierarchy && items.Contains("Resume") && items.Contains("Settings") && items.Contains("Shop") && items.Contains("Leave match"),
                      $"it opens the round menu ({items})");
                Check(balance != null && balance.text == CoinBalance.Text, $"with the coin balance beside the Shop ({(balance != null ? balance.text : "none")})");
                Shot("round_menu");
                MenuItem("MenuLeave")?.onClick.Invoke();
                exitAt = now + 0.5f;
                exitStage = 2;
                return;
            }
            case 2:
                string text = confirm != null ? string.Join(" | ", confirm.GetComponentsInChildren<Text>(true).Select(t => t.text)) : "none";
                Check(confirm != null && confirm.activeInHierarchy && text.Contains("Leave this match?") && (text.Contains("room list") || text.Contains("rejoin it")),
                      $"it asks first, saying what leaving costs ({text})");
                Shot("exit_confirm");
                FindButton("ExitMatchConfirm", "STAY")?.onClick.Invoke();
                exitAt = now + 0.5f;
                exitStage = 3;
                return;
            case 3:
                Check(confirm != null && !confirm.activeInHierarchy && OnlineRound.Active, "STAY keeps playing");
                OpenRoundMenu();
                exitAt = now + 0.5f;
                exitStage = 7;
                return;
            case 7:
                MenuItem("MenuLeave")?.onClick.Invoke();
                exitAt = now + 0.5f;
                exitStage = 4;
                return;
            case 4:
                FindButton("ExitMatchConfirm", "LEAVE")?.onClick.Invoke();
                exitAt = now + 2f;
                exitStage = 5;
                return;
            case 5:
                Check(!OnlineRound.Active && FindByName("PlaySetup") is { activeInHierarchy: true } && !Net.InMatch && Net.IsConnected,
                      "LEAVE goes back to the play setup, out of the match, still signed in, the game still running");
                Shot("exit_rooms");
                exitStage = 9;
                Go(Step.Exited);
                return;
        }
    }

    // ================= -playtestOutRejoin: out, Exit, Rejoin: the body lies where it fell =================

    private static int outStage;
    private static float outAt;
    private static Vector3 outFellAt;
    private static readonly FieldInfo LocalBodyField = typeof(OnlineRound).GetField("localBody", Any);

    private static Transform LocalDeadBody() => LocalBodyField?.GetValue(OnlineRoundInstance()) as Transform;

    // The body as drawn: its renderers' box (the gun's too, held in its hand).
    private static bool DrawnBounds(Transform body, out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        if (body == null) return false;
        foreach (Renderer r in body.GetComponentsInChildren<Renderer>(false))
        {
            if (!r.enabled || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
            if (!any) { bounds = r.bounds; any = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return any;
    }

    // What the body rests on: the highest surface under its middle, not the body itself or anyone else.
    private static bool SurfaceUnder(Transform body, Bounds drawn, out float y)
    {
        y = float.NegativeInfinity;
        Vector3 from = new Vector3(drawn.center.x, drawn.max.y + 2f, drawn.center.z);
        foreach (RaycastHit hit in Physics.RaycastAll(from, Vector3.down, drawn.size.y + 60f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.transform.IsChildOf(body)) continue;
            if (hit.collider.GetComponentInParent<NetEntityView>() != null || hit.collider.GetComponentInParent<Ragdoll>() != null) continue;
            if (hit.point.y > drawn.center.y) continue;   // a ceiling, a table top over the body
            y = Mathf.Max(y, hit.point.y);
        }
        return !float.IsNegativeInfinity(y);
    }

    private static float BodyHeight(Transform body)
    {
        CharacterController cc = body != null ? body.GetComponent<CharacterController>() : null;
        return cc != null ? cc.height * body.lossyScale.y : 2f * (body != null ? body.lossyScale.y : 1f);
    }

    private static void CheckOutRejoin()
    {
        if (!OutRejoinTest || outStage >= 9) return;
        float now = Time.realtimeSinceStartup;
        NetEntityView me = NetEntityRegistry.Get(Net.LocalSlot);

        switch (outStage)
        {
            case 0:     // out: a few seconds for the body to come to rest
                if (me == null || !me.Eliminated || OnlineRoundInstance() is not { IsSettled: true }) return;
                outAt = now + 3f;
                outStage = 1;
                return;

            case 1:
            {
                if (now < outAt) return;
                Transform body = LocalDeadBody();
                if (!DrawnBounds(body, out Bounds drawn)) { Check(false, "the hider who is out has a body drawn"); outStage = 9; return; }
                outFellAt = drawn.center;
                float floor = SurfaceUnder(body, drawn, out float y) ? y : float.NaN;
                report.Add($"INFO  out: the body rests at {drawn.center} (lowest {drawn.min.y:F2}, under it {floor:F2}, body height {BodyHeight(body):F2}); "
                           + $"the server has the feet at {(me != null ? me.LastServerFeet.ToString() : "?")}, the knock-back carried it {(me != null ? new Vector2(drawn.center.x - me.LastServerFeet.x, drawn.center.z - me.LastServerFeet.z).magnitude : -1f):F1}");
                Shot("out_before_exit");
                awayMatch = (int)typeof(OnlineRound).GetField("matchId", Any).GetValue(OnlineRoundInstance());
                OpenRoundMenu();
                outAt = now + 0.5f;
                outStage = 2;
                return;
            }

            case 2:
                if (now < outAt) return;
                MenuItem("MenuLeave")?.onClick.Invoke();
                outAt = now + 0.5f;
                outStage = 3;
                return;

            case 3:
                if (now < outAt) return;
                FindButton("ExitMatchConfirm", "LEAVE")?.onClick.Invoke();
                awayAt = now + 3f;
                awayStage = 4;      // the matches list and REJOIN, as -playtestAway does
                outStage = 4;
                Go(Step.Away);
                return;

            case 4:     // back in the round
                if (!OnlineRound.Active || OnlineRoundInstance() is not { IsSettled: true }) return;
                Check(me != null && me.Eliminated, "after the rejoin the server still has this hider out");
                outAt = now + 3f;
                outStage = 5;
                return;

            case 5:
            {
                if (now < outAt) return;
                Transform body = LocalDeadBody();
                Ragdoll ragdoll = body != null ? body.GetComponent<Ragdoll>() : null;
                Check(ragdoll != null && ragdoll.IsRagdolling, "after the rejoin the hider who is out lies down (ragdoll), not standing");
                if (!DrawnBounds(body, out Bounds drawn)) { Check(false, "and has a body drawn"); outStage = 9; return; }
                float h = BodyHeight(body);
                bool surface = SurfaceUnder(body, drawn, out float floor);
                float gap = surface ? drawn.min.y - floor : float.NaN;
                Check(surface && gap > -0.25f * h && gap < 0.25f * h,
                      $"and lies on the floor: not floating, not in it (lowest {drawn.min.y:F2}, floor {floor:F2}, gap {gap:F2} for a body {h:F2} tall)");
                report.Add($"INFO  rejoined: the body at {drawn.center}, the server's feet {(me != null ? me.LastServerFeet.ToString() : "?")}, kept on this device '{PlayerPrefs.GetString("OnlineRound.OutRest", "")}'");
                Vector2 off = new Vector2(drawn.center.x - outFellAt.x, drawn.center.z - outFellAt.z);
                Check(off.magnitude < 1.5f * h, $"where it fell ({off.magnitude:F2} from where it lay before the Exit, body {h:F2} tall)");
                outFellAt = drawn.center;
                Shot("out_rejoined");
                outAt = now + 2f;
                outStage = 6;
                return;
            }

            case 6:
            {
                if (now < outAt) return;
                Transform body = LocalDeadBody();
                if (DrawnBounds(body, out Bounds drawn))
                {
                    float moved = Vector3.Distance(drawn.center, outFellAt);
                    Check(moved < 0.1f * BodyHeight(body), $"and stays there: not falling or sliding ({moved:F3} in 2 s)");
                }
                outStage = 9;
                return;
            }
        }
    }

    // ================= Away: Exit, the matches list, Rejoin, results =================

    private static void CheckAway()
    {
        if (!(AwayTest || AwayEndTest) || awayStage >= 9) return;
        float now = Time.realtimeSinceStartup;
        OnlineRound round = OnlineRoundInstance();

        switch (awayStage)
        {
            case 0:     // a few seconds into the hide: the HUD's Shop and coin balance
                if (Net.Phase != MatchPhase.Hide) return;
                if (awayAt == 0f) { awayAt = now + 3f; return; }
                if (now < awayAt) return;
                awayMatch = (int)typeof(OnlineRound).GetField("matchId", Any).GetValue(round);
                Check(FindByName("GlobalCoinPill") is not { activeInHierarchy: true }, "in the round the coin pill is not over the HUD (the balance is in the round menu)");
                Shot("hud_shop");
                if (!Seeker) Object.FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include)?.OpenDrawing();   // painting when the shop opens
                Check(OpenRoundMenu() != null, "the menu button takes a tap (painting or not)");
                awayAt = now + 0.6f;
                awayStage = 5;
                return;

            case 5:
            {
                if (now < awayAt) return;
                Button shop = MenuItem("MenuShop");
                Text balance = FindByName("MenuBalance")?.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.GetComponent<CoinLabel>() != null);
                Check(shop != null && balance != null && balance.text == CoinBalance.Text,
                      $"the round menu has the Shop and the balance ({(balance != null ? balance.text : "none")})");
                Shot("round_menu");
                if (shop != null) shop.onClick.Invoke();
                awayAt = now + 1.5f;
                awayStage = 1;
                return;
            }

            case 1:
                if (now < awayAt) return;
                Canvas shopCanvas = FindByName("ShopScreenCanvas")?.GetComponent<Canvas>();
                Check(ShopScreen.IsOpen && shopCanvas != null && shopCanvas.sortingOrder > 39,
                      $"it opens the shop over the round (canvas order {(shopCanvas != null ? shopCanvas.sortingOrder : -1)})");
                if (!Seeker)
                    Check(Object.FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include) is { DrawingActive: false },
                          "the paint panel, open when the shop opened, is put away (it would cover the shop's tabs)");
                Shot("shop_in_round");
                ShopScreen.CloseIfOpen();
                Check(FindByName("HomeCanvas") is not { activeInHierarchy: true } && OnlineRound.Active, "closing it goes back to the round, not Home");

                // The paint panel above the round's Exit, ready list and notifications.
                ChameleonPaint paint = Object.FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include);
                if (!Seeker && paint != null)
                {
                    paint.OpenDrawing();
                    Canvas panel = FindByName("PickerPanel")?.GetComponent<Canvas>();
                    Canvas roundCanvas = FindByName("OnlineRoundCanvas")?.GetComponent<Canvas>();
                    Canvas overlay = FindByName("OnlineMatchesOverlay")?.GetComponent<Canvas>();
                    Check(panel != null && roundCanvas != null && overlay != null && panel.sortingOrder > roundCanvas.sortingOrder && panel.sortingOrder > overlay.sortingOrder,
                          $"the paint panel draws above the round's buttons and notifications ({(panel != null ? panel.sortingOrder : -1)} over {(roundCanvas != null ? roundCanvas.sortingOrder : -1)}, {(overlay != null ? overlay.sortingOrder : -1)})");
                    awayAt = now + 1f;      // the panel slides in: shoot it once it is in place
                    awayStage = 7;      // below 9: CheckAway stops at 9
                    return;
                }
                awayAt = now + 1f;
                awayStage = 2;
                return;

            case 7:
                if (now < awayAt) return;
                Shot("paint_on_top");
                Object.FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include)?.CloseDrawingNow();
                awayAt = now + 1f;
                awayStage = 2;
                return;

            case 2:     // Exit: the match goes on, the seat is kept
                if (now < awayAt) return;
                // Hider bots already placed need no host, so they are not this game's to run after it
                // comes back: they are looked at before it leaves.
                if (!hiderBotsMeasured && Object.FindAnyObjectByType<BotHideController>() is { OnlineHidingDone: true }) MeasureHiderBots();
                OpenRoundMenu();
                awayAt = now + 0.5f;
                awayStage = 6;
                return;

            case 6:
                if (now < awayAt) return;
                MenuItem("MenuLeave")?.onClick.Invoke();
                awayAt = now + 0.5f;
                awayStage = 3;
                return;

            case 3:
                if (now < awayAt) return;
                FindButton("ExitMatchConfirm", "LEAVE")?.onClick.Invoke();
                awayAt = now + 3f;
                awayStage = 4;
                Go(Step.Away);
                return;
        }
    }

    // The rest happens off the round (Step.Away): the list, rejoining, or the match ending meanwhile.
    private static void PlayAway()
    {
        float now = Time.realtimeSinceStartup;
        switch (awayStage)
        {
            case 4:
                if (now < awayAt) return;
                if (OnlineMatches.AwayCount == 0 && now < awayAt + 8f) return;   // the list of matches comes from the server a moment later
                MyMatchEntry kept = Net.MyMatches.FirstOrDefault(m => m.MatchId == (uint)awayMatch);
                Check(!OnlineRound.Active && FindByName("PlaySetup") is { activeInHierarchy: true } && kept.State == MyMatchState.Away && kept.SecondsLeft > 0,
                      $"after Exit the match goes on and is listed as in progress ({kept.State}, {kept.Phase}, {kept.SecondsLeft} s left)");
                Check(FindByName("LoadingCanvas") is not { activeInHierarchy: true }, "no loading screen on the way out");
                Check(Object.FindObjectsByType<MatchesBadge>(FindObjectsInactive.Exclude).Any(b => b.Count != null && b.Count.enabled && b.Count.text == "1"),
                      "the Matches tab's badge says a match is in progress");
                {
                    // Notices sit top right, under the coin balance: never the middle of the screen.
                    var notices = Object.FindObjectsByType<Button>(FindObjectsInactive.Exclude).Where(b => b.name == "Notice").ToList();
                    bool placed = notices.Count > 0 && notices.All(b =>
                    {
                        // In the canvas's own space (in batch mode Screen is not the picture's size).
                        var canvasRect = (RectTransform)b.GetComponentInParent<Canvas>().rootCanvas.transform;
                        var corners = new Vector3[4];
                        ((RectTransform)b.transform).GetWorldCorners(corners);
                        Vector3 low = canvasRect.InverseTransformPoint(corners[0]);
                        Rect r = canvasRect.rect;
                        return low.x > r.center.x && low.y > r.center.y + r.height * 0.05f;
                    });
                    Check(placed, $"its notice is in the top-right corner, clear of the middle ({notices.Count} shown)");
                }
                Shot("away_rooms");
                if (AwayEndTest)
                {
                    awayAt = now + 120f;
                    awayStage = 20;
                    return;
                }
                if (SwitchMapsTest)
                {
                    awayStage = 30;
                    awayAt = now + 0.5f;
                    return;
                }
                OnlineMatches.ShowList();
                awayAt = now + 1.5f;
                awayStage = 5;
                return;

            // ---- -playtestSwitchMaps ----
            case 30:    // match B: Practice, on another map (and with -playtestSwitchSides the other side)
                if (now < awayAt) return;
                if (System.Environment.GetCommandLineArgs().Contains("-playtestSwitchSides"))
                {
                    SetupButton(Seeker ? "HiderChoice" : "SeekerChoice")?.onClick.Invoke();
                    report.Add($"INFO  the second match is played on the other side ({(Seeker ? "hider" : "seeker")})");
                }
                {
                    Button practice = FindButton("PlaySetup", "Practice");
                    Check(practice != null, "the play setup offers Practice for a second match");
                    practice?.onClick.Invoke();
                }
                awayAt = now + 1f;
                awayStage = 31;
                return;

            case 31:
                if (now < awayAt) return;
                {
                    LevelManager levels = Object.FindAnyObjectByType<LevelManager>();
                    Button mapB = levels != null ? SetupButton("Map_" + levels.RoomNameAt(SwitchMapB)) : null;
                    if (mapB == null) { Check(false, "the setup offers the second match's map"); Go(Step.Done); return; }
                    mapB.onClick.Invoke();
                    SetupButton("SetupPlay")?.onClick.Invoke();
                }
                awayAt = now + 40f;
                awayStage = 32;
                return;

            case 32:    // in match B
                {
                    OnlineRound roundB = OnlineRoundInstance();
                    int idB = OnlineRound.Active ? (int)typeof(OnlineRound).GetField("matchId", Any).GetValue(roundB) : -1;
                    if (!OnlineRound.Active || idB == awayMatch || roundB == null || !roundB.IsSettled)
                    {
                        if (now < awayAt) return;
                        Check(false, $"a second match starts on another map (active {OnlineRound.Active}, id {idB})");
                        Go(Step.Done);
                        return;
                    }
                    switchMatchB = idB;
                    string roomB = LevelManager.Instance.RoomNameAt(SwitchMapB);
                    Check(LevelManager.Instance.CurrentRoomName == roomB, $"the second match ({idB}) is on {roomB} ({LevelManager.Instance.CurrentRoomName})");
                }
                awayAt = now + 5f;
                awayStage = 33;
                return;

            case 33:    // a few seconds walking in B, then Exit
                if (now < awayAt)
                {
                    DriveJoystick(true);
                    return;
                }
                DriveJoystick(false);
                OpenRoundMenu();
                awayAt = now + 0.6f;
                awayStage = 39;
                return;

            case 39:
                if (now < awayAt) return;
                MenuItem("MenuLeave")?.onClick.Invoke();
                awayAt = now + 0.6f;
                awayStage = 34;
                return;

            case 34:
                if (now < awayAt) return;
                FindButton("ExitMatchConfirm", "LEAVE")?.onClick.Invoke();
                awayAt = now + 3f;
                awayStage = 35;
                return;

            case 35:    // both listed; REJOIN the first
                if (now < awayAt) return;
                {
                    Check(!OnlineRound.Active && Net.MyMatches.Count(m => m.State == MyMatchState.Away) >= 2,
                          $"both matches go on and are listed ({string.Join(", ", Net.MyMatches.Select(m => m.MatchId + ":" + m.State + ":" + m.Map))})");
                    OnlineMatches.ShowList();
                }
                awayAt = now + 1.5f;
                awayStage = 36;
                return;

            case 36:
                if (now < awayAt) return;
                {
                    Transform row = FindByName("MyMatchesScreen")?.GetComponentsInChildren<Transform>(false).FirstOrDefault(t => t.name == "Match_" + awayMatch);
                    Button rejoinA = row != null ? row.GetComponentsInChildren<Button>(false).FirstOrDefault(b => b.name == "Rejoin") : null;
                    Check(rejoinA != null, $"the first match ({awayMatch}) can be rejoined from the list");
                    if (rejoinA == null) { Go(Step.Done); return; }
                    rejoinA.onClick.Invoke();
                    rejoinWatchFrom = now;
                    rejoinCoveredAtTap = FindByName("LoadingCanvas") is { activeInHierarchy: true };
                }
                awayAt = now + 20f;
                awayStage = 37;
                return;

            case 37:    // back in A, settled
                {
                    OnlineRound roundA = OnlineRoundInstance();
                    int idA = OnlineRound.Active ? (int)typeof(OnlineRound).GetField("matchId", Any).GetValue(roundA) : -1;
                    if (idA != awayMatch || !roundA.IsSettled)
                    {
                        if (now < awayAt) return;
                        Check(false, $"the first match comes back ({idA})");
                        Go(Step.Done);
                        return;
                    }
                    string roomA = LevelManager.Instance.RoomNameAt(MapIndex);
                    Check(LevelManager.Instance.CurrentRoomName == roomA, $"the first match is on its own map again ({LevelManager.Instance.CurrentRoomName}, expected {roomA})");
                    switchRescuesBefore = FallGuard.Rescues;
                    switchLowest = float.MaxValue;
                    switchUnderFloor = 0;
                    switchSamples = 0;
                }
                awayAt = now + 6f;
                awayStage = 38;
                return;

            case 38:    // six seconds standing in A: on its floor, never falling through
                {
                    NetEntityView local = NetEntityRegistry.Get(Net.LocalSlot);
                    Transform body = PlayerRef.Active;
                    if (local != null && body != null)
                    {
                        Vector3 feet = local.FeetPosition;
                        switchSamples++;
                        switchLowest = Mathf.Min(switchLowest, feet.y);
                        bool floorUnder = Physics.Raycast(feet + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 4f, ~0, QueryTriggerInteraction.Ignore)
                                          && hit.collider.GetComponentInParent<Animator>() == null;
                        if (!floorUnder) switchUnderFloor++;
                        if (switchSamples == 1 || switchSamples % 600 == 0)
                            report.Add($"INFO  rejoined A: feet {feet}, server {local.LastServerFeet}, floor under {floorUnder}{(floorUnder ? " at " + hit.point.y.ToString("F1") : "")}, placements {Net.PositionPlacements}, scale {body.localScale.y:F2}");
                    }
                    if (now < awayAt) return;
                    Check(FallGuard.Rescues == switchRescuesBefore && switchUnderFloor == 0,
                          $"after switching maps and rejoining, the player stays on the floor ({switchUnderFloor} of {switchSamples} samples without floor under the feet, lowest feet {switchLowest:F1}, {FallGuard.Rescues - switchRescuesBefore} falls caught)");
                    Shot("rejoined_first");
                }
                // The round was begun again: other bodies are new objects.
                proneBody = null;
                hostedSeeker = null;
                bodyViews = null;
                if (!bodiesChecked) bodyStage = 0;
                awayStage = 9;
                Go(Step.Round);
                return;
            case 5:
                if (now < awayAt) return;
                Button rejoin = FindByName("MyMatchesScreen")?.GetComponentsInChildren<Button>(false).FirstOrDefault(b => b.name == "Rejoin");
                Check(rejoin != null, "the matches list has the match, with REJOIN");
                Shot("my_matches");
                rejoin?.onClick.Invoke();
                rejoinWatchFrom = now;
                rejoinCoveredAtTap = FindByName("LoadingCanvas") is { activeInHierarchy: true };
                awayAt = now + 4f;
                awayStage = 6;
                return;

            case 6:
                if (now < awayAt) return;
                int matchNow = OnlineRound.Active ? (int)typeof(OnlineRound).GetField("matchId", Any).GetValue(OnlineRoundInstance()) : -1;
                Check(OnlineRound.Active && matchNow == awayMatch, $"REJOIN puts the player back in that match ({matchNow})");
                // The round was begun again: other bodies are new objects.
                proneBody = null;
                hostedSeeker = null;
                bodyViews = null;
                if (!bodiesChecked) bodyStage = 0;      // a body check under way starts over on the new bodies
                if (freeLookStage == 1) freeLookStage = 0;   // so does the free camera's: the rejoin reset the camera
                awayStage = 9;
                Go(Step.Round);
                return;

            case 20:    // away until the match ends: a notification, then its results
                GameObject toast = Object.FindObjectsByType<Button>().Select(b => b.gameObject)
                    .FirstOrDefault(g => g.name == "Notice" && g.GetComponentsInChildren<Text>().Any(t => t.text.Contains("ended")));
                if (toast == null)
                {
                    if (now < awayAt) return;
                    Check(false, "a notification says the match ended while away");
                    Go(Step.Done);
                    return;
                }
                Check(true, $"a notification says the match ended while away ('{toast.GetComponentInChildren<Text>().text}')");
                Shot("ended_toast");
                toast.GetComponent<Button>().onClick.Invoke();
                awayAt = now + 3f;
                awayStage = 21;
                return;

            case 21:
                if (now < awayAt) return;
                CheckResultsScreen("tapping it opens the full results");
                awayAt = now + 2.5f;
                awayStage = 22;
                return;

            case 22:
                if (now < awayAt) return;
                Check(!Net.MyMatches.Any(m => m.MatchId == (uint)awayMatch && m.State == MyMatchState.Ended) && OnlineMatches.AwayCount == 0,
                      "once its results are viewed the match leaves the matches list");
                awayStage = 9;
                Go(Step.Done);
                return;
        }
    }

    private static void CheckResultsScreen(string what)
    {
        GameObject screen = FindByName("MatchResultsScreen");
        Text[] texts = screen != null ? screen.GetComponentsInChildren<Text>(true) : new Text[0];
        List<int> ranks = texts.Where(t => t.text.StartsWith("#")).Select(t => int.TryParse(t.text.Substring(1), out int r) ? r : 0).ToList();
        Check(screen != null && screen.activeInHierarchy && ranks.Count >= 8, $"{what}: every player's rank, score and coins ({ranks.Count} ranked rows)");

        // One ranking for everyone (no Seekers / Hiders groups), best first; a hider's score is
        // seconds survived of the seek, a seeker's the hiders caught.
        bool grouped = texts.Any(t => t.text == "Seekers" || t.text == "Hiders");
        int hiderScores = texts.Count(t => System.Text.RegularExpressions.Regex.IsMatch(t.text, @"^\d+s / \d+s( \| .+)?$"));   // "| name": who caught them
        int seekerScores = texts.Count(t => System.Text.RegularExpressions.Regex.IsMatch(t.text, @"^\d+ Hiders? Caught$"));
        Check(!grouped && ranks.SequenceEqual(ranks.OrderBy(r => r)) && ranks[0] == 1 && hiderScores == GameTuning.FugitiveSlots && seekerScores == GameTuning.HunterSlots,
              $"one ranking, best first ({string.Join(",", ranks)}); scores as seconds of the seek ({hiderScores}) and hiders caught ({seekerScores})");
        Shot("results_" + (AwayEndTest ? "away" : "end"));
    }

    // A seeker's shot: the seconds to the next one count down on the Shoot button.
    private static void CheckFireCooldown()
    {
        if (!(AwayTest || AwayEndTest) || !Seeker || cooldownSeen || Net.Phase != MatchPhase.Seek) return;
        Text count = FindByName("CooldownText")?.GetComponent<Text>();
        if (count == null || !count.gameObject.activeInHierarchy) return;
        if (!float.TryParse(count.text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float left)) return;
        cooldownSeen = true;
        Check(left > 0f && left <= 3.05f, $"after a shot the Shoot button counts down the cooldown ({count.text})");
        Shot("cooldown");
    }

    // ================= The hiders' ready list =================

    private static int readyStage;
    private static float readyAt;
    private static string readyOtherBanner;

    private static void CheckReadyList()
    {
        if (!ReadyTest || Seeker || readyStage >= 9 || Net.Phase != MatchPhase.Hide) return;
        float now = Time.realtimeSinceStartup;
        OnlineRound round = OnlineRoundInstance();
        var roster = Get<MecchaNetClient.RosterEntry[]>(round, "roster") ?? new MecchaNetClient.RosterEntry[0];
        var humanHiders = roster.Where(e => e.Role == Role.Fugitive && !e.IsBot).Select(e => e.Slot).OrderBy(s => s).ToList();
        byte other = humanHiders.FirstOrDefault(s => s != Net.LocalSlot);
        bool first = humanHiders.Count > 0 && humanHiders[0] == Net.LocalSlot;
        Button chipButton = FindByName("HidersReady")?.GetComponent<Button>();
        Text chip = chipButton != null ? chipButton.GetComponentInChildren<Text>(true) : null;
        GameObject seats = FindByName("HiderReadiness");
        string list = seats != null ? string.Join(" / ", seats.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Seat_"))
            .Select(t => string.Join(": ", t.GetComponentsInChildren<Text>(true).Select(x => x.text)))) : "";
        Text banner = Get<Text>(round, "banner");

        switch (readyStage)
        {
            case 0:
                if (humanHiders.Count < 2 || chip == null || !chip.gameObject.activeInHierarchy) return;
                if (seats == null || !seats.activeInHierarchy) { TapAt(chipButton); return; }   // the chip opens the roster
                Check(chip.text.EndsWith("/5 ready") && list.Contains("(you)") && list.Contains("Hiding"),
                      $"in the hide phase every hider sees who is ready: chip '{chip.text}', roster '{list}'");
                Shot("ready_roster");
                FindButton("HiderReadiness", "Done")?.onClick.Invoke();
                // The first presses Start after 2 s, the other after 6 s.
                readyAt = now + (first ? 2f : 6f);
                readyStage = 1;
                return;
            case 1:
                // The other one's Start, seen here: the banner then and their line in the list.
                if (readyOtherBanner == null && Net.IsHiderReady(other) && banner != null)
                {
                    readyOtherBanner = banner.gameObject.activeInHierarchy ? banner.text : "(no banner)";
                    Shot("ready_other");
                    Check(readyOtherBanner.Contains("is ready") && list.Contains(": Ready") && chip != null && !chip.text.StartsWith("0/"),
                          $"another hider pressing Start shows here at once: banner '{readyOtherBanner}', chip '{(chip != null ? chip.text : "none")}', roster '{list}'");
                }
                if (now >= readyAt && !pressedReadyStart)
                {
                    pressedReadyStart = true;
                    Object.FindAnyObjectByType<BotSeekController>()?.SkipGrace();   // the Start button
                }
                if (pressedReadyStart && readyOtherBanner != null) readyStage = 9;
                return;
        }
    }

    private static bool pressedReadyStart;

    // ================= Next Player, bought once after being knocked out =================

    // -playtestNextPlayer: out of the round, Next player offers the feature first (a tap opens the
    // offer and switches nothing; Not now takes nothing), then Unlock buys it for 100 coins and the
    // button switches between the players still in, there and then.
    private static bool NextPlayerTest => System.Environment.GetCommandLineArgs().Contains("-playtestNextPlayer");

    // -playtestShots: pictures of the screens these modes open, next to the report.
    private static bool Shots => System.Environment.GetCommandLineArgs().Contains("-playtestShots");

    private static void Shot(string name) => Shot(name, 1280, 720);

    private static void Shot(string name, int width, int height)
    {
        if (!Shots) return;
        Camera cam = Camera.main;
        if (cam == null) cam = Object.FindObjectsByType<Camera>().Where(c => c.enabled && c.gameObject.activeInHierarchy).OrderByDescending(c => c.depth).FirstOrDefault();
        if (cam == null) return;
        // Overlay canvases are not drawn by a camera render: put them in front of it for the picture.
        var overlays = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude)
            .Where(c => c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay).ToList();
        foreach (Canvas c in overlays)
        {
            c.renderMode = RenderMode.ScreenSpaceCamera;
            c.worldCamera = cam;
            c.planeDistance = cam.nearClipPlane + 0.05f + 0.001f * (100 - Mathf.Clamp(c.sortingOrder, -100, 100));
        }
        var rt = new RenderTexture(width, height, 24);
        RenderTexture before = cam.targetTexture;
        cam.targetTexture = rt;
        Canvas.ForceUpdateCanvases();   // laid out for the picture's own size, not the batch screen's
        PaintboxHud.Relayout();         // the match HUD in design units at that size
        Canvas.ForceUpdateCanvases();
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();
        cam.targetTexture = before;
        RenderTexture.active = null;
        File.WriteAllBytes(Path.ChangeExtension(ReportPath, null) + "_" + name + ".png", tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        rt.Release();
        foreach (Canvas c in overlays) { c.renderMode = RenderMode.ScreenSpaceOverlay; c.worldCamera = null; }
        Canvas.ForceUpdateCanvases();   // back in place before anything is tapped this frame
        PaintboxHud.Relayout();
        Canvas.ForceUpdateCanvases();
    }
    private static int nextStage;
    private static float nextAt;
    private static long nextCoinsBefore;
    private static byte nextWatched;

    private static void CheckNextPlayer()
    {
        if (!NextPlayerTest || nextStage >= 9) return;
        NetEntityView me = NetEntityRegistry.Get(Net.LocalSlot);
        if (me == null || !me.Eliminated) return;

        OnlineRound round = OnlineRoundInstance();
        Button next = round != null ? Get<Button>(round, "nextButton") : null;
        if (next == null || !next.gameObject.activeInHierarchy) return;
        float now = Time.realtimeSinceStartup;
        if (now < nextAt) return;

        byte watching = (byte)typeof(OnlineRound).GetField("watching", Any).GetValue(round);
        string label = next.GetComponentInChildren<Text>(true).text.Replace("\n", " / ");
        GameObject offer = FindByName("NextPlayerOffer");
        int stillIn = Object.FindObjectsByType<NetEntityView>().Count(v => !v.IsLocal && !v.Eliminated);

        switch (nextStage)
        {
            case 0:
                Check(label.Contains("Unlock") && label.Contains("100"), $"out of the round, Next player offers the unlock first ('{label}')");
                Shot("next_locked");
                Check(!ShopInventory.OwnsFeature(GameFeatures.NextPlayer), "the account does not own Next Player yet");
                nextCoinsBefore = CoinBalance.Cents;
                nextWatched = watching;
                next.onClick.Invoke();
                nextAt = now + 0.5f;
                nextStage = 1;
                return;

            case 1:
                Check(offer != null && offer.activeInHierarchy && watching == nextWatched, $"a tap opens the offer and switches nothing (watching {nextWatched} -> {watching})");
                Shot("next_offer");
                Button notNow = FindButton("NextPlayerOffer", "Not now");
                if (notNow != null) notNow.onClick.Invoke();
                nextAt = now + 0.5f;
                nextStage = 2;
                return;

            case 2:
                Check(offer == null && CoinBalance.Cents == nextCoinsBefore, "Not now closes it and takes nothing");
                next.onClick.Invoke();
                nextAt = now + 0.5f;
                nextStage = 3;
                return;

            case 3:
                Button unlock = FindButton("NextPlayerOffer", "Unlock for");
                Check(unlock != null, "the offer has its Unlock button, with the price");
                if (unlock == null) { nextStage = 9; return; }
                nextWatched = watching;
                unlock.onClick.Invoke();
                nextAt = now + 3f;      // the purchase and the listing after it
                nextStage = 4;
                return;

            case 4:
                long taken = nextCoinsBefore - CoinBalance.Cents;
                Check(offer == null, "Unlock closes the offer once bought");
                Check(taken == 100 * GameTuning.CentsPerCoin, $"it cost 100 coins ({taken / (double)GameTuning.CentsPerCoin} taken)");
                Check(ShopInventory.OwnsFeature(GameFeatures.NextPlayer), "the server lists Next Player as owned");
                Check(label == "Next player", $"the button now just says Next player ('{label}')");
                if (stillIn >= 2) Check(watching != nextWatched, $"and the camera moved on to the next player straight away ({nextWatched} -> {watching})");
                else report.Add($"INFO  {stillIn} player(s) still in when it was bought: nobody else to switch to");
                Shot("next_unlocked");
                nextWatched = watching;
                next.onClick.Invoke();
                nextAt = now + 0.5f;
                nextStage = 5;
                return;

            case 5:
                if (stillIn >= 2) Check(watching != nextWatched && offer == null, $"each tap switches again, with no offer ({nextWatched} -> {watching})");
                nextStage = 9;
                return;
        }
    }

    // ================= The match HUD (2026-11-25) =================
    //
    // A hider in the hide phase, once: the minimap (the room drawn, the player's arrow on it), the
    // stick's centre following the thumb and coming back, Jump yellow while in the air and back on
    // landing, Climb beside the stick, Pose / Rotate / Paint one at a time with the Rotate tray over
    // the Camera button, and the phone's Back (the round menu, a panel closed). The seeker's
    // minimap is looked at too.
    private static int hudStage;
    private static float hudAt;
    private static bool hudSeekerSeen;
    private static PointerEventData hudPointer;

    private static bool CheckHud()
    {
        if (ExitTest || AwayTest || Pair || Kills || Chase) return false;
        float now = Time.realtimeSinceStartup;

        if (Seeker)
        {
            if (hudSeekerSeen || Net.Phase != MatchPhase.Seek || !PaintboxHud.InRound) return false;
            hudSeekerSeen = true;
            CheckMinimap("seeker");
            return false;
        }

        if (hudStage >= 99 || Net.Phase != MatchPhase.Hide || !PaintboxHud.InRound || OnlineRound.AwaitingPlacement) return false;
        if (now < hudAt) return true;

        JoystickMover mover = Object.FindAnyObjectByType<JoystickMover>();
        Joystick stick = mover != null ? Get<Joystick>(mover, "joystick") : null;
        RectTransform handle = stick != null ? typeof(Joystick).GetField("handle", Any).GetValue(stick) as RectTransform : null;   // the base class's field
        GameObject paintUi = FindByName("PaintUICanvas");

        switch (hudStage)
        {
            case 0:
                DriveJoystick(false);
                hudAt = now + 2f;
                hudStage = 1;
                return true;

            case 1:
            {
                CheckMinimap("hider");
                Shot("hud_minimap");

                // A finger on the stick, off its middle: the centre goes with it.
                RectTransform area = stick != null ? stick.transform as RectTransform : null;
                if (stick == null || area == null) { Check(false, "the stick is there"); hudStage = 99; return false; }
                Rect r = ScreenRect(area);
                hudPointer = new PointerEventData(EventSystem.current) { position = r.center + new Vector2(r.width * 0.3f, r.height * 0.2f), button = PointerEventData.InputButton.Left };
                stick.OnPointerDown(hudPointer);
                hudAt = now + 0.3f;
                hudStage = 2;
                return true;
            }

            case 2:
            {
                Vector2 moved = handle != null ? handle.anchoredPosition : Vector2.zero;
                Check(moved.x > 5f && moved.y > 2f, $"the stick's centre follows the thumb (at {moved})");
                Shot("stick_held");
                stick.OnPointerUp(hudPointer);
                hudAt = now + 0.3f;
                hudStage = 3;
                return true;
            }

            case 3:
            {
                Vector2 back = handle != null ? handle.anchoredPosition : Vector2.one;
                Check(back.sqrMagnitude < 0.01f, $"and goes back to the middle when let go ({back})");

                // Jump: yellow from take-off until it lands.
                RectTransform jump = FindByName("FireButtonCanvas")?.transform.Find("Jump") as RectTransform;
                RectTransform pose = paintUi != null ? paintUi.transform.Find("Btn_DoiDang") as RectTransform : null;
                Check(jump != null && pose != null && Mathf.Abs(ScreenRect(jump).center.y - ScreenRect(pose).center.y) < 20f
                      && ScreenRect(jump).xMax <= ScreenRect(pose).xMin + 2f && ScreenRect(pose).xMin - ScreenRect(jump).xMax < 60f,
                      $"the hider's Jump is beside Pose (Jump {(jump != null ? ScreenRect(jump).ToString() : "none")}, Pose {(pose != null ? ScreenRect(pose).ToString() : "none")})");
                jump?.GetComponent<Button>()?.onClick.Invoke();
                if (mover != null && !mover.InJump) mover.Jump();
                hudAt = now + 0.25f;
                hudStage = 4;
                return true;
            }

            case 4:
            {
                Color fill = JumpFill();
                Check(mover != null && mover.InJump && Near(fill, Paintbox.Yellow), $"Jump is yellow while in the air (in the air {mover != null && mover.InJump}, colour {fill})");
                Shot("jump_yellow");
                hudAt = now + 0.1f;
                hudStage = 5;
                return true;
            }

            case 5:
                if (mover != null && mover.InJump && now < hudAt + 4f) return true;   // still in the air
                hudAt = now + 0.4f;
                hudStage = 6;
                return true;

            case 6:
            {
                Color fill = JumpFill();
                Check(mover != null && !mover.InJump && !Near(fill, Paintbox.Yellow), $"and back to its own colour on landing ({fill})");

                // Climb sits beside the stick (it shows only by a wall, so its layout is read, not its place on screen).
                RectTransform climb = FindByName("ClimbButtonsCanvas")?.transform.Find("ClimbUp") as RectTransform;
                RectTransform down = FindByName("ClimbButtonsCanvas")?.transform.Find("ClimbDown") as RectTransform;
                if (climb != null && down != null)
                {
                    float unit = PaintboxHud.Unit(climb.GetComponentInParent<Canvas>(true).rootCanvas.transform);
                    float x = climb.anchoredPosition.x / unit;
                    Check(climb.anchorMin == Vector2.zero && down.anchorMin == Vector2.zero && x > 260f && x < 340f && climb.anchoredPosition.y > down.anchoredPosition.y,
                          $"Climb is beside the stick, up over down (anchored bottom left at {x:F0} design units; the stick reaches 296)");
                    PaintboxHudButton climbSkin = climb.GetComponent<PaintboxHudButton>();
                    Image climbFill = climbSkin != null ? Get<Image>(climbSkin, "fill") : null;
                    WallClimber climber = Object.FindAnyObjectByType<WallClimber>(FindObjectsInactive.Include);
                    Check(climbFill != null && climber != null && !climber.UpHeld && !Near(climbFill.color, Paintbox.Yellow) && !Near(climbFill.color, Paintbox.Hot),
                          $"Climb is not yellow while it is not held ({(climbFill != null ? climbFill.color.ToString() : "no skin")})");
                }

                // Pose, then Rotate: the pose tray closes. Then Paint: the Rotate tray closes.
                paintUi.transform.Find("Btn_DoiDang")?.GetComponent<Button>()?.onClick.Invoke();
                hudAt = now + 0.4f;
                hudStage = 7;
                return true;
            }

            case 7:
            {
                Check(paintUi.transform.Find("PosePanel") is { gameObject: { activeSelf: true } }, "Pose opens its tray");
                paintUi.transform.Find("Btn_XoayNhanVat")?.GetComponent<Button>()?.onClick.Invoke();
                hudAt = now + 0.4f;
                hudStage = 8;
                return true;
            }

            case 8:
            {
                bool rotateOpen = paintUi.transform.Find("ButtonRotationLeft") is { gameObject: { activeSelf: true } };
                Check(rotateOpen && !(paintUi.transform.Find("PosePanel") is { gameObject: { activeSelf: true } }),
                      "Rotate opens its tray and the pose tray closes (one at a time)");
                RectTransform tray = FindByName("RotateTray")?.transform as RectTransform;
                RectTransform camera = paintUi.transform.Find("ButtonCamera") as RectTransform;
                Check(tray != null && camera != null && ScreenRect(tray).yMin >= ScreenRect(camera).yMax - 2f
                      && Mathf.Abs(ScreenRect(tray).xMax - ScreenRect(camera).xMax) < 40f,
                      $"the Rotate tray is over the Camera button, not in the middle (tray {(tray != null ? ScreenRect(tray).ToString() : "none")}, Camera {(camera != null ? ScreenRect(camera).ToString() : "none")})");
                Shot("rotate_tray");
                Object.FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include)?.OpenDrawing();
                hudAt = now + 0.4f;
                hudStage = 9;
                return true;
            }

            case 9:
            {
                ChameleonPaint painter = Object.FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include);
                Check(painter != null && painter.DrawingActive && !(paintUi.transform.Find("ButtonRotationLeft") is { gameObject: { activeSelf: true } }),
                      "Paint opens its sheet and the Rotate tray closes");

                // The phone's Back: the paint sheet first, then the round menu, then the menu closes.
                BackKey.Press();
                hudAt = now + 0.4f;
                hudStage = 10;
                return true;
            }

            case 10:
            {
                ChameleonPaint painter = Object.FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include);
                Check(painter != null && !painter.DrawingActive && !PaintboxHud.MenuOpen, "Back closes the paint sheet first");
                BackKey.Press();
                hudAt = now + 0.4f;
                hudStage = 11;
                return true;
            }

            case 11:
                Check(PaintboxHud.MenuOpen, "Back with nothing open opens the round menu");
                BackKey.Press();
                hudAt = now + 0.4f;
                hudStage = 12;
                return true;

            case 12:
                Check(!PaintboxHud.MenuOpen, "and Back again closes it");
                hudStage = 99;
                return false;
        }
        return false;
    }

    private static void CheckMinimap(string who)
    {
        GameObject map = FindByName("Minimap");
        RawImage picture = map != null ? map.GetComponentsInChildren<RawImage>(true).FirstOrDefault() : null;
        Check(map != null && map.activeInHierarchy && picture != null && picture.texture != null,
              $"the {who} has a minimap of the room under the role chip (shown {map != null && map.activeInHierarchy}, drawn {picture != null && picture.texture != null})");
        RectTransform me = picture != null ? picture.transform.parent.Find("Me") as RectTransform : null;
        Check(me != null && me.anchoredPosition.sqrMagnitude > 0.01f, $"with the player's arrow on it ({(me != null ? me.anchoredPosition.ToString() : "none")})");
    }

    private static Color JumpFill()
    {
        PaintboxHudButton skin = FindByName("FireButtonCanvas")?.transform.Find("Jump")?.GetComponent<PaintboxHudButton>();
        Image fill = skin != null ? Get<Image>(skin, "fill") : null;
        return fill != null ? fill.color : Color.clear;
    }

    private static bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.08f;

    // ================= The lobby, frame by frame (2026-11-25) =================
    //
    // Every change of what the lobby shows (the page's title, its banner, the countdown, the bar's
    // message), with when it happened. A value that goes away and comes straight back is a blink:
    // the user saw the status bar blink before the countdown when a player got ready.
    private static readonly Dictionary<string, List<(float t, string v)>> lobbySeen = new Dictionary<string, List<(float t, string v)>>();
    private static bool lobbyJudged;

    private static void SampleLobby()
    {
        float t = Time.realtimeSinceStartup;
        void Note(string what, string value)
        {
            if (!lobbySeen.TryGetValue(what, out List<(float t, string v)> list)) lobbySeen[what] = list = new List<(float t, string v)>();
            if (list.Count > 0 && list[list.Count - 1].v == value) return;
            list.Add((t, value));
        }

        MatchmakingUI ui = Object.FindAnyObjectByType<MatchmakingUI>(FindObjectsInactive.Include);
        PaintboxPage page = ui != null ? Get<PaintboxPage>(ui, "page") : null;
        Note("canvas", FindByName("MatchmakingCanvas") is { activeInHierarchy: true } ? "on" : "off");
        Note("title", page?.Title != null && page.Title.isActiveAndEnabled ? page.Title.text : "-");
        Note("banner", FindByName("QueueCount") is { activeInHierarchy: true } b ? b.GetComponent<Text>().text : "-");
        Note("countdown", FindByName("QueueCountdown") is { activeInHierarchy: true } c ? c.GetComponent<Text>().text : "-");
        GameObject bar = FindByName("LobbyBar");
        Note("bar", bar != null && bar.activeInHierarchy ? string.Join(" ", bar.GetComponentsInChildren<Text>(false).Select(x => x.text)) : "-");
    }

    private static void JudgeLobby()
    {
        lobbyJudged = true;
        var blinks = new List<string>();
        foreach (KeyValuePair<string, List<(float t, string v)>> pair in lobbySeen)
        {
            List<(float t, string v)> list = pair.Value;
            for (int i = 2; i < list.Count; i++)
                if (list[i].v == list[i - 2].v && list[i].t - list[i - 2].t < 1.5f)
                    blinks.Add($"{pair.Key}: '{list[i - 2].v}' -> '{list[i - 1].v}' -> '{list[i].v}' in {list[i].t - list[i - 2].t:F2} s");
            report.Add($"INFO  lobby {pair.Key}: " + string.Join(" | ", list.Select(x => $"{x.t:F1}s '{x.v}'")));
        }
        Check(blinks.Count == 0, $"the lobby shows steadily until the match: nothing blinks ({(blinks.Count == 0 ? "no value went away and came straight back" : string.Join(" ; ", blinks.Take(4)))})");
    }

    private static void PlayRound()
    {
        if (ExitTest && exitStage == 5) { CheckExit(); return; }   // left: nothing of the round to look at any more
        CheckPlayerPrefab();
        CheckSpawnsApart();
        CheckPlayArea();
        CheckSeekerBotGun();
        CheckSeekerBodyLiesDown();
        CheckFreeLookWhenOut();
        CheckOutRejoin();
        CheckNextPlayer();
        CheckExit();
        CheckAway();
        CheckFireCooldown();
        if (step != Step.Round) return;   // Exit left the match
        if (CheckHud()) return;           // the HUD's own checks hold the stick and the buttons meanwhile
        CheckReadyList();
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

            // Switch to the roller once with the weapon switch over Fire (2026-11-25), tapped where a
            // finger would tap it (whatever is on top there gets the tap), then keep shooting its rounds.
            if (!chosenSpecial && Net.Weapons.Any(w => w.WeaponId == TestWeapon && w.Ammo > 0))
            {
                chosenSpecial = true;
                ammoBeforeRound = Net.Weapons.First(w => w.WeaponId == TestWeapon).Ammo;
                Check(ammoBeforeRound >= 20, $"the match starts with the roller's rounds: its 10 and the pack's 10 at least ({ammoBeforeRound})");
                Button dock = FindByName("WeaponSwitch")?.GetComponent<Button>();
                string before = DockText(dock);
                if (Net.WeaponId != TestWeapon)
                {
                    Image swap = dock != null ? dock.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.sprite == Paintbox.Icon("ArrowLeftRight")) : null;
                    Check(dock != null && dock.gameObject.activeInHierarchy && before.Contains("\u221e") && swap != null && swap.enabled,
                          $"the seeker's weapon switch shows the weapon held (the standard one's unlimited rounds) and that it switches ('{before}')");
                    RectTransform fire = FindByName("FireBtn")?.transform as RectTransform;
                    Check(dock != null && fire != null && ScreenRect(dock.transform as RectTransform).yMin >= ScreenRect(fire).yMax - 2f
                          && Mathf.Abs(ScreenRect(dock.transform as RectTransform).xMax - ScreenRect(fire).xMax) < 30f,
                          $"and it sits over the Fire button (switch {(dock != null ? ScreenRect(dock.transform as RectTransform).ToString() : "none")}, Fire {(fire != null ? ScreenRect(fire).ToString() : "none")})");
                    GameObject hit = TapAt(dock);
                    Check(hit != null && hit.transform.IsChildOf(dock.transform), $"a tap on it reaches it, nothing covers it (hit {(hit != null ? hit.name : "nothing")})");
                }
                nextAction = Time.realtimeSinceStartup + 0.6f;
                weaponTapAt = Time.realtimeSinceStartup;
                return;
            }
            if (chosenSpecial && weaponTapAt > 0f && Time.realtimeSinceStartup > weaponTapAt + 0.5f)
            {
                weaponTapAt = -1f;
                string after = DockText(FindByName("WeaponSwitch")?.GetComponent<Button>());
                Check(Net.WeaponId == TestWeapon && after.Contains(ShopInventory.WeaponName(TestWeapon)) && after.Contains(Net.CurrentWeapon.Ammo.ToString()),
                      $"the tap switched to the roller, and the switch says so with its rounds ('{after}', server: weapon {Net.WeaponId}, {Net.CurrentWeapon.Ammo} left)");
                Check(Mathf.Abs(Net.CooldownAfterShot - 2f) < 0.01f, $"and the roller's own cooldown applies ({Net.CooldownAfterShot} s)");
                Shot("weapon_dock");
            }

            // The Shoot button held down, as a finger holds it: the gun fires again each time the
            // bullet's cooldown ends. Held for 12 s, let go for 5 s (no shot then), held again.
            float now = Time.realtimeSinceStartup;
            bool shot = NewShot();
            if (shot) shotsFired++;
            switch (fireHold)
            {
                case 0:
                    if (FireButton(true)) { fireHold = 1; fireHeldAt = now; }
                    break;
                case 1:
                    if (shot) shotsWhileHeld++;
                    if (now - fireHeldAt > 12f && FireButton(false)) { fireHold = 2; fireLetGoAt = now; }
                    break;
                case 2:
                    if (shot && now - fireLetGoAt > 0.3f) shotsAfterLetGo++;
                    if (now - fireLetGoAt > 5f && FireButton(true)) fireHold = 3;
                    break;
            }
            nextAction = now + 0.1f;
            if (Net.Weapons.Any(w => w.WeaponId == TestWeapon && w.Ammo >= 0 && w.Ammo < ammoBeforeRound)) specialSpent = true;
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

    // ---- Shoes and the held Shoot button (2026-11-24) ----
    private static bool shoesAsked, shoesAnswered, shoesChecked;
    private static int fireHold;                 // 0 not yet, 1 held, 2 let go, 3 held again
    private static float fireHeldAt, fireLetGoAt, lastFireLeft;
    private static int shotsWhileHeld, shotsAfterLetGo;

    // The Shoot button pressed or let go as a finger does it: the events reach the button itself.
    private static bool FireButton(bool down)
    {
        GameObject button = FindByName("FireBtn");
        if (button == null || !button.activeInHierarchy || EventSystem.current == null) return false;
        var pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, pointerId = 3 };
        pointer.position = RectTransformUtility.WorldToScreenPoint(null, ((RectTransform)button.transform).position);
        pointer.pointerPress = button;
        if (down) ExecuteEvents.ExecuteHierarchy(button, pointer, ExecuteEvents.pointerDownHandler);
        else ExecuteEvents.ExecuteHierarchy(button, pointer, ExecuteEvents.pointerUpHandler);
        return true;
    }

    // A new shot shows as the wait before the next one starting again (it only counts down otherwise).
    private static bool NewShot()
    {
        float left = OnlineRound.FireWait(out float l, out _) ? l : 0f;
        bool fresh = left > lastFireLeft + 0.3f;
        lastFireLeft = left;
        return fresh;
    }

    private static void CheckShoesInRound()
    {
        if (shoesChecked) return;
        JoystickMover mover = Object.FindAnyObjectByType<JoystickMover>();
        if (mover == null) return;
        shoesChecked = true;
        Check(Net.ShoeId == 1 && Mathf.Abs(mover.RunSpeed - mover.moveSpeed * 1.1f) < 0.01f,
              $"in the match the server has the sneakers on (ShoeId {Net.ShoeId}) and the player runs 10% faster ({mover.RunSpeed:F1} against {mover.moveSpeed:F1})");
    }

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

    private static readonly HashSet<byte> outOfReach = new HashSet<byte>();

    private static NetEntityView NextHider(out MecchaNetClient.RosterEntry entry)
    {
        entry = default;
        var entries = Get<MecchaNetClient.RosterEntry[]>(OnlineRoundInstance(), "roster");
        NetEntityView me = NetEntityRegistry.Get(Net.LocalSlot);
        foreach (MecchaNetClient.RosterEntry e in entries)
        {
            if (killRealPlayersOnly && e.IsBot) continue;
            NetEntityView v = NetEntityRegistry.Get(e.Slot);
            if (e.Role != Role.Fugitive || v == null || v.Eliminated) continue;
            if (!v.HasPosition && !v.IsHosted) continue;

            // A bullet is spent after OnlineRound.BulletReach: a hider further off cannot be hit
            // from here, and the crosshair does not claim it.
            if (me != null && Vector3.Distance(me.FeetPosition, v.FeetPosition) > OnlineRound.BulletReach - 20f)
            {
                if (outOfReach.Add(e.Slot)) report.Add($"INFO  hider in slot {e.Slot} is {Vector3.Distance(me.FeetPosition, v.FeetPosition):F0} units off, beyond a bullet's {OnlineRound.BulletReach:F0}: not shot at");
                continue;
            }
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
    private static int pairStage, shapeIndex, placementsBeforeClimb, walkTurns;
    private static float walkStuckCheckAt;
    private static Vector3? walkStuckFrom;
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

    // A seeker's aim, up and down, as sent (own) and as drawn on the other screen (partner's).
    private static float ownAimMin, ownAimMax, partnerAimMin, partnerAimMax, partnerGunOffWorst, partnerGunArmWorst;
    private static GunAim.Arm partnerArm;
    private static int partnerGunSamples, partnerGunProneSamples;

    // Sweeps this seeker's aim up and down through the seek, as dragging on the screen does.
    private static void SweepAim()
    {
        Mode1FirstPersonGun g = Object.FindAnyObjectByType<Mode1FirstPersonGun>();
        if (g == null || !g.GunModeOn) return;

        float pitch = 30f * Mathf.Sin(Time.realtimeSinceStartup * 0.9f);
        typeof(Mode1FirstPersonGun).GetField("aimPitch", Any).SetValue(g, pitch);
        Object.FindAnyObjectByType<CameraController>()?.SetAimPitch(pitch);
        ownAimMin = Mathf.Min(ownAimMin, pitch);
        ownAimMax = Mathf.Max(ownAimMax, pitch);
    }

    // The other seeker's gun, as drawn here: tilted by their aim, on its line, lying down too.
    private static void WatchPartnerAim(NetEntityView v, bool prone)
    {
        NetBodyAnimator body = v.GetComponent<NetBodyAnimator>();
        Transform gun = body != null ? body.Gun : null;
        if (gun == null || !gun.gameObject.activeInHierarchy) return;

        partnerAimMin = Mathf.Min(partnerAimMin, v.AimPitch);
        partnerAimMax = Mathf.Max(partnerAimMax, v.AimPitch);
        partnerGunOffWorst = Mathf.Max(partnerGunOffWorst, GunAim.AngleOff(gun, GunAim.Direction(v.transform, v.AimPitch)));
        if (partnerArm == null || partnerArm.Hand == null || !partnerArm.Hand.IsChildOf(v.transform)) partnerArm = GunAim.FindArm(v.transform);
        if (partnerArm != null) partnerGunArmWorst = Mathf.Max(partnerGunArmWorst, GunAim.AngleOff(gun, partnerArm.Direction));
        partnerGunSamples++;
        if (prone) partnerGunProneSamples++;
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
            if (e.Role == Role.Hunter) WatchPartnerAim(v, (v.Flags & EntityFlags.Prone) != 0);

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

        if (Seeker && !Mixed) SweepAim();

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
                if (walkFrom == null)
                {
                    walkFrom = me.FeetPosition;
                    walkStuckFrom = null;
                    walkStuckCheckAt = now + 1.5f;
                }

                // Blocked by furniture (the spawn may face the middle of the room): a quarter turn
                // and on, up to three times, rather than pushing at a table until the time runs out.
                if (now >= walkStuckCheckAt)
                {
                    if (walkStuckFrom != null && Vector3.Distance(walkStuckFrom.Value, me.FeetPosition) < 2f && walkTurns < 3)
                    {
                        PlayerRef.Active.Rotate(0f, 90f, 0f);
                        walkTurns++;
                        report.Add($"INFO  the walk to a wall was blocked at {me.FeetPosition}; turned a quarter round ({walkTurns})");
                    }
                    walkStuckFrom = me.FeetPosition;
                    walkStuckCheckAt = now + 1.5f;
                }

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
            Check(ownAimMax > 20f && ownAimMin < -20f, $"this seeker aimed up and down ({ownAimMin:F0} to {ownAimMax:F0} degrees)");
            Check(partnerGunSamples > 0 && partnerAimMax > 15f && partnerAimMin < -15f,
                  $"the other seeker's aim up and down shows here ({partnerAimMin:F0} to {partnerAimMax:F0} degrees over {partnerGunSamples} frames)");
            Check(partnerGunSamples > 0 && partnerGunOffWorst < 3f,
                  $"their gun points along their aim on this screen, standing and lying down (worst {partnerGunOffWorst:F1} degrees off; {partnerGunProneSamples} of {partnerGunSamples} frames lying down)");
            Check(partnerGunSamples > 0 && partnerGunArmWorst < 2f,
                  $"and lies along their arm, handle in hand (barrel at worst {partnerGunArmWorst:F1} degrees off the forearm)");
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
        if (AwayTest || AwayEndTest) report.Add($"INFO  animations restarted {animRestarts} times (the round was begun again on Rejoin)");
        else Check(animRestarts == 0, $"no animation started over on any body ({animRestarts}{(animNotes.Count > 0 ? ": " + string.Join("; ", animNotes) : "")})");
        report.Add($"INFO  bodies that went back to a state within 0.3 s: {animBounces}, over {animTracks.Count} bodies");
        if (switchStage < 4) report.Add("INFO  the animation switching check did not finish");
        CheckOwnFalls();
        Check(FallGuard.Rescues == 0, $"this player never fell out of the room ({FallGuard.Rescues} times put back)");
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
        Check(worstServerGap < (Handover ? 14f : 8f), $"the server's position kept up with the client's (worst gap {worstServerGap:F1} units: {worstGapWhen})");
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
            if (fireHold >= 2)
            {
                // The roller waits 2 s, the standard weapon 3 s, a catch none: in 12 s held, 4 at the least.
                Check(shotsWhileHeld >= 4, $"holding the Shoot button kept the gun firing after each cooldown ({shotsWhileHeld} shots in 12 s held)");
                Check(shotsAfterLetGo == 0, $"letting go of it stopped the firing at once ({shotsAfterLetGo} shots in the 5 s after)");
            }
            else report.Add($"INFO  the seek ended before the held Shoot button could be judged (stage {fireHold}, {shotsWhileHeld} shots held)");
            Check(chosenSpecial && specialSpent, "switched to the roller with the weapon dock and shot its rounds");
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

        // Other players drawn from the network (not bots this game runs): how often they stood
        // still for want of a newer position (2026-11-25: other players looked frozen).
        string stalls = $"{NetEntityView.Stalls} stalls, {NetEntityView.StalledSeconds:F2} s stalled of {NetEntityView.DrawnSeconds:F0} s drawn, longest {NetEntityView.LongestStall * 1000f:F0} ms";
        if (RemotePlayers)
        {
            float share = NetEntityView.DrawnSeconds > 0f ? NetEntityView.StalledSeconds / NetEntityView.DrawnSeconds : 1f;
            Check(NetEntityView.DrawnSeconds > 20f, $"other players were drawn from the network ({NetEntityView.DrawnSeconds:F0} body-seconds)");
            Check(share < 0.01f && NetEntityView.LongestStall < 0.25f, $"other players move without freezing ({stalls}; {share * 100f:F2}% of the time)");
            foreach (string s in stallLog.Take(12)) report.Add("INFO  stall " + s);
        }
        else report.Add($"INFO  other players drawn from the network: {stalls}");
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

    // ================= Rejoin: never the wrong scene =================

    private static int switchMatchB = -1, switchRescuesBefore, switchUnderFloor, switchSamples;
    private static int SwitchMapB => MapIndex == 1 ? 2 : 1;      // always another map than the first match's
    private static float switchLowest;
    private static float rejoinWatchFrom = -1f;
    private static bool rejoinCoveredAtTap;
    private static int rejoinExposedFrames, rejoinWatchFrames;

    // From the REJOIN tap until the rejoined round is settled (its room loaded, this player placed
    // by the server), every frame must be under the loading screen; then it must come down.
    private static void WatchRejoin()
    {
        if (rejoinWatchFrom < 0f) return;
        float now = Time.realtimeSinceStartup;
        OnlineRound round = OnlineRoundInstance();
        bool settled = OnlineRound.Active && round != null && round.IsSettled;
        bool covered = FindByName("LoadingCanvas") is { activeInHierarchy: true };
        rejoinWatchFrames++;
        if (!settled && !covered) rejoinExposedFrames++;

        if (settled && !covered)
        {
            rejoinWatchFrom = -1f;
            LevelManager levels = LevelManager.Instance;
            Check(rejoinCoveredAtTap && rejoinExposedFrames == 0,
                  $"rejoining shows the loading screen from the tap until the match is ready ({rejoinExposedFrames} of {rejoinWatchFrames} frames uncovered, covered at the tap: {rejoinCoveredAtTap})");
            string matchRoom = typeof(OnlineRound).GetField("roomName", Any)?.GetValue(round) as string;
            Check(levels != null && levels.CurrentRoomName == matchRoom && Net.PositionPlacements > 0,
                  $"and when it goes, the match's own room is up and the player is where the server has them ({levels?.CurrentRoomName} / {matchRoom}, {Net.PositionPlacements} placements)");
        }
        else if (now - rejoinWatchFrom > 20f)
        {
            rejoinWatchFrom = -1f;
            Check(false, $"the rejoined round settled within 20 s (settled {settled}, covered {covered})");
        }
    }

    // ================= -playtestShopTabs: the shop's tabs and purchases, tapped =================
    //
    // Design B's shop (ShopScreen, 2026-11-24): the server's categories as tabs, the server's items
    // as cards, an item's page, the confirmation and the receipt, all tapped where a finger would.

    private static int shopStage;
    private static float shopAt;
    private static int tabMismatches, comingSoonToasts;
    private static readonly List<string> tabProblems = new List<string>();
    private static float purchaseTappedAt;
    private static int ammoBeforeBuy;

    private static Button ShopButton(string name) => FindByName(name) is { activeInHierarchy: true } go ? go.GetComponent<Button>() : null;

    private static IEnumerable<GameObject> ShownItemCards() =>
        Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.activeInHierarchy && g.name.StartsWith("Item_") && g.GetComponent<Button>() != null);

    private static string CardText(GameObject card) =>
        card != null ? string.Join(" | ", card.GetComponentsInChildren<Text>(false).Select(t => t.text)) : "none";

    // Every card shown is of the tab picked, and that tab is the one picked.
    private static void VerifyTab(string category, string when)
    {
        var problems = new List<string>();
        if (ShopScreen.CurrentCategory != category) problems.Add($"showing {ShopScreen.CurrentCategory}");
        int cards = 0;
        foreach (GameObject card in ShownItemCards())
        {
            cards++;
            string id = card.name.Substring(5);
            if (!ShopInventory.TryItem(id, out MetaItem item)) problems.Add($"{id} is not the server's");
            else if (ShopInventory.CategoryOf(item) != category) problems.Add($"{id} is in {ShopInventory.CategoryOf(item)}");
        }
        bool coins = category == "coins";
        int packs = Resources.FindObjectsOfTypeAll<GameObject>().Count(g => g.activeInHierarchy && g.name.StartsWith("Pack_"));
        if (coins && packs != ShopInventory.Packages.Count) problems.Add($"{packs} pack cards for {ShopInventory.Packages.Count} packs");
        int listed = ShopInventory.ItemsIn(category).Count(i => i.Kind != ItemKind.Ammo);   // packs: on their weapon's page
        if (!coins && cards != listed) problems.Add($"{cards} cards for {listed} items");
        if (problems.Count > 0) { tabMismatches++; tabProblems.Add($"{when}: {string.Join("; ", problems)}"); }
    }

    // Through an item's page: its Buy (or a quantity's), the confirmation, then the receipt's Done.
    private static bool shopStepped;

    private static bool TapBuyOnPage(string action)
    {
        Button buy = ShopButton("Action_" + action);
        return buy != null && buy.interactable && TapAt(buy) != null;
    }

    private static string tapLater;
    private static float tapLaterAt;

    // Taps a shop button a moment from now (after the picture just taken), as a finger would.
    private static void TapLater(string name)
    {
        tapLater = name;
        tapLaterAt = Time.realtimeSinceStartup + 0.3f;
    }

    private static void PlayShopTabs()
    {
        float now = Time.realtimeSinceStartup;
        if (tapLater != null)
        {
            if (now < tapLaterAt) return;
            GameObject target = FindByName(tapLater);
            Button button = target is { activeInHierarchy: true } ? target.GetComponent<Button>() : null;
            if (TapAt(button) == null) Check(false, $"{tapLater} is there to tap, and a tap reaches it");
            tapLater = null;
            shopAt = Mathf.Max(shopAt, now + 0.5f);
            return;
        }
        if (now < shopAt) return;
        if (Notices.Visible.Contains("Coming soon")) comingSoonToasts++;

        switch (shopStage)
        {
            case 0:     // Home's Shop button, as a finger taps it
                Button homeShop = FindByName("HomeCanvas")?.GetComponentsInChildren<Button>(false)
                    .FirstOrDefault(b => b.GetComponentsInChildren<Text>(true).Any(t => t.text == "Shop"));
                Check(homeShop != null && TapAt(homeShop) != null, "Home's Shop button is tapped");
                shopAt = now + 2.5f;
                shopStage = 1;
                return;

            case 1:
            {
                string tabs = string.Join(", ", ShopInventory.Categories.Select(c => c.Id));
                bool all = ShopInventory.Categories.All(c => ShopButton("Tab_" + c.Id) != null);
                Check(ShopScreen.IsOpen && all && ShopInventory.Categories.Count >= 5,
                      $"the shop opens with a tab for each of the server's categories ({tabs})");
                Shot("shop_open");
                TapLater("Tab_avatars");
                shopAt = now + 0.5f;
                shopStage = 2;
                return;
            }

            case 2:
                VerifyTab("avatars", "Characters");
                TapAt(ShopButton("Tab_coins"));
                shopAt = now + 0.6f;
                shopStage = 3;
                return;

            case 3:
                VerifyTab("coins", "Coins after Characters");
                Shot("shop_coins");
                TapLater("Tab_weapons");
                shopAt = now + 0.6f;
                shopStage = 4;
                return;

            case 4:
            {
                VerifyTab("weapons", "Weapons after Coins");
                List<GameObject> weaponCards = ShownItemCards().ToList();
                Check(weaponCards.Count > 1 && weaponCards.All(c => ShopInventory.TryItem(c.name.Substring(5), out MetaItem i) && i.Kind == ItemKind.Weapon)
                      && ShopButton("Filter_Ammo") == null,
                      $"the Weapons tab lists the weapons only, no ammo packs ({string.Join(", ", weaponCards.Select(c => c.name))})");
                Shot("shop_weapons");
                shopAt = now + 0.3f;
                shopStage = 6;
                return;
            }

            case 5:
            {
                List<GameObject> cards = ShownItemCards().ToList();
                bool onlyAmmo = cards.Count > 0 && cards.All(c => ShopInventory.TryItem(c.name.Substring(5), out MetaItem i) && i.Kind == ItemKind.Ammo);
                Check(onlyAmmo, $"Ammo shows the ammo packs only ({string.Join(", ", cards.Select(c => c.name))})");
                TapAt(ShopButton("Filter_All"));
                shopAt = now + 0.5f;
                shopStage = 6;
                return;
            }

            case 6:
            {
                // The roller's page: bought there when it is not owned yet.
                GameObject card = FindByName("Item_weapon-roller");
                Check(card != null && TapAt(card.GetComponent<Button>()) != null, $"the roller's card is tapped ({CardText(card)})");
                shopAt = now + 0.5f;
                shopStage = 7;
                return;
            }

            case 7:
            {
                string page = CardText(FindByName("Detail"));
                Check(FindByName("Detail") is { activeInHierarchy: true } && page.Contains("Cooldown") && page.Contains("Bullet size") && page.Contains("Rounds with it"),
                      $"its page shows how it shoots and the rounds it comes with ({(page.Length > 200 ? page.Substring(0, 200) + "..." : page)})");
                Shot("shop_detail");
                if (!shopStepped)
                {
                    // Previous / Next: the other weapons, without going back to the grid.
                    shopStepped = true;
                    Text place = FindByName("DetailPlace")?.GetComponent<Text>();
                    Check(ShopButton("DetailNext") != null && ShopButton("DetailPrevious") != null && place != null && place.text.Contains("/"),
                          $"the page has Previous and Next, and says where it is among the weapons ('{place?.text}')");
                    TapLater("DetailNext");
                    shopAt = now + 0.6f;
                    shopStage = 70;
                    return;
                }
                ammoBeforeBuy = ShopInventory.AmmoOf(TestWeapon);
                if (ShopButton("Action_Buy item") != null)
                {
                    TapLater("Action_Buy item");
                    shopAt = now + 0.4f;
                    shopStage = 8;
                    return;
                }
                // Owned already (bought before the match): its rounds are bought right here.
                Check(ShopButton("Action_Buy rounds") != null, "an owned weapon sells its rounds on its own page");
                shopAt = now + 0.3f;
                shopStage = 10;
                return;
            }

            case 70:
            {
                string page = CardText(FindByName("Detail"));
                Check(!page.Contains("Roller") && page.Contains("WEAPONS"), $"Next shows the next weapon ({(page.Length > 120 ? page.Substring(0, 120) + "..." : page)})");
                Shot("shop_detail_next");
                TapLater("DetailPrevious");
                shopAt = now + 0.6f;
                shopStage = 71;
                return;
            }

            case 71:
                Check(CardText(FindByName("Detail")).Contains("Roller"), "and Previous goes back to the roller");
                shopStage = 7;
                return;

            case 8:     // the confirmation, then the server's answer
            {
                Button confirm = ShopButton("ConfirmPurchase");
                string dialog = CardText(FindByName("Confirm purchase"));
                Check(confirm != null && dialog.Contains("Current balance") && dialog.Contains("Remaining balance"),
                      $"buying asks first, with the balance before and after ({dialog})");
                Shot("shop_confirm");
                purchaseTappedAt = now + 0.3f;
                TapLater("ConfirmPurchase");
                shopAt = now + 0.05f;
                shopStage = 9;
                return;
            }

            case 9:
            {
                Button done = ShopButton("ReceiptDone");
                if (done == null && now - purchaseTappedAt < 4f) return;
                Check(done != null && now - purchaseTappedAt < 2f, $"the receipt follows at once ({(now - purchaseTappedAt) * 1000f:F0} ms: {CardText(FindByName("Receipt"))})");
                Shot("shop_receipt");
                TapLater("ReceiptDone");
                shopAt = now + 1.5f;    // the listing refreshed: the weapon's rounds
                shopStage = 91;
                return;
            }

            case 91:
                Check(ShopInventory.AmmoOf(TestWeapon) == 10, $"a weapon bought comes with 10 rounds ({ShopInventory.AmmoOf(TestWeapon)})");
                ammoBeforeBuy = ShopInventory.AmmoOf(TestWeapon);
                Check(ShopButton("Action_Buy rounds") != null && FindByName("AmmoPack_ammo-roller") is { activeInHierarchy: true },
                      "its page now sells its rounds: the ammo pack and Buy rounds, without leaving it");
                Shot("shop_weapon_rounds");
                shopAt = now + 0.3f;
                shopStage = 10;
                return;

            case 10:    // the ammo's page: two packs, bought
            {
                Button more = FindByName("Actions")?.GetComponentsInChildren<Button>(false).FirstOrDefault(b => b.GetComponentsInChildren<Image>(true).Any(i => i.sprite != null && i.sprite.name == "Plus"));
                if (more != null && more.interactable) TapAt(more);
                shopAt = now + 0.3f;
                shopStage = 11;
                return;
            }

            case 11:
                if (!TapBuyOnPage("Buy rounds")) { Check(false, "the weapon's page has Buy rounds"); shopStage = 13; return; }
                shopAt = now + 0.4f;
                shopStage = 12;
                return;

            case 12:
            {
                Button confirm = ShopButton("ConfirmPurchase");
                if (confirm == null) { Check(false, $"buying ammo asks first ({CardText(FindByName("Modal"))})"); shopStage = 13; return; }
                purchaseTappedAt = now;
                TapAt(confirm);
                shopAt = now + 0.05f;
                shopStage = 121;
                return;
            }

            case 121:
            {
                Button done = ShopButton("ReceiptDone");
                if (done == null && now - purchaseTappedAt < 4f) return;
                Check(done != null, $"the ammo is bought ({CardText(FindByName("Receipt"))})");
                TapAt(done);
                shopAt = now + 1.5f;
                shopStage = 122;
                return;
            }

            case 122:
                Check(ShopInventory.AmmoOf(TestWeapon) == ammoBeforeBuy + 20, $"two packs add 20 rounds to the weapon ({ammoBeforeBuy} -> {ShopInventory.AmmoOf(TestWeapon)})");
                Check(Notices.ShownCount > 0, $"every purchase said so in a notice ('{Notices.Visible.Replace("\n", " / ")}')");
                Shot("shop_ammo_bought");
                TapLater("Back");      // the page, back to the cards
                shopAt = now + 0.4f;
                shopStage = 13;
                return;

            case 13:
                TapAt(ShopButton("Tab_shoes"));
                shopAt = now + 0.6f;
                shopStage = 14;
                return;

            case 14:
                VerifyTab("shoes", "Shoes");
                if (Shoes.Equipped != ShoeSpecs.Standard)
                {
                    // Worn from an earlier run: the standard pair first, so the sneakers can be put on by a tap.
                    if (purchaseTappedAt < shopAt) { purchaseTappedAt = now; Shoes.Equip(ShoeSpecs.Standard, null); }
                    if (now - purchaseTappedAt < 5f) return;
                }
                Shot("shop_shoes");
                TapLater("Item_shoes-sneakers");
                shopAt = now + 0.5f;
                shopStage = 15;
                return;

            case 15:
                // Their page: Buy (then Wear now on the receipt) or Wear.
                if (TapBuyOnPage("Buy item")) { shopAt = now + 0.4f; shopStage = 16; return; }
                Check(TapBuyOnPage("Wear") || ShopButton("Action_Worn") != null, "owned sneakers can be worn from their page");
                purchaseTappedAt = now;
                shopAt = now + 0.05f;
                shopStage = 18;
                return;

            case 16:
                purchaseTappedAt = now;
                TapAt(ShopButton("ConfirmPurchase"));
                shopAt = now + 0.05f;
                shopStage = 17;
                return;

            case 17:
            {
                Button wear = FindByName("Receipt")?.GetComponentsInChildren<Button>(false).FirstOrDefault(b => CardText(b.gameObject).Contains("Wear now"));
                if (wear == null && now - purchaseTappedAt < 4f) return;
                Check(wear != null && TapAt(wear) != null, "the sneakers are bought, and Wear now is tapped on the receipt");
                purchaseTappedAt = now;
                shopAt = now + 0.05f;
                shopStage = 18;
                return;
            }

            case 18:
            {
                if (Shoes.Equipped != 1 && now - purchaseTappedAt < 5f) return;
                Check(Shoes.Equipped == 1, $"the account wears them ({Shoes.Equipped}, '{Notices.Visible.Replace("\n", " / ")}')");
                JoystickMover mover = Object.FindAnyObjectByType<JoystickMover>();
                Check(mover != null && Mathf.Abs(mover.RunSpeed - mover.moveSpeed * 1.1f) < 0.01f, $"and the player runs 10% faster in them ({mover?.RunSpeed:F1})");
                Shot("shop_shoes_bought");
                purchaseTappedAt = now;
                Shoes.Equip(ShoeSpecs.Standard, null);   // as found
                shopAt = now + 0.05f;
                shopStage = 19;
                return;
            }

            case 19:
                if (Shoes.Equipped != ShoeSpecs.Standard && now - purchaseTappedAt < 5f) return;
                // A sale the test server runs (its server.json's "Sales": the Taunt pose 25% off).
                // The open shop goes to Poses (opened again, it would forget to bring Home back).
                if (ShopScreen.IsOpen)
                {
                    object shopNow = typeof(ShopScreen).GetField("instance", Any).GetValue(null);
                    typeof(ShopScreen).GetMethod("SelectCategory", Any).Invoke(shopNow, new object[] { "poses" });
                }
                else ShopScreen.Open(null, "poses");
                shopAt = now + 2.5f;
                shopStage = 20;
                return;

            case 20:
            {
                GameObject taunt = FindByName("Item_pose-taunt");
                string text = taunt != null ? CardText(taunt) : "none";
                ShopInventory.TryItem("pose-taunt", out MetaItem item);
                bool tag = taunt != null && taunt.GetComponentsInChildren<Transform>(false).Any(t => t.name == "SaleTag");
                Text usual = taunt != null ? taunt.GetComponentsInChildren<Text>(false).FirstOrDefault(t => t.name == "UsualPrice") : null;
                Check(item.OnSale && item.PercentOff == 25 && item.OriginalPriceCents == 250000 && item.PriceCents == 187500,
                      $"the server lists the Taunt pose on sale, 25% off 2 500 coins ({item.PriceCents} cents, usual {item.OriginalPriceCents}, '{item.SaleName}')");
                Check(tag && text.Contains("-25%") && text.Contains("1 875") && usual != null && usual.text == "2 500" && usual.transform.Find("Strike") != null,
                      $"its card shows -25%, the sale price and the usual one struck through ('{text}')");
                Shot("shop_sale");
                ShopScreen.ShowItem("pose-taunt");
                shopAt = now + 1f;
                shopStage = 21;
                return;
            }

            case 21:
            {
                string page = FindByName("Detail") is { activeInHierarchy: true } detail ? CardText(detail) : "";
                Check(page.Contains("Playtest Sale") && page.Contains("25% off") && page.Contains("1 875"),
                      $"its page names the sale and when it ends, with the sale price ('{(page.Length > 200 ? page.Substring(0, 200) + "..." : page)}')");
                Shot("shop_sale_item");
                Check(tabMismatches == 0, $"every tab shows its own cards ({(tabProblems.Count == 0 ? "all right" : string.Join(" | ", tabProblems))})");
                Check(comingSoonToasts == 0, $"no 'Coming soon' for an item on sale ({comingSoonToasts})");
                ShopScreen.CloseIfOpen();
                Check(!ShopScreen.IsOpen && FindByName("HomeCanvas") is { activeInHierarchy: true }, "closing the shop goes back to Home");
                Go(Step.Hub);
                return;
            }
        }
    }

    // ================= -playtestQuiet: the balance at launch, with nothing tapped =================
    //
    // A player who has been online before opens the game and touches nothing. Home has to show the
    // server's balance by itself, and no online screen may appear. With -playtestQuietOffline the
    // server cannot be reached: the game has to stop trying, show no number, and stay as it is for
    // Single Play.
    private static bool Quiet => System.Environment.GetCommandLineArgs().Any(a => a == "-playtestQuiet" || a == "-playtestQuietOffline");
    private static bool QuietOffline => System.Environment.GetCommandLineArgs().Contains("-playtestQuietOffline");

    private static bool quietJudged;

    private static void PlayQuiet()
    {
        Text home = HomeCoinLabel();
        bool noScreens = Object.FindAnyObjectByType<OnlineSignIn>() == null && Object.FindAnyObjectByType<OnlineLobby>() == null &&
                         !ShopScreen.IsOpen;

        if (!QuietOffline)
        {
            if (!quietJudged)
            {
                if (!Awaited(CoinBalance.Known, 12f, "signed in at launch with nothing tapped")) return;
                quietJudged = true;
                Check(home != null && home.text == CoinBalance.Text && home.text != CoinBalance.Unknown, $"Home shows the server's balance ('{home?.text}', {Net?.CoinCents} cents)");
                Check(noScreens, "no sign-in, lobby or shop screen appeared");
                Check(FindByName("HomeCanvas") is { activeInHierarchy: true } && !(FindByName("RoleSelectCanvas") is { activeInHierarchy: true }), "Home is still the screen showing");
                Check(Net != null && !Net.InMatch && !NetBootstrap.Asked, "nothing online was started beyond signing in");
                return;
            }

            // Left alone a little longer: still signed in, still quiet.
            if (InStep < 6f) return;
            Check(CoinBalance.Known && noScreens, "and it stays that way");
        }
        else
        {
            // The quiet sign-in gives up after NetBootstrap.QuietSignInSeconds.
            if (InStep < NetBootstrap.QuietSignInSeconds + 4f) return;
            Check(!CoinBalance.Known && home != null && home.text == CoinBalance.Unknown, $"with no server in reach Home shows no number ('{home?.text}')");
            Check(Net != null && !Net.IsConnected && !Net.IsInvoking("Connect"), "the game stopped trying to connect");
            Check(noScreens, "and showed no online screen or error");
            if (ClimbTest)
            {
                Go(Step.Climb);
                return;
            }
            if (SinglePlay)
            {
                // Single Play with no server at all (2026-11-13).
                Go(Step.SinglePlay);
                return;
            }
        }

        Check(errorsLogged == 0, $"no errors logged ({errorsLogged}{(errors.Count > 0 ? ": " + string.Join(" | ", errors) : "")})");
        Finish();
    }

    // ================= -playtestWallet: coins from the Shop, and every coin label =================
    //
    // Against a server running the mock wallet and giving new accounts no coins. There is no wallet
    // screen any more (2026-11-25): a tap on any coin balance opens the Shop on its Coins. A coin
    // pack is paid on the wallet's own page (this test presses its Pay button over HTTP, as the
    // browser would), then the ledger on the History screen, and a shop purchase. After each, every
    // label that shows coins has to show the server's balance without anyone refreshing it by
    // hand, and none may show a number of its own.
    private static bool Wallet => System.Environment.GetCommandLineArgs().Contains("-playtestWallet");

    private static int walletStage, walletStageSeen = -1;
    private static string walletHomeBefore, walletOpenedUrl;
    private static float walletWaitUntil, walletNotBefore;
    private static System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> walletPay;
    private static bool walletBought;
    private static long walletAfterPurchase = -1;
    private static string WalletAfterText => CoinBalance.Format(walletAfterPurchase);
    private static readonly System.Net.Http.HttpClient walletHttp = new System.Net.Http.HttpClient();

    // Home's balance: design B's pill on Home (2026-11-24), the scene's own label before it is built.
    private static Text HomeCoinLabel()
    {
        Text pill = FindByName("HomeBalance")?.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.GetComponent<CoinLabel>() != null);
        if (pill != null) return pill;
        HomeMenuUI menu = Object.FindAnyObjectByType<HomeMenuUI>(FindObjectsInactive.Include);
        return menu != null ? Get<Text>(menu, "coinText") : null;
    }

    // The shop's Coins category: the one the server marks "coins".
    private static string CoinsCategoryId() =>
        ShopInventory.Categories.FirstOrDefault(c => c.Special == "coins").Id;

    // The balance over the open shop.
    private static Text ShopCoinLabel() =>
        FindByName("ShopScreenCanvas")?.GetComponentsInChildren<Text>(false).FirstOrDefault(t => t.GetComponent<CoinLabel>() != null);

    /// <summary>Every text showing on the history screen, joined.</summary>
    private static string HistoryShown()
    {
        GameObject root = FindByName("HistoryScreen");
        if (root == null || !root.activeInHierarchy) return "";
        return string.Join(" | ", root.GetComponentsInChildren<Text>(false).Select(t => t.text));
    }

    /// <summary>True once <paramref name="done"/>; after the wait runs out it fails the check and goes on.</summary>
    private static bool Awaited(bool done, float seconds, string what)
    {
        if (walletWaitUntil == 0f) walletWaitUntil = Time.realtimeSinceStartup + seconds;
        if (!done && Time.realtimeSinceStartup < walletWaitUntil) return false;

        walletWaitUntil = 0f;
        Check(done, done ? what : $"{what} (not within {seconds:F0} s)");
        return true;
    }

    /// <summary>The coin pill of a scene canvas (they are all called CoinText), shown or hidden.</summary>
    private static Text CoinPillOf(string canvas)
    {
        GameObject root = FindByName(canvas);
        return root == null ? null : root.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.gameObject.name == "CoinText");
    }

    private static void PlayWallet()
    {
        // A player's pace, near enough: a moment between one step and the next. Without it this
        // test taps faster than the server lets any one connection ask.
        if (walletStage != walletStageSeen)
        {
            walletStageSeen = walletStage;
            walletNotBefore = Time.realtimeSinceStartup + 0.8f;
        }
        if (Time.realtimeSinceStartup < walletNotBefore) return;

        OnlineLobby lobby = Object.FindAnyObjectByType<OnlineLobby>();

        switch (walletStage)
        {
            case 0:
            {
                Text home = HomeCoinLabel();
                Check(walletHomeBefore == CoinBalance.Unknown, $"before signing in, Home shows no number of its own ('{walletHomeBefore}')");
                Check(CoinBalance.Known && CoinBalance.Cents == Net.CoinCents, "signed in: the balance is the server's");
                Check(Net.CoinCents == 0, $"a new account starts with what the server gives it, here nothing ({Net.CoinCents})");
                Check(home != null && home.text == CoinBalance.Text, $"Home shows it ('{home?.text}')");
                Check(home != null && home.GetComponent<Button>() != null, "tapping the Home balance opens the Shop's Coins");

                PendingTopUp.OpenUrl = url => walletOpenedUrl = url;     // this test is the browser
                OnlineLobby.Open(profile, null);                         // shows the role screen
                lobby = Object.FindAnyObjectByType<OnlineLobby>();

                // The role screen's own coin pill (saved in the scene with "1000" typed into it) is hidden
                // since the play setup went over it (2026-11-23); shown, it would have to be the server's.
                Text rolePill = CoinPillOf("RoleSelectCanvas");
                Check(rolePill == null || !rolePill.gameObject.activeInHierarchy || rolePill.text == CoinBalance.Text,
                      $"the role screen's coin pill is hidden or shows the server's balance, never the scene's number ('{rolePill?.text}', shown {rolePill != null && rolePill.gameObject.activeInHierarchy})");

                typeof(OnlineLobby).GetMethod("ShowSetup", Any).Invoke(lobby, new object[] { null });
                walletStage = 1;
                return;
            }

            case 1:
            {
                // No wallet screen (2026-11-25): the coin balance over the setup opens the Shop's Coins.
                Text pill = SetupCoinLabel();
                if (!Awaited(FindByName("Extra_HISTORY") != null, 8f, "the play setup is up")) return;
                Check(FindByName("Extra_WALLET") == null, "the play setup has no WALLET button");
                Check(pill != null && pill.gameObject.activeInHierarchy && pill.text == "0", $"the coin balance over the setup shows the same balance ('{pill?.text}')");
                Button open = pill != null ? pill.GetComponent<Button>() : null;
                Check(open != null, "the balance can be tapped");
                if (open != null) open.onClick.Invoke();
                walletStage = 3;
                return;
            }

            case 3:
            {
                bool onCoins = ShopScreen.IsOpen && ShopInventory.Loaded && ShopInventory.TopUpsLoaded && ShopScreen.CurrentCategory != null
                               && ShopScreen.CurrentCategory == CoinsCategoryId();
                if (!Awaited(onCoins, 15f, "tapping it opened the Shop on its Coins")) return;
                Check(FindByName("WalletScreen") == null && Resources.FindObjectsOfTypeAll<Canvas>().All(c => c.name != "OnlineWalletCanvas"),
                      "there is no wallet screen any more");
                Button pack = FindByName("Pack_coins-550")?.GetComponent<Button>();
                Check(pack != null && pack.gameObject.activeInHierarchy, "the Coins list the server's 550 coin pack");
                Shot("shop_coins");
                if (pack != null) pack.onClick.Invoke();
                walletStage = pack != null ? 4 : 99;
                return;
            }

            case 4:
            {
                if (!Awaited(walletOpenedUrl != null, 10f, "the deposit opens the wallet's checkout page")) return;
                if (walletOpenedUrl == null) { walletStage = 99; return; }

                Check(walletOpenedUrl.Contains("/checkout/"), $"at {walletOpenedUrl}");
                Check(Net.CoinCents == 0, "nothing is credited for opening a payment");
                walletPay = walletHttp.PostAsync(walletOpenedUrl.Replace("localhost", "192.168.15.150") + "/pay", new System.Net.Http.StringContent(""));
                walletStage = 5;
                return;
            }

            case 5:
                if (!Awaited(walletPay.IsCompleted, 10f, "the wallet's Pay button answered")) return;
                Check(walletPay.IsCompleted && !walletPay.IsFaulted && walletPay.Result.IsSuccessStatusCode, "paid on the wallet's page");
                walletStage = 6;
                return;

            case 6:
            {
                // Nothing here told the game it was paid: the wallet told the server, and the game asked.
                if (!Awaited(Net.CoinCents == 55000, 25f, "the deposit reached the balance")) return;
                Text shopCoins = ShopCoinLabel();
                Check(shopCoins != null && shopCoins.text == "550", $"the shop's balance shows 550 coins ('{shopCoins?.text}', {Net.CoinCents})");
                Check(CoinNotice.Shown > 0 && CoinNotice.Visible.Contains("550"),
                      $"a notice over everything confirms the coins were added ('{CoinNotice.Visible.Replace("\n", " / ")}')");
                // The ledger is the History screen's (Multiplayer's History tab).
                ShopScreen.CloseIfOpen();
                OnlineHistory.Show(null, OnlineHistory.Tab.Wallet, null);
                walletStage = 8;
                return;
            }

            case 8:
            {
                if (!Awaited(HistoryShown().Contains("Deposit (coins-550)"), 10f, "it opens WALLET HISTORY, which lists the deposit")) return;
                Check(HistoryShown().Contains("+550 | 550"), $"with its amount and the balance after it ({HistoryShown()})");
                Check(FindByName("HistoryScreen")?.GetComponentsInChildren<Transform>(false).Any(t => t.name == "Record") != true,
                      "and no match records under it");
                walletStage = 11;
                return;
            }

            case 11:
                // A purchase made elsewhere in the game while the wallet and its history are open.
                Meta.Purchase(new List<MetaPurchaseLine> { new MetaPurchaseLine { ItemId = "next-player", Quantity = 1 } }, receipt =>
                {
                    bool done = !receipt.TimedOut && receipt.Error == MetaError.None && receipt.Status == PurchaseStatus.Completed;
                    Check(done, $"bought Next Player ({(receipt.Error == MetaError.None ? receipt.Status.ToString() : receipt.Error.ToString())})");
                    walletAfterPurchase = receipt.CoinCents;
                    walletBought = true;
                });
                walletStage = 12;
                return;

            case 12:
                if (!Awaited(walletBought, 10f, "the purchase was answered")) return;
                walletStage = 23;
                return;

            case 23:
            {
                if (!Awaited(HistoryShown().Contains("Shop: next-player"), 10f, "the open history lists the purchase as it happens")) return;
                Check(walletAfterPurchase >= 0 && walletAfterPurchase < 55000 && Net.CoinCents == walletAfterPurchase,
                      $"the purchase took its price ({Net.CoinCents}, the receipt says {walletAfterPurchase})");
                Text historyCoins = FindByName("HistoryScreen")?.GetComponentsInChildren<Text>(false).FirstOrDefault(t => t.GetComponent<CoinLabel>() != null);
                Check(historyCoins == null || historyCoins.text == WalletAfterText, $"the balance over the history followed, with no refresh asked for ('{historyCoins?.text}', {WalletAfterText})");
                Object.FindAnyObjectByType<OnlineHistory>()?.SendMessage("Close");
                if (!(FindByName("PlaySetup") is { activeInHierarchy: true })) typeof(OnlineLobby).GetMethod("ShowSetup", Any).Invoke(lobby, new object[] { null });
                walletStage = 13;
                return;
            }

            case 13:
            {
                GameObject rooms = FindByName("PlaySetup");
                if (!Awaited(rooms != null && rooms.activeInHierarchy, 5f, "the play setup is there after the history")) return;
                Text pill = SetupCoinLabel();
                Check(pill != null && pill.text == WalletAfterText, $"and the balance over it is the new one ('{pill?.text}')");

                Call(lobby, "GoHome");
                walletStage = 14;
                return;
            }

            case 14:
            {
                Text home = HomeCoinLabel();
                if (!Awaited(home != null && home.gameObject.activeInHierarchy, 5f, "back on Home")) return;
                Check(home != null && home.text == WalletAfterText, $"Home shows it ('{home?.text}')");

                // The role screen again, as Single Play's Play button shows it.
                HomeUI homeUi = Object.FindAnyObjectByType<HomeUI>();
                if (homeUi != null) homeUi.ShowRoleSelect();
                Text rolePill = CoinPillOf("RoleSelectCanvas");
                Check(rolePill == null || !rolePill.gameObject.activeInHierarchy || rolePill.text == WalletAfterText,
                      $"the role screen's own pill, if shown, shows it too ('{rolePill?.text}')");
                HomeMenuUI menu = Object.FindAnyObjectByType<HomeMenuUI>(FindObjectsInactive.Include);
                if (menu != null) menu.ShowHome();

                ShopUI shop = Object.FindAnyObjectByType<ShopUI>(FindObjectsInactive.Include);
                if (shop != null) shop.OpenFrom(null, "coins");
                walletStage = 15;
                return;
            }

            case 15:
            {
                if (!Awaited(ShopScreen.IsOpen && ShopInventory.TopUpsLoaded && ShopInventory.Loaded && InStep > 1f, 10f, "the shop loaded its listing")) return;

                Text shopCoins = FindByName("ShopScreenCanvas")?.GetComponentsInChildren<Text>(false).FirstOrDefault(t => t.GetComponent<CoinLabel>() != null);
                Check(shopCoins != null && shopCoins.text == WalletAfterText, $"the shop shows it ('{shopCoins?.text}')");
                Check(shopCoins != null && shopCoins.GetComponent<Button>() != null, "and its balance goes to its Coins too");

                // The coin pack cards: the server's amounts, as the server lists its packs.
                var amounts = Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.activeInHierarchy && g.name.StartsWith("Pack_"))
                    .Select(g => g.GetComponentsInChildren<Text>(false).Select(t => t.text).FirstOrDefault(t => t.EndsWith("coins")) ?? "").ToList();
                Check(amounts.Count == ShopInventory.Packages.Count && amounts.All(a => a == "100 coins" || a == "550 coins" || a == "1 200 coins"),
                      $"coin pack cards show the server's amounts only ({string.Join(", ", amounts.Select(a => a == "" ? "(none)" : a))})");
                ShopScreen.CloseIfOpen();

                MatchmakingUI matchmaking = Object.FindAnyObjectByType<MatchmakingUI>(FindObjectsInactive.Include);
                if (matchmaking != null)
                {
                    matchmaking.BeginOnline(false, "");
                    Text pill = Get<Text>(matchmaking, "coinLabelText");
                    string during = pill?.text;
                    bool shown = pill != null && pill.gameObject.activeInHierarchy;
                    matchmaking.EndOnline();
                    Check(pill != null && pill.GetComponent<CoinLabel>() != null && during == WalletAfterText && pill.text == WalletAfterText,
                          $"the matchmaking screen's coin pill is the server's balance, online and after ('{during}', '{pill?.text}')");
                    if (!shown) report.Add("INFO  the matchmaking screen's coin pill is switched off in the scene; it is bound for when it is shown");
                }
                else report.Add("INFO  no matchmaking screen in the scene");

                // Nothing left on any screen with the number the scene was saved with.
                var stale = Resources.FindObjectsOfTypeAll<Text>()
                    .Where(t => t.gameObject.scene.IsValid() && t.gameObject.name == "CoinText" && t.GetComponent<CoinLabel>() == null
                                && t.GetComponentInParent<ShopUI>(true) == null)   // the scene's old shop panel, never shown since design B's shop
                    .Select(t => UiFind.PathOf(t.transform)).ToList();
                Check(stale.Count == 0, $"every coin pill in the scene follows the server's balance ({(stale.Count == 0 ? "none left out" : string.Join(", ", stale))})");

                walletStage = 99;
                return;
            }

            default:
                PendingTopUp.OpenUrl = Application.OpenURL;
                Check(errorsLogged == 0, $"no errors logged ({errorsLogged}{(errors.Count > 0 ? ": " + string.Join(" | ", errors) : "")})");
                Finish();
                return;
        }
    }

    // ================= Finish =================

    private static void Finish()
    {
        step = Step.Done;
        SessionState.SetBool(ActiveKey, false);
        EditorApplication.update -= Tick;
        Application.logMessageReceived -= CountErrors;
        ShopInventory.SetEquippedWeapon(0);                   // leave the editor's weapon as found
        PlayerPrefs.SetInt(CharacterPrefsKey, SessionState.GetInt(CharacterKey, 0));   // and its character
        PlayerPrefs.Save();
        if (Lan) OnlineConfig.SaveHostOverride("");             // the LAN search remembered what it found

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

    // Where a UI element is on the screen, in pixels.
    private static Rect ScreenRect(RectTransform rt)
    {
        if (rt == null) return default;
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        Vector2 a = RectTransformUtility.WorldToScreenPoint(null, corners[0]), b = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
        return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
    }

    private static string DockText(Button dock) =>
        dock != null ? string.Join(" / ", dock.GetComponentsInChildren<Text>(true).Select(t => t.text)) : "none";

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
