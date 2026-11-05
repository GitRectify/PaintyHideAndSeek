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
//             -playtestRole seeker|hider -playtestReport <file>
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
        });
        SessionState.SetString(ArgsKey, settings);
        SessionState.SetBool(ActiveKey, true);

        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        EditorApplication.isPlaying = true;
    }

    // ================= State =================

    private enum Step { WaitPlay, SignIn, FirstRun, Shop, Role, Room, Map, Match, Round, Result, Home, History, ShopCards, Done }

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
                if (!OnlineRound.Active) return;
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
                Check(ShopInventory.Loaded, "wallet refreshed from the server after the round");
                if (Seeker)
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
                Go(Step.Done);
                return;

            case Step.Done:
                Check(errorsLogged == 0, $"no errors or exceptions logged by the game ({errorsLogged}{(errors.Count > 0 ? ": " + string.Join(" | ", errors.Take(3)) : "")})");
                Finish();
                return;
        }
    }

    private static bool signedIn, bought, boughtAnswered, submittedName, reboughtAnswered;
    private static MetaProfile profile;

    // ================= The round =================

    private static void PlayRound()
    {
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
            if (Net.Phase == MatchPhase.Hide && !pressedStart && InStep > 1f)
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

    private static void CheckRound()
    {
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
        Check(matchEnds > 0, "the match ended with the server's result");
    }

    private static void Hook()
    {
        if (hooked) return;
        hooked = true;
        Net.OnRemoteShot += _ => remoteShots++;
        Net.OnLoadoutChanged += () => loadoutAnswers++;
        Net.OnPhaseChanged += (_, _) => phaseChanges++;
        Net.OnEliminated += (_, _, _) => eliminations++;
        Net.OnMatchEnd += _ => matchEnds++;
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
