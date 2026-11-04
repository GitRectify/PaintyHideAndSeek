using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// An online round in the 3D scene, from MatchStart to the result popup. Adapted from the SDK
// sample's OnlineRound; its paint, weapon and pose parts come in later phases.
//
// The existing gameplay scripts keep doing what they do — JoystickMover moves the player,
// CameraController follows, MatchHUD draws the timer — and this class connects them to the server:
//   * GameModeManager is switched into its network mode (BeginNetworkRound): the map is the one the
//     server picked, no bot is started, and the timer, counters and seeker freeze come from here.
//   * NetEntityRegistry.ViewFactory makes characters as snapshots name them: the local player gets
//     NetEntityView (server corrections) and NetInputAdapter (joystick/camera/buttons -> input
//     stream); every other player is a Bot prefab body placed by interpolation.
//   * The gun calls Fire below instead of its local aim-assisted kill; the server judges the hit.
//   * Eliminations, round end and match end come from the server; nothing here decides a hit, a
//     catch or a winner.
public class OnlineRound : MonoBehaviour
{
    private static OnlineRound _instance;

    /// <summary>True from Begin to End: an online round (or its result popup) is on screen.</summary>
    public static bool Active => _instance != null && _instance.active;

    private static MecchaNetClient Net => MecchaNetClient.Instance;

    [Tooltip("Resources path of the body used for other players.")]
    [SerializeField] private string remotePrefabPath = "GameObjects/Bot";

    private OnlineLobby lobby;
    private GameModeManager gmm;
    private HomeUI home;
    private HideModeResultUI results;
    private JoystickMover mover;
    private WallClimber climber;
    private Mode1FirstPersonGun gun;
    private ChameleonPaint paint;
    private GameObject remotePrefab;

    private bool active;
    private bool hunter;
    private bool resultShown;
    private bool localOut;
    private MecchaNetClient.RosterEntry[] roster;
    private readonly HashSet<byte> eliminated = new HashSet<byte>();

    private float phaseEndsAt;
    private int phaseTotal;
    private bool phaseRunning;

    private Transform localBody;
    private NetInputAdapter adapter;
    private NetEntityView localView;
    private float fireReleaseAt;

    private bool leaveArmed;
    private float leaveArmedUntil;

    private Text banner;
    private float bannerUntil;

    // ================= Entry points for the gameplay scripts =================

    /// <summary>The gun's shot, in the direction drawn on screen this frame.</summary>
    public static void Fire(Vector3 worldDirection)
    {
        if (!Active) return;
        _instance.DoFire(worldDirection);
    }

    /// <summary>The in-round Home (HomeUI.GoHome during an online round).</summary>
    public static void RequestLeave()
    {
        if (!Active) return;
        _instance.AskLeave();
    }

    // ================= Lifecycle =================

    private void Awake()
    {
        _instance = this;
        BuildBanner();
    }

    private void OnDestroy()
    {
        if (active) End();
        if (_instance == this) _instance = null;
    }

    /// <summary>
    /// Sets the scene up for the match the server just started. Safe to call again for the next
    /// match (a free room re-queues).
    /// </summary>
    public void Begin(OnlineLobby owner, string mapName, MecchaNetClient.RosterEntry[] entries)
    {
        if (active) End();

        lobby = owner;
        gmm = FindAnyObjectByType<GameModeManager>();
        home = FindAnyObjectByType<HomeUI>();
        results = FindAnyObjectByType<HideModeResultUI>();
        mover = FindAnyObjectByType<JoystickMover>();
        climber = FindAnyObjectByType<WallClimber>(FindObjectsInactive.Include);
        gun = FindAnyObjectByType<Mode1FirstPersonGun>(FindObjectsInactive.Include);
        paint = FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include);
        if (remotePrefab == null) remotePrefab = Resources.Load<GameObject>(remotePrefabPath);

        active = true;
        hunter = Net.LocalRole == Role.Hunter;
        resultShown = false;
        localOut = false;
        leaveArmed = false;
        roster = entries;
        eliminated.Clear();
        if (results != null) results.HideAll();

        if (home != null) home.EnterOnlineRound(hunter);
        if (gmm != null) gmm.BeginNetworkRound(hunter, mapName);

        localBody = PlayerRef.ResolveTransform("Player");
        NetEntityRegistry.ViewFactory = MakeView;

        Net.OnPhaseChanged += HandlePhaseChanged;
        Net.OnEliminated += HandleEliminated;
        Net.OnRoundEnd += HandleRoundEnd;
        Net.OnMatchEnd += HandleMatchEnd;
        Net.OnIdleWarning += HandleIdleWarning;
        Net.OnSpotAlert += HandleSpotAlert;
        Net.OnRosterChanged += HandleRosterChanged;

        // Start from the phase the client last heard (the ready gate, normally).
        HandlePhaseChanged(Net.Phase, Net.PhaseSecondsLeft);
        RefreshCounters();
        ShowBanner(hunter ? "You are a SEEKER. Wait for the hiders to hide..." : "You are a HIDER. Paint yourself and hide!", 4f);
    }

    /// <summary>Takes the scene out of online mode. The caller decides where to go next.</summary>
    public void End()
    {
        if (!active) return;
        active = false;

        if (Net != null)
        {
            Net.OnPhaseChanged -= HandlePhaseChanged;
            Net.OnEliminated -= HandleEliminated;
            Net.OnRoundEnd -= HandleRoundEnd;
            Net.OnMatchEnd -= HandleMatchEnd;
            Net.OnIdleWarning -= HandleIdleWarning;
            Net.OnSpotAlert -= HandleSpotAlert;
            Net.OnRosterChanged -= HandleRosterChanged;
        }

        NetEntityRegistry.ViewFactory = null;
        NetEntityRegistry.Clear();   // destroys the remote bodies

        // The local player is the scene's own character: strip the network parts back off so
        // Single Play drives it exactly as before.
        if (adapter != null) Destroy(adapter);
        if (localView != null) Destroy(localView);
        adapter = null;
        localView = null;

        if (results != null) results.ClearOnline();
        if (gmm != null) gmm.EndNetworkRound();
        banner.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (!active) return;

        UpdateTimer();
        FeedInput();
        RefreshCounters();

        if (leaveArmed && Time.unscaledTime > leaveArmedUntil) leaveArmed = false;
        if (banner.gameObject.activeSelf && Time.unscaledTime > bannerUntil) banner.gameObject.SetActive(false);
    }

    // ================= Characters =================

    private NetEntityView MakeView(byte slot, Role role, bool isLocal)
    {
        if (isLocal)
        {
            if (localBody == null) return null;

            localView = localBody.GetComponent<NetEntityView>();
            if (localView == null) localView = localBody.gameObject.AddComponent<NetEntityView>();
            localView.Initialise(slot, role, true);

            adapter = localBody.GetComponent<NetInputAdapter>();
            if (adapter == null) adapter = localBody.gameObject.AddComponent<NetInputAdapter>();
            return localView;
        }

        if (remotePrefab == null)
        {
            Debug.LogWarning($"[Online] Resources/{remotePrefabPath} not found; other players are invisible.");
            return null;
        }

        GameObject parent = GameObject.Find("Characters");
        GameObject body = Instantiate(remotePrefab, Vector3.down * 500f, Quaternion.identity,
                                      parent != null ? parent.transform : null);
        body.name = "Net_" + slot;
        body.SetActive(true);

        NetEntityView view = body.AddComponent<NetEntityView>();
        view.Initialise(slot, role, false);

        // Name tags only over teammates: a name floating above a camouflaged hider would give
        // the whole game away.
        BotNameTag tag = body.GetComponentInChildren<BotNameTag>(true);
        if (tag != null)
        {
            bool teammate = (role == Role.Hunter) == hunter;
            tag.SetName(NameOf(slot));
            tag.SetVisible(teammate);
        }

        return view;
    }

    // ================= Input =================

    private void FeedInput()
    {
        if (adapter == null) return;

        adapter.SetClimbUp(climber != null && climber.ClimbUpHeld);
        adapter.SetClimbDown(climber != null && climber.ClimbDownHeld);
        adapter.SetProne(gun != null && gun.IsProne);
        adapter.SetJump(mover != null && Time.unscaledTime - mover.LastJumpTime < 0.15f);
        adapter.SetPaintPanelOpen(paint != null && paint.DrawingActive);

        if (fireReleaseAt > 0f && Time.unscaledTime > fireReleaseAt)
        {
            adapter.FireReleased();
            fireReleaseAt = 0f;
        }
    }

    private void DoFire(Vector3 worldDirection)
    {
        if (localOut) return;

        // The shot itself is the Fire message (direction + the tick on screen); the held flag only
        // animates the shooter for others, so it is released a moment later.
        if (adapter != null) adapter.FirePressed(worldDirection);
        else Net.Fire(worldDirection);
        fireReleaseAt = Time.unscaledTime + 0.2f;
    }

    // ================= Phases, timer, counters =================

    private void HandlePhaseChanged(MatchPhase phase, int secondsLeft)
    {
        phaseTotal = Mathf.Max(1, secondsLeft);
        phaseEndsAt = Time.unscaledTime + secondsLeft;
        phaseRunning = !Net.PhasePaused && (phase == MatchPhase.Hide || phase == MatchPhase.Seek);

        if (gmm != null)
        {
            gmm.NetSeekerFrozen = phase == MatchPhase.Hide || phase == MatchPhase.Lobby;
            gmm.NetTimerSeconds = secondsLeft;
            gmm.NetTimerFraction = 0f;
        }

        if (Net.PhasePaused) ShowBanner("Paused: a seat is being refilled.", 3f);
        else if (phase == MatchPhase.Hide && hunter) ShowBanner("Hiders are hiding. You are frozen until the seek starts.", 3f);
        else if (phase == MatchPhase.Seek) ShowBanner(hunter ? "GO! Find the hiders." : "The seekers are coming. Stay hidden!", 3f);
    }

    private void UpdateTimer()
    {
        if (gmm == null || !phaseRunning) return;

        float left = Mathf.Max(0f, phaseEndsAt - Time.unscaledTime);
        gmm.NetTimerSeconds = Mathf.CeilToInt(left);
        gmm.NetTimerFraction = 1f - left / phaseTotal;
    }

    private void HandleRosterChanged(MecchaNetClient.RosterEntry[] entries)
    {
        roster = entries;
    }

    private void RefreshCounters()
    {
        if (gmm == null || roster == null) return;

        int fugitives = 0;
        int hunters = 0;
        foreach (MecchaNetClient.RosterEntry e in roster)
        {
            // Snapshots carry the Eliminated flag too: it covers anyone whose elimination event
            // was missed.
            NetEntityView view = NetEntityRegistry.Get(e.Slot);
            if (view != null && view.Eliminated) eliminated.Add(e.Slot);
            if (eliminated.Contains(e.Slot)) continue;

            if (e.Role == Role.Hunter) hunters++;
            else if (e.Role == Role.Fugitive) fugitives++;
        }

        gmm.NetFugitivesLeft = fugitives;
        gmm.NetHuntersLeft = hunters;
    }

    // ================= Eliminations =================

    private void HandleEliminated(byte victim, byte by, EliminationCause cause)
    {
        eliminated.Add(victim);

        NetEntityView view = NetEntityRegistry.Get(victim);
        NetEntityView killer = NetEntityRegistry.Get(by);
        Transform body = victim == Net.LocalSlot ? localBody : view != null ? view.transform : null;

        if (body != null && (cause == EliminationCause.Shot || cause == EliminationCause.Caught))
        {
            Vector3 from = killer != null ? killer.transform.position : body.position - body.forward;
            Vector3 push = body.position - from;
            push.y = 0f;
            Ragdoll.Trigger(body, body.position + Vector3.up, push.sqrMagnitude > 0.001f ? push.normalized : -body.forward,
                            1f, victim != Net.LocalSlot);
        }

        if (victim == Net.LocalSlot)
        {
            localOut = true;
            if (gmm != null) gmm.PlayerFrozen = true;
            ShowBanner(cause == EliminationCause.Shot ? "You were shot! Watch the rest of the round."
                                                      : "You were caught! Watch the rest of the round.", 5f);
        }
        else if (by == Net.LocalSlot)
        {
            ShowBanner($"You got {NameOf(victim)}!", 2.5f);
        }
        else
        {
            ShowBanner($"{NameOf(victim)} is out.", 2f);
        }
    }

    private void HandleSpotAlert(byte slot)
    {
        if (slot == Net.LocalSlot && !hunter) ShowBanner("A seeker has spotted you! Move!", 2f);
    }

    private void HandleIdleWarning(int seconds)
    {
        ShowBanner($"Move or you will be removed for being idle in {seconds}s.", 4f);
    }

    // ================= Results =================

    private void HandleRoundEnd(MecchaNetClient.RoundEndInfo info)
    {
        phaseRunning = false;
        if (gmm != null) gmm.PlayerFrozen = true;

        string score = hunter ? info.Eliminations.ToString() : info.SurvivalSeconds + "s";
        ShowResult(info.Won, info.Rank, score, 0);
    }

    private void HandleMatchEnd(MecchaNetClient.MatchEndInfo info)
    {
        phaseRunning = false;
        if (gmm != null) gmm.PlayerFrozen = true;

        string score = hunter ? info.Eliminations.ToString() : info.SurvivalSeconds + "s";
        ShowResult(info.Won, info.Rank, score, info.CoinCentsAwarded);

        if (info.Status != MatchStatus.Completed && info.Status != MatchStatus.Reconnected)
            ShowBanner(StatusText(info.Status), 6f);
        else if (!string.IsNullOrEmpty(info.Explanation))
            ShowBanner(info.Explanation, 8f);
    }

    private void ShowResult(bool won, int rank, string score, long coinCents)
    {
        resultShown = true;
        string rankText = rank > 0 ? $"#{rank}/8" : "-";

        if (results != null)
        {
            results.ShowOnlineResult(won, rankText, score, coinCents,
                                     () => lobby.GoHomeFromRound(),
                                     () => lobby.PlayAgainFromRound());
        }
        else
        {
            // No result popup in this scene: say it in the banner.
            ShowBanner(won ? "You won!" : "You lost.", 5f);
        }
    }

    private static string StatusText(MatchStatus status)
    {
        switch (status)
        {
            case MatchStatus.IdleQuit: return "You were removed for being idle.";
            case MatchStatus.Quit: return "You left the match.";
            case MatchStatus.DisconnectExpired: return "You were disconnected for too long.";
            case MatchStatus.VoidRefund: return "The match was cancelled. Your entry was refunded.";
            case MatchStatus.VoidForfeit: return "The match was cancelled; leaving early forfeited your entry.";
            case MatchStatus.RefundedPreStart: return "You left before the start. Your entry was refunded.";
            case MatchStatus.HeldForReview: return "This result is being reviewed before it is paid.";
            case MatchStatus.CaughtThenLeft: return "You left after being caught.";
            default: return status.ToString();
        }
    }

    // Leaving a running round is a quit, so the in-round Home asks twice.
    private void AskLeave()
    {
        if (resultShown) { lobby.GoHomeFromRound(); return; }

        if (!leaveArmed)
        {
            leaveArmed = true;
            leaveArmedUntil = Time.unscaledTime + 3f;
            ShowBanner("Tap Home again to leave. Leaving now counts as quitting this round.", 3f);
            return;
        }

        lobby.GoHomeFromRound();
    }

    // ================= Banner =================

    private void BuildBanner()
    {
        Canvas canvas = OnlineUI.CreateCanvas("OnlineRoundCanvas", transform, 39);
        banner = OnlineUI.NewLabel(canvas.transform, "", 36, Vector2.zero, new Vector2(1400f, 90f), TextAnchor.MiddleCenter, true);
        RectTransform rt = banner.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -150f);

        Outline outline = banner.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new Vector2(2f, -2f);

        banner.gameObject.SetActive(false);
    }

    private void ShowBanner(string text, float seconds)
    {
        banner.text = text;
        banner.gameObject.SetActive(true);
        bannerUntil = Time.unscaledTime + seconds;
    }

    private string NameOf(byte slot)
    {
        if (roster != null)
        {
            foreach (MecchaNetClient.RosterEntry e in roster)
            {
                if (e.Slot != slot) continue;
                if (string.IsNullOrEmpty(e.Name)) break;
                return e.Name.Replace('<', '(').Replace('>', ')');
            }
        }
        return "Player " + (slot + 1);
    }
}
