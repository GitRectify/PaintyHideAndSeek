using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output (every method in dump.cs, TypeDefIndex
// 9650, plus the SpawnSeekerBullet closure, the ShowCaughtDelayed coroutine and the RevivePlayer
// lambda). Fields (names, order, attributes, offsets) and method accessibility match dump.cs. All
// string literals are confirmed against Dumpstringliteral.json.
//
// Decoding notes:
// - Unnamed helpers in the raw, decoded from libil2cpp.so: FUN_022f7090 = Vector3.zero,
//   FUN_02349a84 = Vector3.up, FUN_02349bd8 = Vector3.Normalize, FUN_02349ac8 = Vector3.magnitude,
//   FUN_02349ca4 = Vector2.magnitude, FUN_02349b40 = Vector3.Distance.
// - Writes straight into BotLook's private fields are its inlined public methods:
//   _lookPoint+_hasTarget = LookAt, _aimPoint+_aiming+!_looseAim = AimAt,
//   _loosePoint+!_aiming+_looseAim = AimLoose, clearing all three flags = LookForward.
// - Writes to GameModeManager._PlayerFrozen_k__BackingField are the inlined PlayerFrozen setter.
// - SeekBot's ctor (RVA 0x2249D0C) only calls the object ctor: no field defaults.
//
// Convention: where raw jumps to the NullReferenceException stub, the C# just dereferences
// naturally; explicit null checks are kept only where raw really skips.
public class BotSeekController : MonoBehaviour
{
    private enum State
    {
        Idle,
        Grace,
        Searching,
        Over
    }

    private class SeekBot
    {
        public Transform tf;                 // 0x10
        public Animator anim;                // 0x18
        public SkinnedMeshRenderer smr;      // 0x20
        public CharacterController cc;       // 0x28
        public Ragdoll ragdoll;              // 0x30
        public Vector3 originPos;            // 0x38
        public Quaternion originRot;         // 0x44
        public Color originColor;            // 0x54
        public Vector3[] corners;            // 0x68
        public int ci;                       // 0x70
        public int targetIdx;                // 0x74
        public float repathTimer;            // 0x78
        public float scanTimer;              // 0x7C
        public float sightTimer;             // 0x80
        public float lostTimer;              // 0x84
        public float stuckTimer;             // 0x88
        public float sidestepTimer;          // 0x8C
        public float noProgressTimer;        // 0x90
        public float bestDist;               // 0x94
        public float camo01;                 // 0x98
        public float camoTimer;              // 0x9C
        public float activateTimer;          // 0xA0
        public float targetTimer;            // 0xA4
        public float targetBudget;           // 0xA8
        public Vector3 progressAnchor;       // 0xAC
        public float progressTimer;          // 0xB8
        public float fireTimer;              // 0xBC
        public float vy;                     // 0xC0
        public Transform gunTf;              // 0xC8
        public int sidestepDir;              // 0xD0
        public bool alerted;                 // 0xD4
        public Vector3 lastSeen;             // 0xD8
        public bool investigating;           // 0xE4
        public float investigateTimer;       // 0xE8
        public Vector3 investigatePoint;     // 0xEC
        public Vector3 idleAnchor;           // 0xF8
        public float idleTimer;              // 0x104
        public float sniff01;                // 0x108
        public float sniffHoldTimer;         // 0x10C
        public float sniffSuppressTimer;     // 0x110
        public float sniffPathTimer;         // 0x114
        public bool sniffHold;               // 0x118
        public bool sniffReachable;          // 0x119
        public BotLook look;                 // 0x120
        public float glanceTimer;            // 0x128
        public float glanceTargetYaw;        // 0x12C
        public float poiTimer;               // 0x130
        public Vector3 poiPoint;             // 0x134
        public Vector3 seenVel;              // 0x140
        public bool seenHasVel;              // 0x14C
        public float backupTimer;            // 0x150
    }

    [Header("Scene")]
    public string roomRootName = "Room";
    public string playerName = "Player";
    public string[] botNames = new string[] { "Bot1", "Bot2", "Bot3", "Bot4" };
    [Tooltip("PLAY NOW only: the button the hider taps to END the hide countdown early so the seekers start hunting immediately. Shown only while you (the hider) are in the grace/paint window.")]
    public string startButtonName = "ButtonStart";

    [Header("Timing")]
    [Tooltip("PLAY NOW (you hide): seconds you get to hide before the seeker bots start hunting you. NOT used in Seeker mode — there the seeker bots wait exactly as long as the opening hide countdown and start hunting together with you.")]
    public float graceTime = 100f;
    [Tooltip("PLAY NOW (you hide): seconds you must survive (unseen) once the seekers start hunting, to win.")]
    public float roundTime = 45f;
    [Tooltip("SEEKER MODE (you hunt): seconds the ally seeker-bots keep hunting the hiders once searching begins. Separate from roundTime — set it to match the player's hunt time (BotHideController.seekTime) so the bots don't stop early.")]
    public float seekerHuntTime = 60f;
    public float walkSpeed = 9f;
    public float arriveDistance = 2.5f;
    public float repathInterval = 1f;
    public float scanTime = 1.4f;
    [Tooltip("SEEKER ANTI-FREEZE: if a searching bot hasn't ACTUALLY moved for this many seconds (e.g. it wandered somewhere from which no search point is reachable and just cycles them), it's popped back to its spawn so it can never stay frozen mid-round. 0 = disable.")]
    public float stuckEscapeTime = 4f;
    [Tooltip("SEEKER PATROL: seconds a checked search spot stays 'done' before it re-opens for patrol. Seekers continuously re-sweep — heading back toward the start and re-checking OLD spots on a rolling basis — so no spot stays safe forever once a bot has passed it. Larger = old spots stay safe longer between visits.")]
    public float recheckTime = 25f;
    [Tooltip("Spacing (world units) of the auto search-point grid sampled straight from the baked NavMesh floor. This supplements the furniture-based points so seekers roam ANY map — incl. big maps like the warehouse Room2 whose props are far larger than the furniture-size heuristic (which there yields ~1 point). Smaller = denser coverage.")]
    public float searchGridCell = 28f;
    [Tooltip("Downward gravity applied to seeker bots while searching. The CharacterController has no gravity of its own, so without this a bot that rode UP a ramp/step (CC climbs slopes on a horizontal move) would float over lower ground forever. World is large -> needs to be strong (matches the player's JoystickMover gravity).")]
    public float botGravity = 70f;
    [Tooltip("Tallest raised floor / step (world units) a seeker bot can walk up — mirrors the player's climbStepHeight (§43b). The bot CharacterController's default stepOffset 0.3 × the 0.5 Play-Now scale = only 0.15u effective, far under the NavMesh agentClimb (4u), so bots got stuck pacing back and forth at any low ledge the path crossed. Counter-scaled + clamped to Unity's cap in ResetBots.")]
    public float botClimbStepHeight = 4f;

    [Header("Mode-2 player size")]
    [Tooltip("Player is scaled to this while Mode 2 is active (smaller = easier to hide). Feet are auto-anchored to the floor so the character doesn't float. 1 = no scaling.")]
    public float playerScaleInMode2 = 0.5f;

    [Header("Obstacle / crowd avoidance")]
    [Tooltip("Bots steer away from each other within this distance so two of them don't clump / stick together.")]
    public float botAvoidRadius = 9f;
    [Tooltip("How hard the bot-vs-bot separation steering pushes (0 = off).")]
    public float botAvoidStrength = 1.3f;

    [Header("Chameleon camouflage (paint blends you from the bots' eyes)")]
    [Tooltip("When on, a player whose paint matches the surface behind them is seen from a SHORTER range.")]
    public bool camoAffectsVision = true;
    [Tooltip("Effective vision range against a PERFECTLY camouflaged player. Must stay > catchRange so it's never fully invisible.")]
    public float minVisionRange = 14f;
    [Tooltip("RGB colour-distance at/above which camouflage gives ZERO benefit (lower = stricter match needed).")]
    [Range(0.05f, 1f)]
    public float camoMatchTolerance = 0.45f;
    [Tooltip("Fraction of the vision-range drop a perfect match may apply (1 = full).")]
    [Range(0f, 1f)]
    public float camoStrength = 0.85f;
    [Tooltip("Seconds between per-bot background colour re-samples (perf throttle).")]
    public float camoRefresh = 0.3f;
    [Tooltip("Seconds between recomputing the player's average body colour (perf throttle).")]
    public float playerColorRefresh = 0.75f;

    [Header("Bot senses (world units — tuned to this scene's large scale)")]
    [Tooltip("How far a bot can spot the player.")]
    public float visionRange = 46f;
    [Tooltip("Full field-of-view angle of the vision cone (degrees).")]
    public float visionAngle = 100f;
    [Tooltip("Within this distance a bot notices the player even outside its cone (and spots them instantly). Camo (paint match) shrinks this toward minCatchRange, so good paint matters even up close.")]
    public float catchRange = 11f;
    [Tooltip("Instant-catch distance against a PERFECTLY camouflaged player. Good paint lets you stand much closer before being caught point-blank. Keep > 0 so you can never be invisible while touching a bot.")]
    public float minCatchRange = 3f;
    [Tooltip("Seconds of continuous sight before the player is declared spotted.")]
    public float spotHold = 1.4f;
    [Tooltip("Seconds an alerted bot keeps chasing after losing sight before giving up.")]
    public float loseTrack = 4f;
    [Tooltip("SEEKER SUSPICION: after losing a target, seconds a bot spends walking to the last spot it saw them and looking around before giving up and resuming its blind sweep. Makes bots read as actually searching instead of forgetting instantly. Difficulty-neutral — it only runs AFTER you've broken line of sight.")]
    public float investigateTime = 5f;

    [Header("Exposed high targets (anti ceiling-camping)")]
    [Tooltip("A player/hider clinging high on a wall or up near the ceiling is out in the open — a silhouette camo can't hide. When ON, a bot with a CLEAR line of sight sees such a HIGH target from much farther (highVisionRange) so it can be shot. Hiding high BEHIND cover still works (blocked sight); low/mid hiding is unchanged.")]
    public bool highExposureBoost = true;
    [Tooltip("Height ABOVE THE FLOOR (world units) at which a target counts as 'exposed high' and gets the extended see-range. Keep above the normal wall-cling height (~16u) so ordinary wall-hiding isn't affected — only near-ceiling camping.")]
    public float exposedHeightAboveFloor = 25f;
    [Tooltip("See-range used against an exposed HIGH target that has clear line of sight (large, to spot a ceiling-camper from across the room). Camo does NOT reduce this — a silhouette up high can't be painted away; cover (blocked sight) is the only escape.")]
    public float highVisionRange = 200f;

    [Header("Proximity sense (find a CONCEALED hider by getting close)")]
    [Tooltip("When ON, a seeker that comes CLOSE to a target it can't directly see stops and 'checks' the spot. Whether it FINDS you is driven by your CAMO accuracy (good paint = much harder) and your HIDING POSITION (behind cover + tucked away = harder; exposed = easier). Lets bots eventually find a concealed player WITHOUT being omniscient.")]
    public bool proximityReveal = true;
    [Tooltip("How close (world units) a bot must be to start sensing/checking a nearby concealed target.")]
    public float sniffRadius = 10f;
    [Tooltip("Base seconds to detect a CONCEALED, UN-painted target at close range. The core difficulty dial. Good camo multiplies this up a lot; a target the bot has line of sight to (exposed) is found sooner.")]
    public float sniffTime = 5f;
    [Tooltip("Max seconds a bot will hold and 'check' one spot before giving up and moving on. If detection would take longer than this (great camo / well hidden), the bot fails to find you and leaves. Keep > sniffTime so a poorly-hidden target is still caught.")]
    public float sniffMaxHold = 8f;
    [Tooltip("Detect-time multiplier at PERFECT camo. LOW = paint matters a lot: perfect paint makes detection take ~1/this as long (usually longer than sniffMaxHold -> you survive the check).")]
    [Range(0.05f, 1f)]
    public float sniffCamoMul = 0.3f;
    [Tooltip("How many times FASTER a bot senses a target it has clear line of sight to (exposed) vs one fully behind cover. 1 = position doesn't matter; higher = hiding behind cover is much safer.")]
    public float sniffExposedSpeedup = 2.5f;
    [Tooltip("Seconds a bot waits before re-checking the same area after giving up (so it doesn't instantly re-hold the same failed spot).")]
    public float sniffCooldown = 4f;
    [Tooltip("Targets higher than this above the floor are exempt from proximity sensing (climbers are handled by highExposureBoost instead).")]
    public float sniffMaxHeight = 12f;
    [Tooltip("When ON, a bot can sense a target within sniffRadius that's blocked ONLY by FURNITURE (a counter/shelf it peers past) even if it can't navmesh-path there — lets bots find a player squeezed into a pocket they can't physically enter (small Mode-2 player fits gaps the full-size bot doesn't). A real room WALL still blocks sensing (no seeing into sealed rooms).")]
    public bool sniffThroughFurniture = true;

    [Header("Search escalation (the net closes in over time)")]
    [Tooltip("When ON, the longer a round runs WITHOUT finding the target, the better the seekers get — camo protects less, vision/sense reach grows, and close sensing speeds up — so a well-hidden player is EVENTUALLY found instead of hiding forever. Early game plays normally (full camo/hiding advantage).")]
    public bool searchEscalation = true;
    [Tooltip("Seconds of SEARCHING after which escalation reaches FULL strength (the 'heat' ramps 0->1 over this time). Keep below the round length so the final stretch is max-intensity. Larger = the player stays safe longer.")]
    public float escalationFullTime = 90f;
    [Tooltip("At full heat, fraction of the CAMO benefit eroded (0 = camo always works, 1 = camo useless late game). Good paint keeps you safe EARLY but matters less as the net closes in.")]
    [Range(0f, 1f)]
    public float escalationCamoErode = 0.6f;
    [Tooltip("At full heat, the proximity-sense detect-time multiplier (LOWER = senses much faster late in the round).")]
    [Range(0.1f, 1f)]
    public float escalationSniffSpeedup = 0.35f;
    [Tooltip("At full heat, how much the see-range / sense-range grows (0.5 = +50%). Bots reach farther as the round wears on.")]
    public float escalationRangeBoost = 0.5f;

    [Header("Pack hunting (seekers act as a TEAM — smart, not stronger senses)")]
    [Tooltip("When ON, the moment a seeker SPOTS or senses the target, nearby ally seekers drop their patrol and CONVERGE on that spot to help corner it. Only allies within convergeRadius respond, so it reads as a coordinated pack rather than every bot swarming you from across the map. Difficulty-neutral senses — bots just react to a REAL sighting together instead of alone.")]
    public bool packConverge = true;
    [Tooltip("Only ally seekers within this distance (world units) of a sighting respond to it. Larger = a bigger chunk of the pack swarms; smaller = only the immediate neighbours help.")]
    public float convergeRadius = 60f;
    [Tooltip("While a seeker keeps a target engaged, it re-calls its allies to the target's CURRENT position this often (seconds) so the converging pack tracks a moving target instead of piling onto a stale spot.")]
    public float convergeInterval = 1.5f;
    [Tooltip("When ON, a seeker that LOSES sight heads to where the target was MOVING (extrapolated from its last velocity) instead of the exact last-seen spot — it reads as guessing your escape route and cutting it off. Difficulty-neutral: only runs AFTER you break line of sight.")]
    public bool predictiveSearch = true;
    [Tooltip("Seconds of the target's motion to extrapolate ahead when guessing its escape point (larger = the bot leads farther in front of where you were seen).")]
    public float predictLead = 1.2f;
    [Tooltip("Cap (world units) on how far ahead of the last-seen spot the predicted escape point may be, so a fast sprint doesn't fling the guess across the map.")]
    public float predictMaxDist = 30f;
    [Tooltip("Only predict a lead point if the target was moving at least this fast (world units/sec) when sight was lost; a near-stationary target is investigated right at its last-seen spot.")]
    public float predictMinSpeed = 2f;

    [Header("Late-game homing (bots converge on your AREA if you stay unfound — detection stays HONEST)")]
    [Tooltip("PLAY NOW: when ON, the longer a round runs WITHOUT finding the player, the more often a seeker aims its NEXT search spot right where you actually are, so the pack physically closes in on your area instead of maybe never wandering over. It only brings them TO your area — whether they then SEE you is UNCHANGED (camo / hiding position / proximity-sense still decide the catch).")]
    public bool homeInWhenLost = true;
    [Tooltip("Once homing is active (past homeInStartFraction of the hunt), the chance (0..1) that a bot's NEXT search target is a spot near the player instead of a normal patrol point. ~0.8 = most picks head toward you, the rest keep patrolling (so it isn't a robotic beeline). Applied per target-pick, so a bot reliably reaches your area over a few picks.")]
    [Range(0f, 1f)]
    public float homeInChance = 0.8f;
    [Tooltip("Fraction of the hunt (0..1) that must elapse before homing turns on. 0.667 = only in the LAST THIRD of the round do the bots start heading to your area; before that it's a fair blind search. From this mark to the end, each search-target pick has homeInChance to head toward you. Lower = bots start converging earlier.")]
    [Range(0f, 1f)]
    public float homeInStartFraction = 0.667f;
    [Tooltip("When false (Mode 1 ally seekers), the bots roam/search but never lock onto the player.")]
    public bool chasePlayer = true;

    [Header("Seeker gun (a clone of the player's gun, placed in each seeker bot's hand)")]
    public string gunObjectName = "gun";
    public string handBoneName = "mixamorig:RightHand";

    [Header("Seeker shooting (a spotted target is SHOT — the bullet's hit ends it, not the sighting)")]
    [Tooltip("Seconds between a seeker bot's shots while it keeps a target locked. The bullet's impact — not the spotting — is what catches the player / removes a hider.")]
    public float fireInterval = 0.7f;
    [Tooltip("Bullet travel speed (world units / second). Matches the player's gun.")]
    public float bulletSpeed = 160f;
    [Tooltip("Bullet visual diameter (world units) — the scene is large-scale, keep it chunky.")]
    public float bulletSize = 1.2f;
    [Tooltip("Seconds before a bullet that hit nothing despawns.")]
    public float bulletLife = 3f;
    public Color bulletColor = new Color(1f, 0.55f, 0.15f, 1f);

    private GameObject bulletPrefab;                                                     // 0x150
    private State state;                                                                 // 0x158
    private readonly List<SeekBot> bots = new List<SeekBot>();                           // 0x160
    private readonly List<Transform> targets = new List<Transform>();                    // 0x168
    private readonly Dictionary<Transform, Ragdoll> targetRagdoll = new Dictionary<Transform, Ragdoll>(); // 0x170
    private readonly List<Vector3> searchPoints = new List<Vector3>();                   // 0x178
    private float[] checkedAt;                                                           // 0x180
    private bool[] claimed;                                                              // 0x188
    private float floorY;                                                                // 0x190
    private Transform playerTf;                                                          // 0x198
    private Renderer[] playerRends;                                                      // 0x1A0
    private Bounds playerBounds;                                                         // 0x1A8
    private ChameleonPaint paint;                                                        // 0x1C0
    private Color playerAvgColor = Color.gray;                                           // 0x1C8
    private float playerColorTimer;                                                      // 0x1D8
    private Texture2D _camoBlock;                                                        // 0x1E0
    private float timeLeft;                                                              // 0x1E8
    private float graceLeft;                                                             // 0x1EC
    private float _searchHeat;                                                           // 0x1F0
    private Vector3 _playerOrigScale = Vector3.one;                                      // 0x1F4
    private bool _playerScaled;                                                          // 0x200
    private NavMeshData navData;                                                         // 0x208
    private NavMeshDataInstance navInstance;                                             // 0x210
    private readonly RaycastHit[] _rayHits = new RaycastHit[32];                         // 0x218
    private readonly Dictionary<Collider, bool> _isCharacterCache = new Dictionary<Collider, bool>(); // 0x220
    private readonly Dictionary<Material, Color> _bgColorCache = new Dictionary<Material, Color>();   // 0x228
    private int _lastGraceSec = -1;                                                      // 0x230
    private GameObject startBtnGO;                                                       // 0x238
    private bool startBtnResolved;                                                       // 0x240
    private Text statusText;                                                             // 0x248
    private Text resultText;                                                             // 0x250
    private GameObject statusCanvas;                                                     // 0x258
    private Font font;                                                                   // 0x260
    private HideModeResultUI resultUI;                                                   // 0x268
    [Tooltip("PLAY NOW: seconds the player's ragdoll flops in view after being caught BEFORE the CaughtPopup appears.")]
    public float caughtPopupDelay = 3f;
    [Tooltip("PLAY NOW: when a seeker SPOTS you, the camera swings to that bot's point of view and you're frozen (joystick hidden); the bot fires this many seconds later.")]
    public float spotShootDelay = 3f;
    private bool _spotting;                                                              // 0x278
    private bool _spotFired;                                                             // 0x279
    private SeekBot _spotBot;                                                            // 0x280
    private float _spotTimer;                                                            // 0x288
    private CameraController camCtrl;                                                    // 0x290
    private Coroutine _caughtRoutine;                                                    // 0x298
    private int _caughtTimeLeft;                                                         // 0x2A0
    private GameModeManager gmm;                                                         // 0x2A8
    private BotManager botMgr;                                                           // 0x2B0
    private Ragdoll playerRagdoll;                                                       // 0x2B8
    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int PoseHash = Animator.StringToHash("Pose");
    private static readonly int GunFireHash = Animator.StringToHash("GunFire");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    // Used only by the revive callback (RevivePlayer's lambda) - separate from resultUI.
    public HideModeResultUI hideUI;                                                      // 0x2C0

    // ====================== PROPERTIES ======================

    public bool Active => state == State.Grace || state == State.Searching;

    public int TimerSeconds => (int)Mathf.Max(0f, state == State.Grace ? graceLeft : timeLeft);

    public float TimerFraction
    {
        get
        {
            float total = state == State.Grace ? graceTime : roundTime;
            float left = state == State.Grace ? graceLeft : timeLeft;
            return Mathf.Clamp01(left / Mathf.Max(0.01f, total));
        }
    }

    public int FugitivesLeft => state != State.Over ? 1 : 0;

    public int HuntersLeft => bots.Count;

    public int BotCount => bots.Count;

    // ====================== SETUP / LIFECYCLE ======================

    public void SetBots(string[] names)
    {
        botNames = names ?? new string[0];
        ResolveBots();
        ComputeFloorY();
    }

    private void Start()
    {
        font = UIFont.Default;
        ResolveBots();
        ComputeFloorY();

        GameObject player = PlayerRef.Resolve(playerName);
        if (player != null)
        {
            playerTf = player.transform;
            playerRends = playerTf.GetComponentsInChildren<Renderer>();
        }

        paint = Object.FindFirstObjectByType<ChameleonPaint>();
        resultUI = Object.FindFirstObjectByType<HideModeResultUI>();
        gmm = Object.FindFirstObjectByType<GameModeManager>();
        botMgr = Object.FindFirstObjectByType<BotManager>();
        camCtrl = Object.FindFirstObjectByType<CameraController>();

        if (bulletPrefab == null)
        {
            GameObject loaded = Resources.Load<GameObject>("Bullet");
            if (loaded != null)
            {
                bulletPrefab = loaded;
            }
        }

        BuildStatusUI();
        SetStatusVisible(false);
    }

    private void OnDestroy()
    {
        if (navInstance.valid)
        {
            NavMesh.RemoveNavMeshData(navInstance);
        }
        if (_camoBlock != null)
        {
            Object.Destroy(_camoBlock);
        }
    }

    public void StartSeeking()
    {
        if (playerTf == null)
        {
            GameObject player = PlayerRef.Resolve(playerName);
            if (player != null)
            {
                playerTf = player.transform;
                playerRends = playerTf.GetComponentsInChildren<Renderer>();
            }
        }

        if (paint == null)
        {
            paint = Object.FindFirstObjectByType<ChameleonPaint>();
        }

        EnsureColliders();
        if (chasePlayer)
        {
            ScalePlayerForMode2();
        }
        EnsureNavMesh();
        ComputeFloorY();
        ResetBots();
        AttachGunsToSeekers();
        BuildSearchPoints();

        checkedAt = new float[searchPoints.Count];
        for (int i = 0; i < checkedAt.Length; i++)
        {
            checkedAt[i] = -9999f;
        }
        claimed = new bool[searchPoints.Count];

        graceLeft = graceTime;
        state = State.Grace;
        timeLeft = chasePlayer ? roundTime : seekerHuntTime;
        SetStatusVisible(chasePlayer);
        if (resultText != null)
        {
            resultText.text = "";
        }

        if (chasePlayer)
        {
            AudioManager.ResetCountdown();
            AudioManager.PhaseStart();
        }
    }

    public void StopMode()
    {
        state = State.Idle;
        if (_caughtRoutine != null)
        {
            StopCoroutine(_caughtRoutine);
            _caughtRoutine = null;
        }
        RestorePlayerRagdoll();
        ResetBots();
        RestorePlayer();
        SetStatusVisible(false);
        SetStartButtonVisible(false);
        _isCharacterCache.Clear();
        if (resultText != null)
        {
            resultText.text = "";
        }
    }

    // The hider's "start now" button: shows an interstitial, then ends the grace window. A null
    // RootManager throws.
    public void SkipGrace()
    {
        // Singleton<RootManager>.Instance.ShowInterAds_Native();
        if (state == State.Grace && chasePlayer)
        {
            graceLeft = 0f;
        }
    }

    private void ResolveStartButton()
    {
        if (startBtnResolved) return;
        startBtnResolved = true;
        startBtnGO = string.IsNullOrEmpty(startButtonName) ? null : FindInScene(startButtonName);
    }

    private void SetStartButtonVisible(bool v)
    {
        if (!startBtnResolved)
        {
            ResolveStartButton();
        }
        if (startBtnGO == null) return;
        if (startBtnGO.activeSelf == v) return;
        startBtnGO.SetActive(v);
    }

    // ====================== MAIN LOOP ======================

    // Grace: in Play Now the player's hide countdown runs here; in Seeker mode the bots just wait for
    // GameModeManager's opening countdown. Searching: spot sequence, timers, camo colour, and per-bot
    // move/detect/look. Running out the clock without being caught is a win.
    private void Update()
    {
        SetStartButtonVisible(chasePlayer && state == State.Grace);

        if (state == State.Idle || state == State.Over) return;
        if (playerTf == null) return;

        playerBounds = PlayerBounds();

        if (state == State.Grace)
        {
            if (!chasePlayer)
            {
                if (gmm != null && gmm.SeekerHideCountdown) return;
            }
            else
            {
                graceLeft -= Time.deltaTime;
                AudioManager.Countdown(graceLeft);
                int sec = (int)Mathf.Max(0f, graceLeft);
                if (statusText != null && sec != _lastGraceSec)
                {
                    statusText.text = "HIDE! Seekers search in " + sec.ToString() + "s";
                    _lastGraceSec = sec;
                }
                if (graceLeft > 0f) return;
            }

            state = State.Searching;
            if (statusText != null)
            {
                statusText.text = "";
            }
            if (chasePlayer)
            {
                AudioManager.ResetCountdown();
                AudioManager.PhaseSeek();
            }
            return;
        }

        if (_spotting)
        {
            UpdateSpot();
            return;
        }

        timeLeft -= Time.deltaTime;
        _searchHeat = SearchHeat();
        if (chasePlayer)
        {
            AudioManager.Countdown(timeLeft);
        }

        if (camoAffectsVision && paint != null)
        {
            playerColorTimer -= Time.deltaTime;
            if (playerColorTimer <= 0f)
            {
                if (paint.TryGetAverageBodyColor(out Color avg))
                {
                    playerAvgColor = avg;
                }
                playerColorTimer = playerColorRefresh;
            }
        }

        for (int i = 0; i < bots.Count; i++)
        {
            SeekBot b = bots[i];
            if (b.activateTimer > 0f)
            {
                b.activateTimer -= Time.deltaTime;
                if (b.anim != null)
                {
                    b.anim.SetFloat(SpeedHash, 0f);
                }
                continue;
            }

            if (b.fireTimer > 0f)
            {
                b.fireTimer -= Time.deltaTime;
            }
            StepBot(b, Time.deltaTime);
            Detect(b, Time.deltaTime);
            UpdateLookTarget(b, Time.deltaTime);
            if (state == State.Over) return;
        }

        if (timeLeft <= 0f)
        {
            Win();
        }
    }

    // ====================== MOVEMENT ======================

    private void StepBot(SeekBot b, float dt)
    {
        if (b.tf == null) return;

        // Gravity (dt capped at 0.05 s).
        if (b.cc != null && b.cc.enabled)
        {
            float gdt = Mathf.Min(dt, 0.05f);
            if (b.cc.isGrounded && b.vy < 0f)
            {
                b.vy = -2f;
            }
            else
            {
                b.vy -= gdt * botGravity;
            }
            if (Mathf.Abs(b.vy) > 0.001f)
            {
                b.cc.Move(Vector3.up * (gdt * b.vy));
            }
        }

        if (b.investigating)
        {
            b.idleAnchor = b.tf.position;
            b.idleTimer = 0f;
            StepInvestigate(b, dt);
            return;
        }

        if (b.sniffHold)
        {
            if (b.anim != null)
            {
                b.anim.SetFloat(SpeedHash, 0f);
            }
            LookAroundInPlace(b, dt);
            b.idleAnchor = b.tf.position;
            b.idleTimer = 0f;
            return;
        }

        // Anti-freeze: a bot that hasn't left a 2-unit radius for stuckEscapeTime is sent home.
        if (stuckEscapeTime > 0f)
        {
            if (HorizDist(b.tf.position, b.idleAnchor) <= 2f)
            {
                b.idleTimer += dt;
                if (b.idleTimer >= stuckEscapeTime)
                {
                    EscapeIdle(b);
                    return;
                }
            }
            else
            {
                b.idleAnchor = b.tf.position;
                b.idleTimer = 0f;
            }
        }

        // Pick the goal: the last-seen point when alerted, otherwise a search point.
        Vector3 goal;
        if (b.alerted)
        {
            goal = b.lastSeen;
        }
        else
        {
            if (b.targetIdx < 0)
            {
                b.targetIdx = PickSearchIndex(b);
                b.corners = null;
                b.targetTimer = 0f;
                b.progressTimer = 0f;
                b.noProgressTimer = 0f;
                b.bestDist = float.MaxValue;
                b.progressAnchor = b.tf.position;
                if (b.targetIdx >= 0)
                {
                    float d = Vector3.Distance(b.tf.position, searchPoints[b.targetIdx]);
                    b.targetBudget = Mathf.Clamp(d / Mathf.Max(1f, walkSpeed) * 2.2f + 2.5f, 4f, 12f);
                }
                if (b.targetIdx < 0)
                {
                    if (b.anim != null)
                    {
                        b.anim.SetFloat(SpeedHash, 0f);
                    }
                    return;
                }
            }

            // At the search point: stand and look around for scanTime, then mark it checked.
            Vector3 sp = searchPoints[b.targetIdx];
            Vector3 pos = b.tf.position;
            Vector3 toPoint = new Vector3(sp.x - pos.x, 0f, sp.z - pos.z);
            if (toPoint.magnitude <= arriveDistance * 2f)
            {
                if (b.anim != null)
                {
                    b.anim.SetFloat(SpeedHash, 0f);
                }
                b.scanTimer += dt;
                LookAroundInPlace(b, dt);
                if (b.scanTimer < scanTime) return;
                b.scanTimer = 0f;
                MarkChecked(b.targetIdx);
                b.targetIdx = -1;
                return;
            }

            goal = searchPoints[b.targetIdx];
        }

        // Path (re)planning. A search point the path can't actually reach is marked checked.
        b.repathTimer -= dt;
        if (b.corners == null || b.ci >= b.corners.Length || b.repathTimer <= 0f)
        {
            b.corners = ComputePath(b.tf.position, goal);
            b.ci = 0;
            b.repathTimer = repathInterval;
            if (!b.alerted && b.corners != null && b.corners.Length != 0)
            {
                Vector3 last = b.corners[b.corners.Length - 1];
                if (HorizDist(last, searchPoints[b.targetIdx]) > arriveDistance * 2f)
                {
                    MarkChecked(b.targetIdx);
                    b.targetIdx = -1;
                    b.corners = null;
                    if (b.anim != null)
                    {
                        b.anim.SetFloat(SpeedHash, 0f);
                    }
                    return;
                }
            }
        }

        if (b.corners == null || b.corners.Length == 0)
        {
            if (b.anim != null)
            {
                b.anim.SetFloat(SpeedHash, 0f);
            }
            if (b.alerted) return;
            MarkChecked(b.targetIdx);
            b.targetIdx = -1;
            return;
        }

        if (b.ci >= b.corners.Length)
        {
            if (b.anim != null)
            {
                b.anim.SetFloat(SpeedHash, 0f);
            }
            if (!b.alerted)
            {
                MarkChecked(b.targetIdx);
                b.corners = null;
                b.targetIdx = -1;
            }
            else
            {
                b.corners = null;
            }
            return;
        }

        // Steer toward the current corner.
        Vector3 corner = b.corners[b.ci];
        Vector3 here = b.tf.position;
        Vector3 toCorner = new Vector3(corner.x - here.x, 0f, corner.z - here.z);
        float cornerDist = toCorner.magnitude;
        if (cornerDist <= arriveDistance)
        {
            b.ci++;
            return;
        }
        cornerDist = Mathf.Max(cornerDist, 0.001f);
        Vector3 dir = toCorner / cornerDist;

        Vector3 sep = SeparationFrom(b);
        if (sep != Vector3.zero)
        {
            dir = Vector3.Normalize(dir + sep * botAvoidStrength);
        }

        // Stuck side-step: mostly sideways (perpendicular to the path) with 35% forward.
        if (b.sidestepTimer > 0f)
        {
            b.sidestepTimer -= dt;
            dir = Vector3.Normalize(dir * 0.35f + Vector3.Cross(Vector3.up, dir) * b.sidestepDir);
        }

        Vector3 before = b.tf.position;
        float step = walkSpeed * dt;
        if (b.cc != null && b.cc.enabled)
        {
            b.cc.Move(dir * step);
        }
        else
        {
            b.tf.position = b.tf.position + dir * step;
        }
        if (b.anim != null)
        {
            b.anim.SetFloat(SpeedHash, 1f);
        }

        // Progress check every 1.4 s: less than 4 units covered means the pursuit is abandoned.
        b.progressTimer += dt;
        if (b.progressTimer >= 1.4f)
        {
            float moved = HorizDist(b.tf.position, b.progressAnchor);
            b.progressTimer = 0f;
            b.progressAnchor = b.tf.position;
            if (moved < 4f)
            {
                AbandonPursuit(b);
                return;
            }
        }

        // Per-frame stuck detection.
        Vector3 after = b.tf.position;
        float movedNow = new Vector2(after.x - before.x, after.z - before.z).magnitude;
        if (movedNow >= walkSpeed * dt * 0.35f)
        {
            b.stuckTimer = Mathf.Max(0f, b.stuckTimer - dt * 1.5f);
        }
        else
        {
            b.stuckTimer += dt;
            if (b.stuckTimer > 0.3f && b.sidestepTimer <= 0f)
            {
                b.sidestepTimer = 0.5f;
                b.sidestepDir = Random.value < 0.5f ? 1 : -1;
                b.corners = null;
                b.repathTimer = 0f;
            }

            if (b.stuckTimer > 1.5f)
            {
                // Hard stuck: snap onto the nearest floor NavMesh and drop the current target.
                Vector3 probe = new Vector3(b.tf.position.x, floorY, b.tf.position.z);
                if (b.cc != null
                    && NavMesh.SamplePosition(probe, out NavMeshHit hit, 12f, -1)
                    && hit.position.y <= floorY + 3f)
                {
                    b.cc.enabled = false;
                    b.tf.position = new Vector3(hit.position.x, b.tf.position.y, hit.position.z);
                    b.cc.enabled = true;
                }
                if (!b.alerted)
                {
                    MarkChecked(b.targetIdx);
                    b.targetIdx = -1;
                }
                b.stuckTimer = 0f;
                b.sidestepTimer = 0f;
                b.corners = null;
                b.progressTimer = 0f;
                b.progressAnchor = b.tf.position;
                if (b.anim != null)
                {
                    b.anim.SetFloat(SpeedHash, 0f);
                }
                return;
            }
        }

        Vector3 face;
        if (b.alerted)
        {
            Vector3 p = b.tf.position;
            face = new Vector3(b.lastSeen.x - p.x, 0f, b.lastSeen.z - p.z);
        }
        else
        {
            // Give up on a search point that isn't getting closer (2.5 s) or is over budget.
            b.targetTimer += dt;
            Vector3 p = b.tf.position;
            float goalDist = new Vector2(goal.x - p.x, goal.z - p.z).magnitude;
            bool giveUp = false;
            if (goalDist < b.bestDist - 0.5f)
            {
                b.bestDist = goalDist;
                b.noProgressTimer = 0f;
            }
            else
            {
                b.noProgressTimer += dt;
                if (b.noProgressTimer > 2.5f) giveUp = true;
            }
            if (!giveUp && b.targetTimer > b.targetBudget) giveUp = true;

            if (giveUp)
            {
                MarkChecked(b.targetIdx);
                b.targetIdx = -1;
                b.noProgressTimer = 0f;
                b.bestDist = float.MaxValue;
                b.targetTimer = 0f;
                b.corners = null;
                if (b.anim != null)
                {
                    b.anim.SetFloat(SpeedHash, 0f);
                }
                return;
            }
            face = new Vector3(dir.x, 0f, dir.z);
        }

        if (face.x * face.x + face.z * face.z <= 0.001f) return;
        b.tf.rotation = Quaternion.Slerp(b.tf.rotation, Quaternion.LookRotation(face), dt * 10f);
    }

    // Steps the bot 8 units backwards (onto the floor NavMesh) and drops the current chase/target.
    private void AbandonPursuit(SeekBot b)
    {
        if (b.tf == null) return;

        Vector3 fwd = b.tf.forward;
        Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
        Vector3 back = flat.sqrMagnitude >= 0.01f ? -flat.normalized : Vector3.forward;

        Vector3 pos = b.tf.position;
        if (b.cc != null)
        {
            Vector3 probe = new Vector3(back.x * 8f + pos.x, floorY, back.z * 8f + pos.z);
            if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 14f, -1) && hit.position.y <= floorY + 3f)
            {
                b.cc.enabled = false;
                b.tf.position = new Vector3(hit.position.x, b.tf.position.y, hit.position.z);
                b.cc.enabled = true;
            }
        }

        if (!b.alerted)
        {
            if (b.targetIdx >= 0)
            {
                MarkChecked(b.targetIdx);
            }
        }
        else
        {
            b.alerted = false;
            b.sightTimer = 0f;
            b.lostTimer = 0f;
        }

        b.corners = null;
        b.targetIdx = -1;
        b.repathTimer = 0f;
        b.targetTimer = 0f;
        b.progressTimer = 0f;
        b.noProgressTimer = 0f;
        b.bestDist = float.MaxValue;
        b.stuckTimer = 0f;
        b.sidestepTimer = 0f;
        b.progressAnchor = b.tf.position;
        if (b.anim != null)
        {
            b.anim.SetFloat(SpeedHash, 0f);
        }
    }

    private void BeginInvestigate(SeekBot b, Vector3 point)
    {
        b.investigating = true;
        b.investigatePoint = point;
        b.investigateTimer = Mathf.Max(0.5f, investigateTime);
        if (b.targetIdx >= 0)
        {
            ReleaseClaim(b.targetIdx);
            b.targetIdx = -1;
        }
        b.corners = null;
        b.repathTimer = 0f;
        b.targetTimer = 0f;
        b.noProgressTimer = 0f;
        b.bestDist = float.MaxValue;
        b.stuckTimer = 0f;
        b.sidestepTimer = 0f;
        b.progressTimer = 0f;
        b.progressAnchor = b.tf != null ? b.tf.position : Vector3.zero;
    }

    // Walks to investigatePoint and looks around there until investigateTimer runs out.
    private void StepInvestigate(SeekBot b, float dt)
    {
        b.investigateTimer -= dt;

        float dx = b.investigatePoint.x - b.tf.position.x;
        float dz = b.investigatePoint.z - b.tf.position.z;
        if (Mathf.Sqrt(dx * dx + dz * dz) <= arriveDistance * 2.5f || b.investigateTimer <= 0f)
        {
            if (b.anim != null)
            {
                b.anim.SetFloat(SpeedHash, 0f);
            }
            LookAroundInPlace(b, dt);
            if (b.investigateTimer > 0f) return;
            EndInvestigate(b);
            return;
        }

        b.repathTimer -= dt;
        if (b.corners == null || b.ci >= b.corners.Length || b.repathTimer <= 0f)
        {
            b.corners = ComputePath(b.tf.position, b.investigatePoint);
            b.ci = 0;
            b.repathTimer = repathInterval;
            if (b.corners == null)
            {
                EndInvestigate(b);
                return;
            }
        }
        if (b.corners.Length == 0)
        {
            EndInvestigate(b);
            return;
        }
        if (b.ci >= b.corners.Length)
        {
            b.corners = null;
            return;
        }

        Vector3 corner = b.corners[b.ci];
        float cx = corner.x - b.tf.position.x;
        float cz = corner.z - b.tf.position.z;
        float cornerDist = Mathf.Sqrt(cx * cx + cz * cz);
        if (cornerDist <= arriveDistance)
        {
            b.ci++;
            return;
        }
        cornerDist = Mathf.Max(cornerDist, 0.001f);
        Vector3 dir = new Vector3(cx, 0f, cz) / cornerDist;

        Vector3 sep = SeparationFrom(b);
        if (sep != Vector3.zero)
        {
            dir = Vector3.Normalize(dir + sep * botAvoidStrength);
        }

        float step = walkSpeed * dt;
        if (b.cc != null && b.cc.enabled)
        {
            b.cc.Move(dir * step);
        }
        else
        {
            b.tf.position = b.tf.position + dir * step;
        }
        if (b.anim != null)
        {
            b.anim.SetFloat(SpeedHash, 1f);
        }

        if (dir.z * dir.z + dir.x * dir.x > 0.001f)
        {
            b.tf.rotation = Quaternion.Slerp(b.tf.rotation, Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z)), dt * 10f);
        }

        b.progressTimer += dt;
        if (b.progressTimer < 1.4f) return;
        float moved = HorizDist(b.tf.position, b.progressAnchor);
        b.progressTimer = 0f;
        b.progressAnchor = b.tf.position;
        if (moved < 4f)
        {
            EndInvestigate(b);
        }
    }

    private void EndInvestigate(SeekBot b)
    {
        b.corners = null;
        b.investigating = false;
        b.investigateTimer = 0f;
        b.targetIdx = -1;
        b.repathTimer = 0f;
        b.progressTimer = 0f;
        b.progressAnchor = b.tf != null ? b.tf.position : Vector3.zero;
        if (b.anim != null)
        {
            b.anim.SetFloat(SpeedHash, 0f);
        }
    }

    // Pulls idle allies within convergeRadius to investigate the point (skipping those already
    // investigating the same spot). The investigation lasts at least long enough to walk there.
    private void CallForBackup(SeekBot caller, Vector3 point)
    {
        if (!packConverge) return;

        for (int i = 0; i < bots.Count; i++)
        {
            SeekBot b = bots[i];
            if (b == caller) continue;
            if (b.tf == null || b.alerted || b.sniffHold || b.activateTimer > 0f) continue;

            float d = HorizDist(b.tf.position, point);
            if (d > convergeRadius) continue;
            if (b.investigating && HorizDist(b.investigatePoint, point) < arriveDistance * 2f) continue;

            BeginInvestigate(b, point);
            b.investigateTimer = Mathf.Max(investigateTime, d / Mathf.Max(1f, walkSpeed) + 3f);
        }
    }

    // Where to look after losing the target: the last-seen spot, or (predictive search) a point
    // ahead along the target's last velocity, snapped to the floor NavMesh when possible.
    private Vector3 PredictLostPoint(SeekBot b)
    {
        if (!predictiveSearch || !b.seenHasVel)
        {
            return b.lastSeen;
        }

        float speed = b.seenVel.magnitude;
        Vector3 p = b.lastSeen;
        if (speed < predictMinSpeed)
        {
            return p;
        }

        Vector3 dir = b.seenVel.normalized;
        float lead = Mathf.Min(speed * predictLead, predictMaxDist);
        Vector3 guess = p + dir * lead;
        Vector3 probe = new Vector3(guess.x, floorY, guess.z);
        if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 12f, -1) && hit.position.y <= floorY + 4f)
        {
            return new Vector3(hit.position.x, b.lastSeen.y, hit.position.z);
        }
        return guess;
    }

    // Anti-freeze escape: walk home if possible, else hop onto the main NavMesh nearby, else
    // teleport straight to the home point.
    private void EscapeIdle(SeekBot b)
    {
        if (b.tf == null) return;

        Vector3 home = ValidRoomHome(b.originPos, bots.IndexOf(b));
        if (b.targetIdx >= 0)
        {
            MarkChecked(b.targetIdx);
        }
        b.alerted = false;
        b.sightTimer = 0f;
        b.lostTimer = 0f;

        if (PathReaches(b.tf.position, home))
        {
            BeginInvestigate(b, home);
            b.investigateTimer = Mathf.Max(investigateTime, HorizDist(b.tf.position, home) / Mathf.Max(1f, walkSpeed) + 4f);
            b.idleAnchor = b.tf.position;
            b.idleTimer = 0f;
            return;
        }

        if (FindMainNavmeshNear(b.tf.position, home, out Vector3 near))
        {
            if (b.cc != null) b.cc.enabled = false;
            b.tf.position = new Vector3(near.x, b.tf.position.y, near.z);
            if (b.cc != null) b.cc.enabled = true;

            b.corners = null;
            b.targetIdx = -1;
            b.repathTimer = 0f;
            b.investigating = false;
            b.targetTimer = 0f;
            b.noProgressTimer = 0f;
            b.bestDist = float.MaxValue;
            b.stuckTimer = 0f;
            b.sidestepTimer = 0f;
            b.progressTimer = 0f;
            b.progressAnchor = b.tf.position;
            b.vy = 0f;
            b.idleAnchor = b.tf.position;
            b.idleTimer = 0f;
            if (b.anim != null)
            {
                b.anim.SetFloat(SpeedHash, 0f);
            }
            return;
        }

        if (b.cc != null) b.cc.enabled = false;
        b.tf.position = home;
        if (b.cc != null) b.cc.enabled = true;

        b.corners = null;
        b.targetIdx = -1;
        b.progressAnchor = home;
        b.idleAnchor = home;
        b.repathTimer = 0f;
        b.investigating = false;
        b.targetTimer = 0f;
        b.noProgressTimer = 0f;
        b.bestDist = float.MaxValue;
        b.stuckTimer = 0f;
        b.sidestepTimer = 0f;
        b.progressTimer = 0f;
        b.vy = 0f;
        b.idleTimer = 0f;
        if (b.anim != null)
        {
            b.anim.SetFloat(SpeedHash, 0f);
        }
        Debug.Log("[BotSeek] " + b.tf.name + " deeply stranded — teleported to spawn.");
    }

    // Rings of radius 3..21 (step 3), 8 directions each: the first floor NavMesh point from which a
    // path reaches mainRef.
    private bool FindMainNavmeshNear(Vector3 pos, Vector3 mainRef, out Vector3 result)
    {
        result = pos;
        for (float r = 3f; r <= 22f; r += 3f)
        {
            for (int a = 0; a < 360; a += 45)
            {
                float rad = a * Mathf.Deg2Rad;
                Vector3 probe = new Vector3(pos.x + r * Mathf.Cos(rad), floorY, pos.z + r * Mathf.Sin(rad));
                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 2.5f, -1)
                    && hit.position.y <= floorY + 3f
                    && PathReaches(hit.position, mainRef))
                {
                    result = hit.position;
                    return true;
                }
            }
        }
        return false;
    }

    private bool PathReaches(Vector3 from, Vector3 to)
    {
        Vector3[] c = ComputePath(from, to);
        if (c == null || c.Length == 0) return false;
        return HorizDist(c[c.Length - 1], to) <= arriveDistance * 2f;
    }

    // A floor point on a spiral (radius 18..74) around the player (or this object), 7 units above
    // the NavMesh. Fallbacks: the NavMesh point nearest the centre (+7), then the NavMesh point
    // near origin (origin's own y kept), then origin itself.
    private Vector3 ValidRoomHome(Vector3 origin, int seed)
    {
        Transform t = playerTf != null ? playerTf : transform;
        Vector3 c = t.position;
        NavMeshHit hit;

        for (int r = 18; r <= 74; r += 8)
        {
            float a = seed * 1.3f + r / 2f;
            Vector3 probe = new Vector3(c.x + Mathf.Cos(a) * r, floorY, c.z + Mathf.Sin(a) * r);
            if (NavMesh.SamplePosition(probe, out hit, 8f, -1) && hit.position.y <= floorY + 4f)
            {
                return new Vector3(hit.position.x, hit.position.y + 7f, hit.position.z);
            }
        }

        c.y = floorY;
        if (NavMesh.SamplePosition(c, out hit, 100f, -1))
        {
            return new Vector3(hit.position.x, hit.position.y + 7f, hit.position.z);
        }

        if (NavMesh.SamplePosition(new Vector3(origin.x, floorY, origin.z), out hit, 20f, -1))
        {
            return new Vector3(hit.position.x, origin.y, hit.position.z);
        }
        return origin;
    }

    // ====================== DETECTION ======================

    // Chooses between the player (if seen) and the nearest visible hider bot, falls back to the
    // proximity sniff, updates alert/last-seen/velocity state, and acts: the player gets the spot
    // sequence (Play Now), anything else is shot directly.
    private void Detect(SeekBot b, float dt)
    {
        bool isPlayer = CanSee(b);
        Transform hider = NearestVisibleHiderBot(b);
        Transform target = isPlayer ? playerTf : null;
        bool sniffed = false;

        if (hider != null)
        {
            if (target == null || HorizDist(b.tf.position, hider.position) < HorizDist(b.tf.position, target.position))
            {
                isPlayer = false;
                target = hider;
            }
        }

        if (target != null)
        {
            b.sniffHold = false;
            b.sniff01 = 0f;
            b.sniffHoldTimer = 0f;
        }
        else if (!proximityReveal)
        {
            b.sniffHold = false;
        }
        else
        {
            target = ProximitySniff(b, dt, out isPlayer, out sniffed);
        }

        if (target == null)
        {
            b.sightTimer = Mathf.Max(0f, b.sightTimer - dt * 0.7f);
            if (!b.alerted) return;
            b.lostTimer += dt;
            if (b.lostTimer < loseTrack) return;

            b.alerted = false;
            b.lostTimer = 0f;
            Vector3 guess = PredictLostPoint(b);
            BeginInvestigate(b, guess);
            if (packConverge)
            {
                CallForBackup(b, guess);
            }
            return;
        }

        bool wasAlerted = b.alerted;
        if (!wasAlerted)
        {
            if (b.targetIdx >= 0)
            {
                ReleaseClaim(b.targetIdx);
                b.targetIdx = -1;
            }
            b.progressTimer = 0f;
            b.progressAnchor = b.tf.position;
            b.seenHasVel = false;
            b.investigating = false;
            b.seenVel = Vector3.zero;
            b.alerted = true;
        }
        else
        {
            b.investigating = false;
            if (dt > 0.0001f)
            {
                Vector3 tp = target.position;
                Vector3 vel = new Vector3((tp.x - b.lastSeen.x) / dt, 0f, (tp.z - b.lastSeen.z) / dt);
                if (b.seenHasVel)
                {
                    vel = Vector3.Lerp(b.seenVel, vel, 0.4f);
                }
                b.seenVel = vel;
                b.seenHasVel = true;
            }
            b.alerted = true;
        }

        b.lastSeen = target.position;
        b.lostTimer = 0f;

        if (packConverge)
        {
            b.backupTimer -= dt;
            if (!wasAlerted || b.backupTimer <= 0f)
            {
                CallForBackup(b, target.position);
                b.backupTimer = convergeInterval;
            }
        }

        b.sightTimer += dt;
        float dist = HorizDist(b.tf.position, target.position);
        float reach = (isPlayer && camoAffectsVision)
            ? catchRange + (minCatchRange - catchRange) * Mathf.Clamp01(b.camo01)
            : catchRange;

        if (sniffed || b.sightTimer >= spotHold || dist < reach)
        {
            if (!isPlayer || !chasePlayer)
            {
                FireAtTarget(b, target, isPlayer);
                return;
            }
            if (!_spotting)
            {
                BeginSpot(b);
            }
        }
    }

    // Escalation heat 0..1: how far into escalationFullTime the search has run.
    private float SearchHeat()
    {
        if (!searchEscalation || state != State.Searching) return 0f;
        float total = chasePlayer ? roundTime : seekerHuntTime;
        return Mathf.Clamp01((total - timeLeft) / Mathf.Max(1f, escalationFullTime));
    }

    private void UpdateLookTarget(SeekBot b, float dt)
    {
        if (b.look == null) return;
        if (b.tf == null) return;

        if (b.alerted)
        {
            Vector3 p = b.lastSeen + Vector3.up * 3f;
            b.look.LookAt(p);
            b.look.AimAt(p);
            return;
        }

        if (_spotting && _spotBot == b && playerTf != null)
        {
            b.look.LookAt(playerBounds.center);
            b.look.AimAt(playerBounds.center);
            return;
        }

        Vector3 lookPoint;
        if (b.sniffHold)
        {
            if (chasePlayer && playerTf != null)
            {
                lookPoint = playerBounds.center;
            }
            else
            {
                lookPoint = b.tf.position + b.tf.forward * 4f;
            }
        }
        else if (b.investigating)
        {
            lookPoint = b.investigatePoint + Vector3.up * 3f;
        }
        else
        {
            b.poiTimer -= dt;
            if (b.poiTimer <= 0f)
            {
                b.poiPoint = PickPOI(b);
                b.poiTimer = Random.Range(1f, 2.6f);
            }
            lookPoint = b.poiPoint;
        }

        b.look.LookAt(lookPoint);
        b.look.AimLoose(lookPoint);
    }

    // Idle glance target: half the time a random search point (raised 0..7), otherwise a point
    // 6..20 units away within ±110° of the bot's heading.
    private Vector3 PickPOI(SeekBot b)
    {
        Vector3 pos = b.tf.position;
        if (searchPoints.Count > 0 && Random.value < 0.5f)
        {
            Vector3 sp = searchPoints[Random.Range(0, searchPoints.Count)];
            return sp + Vector3.up * Random.Range(0f, 7f);
        }

        float yaw = b.tf.eulerAngles.y + Random.Range(-110f, 110f);
        Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        float dist = Random.Range(6f, 20f);
        float x = pos.x + dir.x * dist;
        float z = pos.z + dir.z * dist;
        float y = pos.y + Random.Range(-4f, 12f);
        return new Vector3(x, y, z);
    }

    // Turns toward a random yaw 45..115° left/right, picking a new one every 0.5..1.2 s.
    private void LookAroundInPlace(SeekBot b, float dt)
    {
        b.glanceTimer -= dt;
        if (b.glanceTimer <= 0f)
        {
            float turn = Random.Range(45f, 115f);
            if (Random.value >= 0.5f)
            {
                turn = -turn;
            }
            b.glanceTargetYaw = b.tf.eulerAngles.y + turn;
            b.glanceTimer = Random.Range(0.5f, 1.2f);
        }
        b.tf.rotation = Quaternion.RotateTowards(b.tf.rotation, Quaternion.Euler(0f, b.glanceTargetYaw, 0f), dt * 150f);
    }

    // Close-range "check": hold near a concealed target and fill sniff01. Camo (eroded by heat)
    // slows it, a clear line of sight speeds it up, and heat speeds it up further. Returns the
    // target only once sniff01 reaches 1; gives up after sniffMaxHold and cools down.
    private Transform ProximitySniff(SeekBot b, float dt, out bool isPlayer, out bool sniffed)
    {
        isPlayer = false;
        sniffed = false;

        if (b.sniffSuppressTimer > 0f)
        {
            b.sniffHold = false;
            b.sniff01 = 0f;
            b.sniffHoldTimer = 0f;
            b.sniffSuppressTimer -= dt;
            return null;
        }

        Transform t = NearestSniffTarget(b, out isPlayer);
        if (t == null)
        {
            b.sniffHold = false;
            b.sniff01 = 0f;
            b.sniffHoldTimer = 0f;
            return null;
        }

        b.sniffHold = true;
        b.sniffHoldTimer += dt;

        Vector3 eye = b.smr != null ? b.smr.bounds.center : b.tf.position + Vector3.up * 8f;
        Vector3 aim = isPlayer ? playerBounds.center : HiderAimPoint(t);
        Vector3 d = aim - eye;
        float dist = d.magnitude;
        bool covered = dist >= 0.01f && !LineClear(eye, d / dist, dist - 1f);

        float heat = _searchHeat;
        float camo = Mathf.Clamp01(b.camo01 * (1f - heat * escalationCamoErode));
        float need = Mathf.Max(0.25f, sniffTime);
        if (isPlayer && camoAffectsVision)
        {
            need /= Mathf.Max(0.05f, Mathf.Lerp(1f, sniffCamoMul, camo));
        }
        if (!covered)
        {
            need /= Mathf.Max(1f, sniffExposedSpeedup);
        }

        b.sniff01 += dt / (need * Mathf.Lerp(1f, escalationSniffSpeedup, heat));
        if (b.sniff01 >= 1f)
        {
            sniffed = true;
            b.sniff01 = 1f;
            return t;
        }

        if (b.sniffHoldTimer >= sniffMaxHold)
        {
            b.sniff01 = 0f;
            b.sniffHoldTimer = 0f;
            b.sniffHold = false;
            b.sniffSuppressTimer = sniffCooldown;
            if (b.targetIdx >= 0)
            {
                MarkChecked(b.targetIdx);
                b.corners = null;
                b.targetIdx = -1;
            }
        }
        return null;
    }

    // Nearest target (player or live hider bot) within the heat-boosted sniff radius and below
    // sniffMaxHeight. Reachability is re-evaluated every 0.4 s. Dead/inactive targets are pruned.
    private Transform NearestSniffTarget(SeekBot b, out bool isPlayer)
    {
        isPlayer = false;
        float best = Mathf.Max(0.5f, sniffRadius * (_searchHeat * escalationRangeBoost + 1f));
        Transform bestT = null;

        if (chasePlayer && playerTf != null && playerBounds.center.y - floorY <= sniffMaxHeight)
        {
            float d = HorizDist(b.tf.position, playerBounds.center);
            if (d < best)
            {
                bestT = playerTf;
                isPlayer = true;
                best = d;
            }
        }

        for (int i = targets.Count - 1; i >= 0; i--)
        {
            Transform t = targets[i];
            if (t == null || !t.gameObject.activeInHierarchy)
            {
                targets.RemoveAt(i);
                continue;
            }
            if (TargetIsCorpse(t)) continue;
            if (t.position.y - floorY > sniffMaxHeight) continue;

            float d = HorizDist(b.tf.position, t.position);
            if (d < best)
            {
                isPlayer = false;
                bestT = t;
                best = d;
            }
        }

        if (bestT == null) return null;

        Vector3 center = isPlayer ? playerBounds.center : bestT.position;
        b.sniffPathTimer -= Time.deltaTime;
        bool ok;
        if (b.sniffPathTimer <= 0f)
        {
            ok = SniffBarrierOK(b, center);
            b.sniffReachable = ok;
            b.sniffPathTimer = 0.4f;
        }
        else
        {
            ok = b.sniffReachable;
        }
        return ok ? bestT : null;
    }

    // Reachable on foot within sniffRadius * 2.2 of path length.
    private bool SniffReachable(Vector3 from, Vector3 target)
    {
        Vector3[] c = ComputePath(from, target);
        if (c == null || c.Length == 0) return false;
        if (HorizDist(c[c.Length - 1], target) > arriveDistance * 2f) return false;

        float len = 0f;
        for (int i = 1; i < c.Length; i++)
        {
            len += Vector3.Distance(c[i - 1], c[i]);
        }
        return len <= sniffRadius * 2.2f;
    }

    // Reachable on foot, or (sniffThroughFurniture) only furniture - nothing that blocks shots -
    // between the bot's eye and the target.
    private bool SniffBarrierOK(SeekBot b, Vector3 targetCenter)
    {
        if (SniffReachable(b.tf.position, targetCenter)) return true;
        if (!sniffThroughFurniture) return false;

        Vector3 eye = b.smr != null ? b.smr.bounds.center : b.tf.position + Vector3.up * 8f;
        Vector3 d = targetCenter - eye;
        float dist = d.magnitude;
        if (dist < 0.5f) return true;

        int n = Physics.RaycastNonAlloc(eye, d / dist, _rayHits, dist);
        for (int i = 0; i < n; i++)
        {
            Collider col = _rayHits[i].collider;
            if (col is CharacterController) continue;
            if (IsCharacter(col)) continue;
            if (Bullet.BlocksShot(col.gameObject)) return false;
        }
        return true;
    }

    // ====================== SPOT SEQUENCE (Play Now) ======================

    // Freezes the player and swings the camera to the spotting bot; UpdateSpot fires after
    // spotShootDelay.
    private void BeginSpot(SeekBot b)
    {
        _spotting = true;
        _spotBot = b;
        _spotTimer = 0f;
        _spotFired = false;

        if (gmm != null)
        {
            gmm.PlayerFrozen = true;
        }
        if (camCtrl != null && b != null && b.tf != null && playerTf != null)
        {
            camCtrl.SetSpotView(b.tf, playerTf);
        }
        SetStatusVisible(false);

        string who = (b != null && b.tf != null) ? b.tf.name : "?";
        Debug.Log("[BotSeek] Player SPOTTED by " + who + " — shot in " + spotShootDelay.ToString() + "s");
    }

    // The spotting bot turns to face the player and aims (this no longer returns early - the look
    // update falls through to the firing logic, as in raw). It fires once after spotShootDelay;
    // if the round is still running 1.5 s after that (the shot missed), the player loses anyway.
    private void UpdateSpot()
    {
        _spotTimer += Time.deltaTime;

        if (_spotBot != null && _spotBot.tf != null && playerTf != null)
        {
            if (_spotBot.anim != null)
            {
                _spotBot.anim.SetFloat(SpeedHash, 0f);
            }

            Vector3 pp = playerTf.position;
            Vector3 bp = _spotBot.tf.position;
            Vector3 flat = new Vector3(pp.x - bp.x, 0f, pp.z - bp.z);
            if (flat.sqrMagnitude > 0.01f)
            {
                Transform t = _spotBot.tf;
                t.rotation = Quaternion.Slerp(t.rotation, Quaternion.LookRotation(flat.normalized), Time.deltaTime * 8f);
            }

            if (camCtrl != null)
            {
                camCtrl.SetSpotView(_spotBot.tf, playerTf);
            }

            if (_spotBot.look != null)
            {
                _spotBot.look.LookAt(playerBounds.center);
                _spotBot.look.AimAt(playerBounds.center);
            }
        }

        if (!_spotFired)
        {
            if (_spotTimer >= Mathf.Max(0.1f, spotShootDelay))
            {
                _spotFired = true;
                if (_spotBot != null)
                {
                    _spotBot.fireTimer = 0f;
                    FireAtTarget(_spotBot, playerTf, true);
                }
            }
        }
        else if (state == State.Searching && _spotTimer >= spotShootDelay + 1.5f)
        {
            Lose(_spotBot);
        }
    }

    private void ClearSpot()
    {
        _spotting = false;
        _spotBot = null;
        _spotTimer = 0f;
        _spotFired = false;
        if (gmm != null)
        {
            gmm.PlayerFrozen = false;
        }
        if (camCtrl != null)
        {
            camCtrl.ClearSpotView();
        }
    }

    private static float HorizDist(Vector3 a, Vector3 c)
    {
        float dx = a.x - c.x;
        float dz = a.z - c.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // Seeker mode: the nearest live hider bot in the vision cone (or within catchRange) with a
    // clear line of sight. Exposed-high hiders get highVisionRange. Missing, inactive and dead
    // hiders are pruned from the list.
    private Transform NearestVisibleHiderBot(SeekBot b)
    {
        if (targets.Count == 0) return null;
        if (b.tf == null) return null;

        Vector3 eye = b.smr != null ? b.smr.bounds.center : b.tf.position + Vector3.up * 8f;
        Vector3 f = b.tf.forward;
        Vector3 flatFwd = new Vector3(f.x, 0f, f.z);
        Vector3 fwd = flatFwd.sqrMagnitude >= 0.001f ? flatFwd.normalized : Vector3.forward;
        float cosHalf = Mathf.Cos(visionAngle * 0.5f * Mathf.Deg2Rad);

        float best = float.MaxValue;
        Transform bestT = null;
        for (int i = targets.Count - 1; i >= 0; i--)
        {
            Transform t = targets[i];
            if (t == null || !t.gameObject.activeInHierarchy || TargetIsCorpse(t))
            {
                targets.RemoveAt(i);
                continue;
            }

            Vector3 c = t.position + Vector3.up * 6f;
            float range = visionRange;
            if (highExposureBoost && c.y - floorY > exposedHeightAboveFloor)
            {
                range = Mathf.Max(range, highVisionRange);
            }

            Vector3 d = c - eye;
            float dist = d.magnitude;
            if (!(dist <= range && dist < best)) continue;

            Vector3 flat = new Vector3(d.x, 0f, d.z);
            bool inCone = flat.sqrMagnitude > 0.0001f && Vector3.Dot(fwd, flat.normalized) >= cosHalf;
            if (!inCone && dist > catchRange) continue;

            if (LineClear(eye, d / Mathf.Max(dist, 0.001f), dist - 1.5f))
            {
                bestT = t;
                best = dist;
            }
        }
        return bestT;
    }

    // A hider bot was hit: remove it from the target list, ragdoll it (or hide it if it has no
    // ragdoll) and hide its name tag. A null seeker throws.
    private void FoundHider(SeekBot b, Transform hider)
    {
        if (hider != null)
        {
            targets.Remove(hider);

            Vector3 dir = (b != null && b.tf != null) ? b.tf.forward : hider.forward;
            Vector3 hp = hider.position;
            SkinnedMeshRenderer smr = hider.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Vector3 hitPoint = smr != null ? smr.bounds.center : hp + Vector3.up * 6f;

            if (Ragdoll.Trigger(hider, hitPoint, dir, 1f, true) == null)
            {
                hider.gameObject.SetActive(false);
            }
            if (botMgr != null)
            {
                botMgr.SetTagVisible(hider, false);
            }
        }

        b.corners = null;
        b.alerted = false;
        b.sightTimer = 0f;
        b.lostTimer = 0f;
        b.targetIdx = -1;

        string seeker = b.tf != null ? b.tf.name : "seeker";
        string found = hider != null ? hider.name : "?";
        Debug.Log("[BotSeek] " + seeker + " found " + found + ".");
    }

    // ====================== SHOOTING ======================

    private void FireAtTarget(SeekBot b, Transform target, bool targetIsPlayer)
    {
        if (b == null) return;
        if (b.tf == null) return;
        if (target == null) return;
        if (b.fireTimer > 0f) return;

        b.fireTimer = Mathf.Max(0.05f, fireInterval);
        if (b.anim != null)
        {
            b.anim.SetTrigger(GunFireHash);
        }
        AudioManager.WaterGunShot();

        Vector3 aim = targetIsPlayer ? playerBounds.center : HiderAimPoint(target);
        Vector3 muzzle = SeekerMuzzle(b, aim);
        Vector3 d = aim - muzzle;
        if (d.sqrMagnitude < 0.0001f)
        {
            d = b.tf.forward;
        }
        SpawnSeekerBullet(b, muzzle, d.normalized, target, targetIsPlayer);
    }

    private Vector3 HiderAimPoint(Transform t)
    {
        SkinnedMeshRenderer smr = t.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr != null) return smr.bounds.center;
        CharacterController cc = t.GetComponent<CharacterController>();
        if (cc != null) return cc.bounds.center;
        return t.position + Vector3.up * 4f;
    }

    // The gun's position if it's active and has a clear line to the aim point; otherwise the eye.
    private Vector3 SeekerMuzzle(SeekBot b, Vector3 aim)
    {
        Vector3 eye = b.smr != null ? b.smr.bounds.center : b.tf.position + Vector3.up * 8f;
        if (b.gunTf != null && b.gunTf.gameObject.activeInHierarchy)
        {
            Vector3 g = b.gunTf.position;
            Vector3 d = aim - g;
            float dist = d.magnitude;
            if (dist > 0.5f && LineClear(g, d / dist, dist - 1.5f))
            {
                return g;
            }
        }
        return eye;
    }

    // Spawns the bullet prefab (or a glowing sphere if there is none). Only a HIT decides the
    // outcome: hitting the player (or a child of it) loses the round; hitting the targeted live
    // hider bot finds it. Hits are ignored unless the round is still in the Searching state.
    private void SpawnSeekerBullet(SeekBot shooter, Vector3 origin, Vector3 dir, Transform target, bool targetIsPlayer)
    {
        Quaternion rot = Quaternion.LookRotation(dir);
        GameObject go;
        Bullet bullet;

        if (bulletPrefab != null)
        {
            go = Object.Instantiate(bulletPrefab, origin, rot);
            go.name = "BotBullet";
            bullet = go.GetComponent<Bullet>();
            if (bullet == null)
            {
                bullet = go.AddComponent<Bullet>();
            }
            if (bullet.speed <= 0f) bullet.speed = bulletSpeed;
            if (bullet.maxLife <= 0f) bullet.maxLife = bulletLife;
            if (bullet.radius <= 0f) bullet.radius = Mathf.Max(0.01f, bulletSize * 0.5f);
            if (go.GetComponent<TrailRenderer>() == null)
            {
                AddTracer(go);
            }
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "BotBullet";
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.localScale = Vector3.one * bulletSize;
            go.transform.SetPositionAndRotation(origin, rot);

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            Material m = mr.material;
            m.color = bulletColor;
            if (m.HasProperty("_BaseColor"))
            {
                m.SetColor("_BaseColor", bulletColor);
            }
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", bulletColor * 2.2f);
            }
            AddTracer(go);

            bullet = go.AddComponent<Bullet>();
            bullet.speed = bulletSpeed;
            bullet.maxLife = bulletLife;
            bullet.radius = bulletSize * 0.5f;
        }

        // <>c__DisplayClass169_0.<SpawnSeekerBullet>b__0
        bullet.InitShot(shooter.tf, hitGo =>
        {
            if (state != State.Searching) return;
            if (hitGo == null) return;
            Transform ht = hitGo.transform;

            if (targetIsPlayer)
            {
                if (playerTf == null) return;
                if (ht != playerTf && !ht.IsChildOf(playerTf)) return;
                Lose(shooter);
            }
            else
            {
                if (target == null) return;
                if (!target.gameObject.activeSelf) return;
                if (TargetIsCorpse(target)) return;
                if (ht != target && !ht.IsChildOf(target)) return;
                FoundHider(shooter, target);
            }
        });
    }

    // Short fading trail in bulletColor. Material: Resources "BulletTrail", else the bullet mesh's
    // own shared material, else left unset.
    private void AddTracer(GameObject go)
    {
        TrailRenderer tr = go.AddComponent<TrailRenderer>();
        tr.time = 0.25f;
        tr.startWidth = Mathf.Max(0.1f, go.transform.localScale.x * 0.6f);
        tr.endWidth = 0f;
        tr.numCapVertices = 2;

        Material mat = Resources.Load<Material>("BulletTrail");
        if (mat != null)
        {
            tr.sharedMaterial = mat;
        }
        else
        {
            MeshRenderer mr = go.GetComponentInChildren<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                tr.sharedMaterial = mr.sharedMaterial;
            }
        }

        tr.startColor = bulletColor;
        tr.endColor = new Color(bulletColor.r, bulletColor.g, bulletColor.b, 0f);
    }

    // Seeker mode: the hider bots to hunt.
    public void SetTargets(IEnumerable<Transform> ts)
    {
        targets.Clear();
        targetRagdoll.Clear();
        if (ts == null) return;
        foreach (Transform t in ts)
        {
            if (t != null)
            {
                targets.Add(t);
            }
        }
    }

    // True if the target's (cached) Ragdoll is currently ragdolling, i.e. it's already been shot.
    private bool TargetIsCorpse(Transform t)
    {
        if (t == null) return false;
        if (!targetRagdoll.TryGetValue(t, out Ragdoll ragdoll) || ragdoll == null)
        {
            ragdoll = t.GetComponent<Ragdoll>();
            targetRagdoll[t] = ragdoll;
            if (ragdoll == null) return false;
        }
        return ragdoll.IsRagdolling;
    }

    // Gives each seeker a BotLook and the "Gun" animator layer, and a clone of the scene's gun
    // (named "BotGun") in its hand bone, placed with the GunHolder's offsets.
    private void AttachGunsToSeekers()
    {
        GameObject gunSrc = FindInScene(gunObjectName);
        // GunHolder holder = Object.FindFirstObjectByType<GunHolder>();
        // Vector3 localPos = holder != null ? holder.localPosition : Vector3.zero;
        // Vector3 localEuler = holder != null ? holder.localEuler : Vector3.zero;
        // Vector3 localScale = holder != null ? holder.localScale : Vector3.one;

        Vector3 localPos = gunSrc.transform.localPosition;
        Vector3 localEuler = gunSrc.transform.localEulerAngles;
        Vector3 localScale = gunSrc.transform.localScale;

        for (int i = 0; i < bots.Count; i++)
        {
            SeekBot b = bots[i];
            if (b == null) continue;
            if (b.tf == null) continue;

            if (b.look == null)
            {
                b.look = b.tf.GetComponent<BotLook>();
                if (b.look == null)
                {
                    b.look = b.tf.gameObject.AddComponent<BotLook>();
                }
            }

            SetGunPose(b.anim, true);
            if (gunSrc == null) continue;

            Transform hand = null;
            Transform[] all = b.tf.GetComponentsInChildren<Transform>(true);
            for (int j = 0; j < all.Length; j++)
            {
                if (all[j].name == handBoneName)
                {
                    hand = all[j];
                    break;
                }
            }
            if (hand == null) continue;

            Transform existing = hand.Find("BotGun");
            if (existing != null)
            {
                b.gunTf = existing;
            }
            else
            {
                GameObject gun = Object.Instantiate(gunSrc, hand, false);
                gun.name = "BotGun";
                gun.transform.localPosition = localPos;
                gun.transform.localEulerAngles = localEuler;
                gun.transform.localScale = localScale;
                gun.SetActive(true);
                b.gunTf = gun.transform;
            }
        }
    }

    private static void SetGunPose(Animator anim, bool on)
    {
        if (anim == null) return;
        for (int i = 0; i < anim.layerCount; i++)
        {
            if (anim.GetLayerName(i) == "Gun")
            {
                anim.SetLayerWeight(i, on ? 1f : 0f);
                return;
            }
        }
    }

    private GameObject FindInScene(string n)
    {
        GameObject go = GameObject.Find(n);
        if (go != null) return go;

        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t.name == n && t.gameObject.scene.IsValid())
            {
                return t.gameObject;
            }
        }
        return null;
    }

    // ====================== VISION ======================

    // Samples the player's centre, upper body (+0.6 × half-height) and lower body (-0.5 ×
    // half-height). Range shrinks with (heat-eroded) camo and grows with heat; an exposed-high
    // player gets highVisionRange. A point counts if it's in the cone OR within the camo-shrunk
    // catch range, and the line to it is clear.
    private bool CanSee(SeekBot b)
    {
        if (!chasePlayer) return false;
        if (b.tf == null) return false;

        Vector3 eye = b.smr != null ? b.smr.bounds.center : b.tf.position + Vector3.up * 8f;
        Vector3 pc = playerBounds.center;

        float camo = 0f;
        if (camoAffectsVision && paint != null)
        {
            b.camoTimer -= Time.deltaTime;
            if (Vector3.Distance(eye, pc) <= visionRange && b.camoTimer <= 0f)
            {
                b.camo01 = ComputeCamo01(eye);
                b.camoTimer = camoRefresh;
            }
            camo = b.camo01;
        }

        float heat = _searchHeat;
        float c = Mathf.Clamp01(camo * (1f - heat * escalationCamoErode));
        float range = (heat * escalationRangeBoost + 1f) * Mathf.Lerp(visionRange, minVisionRange, c);
        if (highExposureBoost && pc.y - floorY > exposedHeightAboveFloor)
        {
            range = Mathf.Max(range, highVisionRange);
        }
        float reach = Mathf.Lerp(catchRange, minCatchRange, c);

        Vector3[] points = new Vector3[]
        {
            pc,
            pc + Vector3.up * (playerBounds.extents.y * 0.6f),
            pc - Vector3.up * (playerBounds.extents.y * 0.5f)
        };

        Vector3 f = b.tf.forward;
        Vector3 flatFwd = new Vector3(f.x, 0f, f.z);
        Vector3 fwd = flatFwd.sqrMagnitude >= 0.001f ? flatFwd.normalized : Vector3.forward;
        float cosHalf = Mathf.Cos(visionAngle * 0.5f * Mathf.Deg2Rad);

        for (int i = 0; i < points.Length; i++)
        {
            Vector3 d = points[i] - eye;
            float dist = d.magnitude;
            if (dist > range) continue;

            Vector3 dir = d / Mathf.Max(dist, 0.001f);
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            bool inCone = flat.sqrMagnitude > 0.0001f && Vector3.Dot(fwd, flat.normalized) >= cosHalf;
            if ((dist <= reach || inCone) && LineClear(eye, dir, dist - 1f))
            {
                return true;
            }
        }
        return false;
    }

    // True unless something other than a character blocks the ray.
    private bool LineClear(Vector3 origin, Vector3 dir, float dist)
    {
        if (dist <= 0f) return true;
        int n = Physics.RaycastNonAlloc(origin, dir, _rayHits, dist);
        for (int i = 0; i < n; i++)
        {
            Collider col = _rayHits[i].collider;
            if (col is CharacterController) continue;
            if (!IsCharacter(col)) return false;
        }
        return true;
    }

    // Anything under an Animator counts as a character (cached per collider).
    private bool IsCharacter(Collider col)
    {
        if (col == null) return false;
        if (!_isCharacterCache.TryGetValue(col, out bool isChar))
        {
            isChar = col.GetComponentInParent<Animator>() != null;
            _isCharacterCache[col] = isChar;
        }
        return isChar;
    }

    // ====================== CAMOUFLAGE ======================

    // How well the player's average paint matches the surface behind them from this eye:
    // smoothstep(1 - colourDistance / tolerance) × camoStrength, 0..1. Gray = "no surface found".
    private float ComputeCamo01(Vector3 eye)
    {
        Color bg = ColorBehindPlayer(eye);
        if (bg == Color.gray) return 0f;

        float d = ColorDist(playerAvgColor, bg);
        float t = 1f - Mathf.Clamp01(d / Mathf.Max(0.05f, camoMatchTolerance));
        return Mathf.Clamp01(camoStrength * t * t * (3f - 2f * t));
    }

    // Colour of the first non-character surface behind the player along the eye->player line.
    private Color ColorBehindPlayer(Vector3 eye)
    {
        Vector3 d = playerBounds.center - eye;
        if (d.sqrMagnitude < 0.01f) return Color.gray;

        Vector3 dir = d.normalized;
        float back = playerBounds.extents.magnitude + 1.5f;
        Vector3 origin = playerBounds.center + dir * back;
        int n = Physics.RaycastNonAlloc(origin, dir, _rayHits, visionRange);

        bool found = false;
        float best = float.MaxValue;
        RaycastHit hit = default(RaycastHit);
        for (int i = 0; i < n; i++)
        {
            Collider col = _rayHits[i].collider;
            if (col is CharacterController) continue;
            if (IsCharacter(col)) continue;
            if (_rayHits[i].distance < best)
            {
                best = _rayHits[i].distance;
                hit = _rayHits[i];
                found = true;
            }
        }
        if (!found) return Color.gray;

        Renderer r = hit.collider.GetComponent<Renderer>();
        if (r == null)
        {
            r = hit.collider.GetComponentInParent<Renderer>();
        }
        Vector2 uv = hit.collider is MeshCollider ? hit.textureCoord : Vector2.zero;
        if (uv == Vector2.zero)
        {
            uv = MeshUvCenter(r);
        }
        Material m = r != null ? r.sharedMaterial : null;
        return SampleTexAtUV(m, uv);
    }

    private static float ColorDist(Color a, Color b)
    {
        float dr = a.r - b.r;
        float dg = a.g - b.g;
        float db = a.b - b.b;
        return Mathf.Sqrt(dr * dr + dg * dg + db * db);
    }

    // Centre of the mesh's UV bounding box (0.5, 0.5 when unknown / unreadable).
    private Vector2 MeshUvCenter(Renderer obj)
    {
        if (obj == null) return new Vector2(0.5f, 0.5f);
        MeshFilter mf = obj.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) return new Vector2(0.5f, 0.5f);

        Vector2[] uv = mf.sharedMesh.uv;
        if (uv == null || uv.Length == 0) return new Vector2(0.5f, 0.5f);

        Vector2 min = uv[0];
        Vector2 max = uv[0];
        for (int i = 1; i < uv.Length; i++)
        {
            min = Vector2.Min(min, uv[i]);
            max = Vector2.Max(max, uv[i]);
        }
        return (min + max) * 0.5f;
    }

    // Average colour of a 6x6 texel block at uv, tinted by _BaseColor, cached per material. The
    // texture (_BaseMap, then _MainTex, then mainTexture) is blitted to a temporary ARGB32 sRGB
    // render texture (max 1024²) so non-readable textures work. No texture = the tint, alpha 1.
    private Color SampleTexAtUV(Material m, Vector2 uv)
    {
        if (m != null && _bgColorCache.TryGetValue(m, out Color cached))
        {
            return cached;
        }

        Color tint = Color.white;
        Texture tex = null;
        if (m != null)
        {
            if (m.HasProperty("_BaseColor"))
            {
                tint = m.GetColor("_BaseColor");
            }
            if (m.HasProperty("_BaseMap"))
            {
                tex = m.GetTexture("_BaseMap");
            }
            if (tex == null && m.HasProperty("_MainTex"))
            {
                tex = m.GetTexture("_MainTex");
            }
            if (tex == null)
            {
                tex = m.mainTexture;
            }
        }

        Color result;
        if (tex == null)
        {
            result = new Color(tint.r, tint.g, tint.b, 1f);
            if (m == null) return result;
        }
        else
        {
            int w = Mathf.Min(tex.width, 1024);
            int h = Mathf.Min(tex.height, 1024);

            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;

            float u = Mathf.Clamp01(uv.x - (int)uv.x);
            int px = Mathf.Clamp(Mathf.RoundToInt(u * (w - 1)) - 3, 0, Mathf.Max(0, w - 6));
            float v = Mathf.Clamp01(uv.y - (int)uv.y);
            int py = Mathf.Clamp(Mathf.RoundToInt(v * (h - 1)) - 3, 0, Mathf.Max(0, h - 6));

            if (_camoBlock == null)
            {
                _camoBlock = new Texture2D(6, 6, TextureFormat.RGBA32, false);
            }
            _camoBlock.ReadPixels(new Rect(px, py, 6f, 6f), 0, 0, false);
            _camoBlock.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            Color[] pix = _camoBlock.GetPixels();
            float sr = 0f, sg = 0f, sb = 0f;
            for (int i = 0; i < pix.Length; i++)
            {
                sr += pix[i].r;
                sg += pix[i].g;
                sb += pix[i].b;
            }
            float count = pix.Length;
            result = new Color(tint.r * (sr / count), tint.g * (sg / count), tint.b * (sb / count), tint.a);
            if (m == null) return result;
        }

        _bgColorCache[m] = result;
        return result;
    }

    // ====================== ROUND END ======================

    // The player was caught. Play Now: ground + ragdoll the player, then the Caught popup after
    // caughtPopupDelay. Otherwise just a result text.
    private void Lose(SeekBot finder)
    {
        state = State.Over;
        StopAllBots();

        string who = (finder != null && finder.tf != null) ? finder.tf.name : "a bot";
        Debug.Log("[BotSeek] Player was FOUND by " + who + "!");
        if (statusText != null)
        {
            statusText.text = "";
        }

        if (chasePlayer && resultUI != null)
        {
            GroundPlayer();
            RagdollPlayer(finder);
            _caughtTimeLeft = (int)Mathf.Max(0f, timeLeft);
            SetStatusVisible(false);
            if (_caughtRoutine != null)
            {
                StopCoroutine(_caughtRoutine);
            }
            _caughtRoutine = StartCoroutine(ShowCaughtDelayed());
            return;
        }

        if (resultText != null)
        {
            resultText.text = "SPOTTED!\nFound by " + who + ".";
        }
    }

    // Decompiled from <ShowCaughtDelayed>d__185.MoveNext.
    private IEnumerator ShowCaughtDelayed()
    {
        yield return new WaitForSeconds(Mathf.Max(0f, caughtPopupDelay));
        _caughtRoutine = null;
        if (state == State.Over && chasePlayer && resultUI != null)
        {
            resultUI.ShowCaught(RevivePlayer, ShowDefeatFromCaught);
        }
    }

    // "Revive" on the Caught popup: rewarded ad, then the round restarts from the grace phase.
    // A null AdMgr throws.
    private void RevivePlayer()
    {
        // if (!AdMgr.Instance.IsRewardReady) return;

        // // <RevivePlayer>b__187_0
        // AdMgr.Instance.OnRewardView(() =>
        // {
            // hideUI.HideAll();
            // RestorePlayerRagdoll();
            // GroundPlayer();
            // ResetBots();
            // _lastGraceSec = -1;
            // state = State.Grace;
            // timeLeft = roundTime;
            // graceLeft = graceTime;
            // SetStatusVisible(chasePlayer);
            // if (resultText != null)
            // {
            //     resultText.text = "";
            // }
            // RootManager.Instance?.SetNumber(1);
            // Debug.Log("[BotSeek] Player revived — hide again!");
        // }, RewardFail);
    }

    private void RewardFail()
    {
    }

    private void GroundPlayer()
    {
        WallClimber climber = Object.FindFirstObjectByType<WallClimber>();
        if (climber != null)
        {
            climber.DropToGround();
            return;
        }
        if (playerTf != null)
        {
            Grounding.ToFloor(playerTf, playerRends);
        }
    }

    // Ragdolls the player, pushed away from the finder (or backwards if there's no finder).
    private void RagdollPlayer(SeekBot finder)
    {
        if (playerTf == null) return;

        if (playerRagdoll == null)
        {
            playerRagdoll = playerTf.GetComponent<Ragdoll>();
            if (playerRagdoll == null)
            {
                playerRagdoll = playerTf.gameObject.AddComponent<Ragdoll>();
            }
        }

        Vector3 push;
        if (finder != null && finder.tf != null)
        {
            Vector3 d = playerTf.position - finder.tf.position;
            push = new Vector3(d.x, 0f, d.z);
        }
        else
        {
            Vector3 f = playerTf.forward;
            push = new Vector3(-f.x, 0f, -f.z);
        }
        playerRagdoll.Activate(playerBounds.center, push, 1f, false);
    }

    private void RestorePlayerRagdoll()
    {
        if (playerRagdoll == null && playerTf != null)
        {
            playerRagdoll = playerTf.GetComponent<Ragdoll>();
        }
        if (playerRagdoll == null) return;
        playerRagdoll.Teardown();
        playerRagdoll = null;
    }

    private void ShowDefeatFromCaught()
    {
        if (resultUI != null)
        {
            resultUI.ShowDefeat("#5/8", _caughtTimeLeft, 0);
        }
    }

    private void Win()
    {
        state = State.Over;
        StopAllBots();
        if (statusText != null)
        {
            statusText.text = "";
        }

        if (chasePlayer && resultUI != null)
        {
            SetStatusVisible(false);
            resultUI.ShowVictory("#1/8", Mathf.RoundToInt(roundTime), 100);
            return;
        }

        if (resultText != null)
        {
            resultText.text = "YOU STAYED HIDDEN!\nYou win.";
        }
    }

    private void StopAllBots()
    {
        for (int i = 0; i < bots.Count; i++)
        {
            SeekBot b = bots[i];
            if (b.anim != null)
            {
                b.anim.SetFloat(SpeedHash, 0f);
            }
            if (b.look != null)
            {
                b.look.LookForward();
            }
        }
    }

    // ====================== BOTS ======================

    private void ResolveBots()
    {
        bots.Clear();
        for (int i = 0; i < botNames.Length; i++)
        {
            GameObject go = GameObject.Find(botNames[i]);
            if (go == null) continue;

            SkinnedMeshRenderer smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Color originColor = Color.white;
            if (smr != null && smr.sharedMaterial != null && smr.sharedMaterial.HasProperty(BaseColorId))
            {
                originColor = smr.sharedMaterial.GetColor(BaseColorId);
            }

            Ragdoll ragdoll = go.GetComponent<Ragdoll>();
            SeekBot b = new SeekBot();
            b.tf = go.transform;
            b.anim = go.GetComponentInChildren<Animator>(true);
            b.smr = smr;
            b.cc = go.GetComponent<CharacterController>();
            b.ragdoll = ragdoll;
            b.originPos = go.transform.position;
            b.originRot = go.transform.rotation;
            b.originColor = originColor;
            bots.Add(b);
        }
    }

    // Puts every bot at a valid home point with its original rotation and colour, fixes its step
    // height for its scale, and resets all per-bot state. Activation is staggered 0.12 s per bot
    // and camo sampling is spread across bots.
    public void ResetBots()
    {
        ClearSpot();

        int seed = 0;
        foreach (SeekBot b in bots)
        {
            if (b != null && b.tf != null)
            {
                if (b.ragdoll != null)
                {
                    b.ragdoll.Deactivate();
                }

                Vector3 home = ValidRoomHome(b.originPos, seed);
                if (b.cc != null) b.cc.enabled = false;
                b.tf.SetPositionAndRotation(home, b.originRot);
                if (b.cc != null) b.cc.enabled = true;

                if (b.cc != null)
                {
                    float s = Mathf.Max(0.01f, b.tf.lossyScale.y);
                    b.cc.stepOffset = Mathf.Clamp(botClimbStepHeight / s, 0.1f, s * b.cc.height - 0.5f);
                }

                if (b.anim != null)
                {
                    b.anim.SetFloat(SpeedHash, 0f);
                    b.anim.SetInteger(PoseHash, 0);
                }
                if (b.smr != null)
                {
                    b.smr.material.SetColor(BaseColorId, b.originColor);
                }

                b.corners = null;
                b.ci = 0;
                b.targetIdx = -1;
                b.alerted = false;
                b.sidestepDir = 0;
                b.repathTimer = 0f;
                b.scanTimer = 0f;
                b.sightTimer = 0f;
                b.lostTimer = 0f;
                b.stuckTimer = 0f;
                b.sidestepTimer = 0f;
                b.noProgressTimer = 0f;
                b.bestDist = float.MaxValue;
                b.targetTimer = 0f;
                b.targetBudget = 8f;
                b.vy = 0f;
                b.progressTimer = 0f;
                b.fireTimer = 0f;
                b.progressAnchor = b.tf.position;
                b.camo01 = 0f;
                b.investigating = false;
                b.investigateTimer = 0f;
                b.camoTimer = camoRefresh / Mathf.Max(1, bots.Count) * seed;
                b.activateTimer = seed * 0.12f;
                b.seenHasVel = false;
                b.backupTimer = 0f;
                b.seenVel = Vector3.zero;
                b.idleAnchor = b.tf.position;
                b.glanceTimer = 0f;
                b.poiTimer = 0f;
                b.idleTimer = 0f;
                b.sniff01 = 0f;
                b.sniffHoldTimer = 0f;
                b.sniffSuppressTimer = 0f;
                b.sniffPathTimer = 0f;
                b.sniffHold = false;
                b.sniffReachable = false;
                b.poiPoint = b.tf.position;
                if (b.look != null)
                {
                    b.look.LookForward();
                }
            }
            seed++;
        }
    }

    // Combined bounds of all player renderers, or a 4-unit box at the player (or origin).
    private Bounds PlayerBounds()
    {
        if (playerRends != null && playerRends.Length != 0)
        {
            Bounds bb = playerRends[0].bounds;
            for (int i = 1; i < playerRends.Length; i++)
            {
                bb.Encapsulate(playerRends[i].bounds);
            }
            return bb;
        }
        Vector3 p = playerTf != null ? playerTf.position : Vector3.zero;
        return new Bounds(p, Vector3.one * 4f);
    }

    private void ScalePlayerForMode2()
    {
        if (_playerScaled) return;
        if (playerTf == null || playerScaleInMode2 >= 0.999f) return;

        _playerOrigScale = playerTf.localScale;
        CharacterController cc = playerTf.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        playerTf.localScale = Vector3.one * playerScaleInMode2;
        if (cc != null) cc.enabled = true;
        Grounding.ToFloor(playerTf, playerRends);
        _playerScaled = true;
    }

    private void RestorePlayer()
    {
        if (!_playerScaled) return;
        if (playerTf == null) return;

        CharacterController cc = playerTf.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        playerTf.localScale = _playerOrigScale;
        if (cc != null) cc.enabled = true;
        Grounding.ToFloor(playerTf, playerRends);
        _playerScaled = false;
    }

    // ====================== SEARCH POINTS ======================

    // One point per furniture-sized room mesh (2..22 wide, at least 3 tall, not a wall/floor/etc.
    // by name), snapped to the floor NavMesh; then the floor grid; then a 25-unit ring of 6 as a
    // last resort.
    private void BuildSearchPoints()
    {
        searchPoints.Clear();

        GameObject room = GameObject.Find(roomRootName);
        if (room != null)
        {
            string[] skip = new string[] { "Wall", "Floor", "Ceil", "Wallpaper", "Window", "Door", "Rug", "Cards" };
            MeshRenderer[] renderers = room.GetComponentsInChildren<MeshRenderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer mr = renderers[i];
                Bounds bb = mr.bounds;
                float footprint = Mathf.Max(bb.size.x, bb.size.z);
                if (footprint > 22f || bb.size.y < 3f || footprint < 2f) continue;

                bool excluded = false;
                for (int j = 0; j < skip.Length; j++)
                {
                    if (mr.gameObject.name.Contains(skip[j]))
                    {
                        excluded = true;
                        break;
                    }
                }
                if (excluded) continue;

                Vector3 probe = new Vector3(mr.bounds.center.x, floorY, mr.bounds.center.z);
                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, Mathf.Max(footprint, 6f) + 6f, -1)
                    && hit.position.y <= floorY + 3f)
                {
                    searchPoints.Add(hit.position);
                }
            }
        }

        AddFloorGridPoints();

        if (searchPoints.Count == 0)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3 p = transform.position;
                searchPoints.Add(new Vector3(p.x + Mathf.Cos(i) * 25f, floorY, p.z + Mathf.Sin(i) * 25f));
            }
        }
    }

    private static long GridKey(Vector3 p, float cell)
    {
        return (long)(int)(p.x / cell) * 100000L + (int)(p.z / cell);
    }

    // A grid (cell >= 8) over the floor-level NavMesh; one point per cell not already covered.
    private void AddFloorGridPoints()
    {
        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        if (tri.vertices.Length < 1) return;

        float minX = float.MaxValue, maxX = -float.MaxValue;
        float minZ = float.MaxValue, maxZ = -float.MaxValue;
        bool any = false;
        for (int i = 0; i < tri.vertices.Length; i++)
        {
            Vector3 v = tri.vertices[i];
            if (v.y > floorY + 3f) continue;
            any = true;
            minX = Mathf.Min(minX, v.x);
            maxX = Mathf.Max(maxX, v.x);
            minZ = Mathf.Min(minZ, v.z);
            maxZ = Mathf.Max(maxZ, v.z);
        }
        if (!any) return;

        float cell = Mathf.Max(8f, searchGridCell);
        HashSet<long> used = new HashSet<long>();
        for (int i = 0; i < searchPoints.Count; i++)
        {
            used.Add(GridKey(searchPoints[i], cell));
        }

        for (float x = minX; x <= maxX; x += cell)
        {
            for (float z = minZ; z <= maxZ; z += cell)
            {
                if (NavMesh.SamplePosition(new Vector3(x, floorY, z), out NavMeshHit hit, cell * 0.75f, -1)
                    && hit.position.y <= floorY + 3f
                    && used.Add(GridKey(hit.position, cell)))
                {
                    searchPoints.Add(hit.position);
                }
            }
        }
    }

    // Late-game homing (Play Now, past homeInStartFraction, homeInChance): the free point nearest
    // the player. Otherwise the nearest free unchecked point, else the longest-unchecked free
    // point, else any. The chosen point is claimed.
    private int PickSearchIndex(SeekBot b)
    {
        if (searchPoints.Count == 0) return -1;

        if (homeInWhenLost && chasePlayer && playerTf != null)
        {
            float frac = roundTime > 0.01f ? (roundTime - timeLeft) / roundTime : 0f;
            if (frac >= homeInStartFraction && Random.value < homeInChance)
            {
                int near = NearestSearchPointTo(playerBounds.center);
                if (near >= 0)
                {
                    claimed[near] = true;
                    return near;
                }
            }
        }

        int idx = NearestFree(b, true);
        if (idx < 0)
        {
            idx = OldestFree(b);
            if (idx < 0)
            {
                idx = Random.Range(0, searchPoints.Count);
            }
        }
        claimed[idx] = true;
        return idx;
    }

    private int NearestFree(SeekBot b, bool respectChecked)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < searchPoints.Count; i++)
        {
            if (claimed[i]) continue;
            if (respectChecked && Time.time - checkedAt[i] < recheckTime) continue;

            float d = Vector3.Distance(searchPoints[i], b.tf.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    private int OldestFree(SeekBot b)
    {
        int best = -1;
        float oldest = float.MaxValue;
        for (int i = 0; i < searchPoints.Count; i++)
        {
            if (claimed[i]) continue;
            if (checkedAt[i] < oldest)
            {
                oldest = checkedAt[i];
                best = i;
            }
        }
        return best;
    }

    private int NearestSearchPointTo(Vector3 pos)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < searchPoints.Count; i++)
        {
            if (claimed[i]) continue;
            float d = Vector3.Distance(searchPoints[i], pos);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    private void MarkChecked(int idx)
    {
        if (idx < 0) return;
        if (idx < checkedAt.Length)
        {
            checkedAt[idx] = Time.time;
            claimed[idx] = false;
        }
    }

    private void ReleaseClaim(int idx)
    {
        if (idx < 0) return;
        if (idx < claimed.Length)
        {
            claimed[idx] = false;
        }
    }

    // Horizontal push away from other bots within botAvoidRadius, stronger when closer.
    private Vector3 SeparationFrom(SeekBot self)
    {
        Vector3 push = Vector3.zero;
        if (botAvoidRadius <= 0.01f) return push;

        for (int i = 0; i < bots.Count; i++)
        {
            SeekBot other = bots[i];
            if (other == self) continue;
            if (other.tf == null) continue;

            Vector3 away = self.tf.position - other.tf.position;
            away.y = 0f;
            float d = away.magnitude;
            if (d > 0.01f && d < botAvoidRadius)
            {
                push += away / d * (1f - d / botAvoidRadius);
            }
        }
        return push;
    }

    // NavMesh path between the nearest NavMesh points (18-unit search); null if none.
    private Vector3[] ComputePath(Vector3 from, Vector3 to)
    {
        if (NavMesh.SamplePosition(from, out NavMeshHit fromHit, 18f, -1))
        {
            from = fromHit.position;
        }
        if (NavMesh.SamplePosition(to, out NavMeshHit toHit, 18f, -1))
        {
            to = toHit.position;
        }

        NavMeshPath path = new NavMeshPath();
        if (!NavMesh.CalculatePath(from, to, -1, path)) return null;
        if (path.corners == null) return null;
        if (path.corners.Length == 0) return null;
        return path.corners;
    }

    // floorY = the lowest NavMesh point found 12 units below each bot's spawn (30-unit search), or
    // the first bot's spawn (or this object) minus 7 when none is found.
    private void ComputeFloorY()
    {
        float baseY = bots.Count >= 1 ? bots[0].originPos.y : transform.position.y;
        floorY = baseY - 7f;

        bool found = false;
        float lowest = float.MaxValue;
        for (int i = 0; i < bots.Count; i++)
        {
            SeekBot b = bots[i];
            if (b.tf == null) continue;
            Vector3 probe = b.originPos - Vector3.up * 12f;
            if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 30f, -1) && hit.position.y < lowest)
            {
                found = true;
                lowest = hit.position.y;
            }
        }
        if (found)
        {
            floorY = lowest;
        }
    }

    // Adds MeshColliders to readable room meshes that have no collider, then ramps (so raycasts and
    // the NavMesh see the furniture).
    private void EnsureColliders()
    {
        GameObject room = GameObject.Find(roomRootName);
        if (room == null) return;

        MeshRenderer[] renderers = room.GetComponentsInChildren<MeshRenderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            MeshRenderer mr = renderers[i];
            if (mr.GetComponent<Collider>() != null) continue;
            MeshFilter mf = mr.GetComponent<MeshFilter>();
            if (mf == null) continue;
            if (mf.sharedMesh == null) continue;
            if (!mf.sharedMesh.isReadable) continue;
            if (mf.sharedMesh.vertexCount <= 0) continue;
            mr.gameObject.AddComponent<MeshCollider>();
        }
        StairRampBuilder.EnsureRampsForRoom(room);
    }

    // Bakes a runtime NavMesh over the room (agent r 2.5, h 12, climb 4, slope 45; colliders only;
    // bounds expanded by 12) when there is no NavMesh within 25 units of the player.
    private void EnsureNavMesh()
    {
        Transform t = playerTf != null ? playerTf : transform;
        if (NavMesh.SamplePosition(t.position, out NavMeshHit near, 25f, -1)) return;

        GameObject room = GameObject.Find(roomRootName);
        if (room == null) return;

        Bounds bb = new Bounds(room.transform.position, Vector3.one);
        Renderer[] rends = room.GetComponentsInChildren<Renderer>();
        for (int i = 0; i < rends.Length; i++)
        {
            if (i > 0)
            {
                bb.Encapsulate(rends[i].bounds);
            }
            else
            {
                bb = rends[i].bounds;
            }
        }
        bb.Expand(12f);

        NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = 2.5f;
        settings.agentHeight = 12f;
        settings.agentClimb = 4f;
        settings.agentSlope = 45f;

        List<NavMeshBuildMarkup> markups = new List<NavMeshBuildMarkup>();
        List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(room.transform, -1, NavMeshCollectGeometry.PhysicsColliders, 0, markups, sources);

        navData = NavMeshBuilder.BuildNavMeshData(settings, sources, bb, Vector3.zero, Quaternion.identity);
        if (navInstance.valid)
        {
            NavMesh.RemoveNavMeshData(navInstance);
        }
        navInstance = NavMesh.AddNavMeshData(navData);

        Debug.Log("[BotSeek] NavMesh baked (no existing one found): sources=" + sources.Count.ToString() + " valid=" + navInstance.valid.ToString());
    }

    // ====================== STATUS UI ======================

    public void AuthorUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }
        BuildStatusUI();
        SetStatusVisible(false);
    }

    // "SeekStatusCanvas" (sort 11, 1080x1920): the top-centre grace countdown ("SeekStatus") and
    // the centred result text ("SeekResult"). Styling only applies to newly created objects.
    private void BuildStatusUI()
    {
        statusCanvas = SceneUI.GetOrCreateCanvas("SeekStatusCanvas", out bool canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(statusCanvas);
        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 11;
            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(statusCanvas);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(statusCanvas);

        GameObject statusGO = SceneUI.GetOrCreate("SeekStatus", statusCanvas.transform, out bool statusCreated);
        statusText = SceneUI.GetOrAdd<Text>(statusGO);
        statusText.font = font;
        statusText.raycastTarget = false;
        if (statusCreated)
        {
            statusText.text = "";
            statusText.fontSize = 40;
            statusText.alignment = TextAnchor.UpperCenter;
            statusText.color = Color.white;
            statusText.horizontalOverflow = HorizontalWrapMode.Overflow;
            statusText.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform rt = statusText.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -150f);
            rt.sizeDelta = new Vector2(980f, 60f);
        }

        GameObject resultGO = SceneUI.GetOrCreate("SeekResult", statusCanvas.transform, out bool resultCreated);
        resultText = SceneUI.GetOrAdd<Text>(resultGO);
        resultText.font = font;
        resultText.raycastTarget = false;
        if (!resultCreated) return;

        resultText.text = "";
        resultText.fontSize = 72;
        resultText.alignment = TextAnchor.MiddleCenter;
        resultText.color = Color.white;
        resultText.horizontalOverflow = HorizontalWrapMode.Overflow;
        resultText.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform rrt = resultText.rectTransform;
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.anchoredPosition = new Vector2(0f, 120f);
        rrt.sizeDelta = new Vector2(1000f, 320f);
    }

    private void SetStatusVisible(bool v)
    {
        if (statusCanvas != null)
        {
            statusCanvas.SetActive(v);
        }
    }
}
