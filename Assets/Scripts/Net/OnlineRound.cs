using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// An online round in the 3D scene, from MatchStart to the result popup. Adapted from the SDK
// sample's OnlineRound.
//
// The existing gameplay scripts keep doing what they do — JoystickMover moves the player and
// that movement is the truth (the server only checks it against limits), CameraController follows,
// MatchHUD draws the timer — and this class connects them to the server:
//   * GameModeManager is switched into its network mode (BeginNetworkRound): the map is the one the
//     player picked in the map picker (the server matched them with others on it), no bot is
//     started, and the timer, counters and seeker freeze come from here.
//   * NetEntityRegistry.ViewFactory makes characters as snapshots name them: the local player gets
//     NetEntityView (server corrections) and NetInputAdapter (joystick/camera/buttons -> input
//     stream); every other player is the character they chose (Resources/Players), and a bot the
//     Bot prefab, placed by interpolation.
//     NetBodyAnimator plays their prone and climbing from the replicated flags, and each remote
//     shot (OnRemoteShot: real aim and bullet type) its gun animation, sound and a visual-only
//     tracer.
//   * The gun calls TryFire/Fire below, with its muzzle, instead of its local aim-assisted kill; the
//     server judges the hit. A seeker always has bullets: the chosen special bullet type (bought,
//     counted per type) while any is left, then default bullets, which never run out. Each type
//     has its own cooldown and radius (BulletSpecs, shared with the server); tapping the bullet
//     label switches type, and the choice is remembered (ShopInventory.PreferredBullet). ChameleonPaint sends its strokes,
//     fills, resets and metallic / roughness sliders through SendStroke/SendFill/SendPaintReset/
//     SendPaintMaterial, and other painters' canvases and sliders are rebuilt here from what the
//     server relays.
//   * As in Single Play: the hider's Start button (BotSeekController.SkipGrace) sends SkipHide, and
//     the seek starts once every hider has pressed it; a seeker bot that spots the player freezes
//     them and the camera turns to it (SpotAlert names the bot) until its shot.
//   * The server has no map, so this scene answers what needs one: which point a spawn number
//     means (SpawnPointResolver) and how far a shot flies before a wall (Fire).
//   * The server has no bots of its own either: when it gives this player the empty seats, this
//     game runs Single Play's bot controllers on them and reports what they do (OnlineRound.Bots.cs).
//   * Out of the round, the player spectates: the server sends everyone, and the camera follows the
//     players still in (Next switches) until the result.
//   * Poses and weapons are the shop's: PoseSelectorUI sends a pick through SendPose (owned poses
//     only), the equipped weapon is selected at the start (SelectWeapon), and snapshots show every
//     other player's pose and gun skin. There is no in-round weapon picker, as offline.
//   * Eliminations, round end and match end come from the server; nothing here decides a hit, a
//     catch or a winner.
//   * A dropped connection is ridden out: the SDK reconnects by itself and the server holds the
//     seat (30 s once it notices), then sends MatchFound again for the same match (Resume). A
//     banner says what is happening, shots wait, and the player's own recent paint is sent again,
//     since strokes made around the drop never reached the server. A seat that ran out ends the
//     round with a notice. OnlineRejoin remembers the round so a restarted app can rejoin it.
//
// The class is split by concern: OnlineRound.Paint.cs (own paint sent, logged and replayed;
// other painters' canvases), OnlineRound.Connection.cs (drops, resume, the rejoin mark),
// OnlineRound.Loadout.cs (poses, weapons, remote guns) and OnlineRound.Bots.cs (running the bots
// when this player is their host). It is still one component.
public partial class OnlineRound : MonoBehaviour
{
    private static OnlineRound _instance;

    /// <summary>True from Begin to End: an online round (or its result popup) is on screen.</summary>
    public static bool Active => _instance != null && _instance.active;

    private static MecchaNetClient Net => MecchaNetClient.Instance;

    [Tooltip("Resources path of the body used for bots. Real players are the character they chose (CharacterSelect's prefabs).")]
    [SerializeField] private string remotePrefabPath = "GameObjects/Bot";

    [Tooltip("Brush radius (texels) of the dabs a one-tap body fill is sent as. Max 126.")]
    [SerializeField] private int fillBrushPx = 96;

    [Tooltip("Spacing (texels) of that grid of dabs; smaller covers more evenly but sends more.")]
    [SerializeField] private int fillSpacing = 64;

    [Tooltip("Top-right offset of the seeker's bullet count, under the HUD.")]
    [SerializeField] private Vector2 bulletsOffset = new Vector2(-40f, -250f);

    [Tooltip("Seconds after being caught before the camera starts following the players still in (BotSeekController.caughtPopupDelay offline).")]
    [SerializeField] private float spectateDelaySeconds = 3f;

    private OnlineLobby lobby;
    private GameModeManager gmm;
    private HomeUI home;
    private HideModeResultUI results;
    private JoystickMover mover;
    private WallClimber climber;
    private Mode1FirstPersonGun gun;
    private ChameleonPaint paint;
    private PoseSelectorUI poses;
    private GunHolder gunHolder;
    private GameObject remotePrefab;
    private CharacterSelect characterSelect;

    private bool active;
    private bool hunter;
    private bool resultShown;
    private bool localOut;
    private MecchaNetClient.RosterEntry[] roster;
    private readonly HashSet<byte> eliminated = new HashSet<byte>();

    private int timerShown = -1;
    private float nextWaitNoticeAt;

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
    private BulletType lastChosen = BulletType.Default;
    private BulletType firedBullet = BulletType.Default;   // the bullet the last shot used, for its size

    private CameraController camCtrl;
    private bool startPressed;         // this hider pressed Start in this match's hide phase
    private NetEntityView spotter;     // the seeker bot whose spot sequence froze this player
    private float spectateAt = -1f;    // out: when the camera starts following someone else
    private byte watching = 0xFF;
    private Button nextButton;

    // ================= Entry points for the gameplay scripts =================

    /// <summary>
    /// Asked by the gun before it shoots (animation, sound, bullet effect): false while the
    /// player is out or inside the cooldown of the bullet fired last, whose extra shots the
    /// server would drop anyway. Never for want of bullets: default bullets are unlimited.
    /// </summary>
    public static bool TryFire()
    {
        if (!Active) return true;
        return _instance.CanFire();
    }

    /// <summary>
    /// How far along this shot it would meet a hider, by the very test the server will judge it
    /// with. The bodies of other players have no colliders online, so this is how the crosshair
    /// lands on them rather than on the wall behind.
    /// </summary>
    public static bool BodyAlong(Vector3 origin, Vector3 direction, out float distance)
    {
        distance = float.MaxValue;
        return Active && _instance.hunter && _instance.NearestHider(origin, direction, ref distance);
    }

    /// <summary>The drawn bullet's size against the default one: the type the last shot used (1 offline).</summary>
    public static float ShotRadiusScale => Active ? BulletSpecs.Of(_instance.firedBullet).RadiusScale : 1f;

    /// <summary>The gun's shot from its muzzle, in the direction drawn on screen this frame.</summary>
    public static void Fire(Vector3 worldOrigin, Vector3 worldDirection)
    {
        if (!Active) return;
        _instance.DoFire(worldOrigin, worldDirection);
    }

    /// <summary>Whether the hiders' Start button shows: a hider in the hide phase who has not pressed it.</summary>
    public static bool WantsStartButton => Active && _instance.StartButtonWanted();

    /// <summary>The hider's Start button (BotSeekController.SkipGrace during an online round).</summary>
    public static void SkipHide()
    {
        if (Active) _instance.PressStart();
    }

    /// <summary>This round is the server's match <paramref name="id"/> (a MatchFound for it is a resume).</summary>
    public static bool IsCurrentMatch(int id) => Active && _instance.matchId == id;

    /// <summary>A short message on the round's banner.</summary>
    public static void ShowNotice(string text)
    {
        if (Active) _instance.ShowBanner(text, 2.5f);
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
    public void Begin(OnlineLobby owner, string mapName, MecchaNetClient.RosterEntry[] entries, int id)
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
        poses = FindAnyObjectByType<PoseSelectorUI>(FindObjectsInactive.Include);
        gunHolder = FindAnyObjectByType<GunHolder>(FindObjectsInactive.Include);
        camCtrl = FindAnyObjectByType<CameraController>();
        characterSelect = FindAnyObjectByType<CharacterSelect>(FindObjectsInactive.Include);
        if (remotePrefab == null) remotePrefab = Resources.Load<GameObject>(remotePrefabPath);

        active = true;
        hunter = Net.LocalRole == Role.Hunter;
        resultShown = false;
        localOut = false;
        leaveArmed = false;
        roster = entries;
        eliminated.Clear();
        nextFireAt = 0f;
        lastChosen = BulletType.Default;
        firedBullet = BulletType.Default;
        startPressed = false;
        spotter = null;
        spectateAt = -1f;
        watching = 0xFF;
        requestedPose = -1;
        matchId = id;
        matchEndShown = false;
        offlineSince = -1f;
        resumeDeadline = 0f;
        paintLog.Clear();
        paintReplay.Clear();
        if (poses != null) poses.ClearSelection();   // a new match starts with no pose
        ClearCanvases();
        if (results != null) results.HideAll();

        if (home != null) home.EnterOnlineRound(hunter);
        if (gmm != null) gmm.BeginNetworkRound(hunter, mapName);

        localBody = PlayerRef.ResolveTransform("Player");
        NetEntityRegistry.ViewFactory = MakeView;
        Net.SpawnPointResolver = ResolveSpawn;

        Net.OnPhaseChanged += HandlePhaseChanged;
        Net.OnEliminated += HandleEliminated;
        Net.OnRoundEnd += HandleRoundEnd;
        Net.OnMatchEnd += HandleMatchEnd;
        Net.OnIdleWarning += HandleIdleWarning;
        Net.OnSpotAlert += HandleSpotAlert;
        Net.OnRosterChanged += HandleRosterChanged;
        Net.OnRemotePaintStroke += HandleRemoteStroke;
        Net.OnRemotePaintReset += HandleRemoteReset;
        Net.OnRemotePaintMaterial += HandleRemoteMaterial;
        Net.OnRemoteShot += HandleRemoteShot;
        Net.OnLoadoutChanged += HandleLoadoutChanged;
        Net.OnAuthenticated += HandleReauthenticated;
        Net.OnBotHost += HandleBotHost;

        // Start from the phase the client last heard (the ready gate, normally).
        nextWaitNoticeAt = Time.unscaledTime + WaitNoticeAfterSeconds;
        HandlePhaseChanged(Net.Phase, Net.PhaseSecondsLeft);
        RefreshCounters();
        ShowBanner(hunter ? "You are a SEEKER. Wait for the hiders to hide..." : "You are a HIDER. Paint yourself and hide!", 4f);

        bulletsLabel.gameObject.SetActive(hunter);
        RefreshBullets();

        // Carry the equipped weapon and the preferred bullet, and fetch pose/weapon ownership as
        // the server has it now.
        SelectEquippedWeapon();
        SelectPreferredBullet();
        ApplyLocalWeapon();
        ShopInventory.Refresh(_ =>
        {
            if (active) SelectEquippedWeapon();
        });

        OnlineRejoin.Mark();
        rejoinMarkAt = Time.unscaledTime + 5f;

        HandleBotHost(Net.HostedBots);     // given them while the scene was still loading
    }

    /// <summary>Takes the scene out of online mode. The caller decides where to go next.</summary>
    public void End()
    {
        if (!active) return;
        active = false;

        // The round may have spent special bullets and paid coins: fetch the wallet as the
        // server has it now, so Home and the shop do not show the counts from before it.
        ShopInventory.Refresh(null);

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
            Net.OnRemotePaintMaterial -= HandleRemoteMaterial;
            Net.OnRemoteShot -= HandleRemoteShot;
            Net.OnLoadoutChanged -= HandleLoadoutChanged;
            Net.OnAuthenticated -= HandleReauthenticated;
            Net.OnBotHost -= HandleBotHost;
        }

        StopHostingBots();
        ClearSpot();
        StopSpectating();

        OnlineRejoin.Clear();
        paintLog.Clear();
        paintReplay.Clear();
        offlineSince = -1f;
        resumeDeadline = 0f;

        NetEntityRegistry.ViewFactory = null;
        if (Net != null) Net.SpawnPointResolver = null;
        NetEntityRegistry.Clear();   // destroys the remote bodies

        // The local player is the scene's own character: strip the network parts back off so
        // Single Play drives it exactly as before.
        if (adapter != null) Destroy(adapter);
        if (localView != null) Destroy(localView);
        adapter = null;
        localView = null;

        ClearCanvases();
        WeaponSkins.Apply(LocalGun(), 0);
        if (poses != null) poses.ClearSelection();
        requestedPose = -1;
        if (results != null) results.ClearOnline();
        if (gmm != null) gmm.EndNetworkRound();
        banner.gameObject.SetActive(false);
        bulletsLabel.gameObject.SetActive(false);
    }

    private const float SpawnLift = 0.2f;           // markers lie on the floor; the feet go just above it
    private const float SpawnBesideRadius = 7f;     // under half the markers' spacing, so neighbours stay apart
    private const float SpawnClearance = 0.5f;
    private const float ShotRayLength = 1000f;
    private const float WaitNoticeAfterSeconds = 1.5f;

    private void Update()
    {
        if (!active) return;

        UpdateTimer();
        CheckPoseAnswer();
        FeedInput();
        PumpBots();
        RefreshCounters();
        ApplyCanvases();
        UpdateSpotView();
        UpdateSpectating();
        WatchConnection();
        if (!active) return;   // a seat that ran out ends the round right there
        PumpPaintReplay();
        PrunePaintLog();

        if (!resultShown && Time.unscaledTime > rejoinMarkAt)
        {
            OnlineRejoin.Mark();
            rejoinMarkAt = Time.unscaledTime + 5f;
        }

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

        GameObject prefab = BodyPrefabOf(slot);
        if (prefab == null)
        {
            Debug.LogWarning($"[Online] Resources/{remotePrefabPath} not found; other players are invisible.");
            return null;
        }

        GameObject parent = GameObject.Find("Characters");
        GameObject body = Instantiate(prefab, Vector3.down * 500f, Quaternion.identity,
                                      parent != null ? parent.transform : null);
        body.name = "Net_" + slot;
        body.SetActive(true);

        // The Players prefabs are built to be the local player: nothing of that may run here,
        // and the scene's lookups for "the" gun or camera profile must keep finding the local one.
        foreach (GunHolder holder in body.GetComponentsInChildren<GunHolder>(true)) DestroyImmediate(holder);
        foreach (SeekerCamProfile profile in body.GetComponentsInChildren<SeekerCamProfile>(true)) DestroyImmediate(profile);

        Transform carried = role == Role.Hunter ? AttachRemoteGun(body) : null;

        // The body's posed limbs reach well outside the bounds its meshes were imported with, so
        // a body could be left undrawn while on screen.
        foreach (SkinnedMeshRenderer mesh in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            mesh.updateWhenOffscreen = true;

        NetEntityView view = body.AddComponent<NetEntityView>();
        view.Initialise(slot, role, false);

        // The gun goes when the body goes (left out, it floated in the air where the body had been)
        // but it is not the body: painted with it, it wore the body's blank canvas and turned white.
        view.Carry(carried);

        // Name tags only over teammates, never an opponent: a name floating above a camouflaged
        // hider would give the whole game away.
        bool teammate = (role == Role.Hunter) == hunter;
        BotNameTag tag = body.GetComponentInChildren<BotNameTag>(true);
        if (teammate && tag == null) tag = body.AddComponent<BotNameTag>();
        if (tag != null)
        {
            tag.enabled = teammate;     // it builds itself on Start: off, it never exists
            if (teammate) tag.SetName(NameOf(slot));
        }

        // Their prone, climbing and gunfire, from the replicated flags (walking speed is the
        // view's own); and a shot's sound and tracer, as Single Play's seeker bots fire.
        NetBodyAnimator animator = body.GetComponent<NetBodyAnimator>();
        if (animator == null) animator = body.AddComponent<NetBodyAnimator>();
        animator.Bind(view);

        view.LoadoutChanged += ApplyRemoteLoadout;
        ApplyRemoteLoadout(view);

        if (canvases.TryGetValue(slot, out RemoteCanvas canvas)) canvas.AppliedTo = null;   // re-applied next frame
        return view;
    }

    // A real player is drawn as the character they chose; a bot, and anyone whose character this
    // version of the game doesn't have, as the Bot prefab.
    private GameObject BodyPrefabOf(byte slot)
    {
        if (roster == null || characterSelect == null || characterSelect.characterPrefabs == null) return remotePrefab;

        foreach (MecchaNetClient.RosterEntry e in roster)
        {
            if (e.Slot != slot) continue;
            if (e.IsBot || e.ModelId >= characterSelect.characterPrefabs.Length) break;

            GameObject prefab = Resources.Load<GameObject>(characterSelect.characterPrefabs[e.ModelId]);
            return prefab != null ? prefab : remotePrefab;
        }

        return remotePrefab;
    }

    // Another player's (or a server bot's) shot, when its body is drawn firing (OnRemoteShot
    // waits for the render time). The server has already judged it; this is the gun animation,
    // the sound and a tracer along the shot's real aim, sized by its bullet type.
    private void HandleRemoteShot(MecchaNetClient.RemoteShot shot)
    {
        if (!active) return;

        NetEntityView view = NetEntityRegistry.Get(shot.Slot);
        NetBodyAnimator animator = view != null ? view.GetComponent<NetBodyAnimator>() : null;
        if (animator != null) animator.PlayShot();
        AudioManager.WaterGunShot();

        if (gun == null) return;
        gun.SpawnRemoteBullet(shot.Origin, shot.Impact - shot.Origin,
                              view != null ? view.transform : null,
                              BulletSpecs.Of(shot.Bullet).RadiusScale);
    }

    // ================= What the server cannot judge: spawns and walls =================

    // The server names a spawn by number; the markers (HunterSpawn_*, HiderSpawn_*) are in this room.
    private Vector3? ResolveSpawn(int index) => ResolveSpawn(index, hunter);

    private Vector3? ResolveSpawn(int index, bool forSeeker)
    {
        LevelManager level = FindAnyObjectByType<LevelManager>();
        GameObject room = level != null ? level.CurrentRoomInstance : null;
        if (room == null) return null;

        string prefix = forSeeker ? "HunterSpawn" : "HiderSpawn";
        var markers = new List<Transform>();
        foreach (Transform t in room.GetComponentsInChildren<Transform>(true))
        {
            if (t.name.StartsWith(prefix, System.StringComparison.Ordinal)) markers.Add(t);
        }
        if (markers.Count == 0) return null;
        markers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        // The server gives each seat on a side the next number, and a room has a marker for every
        // seat, so nobody shares one. Only with more seats than markers does a lap stand beside it.
        int count = markers.Count;
        int seatsOnSide = 0;
        if (roster != null)
        {
            foreach (MecchaNetClient.RosterEntry e in roster)
                if ((e.Role == Role.Hunter) == forSeeker) seatsOnSide++;
        }

        Vector3 marker = markers[index % count].position + Vector3.up * SpawnLift;
        bool shared = seatsOnSide > count && index / count > 0;
        if (!shared && SpawnFree(marker)) return marker;

        // Something solid on the marker, for this size of body: beside it, never on another seat's.
        for (int i = 0; i < 6; i++)
        {
            float angle = (index * 47f + i * 60f) * Mathf.Deg2Rad;
            Vector3 beside = marker + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * SpawnBesideRadius;
            if (SpawnFree(beside)) return beside;
        }

        return marker;
    }

    // Whether the local character fits with its feet here. Other players do not count: they move.
    private bool SpawnFree(Vector3 feet)
    {
        NetEntityView me = NetEntityRegistry.Get(Net.LocalSlot);
        CharacterController cc = me != null ? me.GetComponent<CharacterController>() : null;
        if (cc == null) return true;

        float scale = me.transform.lossyScale.y;
        float radius = cc.radius * scale + SpawnClearance;
        Vector3 low = feet + Vector3.up * (radius + SpawnClearance);
        Vector3 high = feet + Vector3.up * Mathf.Max(radius + SpawnClearance, cc.height * scale - radius);

        foreach (Collider hit in Physics.OverlapCapsule(low, high, radius, ~0, QueryTriggerInteraction.Ignore))
        {
            if (!Bullet.IsCharacter(hit) && !hit.transform.IsChildOf(me.transform)) return false;
        }
        return true;
    }

    // How far along a line the room's shell stops a shot (the bullets' own rule), or -1.
    private static float WallDistanceAlong(Vector3 origin, Vector3 direction, float maxDistance)
    {
        float nearest = -1f;
        foreach (RaycastHit hit in Physics.RaycastAll(origin, direction, maxDistance, ~0, QueryTriggerInteraction.Ignore))
        {
            if (Bullet.IsCharacter(hit.collider) || !Bullet.BlocksShot(hit.collider.gameObject)) continue;
            if (nearest < 0f || hit.distance < nearest) nearest = hit.distance;
        }
        return nearest;
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
        adapter.SetOnWall(climber != null && climber.IsClinging);

        if (fireReleaseAt > 0f && Time.unscaledTime > fireReleaseAt)
        {
            adapter.FireReleased();
            fireReleaseAt = 0f;
        }
    }

    private bool CanFire()
    {
        if (localOut) return false;
        if (!Net.IsConnected) return false;   // the shot would go nowhere; the banner says why
        if (Time.unscaledTime < nextFireAt) return false;

        // No bullet check: with none of the chosen special type left the seeker shoots default
        // bullets. The wait that follows is this bullet's cooldown, as the server counts it.
        firedBullet = Net.NextBullet;
        nextFireAt = Time.unscaledTime + MecchaNetClient.CooldownAfter(firedBullet);
        return true;
    }

    private bool NearestHider(Vector3 origin, Vector3 direction, ref float nearest)
    {
        if (roster == null) return false;

        bool found = false;
        float pad = BulletSpecs.Of(Net.NextBullet).Radius;

        foreach (MecchaNetClient.RosterEntry e in roster)
        {
            if (e.Role != Role.Fugitive || eliminated.Contains(e.Slot)) continue;

            NetEntityView view = NetEntityRegistry.Get(e.Slot);
            if (view == null || view.Eliminated || !view.IsShown) continue;
            if (!view.HasPosition && !view.IsHosted) continue;

            // A bot this game runs isn't told its own pose by the snapshots.
            int pose = view.IsHosted && hostedBots.TryGetValue(e.Slot, out HostedBot bot) && bot.Animator != null
                ? PoseIdFor(bot.Animator.GetInteger(PoseHash))
                : view.Pose;

            Vector3 feet = view.FeetPosition;
            bool climbing = (view.Flags & EntityFlags.Climbing) != 0;

            if (BodyHitBoxes.Raycast(origin.x, origin.y, origin.z, direction.x, direction.y, direction.z, feet.x, feet.y, feet.z,
                                     view.transform.eulerAngles.y, GameTuning.HiderScale, e.ModelId, pose, climbing, pad, out float t) &&
                t < nearest)
            {
                nearest = t;
                found = true;
            }
        }

        return found;
    }

    private void DoFire(Vector3 worldOrigin, Vector3 worldDirection)
    {
        if (localOut) return;

        // The shot itself is the Fire message (muzzle, direction and the tick on screen); the held
        // flag only animates the shooter for others, so it is released a moment later.
        float wall = WallDistanceAlong(worldOrigin, worldDirection, ShotRayLength);
        if (adapter != null) adapter.FirePressed(worldOrigin, worldDirection, wall);
        else Net.Fire(worldOrigin, worldDirection, wall);
        fireReleaseAt = Time.unscaledTime + 0.2f;
    }

    // The bullet the next shot uses and, for a special type, how many are left. When the chosen
    // special type runs out the server goes back to default bullets, which never run out.
    private void RefreshBullets()
    {
        if (bulletsLabel == null || Net == null) return;

        BulletType next = Net.NextBullet;
        bulletsLabel.text = next == BulletType.Default
            ? "Default bullets: unlimited"
            : $"{BulletName(next)}: {Net.SpecialBullets[next]}";

        BulletType chosen = Net.ChosenBullet;
        if (hunter && BulletSpecs.IsSpecial(lastChosen) && chosen == BulletType.Default && Net.SpecialBullets[lastChosen] == 0)
            ShowBanner($"Out of {BulletName(lastChosen)}: back to default bullets.", 2.5f);
        lastChosen = chosen;
    }

    private static string BulletName(BulletType type) =>
        type == BulletType.Default ? "Default bullets" : $"Special bullet {(int)type}";

    // Tapping the bullet label: the next type the player has (default bullets always), as the
    // server decides; the choice is kept for the next match.
    private void CycleBullet()
    {
        if (!active || !hunter || localOut || Net == null) return;
        AudioManager.ButtonTap();

        BulletType current = Net.ChosenBullet;
        for (int step = 1; step <= BulletSpecs.SpecialCount + 1; step++)
        {
            var candidate = (BulletType)(((int)current + step) % (BulletSpecs.SpecialCount + 1));
            if (candidate != BulletType.Default && Net.SpecialBullets[candidate] <= 0) continue;
            ShopInventory.PreferredBullet = candidate;
            Net.SelectBullet(candidate);   // answered with LoadoutState
            if (candidate == BulletType.Default && current != BulletType.Default) ShowBanner("Default bullets", 1.5f);
            return;
        }
    }

    // At the start: the type picked last time, while some of it is left.
    private void SelectPreferredBullet()
    {
        if (!hunter || Net == null) return;
        BulletType wanted = ShopInventory.PreferredBullet;
        if (wanted == Net.ChosenBullet) return;
        if (wanted != BulletType.Default && Net.SpecialBullets[wanted] <= 0) return;
        Net.SelectBullet(wanted);
    }

    // ================= The hiders' Start button =================

    private bool StartButtonWanted() =>
        !hunter && !localOut && !startPressed && !resultShown && Net.Phase == MatchPhase.Hide && !Net.PhasePaused;

    private void PressStart()
    {
        if (!StartButtonWanted()) return;

        startPressed = true;
        Net.SkipHide();
        ShowBanner("Ready! The seek starts once every hider has pressed Start.", 3f);
    }

    // ================= Phases, timer, counters =================

    private void HandlePhaseChanged(MatchPhase phase, int secondsLeft)
    {
        timerShown = -1;
        if (gmm != null) gmm.NetSeekerFrozen = phase == MatchPhase.Hide || phase == MatchPhase.Lobby;
        UpdateTimer();

        if (Net.PhasePaused) ShowBanner("Paused: a seat is being refilled.", 3f);
        else if (phase == MatchPhase.Hide && hunter) ShowBanner("Hiders are hiding. You are frozen until the seek starts.", 3f);
        else if (phase == MatchPhase.Seek) ShowBanner(hunter ? "GO! Find the hiders." : "The seekers are coming. Stay hidden!", 3f);

        if (phase == MatchPhase.Result) StopHostingBots();
    }

    // The timer on screen is the server's clock, read every frame. Nothing here counts it down,
    // starts it or resets it, so every client shows the same time.
    private void UpdateTimer()
    {
        if (gmm == null || resultShown) return;

        MatchPhase phase = Net.Phase;
        gmm.NetTimerWaiting = phase == MatchPhase.Lobby;

        if (phase == MatchPhase.Lobby)
        {
            // Only a slow phone makes this last: the hide starts once every game has loaded.
            if (Time.unscaledTime >= nextWaitNoticeAt)
            {
                nextWaitNoticeAt = Time.unscaledTime + 1f;
                ShowBanner("Waiting for the other players to load...", 1.2f);
            }
            return;
        }

        if (phase != MatchPhase.Hide && phase != MatchPhase.Seek) return;

        float left = Net.PhaseTimeLeft;
        int seconds = Net.PhaseSecondsLeft;

        // A small correction to the clock right at the edge of a second must not tick it back up.
        if (timerShown >= 0 && seconds == timerShown + 1 && left < timerShown + 0.25f) seconds = timerShown;
        timerShown = seconds;

        gmm.NetTimerSeconds = seconds;
        gmm.NetTimerFraction = 1f - left / Mathf.Max(1, Net.PhaseDurationSeconds);
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
            if (eliminated.Contains(e.Slot))
            {
                // Out while this client was offline: the Eliminated event never came.
                if (e.Slot == Net.LocalSlot && !localOut)
                {
                    localOut = true;
                    gmm.PlayerFrozen = true;
                    ClearSpot();
                    spectateAt = Time.unscaledTime + spectateDelaySeconds;
                }
                continue;
            }

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
            ClearSpot();
            spectateAt = Time.unscaledTime + spectateDelaySeconds;
            ShowBanner(cause == EliminationCause.Shot ? "You were shot! Watch the rest of the round."
                                                      : "You were caught! Watch the rest of the round.", 5f);
        }
        else if (by == Net.LocalSlot)
        {
            // A hit that took someone out asks no wait of the next shot; the server has lifted it too.
            if (cause == EliminationCause.Shot) nextFireAt = 0f;

            ShowBanner($"You got {NameOf(victim)}!", 2.5f);
        }
        else
        {
            ShowBanner($"{NameOf(victim)} is out.", 2f);
        }
    }

    // SpotAlert is sent only to the hider who was spotted, and names the seeker bot that spotted
    // them (it used to be compared with this player's own slot, so the banner never showed).
    // As in Single Play's BeginSpot: frozen — the server holds the hider still too — and the camera
    // turned to the bot until its shot, SpotShootDelaySeconds later.
    private void HandleSpotAlert(byte spotterSlot)
    {
        if (hunter || localOut) return;

        spotter = NetEntityRegistry.Get(spotterSlot);
        if (gmm != null) gmm.PlayerFrozen = true;
        ShowBanner("A seeker has spotted you!", 3f);
    }

    // Every frame, as Single Play's UpdateSpot does, since the bot turns as it aims.
    private void UpdateSpotView()
    {
        if (spotter == null || camCtrl == null || localBody == null) return;
        camCtrl.SetSpotView(spotter.transform, localBody);
    }

    private void ClearSpot()
    {
        if (spotter == null && (camCtrl == null || !camCtrl.IsSpotView)) return;
        spotter = null;
        if (camCtrl != null) camCtrl.ClearSpotView();
    }

    // ================= Spectating =================

    // Out of the round: the camera follows the players still in, from spectateDelaySeconds after
    // the catch until the result, moving on when the one watched is out too.
    private void UpdateSpectating()
    {
        if (!localOut || resultShown || camCtrl == null || spectateAt < 0f) return;
        if (Time.unscaledTime < spectateAt) return;

        NetEntityView current = watching != 0xFF ? NetEntityRegistry.Get(watching) : null;
        if (current == null || current.Eliminated) Watch(NextToWatch(watching));

        if (nextButton != null) nextButton.gameObject.SetActive(watching != 0xFF);
    }

    // The next player still in after this slot (in slot order, wrapping), or 0xFF for none.
    private byte NextToWatch(byte after)
    {
        if (roster == null) return 0xFF;

        byte next = 0xFF, first = 0xFF;
        foreach (MecchaNetClient.RosterEntry e in roster)
        {
            if (e.Slot == Net.LocalSlot || eliminated.Contains(e.Slot)) continue;
            NetEntityView view = NetEntityRegistry.Get(e.Slot);
            if (view == null || view.Eliminated) continue;

            if (first == 0xFF || e.Slot < first) first = e.Slot;
            if (after != 0xFF && e.Slot > after && (next == 0xFF || e.Slot < next)) next = e.Slot;
        }

        return next != 0xFF ? next : first;
    }

    private void Watch(byte slot)
    {
        watching = slot;
        NetEntityView view = slot != 0xFF ? NetEntityRegistry.Get(slot) : null;
        camCtrl.SetFollowTarget(view != null ? view.transform : null);
        if (view != null) ShowBanner($"Watching {NameOf(slot)}", 2f);
    }

    private void WatchNext()
    {
        AudioManager.ButtonTap();
        if (camCtrl != null) camCtrl.SetFreeLook(false);   // the button means: show me them
        Watch(NextToWatch(watching));
    }

    private void StopSpectating()
    {
        spectateAt = -1f;
        watching = 0xFF;
        if (camCtrl != null) camCtrl.SetFollowTarget(null);
        if (nextButton != null) nextButton.gameObject.SetActive(false);
    }

    private void HandleIdleWarning(int seconds)
    {
        ShowBanner($"Move or you will be removed for being idle in {seconds}s.", 4f);
    }

    // ================= Results =================

    private void HandleRoundEnd(MecchaNetClient.RoundEndInfo info)
    {
        if (gmm != null) gmm.PlayerFrozen = true;

        string score = hunter ? info.Eliminations.ToString() : info.SurvivalSeconds + "s";
        ShowResult(info.Won, info.Rank, score, 0);
    }

    private void HandleMatchEnd(MecchaNetClient.MatchEndInfo info)
    {
        // The result is sent again to a player who reconnects on the result screen.
        if (matchEndShown) return;
        matchEndShown = true;

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
        ClearSpot();
        if (camCtrl != null) camCtrl.SetFreeLook(false);
        StopSpectating();
        OnlineRejoin.Clear();   // the match is decided: nothing left to rejoin
        string rankText = rank > 0 ? $"#{rank}/{Net.MatchSeats}" : "-";

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

        // Seeker's bullet, top right under the HUD: online, special bullets are a server-counted
        // stock. Tapping it switches bullet type.
        bulletsLabel = OnlineUI.NewLabel(canvas.transform, "", 30, Vector2.zero, new Vector2(420f, 50f), TextAnchor.MiddleRight, true);
        bulletsLabel.raycastTarget = true;
        bulletsLabel.gameObject.AddComponent<Button>().onClick.AddListener(CycleBullet);
        RectTransform brt = bulletsLabel.rectTransform;
        brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(1f, 1f);
        brt.anchoredPosition = bulletsOffset;
        Outline bulletsOutline = bulletsLabel.gameObject.AddComponent<Outline>();
        bulletsOutline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        bulletsLabel.gameObject.SetActive(false);

        // Spectating: switch to the next player still in, bottom centre.
        nextButton = OnlineUI.NewButton(canvas.transform, "Next player", Vector2.zero, new Vector2(360f, 96f),
                                        OnlineUI.Blue, WatchNext);
        RectTransform nrt = (RectTransform)nextButton.transform;
        nrt.anchorMin = nrt.anchorMax = nrt.pivot = new Vector2(0.5f, 0f);
        nrt.anchoredPosition = new Vector2(0f, 60f);
        nextButton.gameObject.SetActive(false);
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
