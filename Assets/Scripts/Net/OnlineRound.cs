using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// An online round in the 3D scene, from MatchStart to the result popup. Adapted from the SDK
// sample's OnlineRound; its weapon-skin and pose parts come with the online shop.
//
// The existing gameplay scripts keep doing what they do — JoystickMover moves the player,
// CameraController follows, MatchHUD draws the timer — and this class connects them to the server:
//   * GameModeManager is switched into its network mode (BeginNetworkRound): the map is the one the
//     server picked, no bot is started, and the timer, counters and seeker freeze come from here.
//   * NetEntityRegistry.ViewFactory makes characters as snapshots name them: the local player gets
//     NetEntityView (server corrections) and NetInputAdapter (joystick/camera/buttons -> input
//     stream); every other player is a Bot prefab body placed by interpolation.
//   * The gun calls TryFire/Fire below instead of its local aim-assisted kill; the server judges
//     the hit and counts the bullets. ChameleonPaint sends its strokes, fills and resets through
//     SendStroke/SendFill/SendPaintReset, and other painters' canvases are rebuilt here from the
//     strokes the server relays. Metallic and roughness are not in the protocol yet: they stay
//     local until the server adds them.
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

    [Tooltip("Brush radius (texels) of the dabs a one-tap body fill is sent as. Max 126.")]
    [SerializeField] private int fillBrushPx = 96;

    [Tooltip("Spacing (texels) of that grid of dabs; smaller covers more evenly but sends more.")]
    [SerializeField] private int fillSpacing = 64;

    [Tooltip("Top-right offset of the seeker's bullet count, under the HUD.")]
    [SerializeField] private Vector2 bulletsOffset = new Vector2(-40f, -250f);

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
    private float nextFireAt;

    private bool leaveArmed;
    private float leaveArmedUntil;

    private Text banner;
    private float bannerUntil;
    private Text bulletsLabel;

    // Each remote painter's canvas, rebuilt from the strokes the server relays.
    private sealed class RemoteCanvas
    {
        public Color32[] Pixels;
        public Texture2D Texture;
        public bool Dirty;
        public NetEntityView AppliedTo;
    }

    private readonly Dictionary<byte, RemoteCanvas> canvases = new Dictionary<byte, RemoteCanvas>();

    // ================= Entry points for the gameplay scripts =================

    /// <summary>
    /// Asked by the gun before it shoots (animation, sound, bullet effect): false while the
    /// player is out, out of bullets, or inside the server's fire interval, whose extra shots
    /// it would drop anyway.
    /// </summary>
    public static bool TryFire()
    {
        if (!Active) return true;
        return _instance.CanFire();
    }

    /// <summary>The gun's shot, in the direction drawn on screen this frame.</summary>
    public static void Fire(Vector3 worldDirection)
    {
        if (!Active) return;
        _instance.DoFire(worldDirection);
    }

    /// <summary>A dab on the local player's canvas (ChameleonPaint), radius in canvas texels.</summary>
    public static void SendStroke(Vector2 uv, int brushPx, Color color, bool erase)
    {
        if (!Active || Net == null) return;
        Net.QueuePaintStroke(uv, brushPx, erase ? Color.white : color, erase ? 2 : 0);
    }

    public static void SendPaintReset()
    {
        if (!Active || Net == null) return;
        Net.SendPaintReset();
    }

    /// <summary>
    /// A one-tap fill has no stroke to send, so it goes as a reset followed by a grid of large
    /// dabs in the fill colour: the server's camouflage average and every other player's view of
    /// this body then match the filled canvas. About 80 dabs, well inside the paint budget.
    /// </summary>
    public static void SendFill(Color color)
    {
        if (!Active || Net == null) return;

        Net.SendPaintReset();
        OnlineRound self = _instance;
        int size = GameTuning.PaintTextureSize;
        int step = Mathf.Max(8, self.fillSpacing);
        int brush = Mathf.Clamp(self.fillBrushPx, 2, 126);

        for (int y = step / 2; y < size; y += step)
        {
            for (int x = step / 2; x < size; x += step)
            {
                Net.QueuePaintStroke(new Vector2((float)x / size, (float)y / size), brush, color, 0);
            }
        }
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
        nextFireAt = 0f;
        ClearCanvases();
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
        Net.OnRemotePaintStroke += HandleRemoteStroke;
        Net.OnRemotePaintReset += HandleRemoteReset;
        Net.OnLoadoutChanged += RefreshBullets;

        // Start from the phase the client last heard (the ready gate, normally).
        HandlePhaseChanged(Net.Phase, Net.PhaseSecondsLeft);
        RefreshCounters();
        ShowBanner(hunter ? "You are a SEEKER. Wait for the hiders to hide..." : "You are a HIDER. Paint yourself and hide!", 4f);

        bulletsLabel.gameObject.SetActive(hunter);
        RefreshBullets();
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
            Net.OnRemotePaintStroke -= HandleRemoteStroke;
            Net.OnRemotePaintReset -= HandleRemoteReset;
            Net.OnLoadoutChanged -= RefreshBullets;
        }

        NetEntityRegistry.ViewFactory = null;
        NetEntityRegistry.Clear();   // destroys the remote bodies

        // The local player is the scene's own character: strip the network parts back off so
        // Single Play drives it exactly as before.
        if (adapter != null) Destroy(adapter);
        if (localView != null) Destroy(localView);
        adapter = null;
        localView = null;

        ClearCanvases();
        if (results != null) results.ClearOnline();
        if (gmm != null) gmm.EndNetworkRound();
        banner.gameObject.SetActive(false);
        bulletsLabel.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (!active) return;

        UpdateTimer();
        FeedInput();
        RefreshCounters();
        ApplyCanvases();

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

        if (canvases.TryGetValue(slot, out RemoteCanvas canvas)) canvas.AppliedTo = null;   // re-applied next frame
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

    private bool CanFire()
    {
        if (localOut) return false;
        if (Time.unscaledTime < nextFireAt) return false;

        if (Net.Bullets <= 0)
        {
            ShowBanner("Out of bullets.", 2f);
            return false;
        }

        nextFireAt = Time.unscaledTime + GameTuning.FireIntervalSeconds;
        return true;
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

    // LoadoutState: the server's bullet count, sent when the match is found and after each
    // accepted shot.
    private void RefreshBullets()
    {
        if (bulletsLabel != null && Net != null) bulletsLabel.text = $"Bullets: {Net.Bullets}";
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

    // ================= Remote paint =================

    // Each remote painter's canvas is rebuilt here from the strokes the server relays, stamped with
    // the same soft round brush as ChameleonPaint.StampAtUV, and shown on their body
    // (NetEntityView.PaintTexture). The server only relays a hider's strokes to a seeker who has
    // seen that hider during the seek.

    private void HandleRemoteStroke(PaintStroke stroke)
    {
        RemoteCanvas canvas = CanvasFor(stroke.EntityId);
        int size = GameTuning.PaintTextureSize;

        int cx = Mathf.Clamp(stroke.U << 1, 0, size - 1);
        int cy = Mathf.Clamp(stroke.V << 1, 0, size - 1);
        int r = Mathf.Max(1, stroke.RadiusHalf << 1);

        Color col;
        if (stroke.Mode == 2) col = Color.white;
        else
        {
            Quantize.Rgb565ToColor(stroke.Color, out float cr, out float cg, out float cb);
            col = new Color(cr, cg, cb, 1f);
        }

        int x0 = Mathf.Max(0, cx - r), x1 = Mathf.Min(size - 1, cx + r);
        int y0 = Mathf.Max(0, cy - r), y1 = Mathf.Min(size - 1, cy + r);
        Color32[] buf = canvas.Pixels;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > r) continue;

                float a = Mathf.Clamp01((1f - d / r) * 1.7f);
                float inv = 1f - a;
                int idx = y * size + x;
                Color32 o = buf[idx];
                buf[idx] = new Color32((byte)(col.r * 255f * a + o.r * inv),
                                       (byte)(col.g * 255f * a + o.g * inv),
                                       (byte)(col.b * 255f * a + o.b * inv), 255);
            }
        }

        canvas.Dirty = true;
    }

    private void HandleRemoteReset(byte slot)
    {
        RemoteCanvas canvas = CanvasFor(slot);
        Color32 white = Color.white;
        for (int i = 0; i < canvas.Pixels.Length; i++) canvas.Pixels[i] = white;
        canvas.Dirty = true;
    }

    private RemoteCanvas CanvasFor(byte slot)
    {
        if (canvases.TryGetValue(slot, out RemoteCanvas canvas)) return canvas;

        int size = GameTuning.PaintTextureSize;
        canvas = new RemoteCanvas
        {
            Pixels = new Color32[size * size],
            Texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp },
        };

        Color32 white = Color.white;
        for (int i = 0; i < canvas.Pixels.Length; i++) canvas.Pixels[i] = white;
        canvas.Dirty = true;
        canvases[slot] = canvas;
        return canvas;
    }

    // Uploads at most once a frame per canvas, however many strokes arrived.
    private void ApplyCanvases()
    {
        foreach (KeyValuePair<byte, RemoteCanvas> pair in canvases)
        {
            RemoteCanvas canvas = pair.Value;

            if (canvas.Dirty)
            {
                canvas.Texture.SetPixels32(canvas.Pixels);
                canvas.Texture.Apply(false);
                canvas.Dirty = false;
            }

            NetEntityView view = NetEntityRegistry.Get(pair.Key);
            if (view != null && !view.IsLocal && canvas.AppliedTo != view)
            {
                view.PaintTexture = canvas.Texture;
                canvas.AppliedTo = view;
            }
        }
    }

    private void ClearCanvases()
    {
        foreach (RemoteCanvas canvas in canvases.Values)
            if (canvas.Texture != null) Destroy(canvas.Texture);
        canvases.Clear();
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

        // Seeker's bullet count, top right under the HUD: online, bullets are a server-counted stock.
        bulletsLabel = OnlineUI.NewLabel(canvas.transform, "", 30, Vector2.zero, new Vector2(320f, 50f), TextAnchor.MiddleRight, true);
        RectTransform brt = bulletsLabel.rectTransform;
        brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(1f, 1f);
        brt.anchoredPosition = bulletsOffset;
        Outline bulletsOutline = bulletsLabel.gameObject.AddComponent<Outline>();
        bulletsOutline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        bulletsLabel.gameObject.SetActive(false);
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
