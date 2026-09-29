using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;

using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

public class BotSeekController : MonoBehaviour
{
    // ====================== NESTED TYPES ======================

    [Serializable]
    public class SeekBot
    {
        // Identity / components (mirrors BotHideController.Bot's layout)
        public Transform tf;
        public Animator anim;
        public SkinnedMeshRenderer smr;
        public CharacterController cc;
        public Ragdoll ragdoll;
        public Vector3 originPos;
        public Quaternion originRot;
        public Color originColor;

        // Weapon
        public Transform gunTf;
        public BotLook look;

        // Alert / detection state
        public bool alerted;
        public float sightTimer;
        public float lostTimer;
        public Vector3 lastSeen;
        public Vector3 seenVel;
        public bool seenHasVel;

        // Investigate (heard/lost-track) state
        public bool investigating;
        public Vector3 investigatePoint;
        public float investigateTimer;

        // Search-point patrol state
        public int targetIdx = -1;
        public Vector3[] corners;
        public int ci;
        public float repathTimer;
        public float progressTimer;
        public Vector3 progressAnchor;
        public float noProgressTimer;
        public float bestDist;
        public float targetTimer;
        public float targetBudget;

        // Movement / navigation helpers
        public float stuckTimer;
        public float sidestepTimer;
        public int sidestepDir;
        public float vy;

        // Idle / scanning
        public Vector3 idleAnchor;
        public float idleTimer;
        public float scanTimer;
        public float glanceTimer;
        public float glanceTargetYaw;

        // Point-of-interest look wander (unalerted idle look target)
        public float poiTimer;
        public Vector3 poiPoint;

        // Sniff (proximity detection through obstacles) state
        public bool sniffHold;
        public float sniff01;
        public float sniffHoldTimer;
        public float sniffSuppressTimer;
        public float sniffPathTimer;
        public bool sniffReachable;

        // Camouflage matching
        public float camo01;
        public float camoTimer;

        // Combat
        public float activateTimer;
        public float fireTimer;
        public float backupTimer;
    }

    // ====================== TUNABLES (from .ctor defaults) ======================

    public string roomRootName = "Room";
    public string playerName = "Player";
    public string[] botNames = { "Bot1", "Bot2", "Bot3", "Bot4" };
    public string startButtonName = "Button Start";

    public float seekerHuntTime = 60f;
    public float walkSpeed = 9f;
    public float graceTime = 8f;
    public float roundTime = 45f;
    public float scanTime = 1.4f;
    public float stuckEscapeTime = 4f;
    public float arriveDistance = 2.5f;
    public float repathInterval = 1f;
    public float botGravity = 70f;
    public float botClimbStepHeight = 4f;
    public float recheckTime = 25f;
    public float searchGridCell = 28f;
    public float playerScaleInMode2 = 0.5f;
    public float botAvoidRadius = 9f;
    public float camoStrength = 0.85f;
    public float camoRefresh = 0.3f;
    public float minVisionRange = 14f;
    public float camoMatchTolerance = 0.45f;
    public float botAvoidStrength = 1.3f;
    public float visionAngle = 100f;
    public float catchRange = 11f;
    public float playerColorRefresh = 0.75f;
    public float visionRange = 46f;
    public bool camoAffectsVision = true;
    public float loseTrack = 4f;
    public float investigateTime = 5f;
    public float minCatchRange = 3f;
    public float spotHold = 1.4f;
    public bool highExposureBoost = true;
    public float exposedHeightAboveFloor = 25f;
    public float highVisionRange = 200f;
    public bool proximityReveal = true;
    public float sniffMaxHold = 8f;
    public float sniffCamoMul = 0.3f;
    public float sniffRadius = 10f;
    public float sniffTime = 5f;
    public float sniffMaxHeight = 12f;
    public float sniffExposedSpeedup = 2.5f;
    public float sniffCooldown = 4f;
    public bool sniffThroughFurniture = true;
    public bool searchEscalation = true;
    public float escalationSniffSpeedup = 0.35f;
    public float escalationRangeBoost = 0.5f;
    public float escalationFullTime = 90f;
    public float escalationCamoErode = 0.6f;
    public bool packConverge = true;
    public float convergeRadius = 60f;
    public float convergeInterval = 1.5f;
    public float predictMinSpeed = 2f;
    public float predictLead = 1.2f;
    public float predictMaxDist = 30f;
    public bool predictiveSearch = true;
    public bool homeInWhenLost = true;
    public float homeInChance = 0.8f;
    public float homeInStartFraction = 0.667f;
    public bool chasePlayer = true;

    public string gunObjectName = "gun";
    public string handBoneName = "mixamorig:RightHand";

    public float bulletSize = 1.2f;
    public float bulletLife = 3f;
    public float fireInterval = 0.7f;
    public float bulletSpeed = 160f;
    public Color bulletColor = new Color(1f, 0.55f, 0.15f, 1f);

    [Header("Scene references (assigned in inspector or resolved at runtime)")]
    public GameObject bulletPrefab;
    public HideModeResultUI hideUI; // used by <>b__187_0 reward-revive callback

    // ====================== RUNTIME STATE ======================

    private readonly List<SeekBot> bots = new List<SeekBot>();
    private readonly List<Transform> targets = new List<Transform>();
    private readonly Dictionary<Transform, Ragdoll> targetRagdoll = new Dictionary<Transform, Ragdoll>();
    private readonly List<Vector3> searchPoints = new List<Vector3>();
    private Color playerAvgColor = Color.gray;
    private Vector3 _playerOrigScale;
    private readonly RaycastHit[] _rayHits = new RaycastHit[32];
    private readonly Dictionary<Collider, bool> _isCharacterCache = new Dictionary<Collider, bool>();
    private readonly Dictionary<Material, Color> _bgColorCache = new Dictionary<Material, Color>();

    private int _lastGraceSec = -1;
    private float caughtPopupDelay = 3f;
    private float spotShootDelay = 3f;

    // 0 = idle/off, 1 = hiding grace / active seek, 2 = escalated (no more grace), 3 = round over
    private int state;
    private float timeLeft;
    private float graceLeft;
    private float floorY;
    private float _searchHeat;
    private bool _spotting;
    private SeekBot _spotBot;
    private float _spotTimer;
    private bool _spotFired;

    private Transform playerTf;
    private Renderer[] playerRends;
    private Bounds playerBounds;
    private Ragdoll playerRagdoll;
    private bool _playerScaled;

    private ChameleonPaint paint;
    private HideModeResultUI resultUI;
    private GameModeManager gmm;
    private BotManager botMgr;
    private CameraController camCtrl;

    private bool[] claimed;
    private float[] checkedAt;

    private UnityEngine.AI.NavMeshData navData;
    private NavMeshDataInstance navInstance;
    private Texture2D _camoBlock;
    private float playerColorTimer;

    private UnityEngine.Font font;
    private GameObject statusCanvas;
    private UnityEngine.UI.Text statusText;
    private UnityEngine.UI.Text resultText;
    private bool startBtnResolved;
    private GameObject startBtnGO;

    private Coroutine _caughtRoutine;
    private int _caughtTimeLeft;

    private static int AnimSpeedHash;  // Animator float param "Speed"
    private static int AnimPoseHash;   // Animator int param "Pose"
    private static int AnimFireHash;   // Animator trigger "GunFire"
    private static int BaseColorPropId; // Shader.PropertyToID("_BaseColor")

    // ====================== PROPERTIES ======================

    public bool Active => state == 1 || state == 2;

    /// Seconds remaining in the current phase (grace countdown while state==1, otherwise round timer).
    public int TimerSeconds => Mathf.FloorToInt(Mathf.Max(0f, state == 1 ? graceLeft : timeLeft));

    /// 0..1 fraction of the current phase's duration remaining.
    public float TimerFraction
    {
        get
        {
            // CONFIRMED via cross-referencing offsets: the same field-selection offset used here
            // for state != 1 is the one StartSeeking/SearchHeat select via chasePlayer==true, i.e.
            // seekerHuntTime — NOT roundTime. This getter picks it purely by state (not chasePlayer),
            // which is a real, preserved quirk of the original: in !chasePlayer ("hide" mode) games,
            // timeLeft is actually initialized from roundTime in StartSeeking, so this fraction can
            // read against the wrong denominator there. Not "fixed" to roundTime since the raw code
            // doesn't do that — kept faithful to the state-only selector actually present.
            float duration = Mathf.Max(0.01f, state == 1 ? graceTime : seekerHuntTime);
            float remaining = state == 1 ? graceLeft : timeLeft;
            return Mathf.Clamp01(remaining / duration);
        }
    }

    // NOTE: returns 1 while the round is active and 0 once state==3 (round over) — a binary flag,
    // not an actual fugitive count. Paste PickSearchIndex/Detect call sites if you want this renamed
    // to reflect real semantics (e.g. RoundActive) rather than kept as a misleading "count".
    public int FugitivesLeft => state != 3 ? 1 : 0;

    // NOTE: identical implementation to BotCount below (both just return bots.Count) — preserved as-is
    // from the dump; the duplication may be intentional (two call sites with different semantic intent
    // that happen to compute the same thing today) or dead duplication from refactoring.
    public int HuntersLeft => bots.Count;

    public int BotCount => bots.Count;

    // ====================== SETUP ======================

    public void SetBots(string[] names)
    {
        botNames = names ?? Array.Empty<string>();
        ResolveBots();
        ComputeFloorY();
    }

    private void ResolveBots()
    {
        bots.Clear();
        if (botNames == null) return;

        foreach (string name in botNames)
        {
            GameObject go = GameObject.Find(name);
            if (go == null) continue;

            SkinnedMeshRenderer smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Color originColor = Color.white;
            if (smr != null)
            {
                Material mat = smr.sharedMaterial;
                if (mat != null && mat.HasProperty(BaseColorPropId))
                {
                    originColor = mat.GetColor(BaseColorPropId);
                }
            }

            SeekBot bot = new SeekBot
            {
                tf = go.transform,
                anim = go.GetComponentInChildren<Animator>(true),
                smr = smr,
                cc = go.GetComponent<CharacterController>(),
                ragdoll = go.GetComponent<Ragdoll>(),
                originPos = go.transform.position,
                originRot = go.transform.rotation,
                originColor = originColor
            };

            bots.Add(bot);
        }
    }

    private void ComputeFloorY()
    {
        float y = bots.Count > 0 ? bots[0].originPos.y : transform.position.y;
        floorY = y - 7f;

        // CONFIRMED: decoded the packed Vector3 offset term (Vector3.up scaled by the broadcast
        // -12.0 constant, i.e. up.x*-12=0, up.y*-12=-12, up.z*-12=0) — this is exactly
        // originPos + Vector3.up * -12, i.e. originPos + Vector3.down * 12. No longer an inference.
        bool found = false;
        float best = float.MaxValue;
        for (int i = 0; i < bots.Count; i++)
        {
            SeekBot b = bots[i];
            if (b.tf == null) continue;

            Vector3 probe = b.originPos + Vector3.down * 12f;
            if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 30f, NavMesh.AllAreas))
            {
                if (hit.position.y < best)
                {
                    found = true;
                    best = hit.position.y;
                }
            }
        }

        if (found)
        {
            floorY = best;
        }
    }

    private void Start()
    {
        font = UIFont.Default;
        ResolveBots();
        ComputeFloorY();

        GameObject playerGO = PlayerRef.Resolve(playerName) as GameObject;
        if (playerGO != null)
        {
            playerTf = playerGO.transform;
            playerRends = playerTf.GetComponentsInChildren<Renderer>();
        }

        paint = Object.FindFirstObjectByType<ChameleonPaint>();
        resultUI = Object.FindFirstObjectByType<HideModeResultUI>();
        gmm = Object.FindFirstObjectByType<GameModeManager>();
        botMgr = Object.FindFirstObjectByType<BotManager>();
        camCtrl = Object.FindFirstObjectByType<CameraController>();

        if (bulletPrefab == null)
        {
            GameObject loaded = Resources.Load<GameObject>("BotBullet"); // StringLiteral_2695 — TODO: confirm exact Resources path
            if (loaded != null)
            {
                bulletPrefab = loaded;
            }
        }

        BuildStatusUI();
        SetStatusVisible(false);
    }

    private void BuildStatusUI()
    {
        bool canvasCreated, statusCreated, resultCreated;

        statusCanvas = SceneUI.GetOrCreateCanvas("BotSeekStatus", out canvasCreated); // StringLiteral_8560 — TODO: confirm exact canvas key
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(statusCanvas);
        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 11;

            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(statusCanvas);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // CONFIRMED via bit-level decode of the packed constant 0x44f0000044870000: (1080, 1920).
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(statusCanvas);

        GameObject statusGO = SceneUI.GetOrCreate("StatusText", statusCanvas.transform, out statusCreated); // StringLiteral_8559 — TODO: confirm exact key
        statusText = SceneUI.GetOrAdd<Text>(statusGO);
        if (statusText != null)
        {
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

                // FIXED: raw packed constant 0x3f8000003f000000 decodes to (0.5, 1.0) for all three
                // of anchorMin/anchorMax/pivot — a top-center anchor, not (0.5, 0.5) center as
                // previously drafted. This pairs correctly with anchoredPosition (0, -150): a
                // top-anchored box offset 150px down, which is the sensible layout for a status
                // countdown near the top of the screen.
                RectTransform rt = statusText.rectTransform;
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -150f);
                // FIXED: raw packed constant 0x4270000044750000 decodes to (980, 60) — the draft had
                // this reversed as (60, 980), which would have made the box extremely tall and narrow
                // instead of a wide, short banner.
                rt.sizeDelta = new Vector2(980f, 60f);
            }

            GameObject resultGO = SceneUI.GetOrCreate("ResultText", statusCanvas.transform, out resultCreated); // StringLiteral_8558 — TODO: confirm exact key
            resultText = SceneUI.GetOrAdd<Text>(resultGO);
            if (resultText != null)
            {
                resultText.font = font;
                resultText.raycastTarget = false;
                if (resultCreated)
                {
                    resultText.text = "";
                    resultText.fontSize = 72;
                    resultText.alignment = TextAnchor.MiddleCenter;
                    resultText.color = Color.white;
                    resultText.horizontalOverflow = HorizontalWrapMode.Overflow;
                    resultText.verticalOverflow = VerticalWrapMode.Overflow;

                    // CONFIRMED (0.5, 0.5) center anchor for anchorMin/anchorMax/pivot, from packed
                    // constant 0x3f0000003f000000 — matches what was already drafted.
                    RectTransform rt2 = resultText.rectTransform;
                    rt2.anchorMin = new Vector2(0.5f, 0.5f);
                    rt2.anchorMax = new Vector2(0.5f, 0.5f);
                    rt2.pivot = new Vector2(0.5f, 0.5f);
                    // FIXED: raw packed constant 0x42f0000000000000 decodes to (0, 124), not (0, 120).
                    rt2.anchoredPosition = new Vector2(0f, 124f);
                    // FIXED: raw packed constant 0x43a00000447a0000 decodes to (1000, 320), not (1000, 250).
                    rt2.sizeDelta = new Vector2(1000f, 320f);
                }
            }
        }
    }

    private void SetStatusVisible(bool v)
    {
        if (statusCanvas != null)
        {
            statusCanvas.SetActive(v);
        }
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
            GameObject playerGO = PlayerRef.Resolve(playerName) as GameObject;
            if (playerGO != null)
            {
                playerTf = playerGO.transform;
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
        state = 1;
        // CONFIRMED: this chasePlayer-based selector is corroborated independently by SearchHeat
        // (same two offsets, same chasePlayer condition) — seekerHuntTime / roundTime pairing is solid.
        timeLeft = chasePlayer ? seekerHuntTime : roundTime;
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

    private void EnsureColliders()
    {
        GameObject room = GameObject.Find(roomRootName);
        if (room == null) return;

        MeshRenderer[] renderers = room.GetComponentsInChildren<MeshRenderer>();
        foreach (MeshRenderer mr in renderers)
        {
            if (mr.GetComponent<Collider>() != null) continue;

            MeshFilter mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            Mesh mesh = mf.sharedMesh;
            if (mesh.isReadable && mesh.vertexCount > 0)
            {
                mr.gameObject.AddComponent<MeshCollider>();
            }
        }

        StairRampBuilder.EnsureRampsForRoom(room);
    }

    private void ScalePlayerForMode2()
    {
        if (_playerScaled) return;
        if (playerTf == null) return;
        if (playerScaleInMode2 >= 0.999f) return;

        _playerOrigScale = playerTf.localScale;

        CharacterController cc = playerTf.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        playerTf.localScale = Vector3.one * playerScaleInMode2;

        if (cc != null) cc.enabled = true;

        Grounding.ToFloor(playerTf, playerRends);
        _playerScaled = true;
    }

    private void EnsureNavMesh()
    {
        Transform anchor = playerTf != null ? playerTf : transform;

        if (!NavMesh.SamplePosition(anchor.position, out NavMeshHit _, 25f, NavMesh.AllAreas))
        {
            GameObject room = GameObject.Find(roomRootName);
            if (room == null) return;

            // Bounds of the room, expanded to encompass every MeshRenderer's bounds.
            // NOTE: simplified from a hand-vectorised min/max merge in the decompiled code to
            // Bounds.Encapsulate, which is mathematically equivalent (union of AABBs).
            //
            // FIXED: the raw pre-loop fallback box is NOT room.transform.position/localScale — it
            // reads the room's position for center, but for extents it reads Vector3.one (not the
            // room's actual scale!) times 0.5 for x/y, and leaves extents.z at its zero-initialized
            // value. That's effectively a degenerate (zero-depth) box. This branch is only ever used
            // if the room GameObject has literally zero MeshRenderer children (near-dead in practice,
            // since the rest of this method and BuildSearchPoints both need renderers to do anything
            // useful), but is reproduced faithfully rather than "fixed" to something more sensible.
            Bounds roomBounds = new Bounds(room.transform.position, new Vector3(1f, 1f, 0f));
            Renderer[] renderers = room.GetComponentsInChildren<Renderer>();
            bool any = false;
            foreach (Renderer r in renderers)
            {
                if (!any)
                {
                    roomBounds = r.bounds;
                    any = true;
                }
                else
                {
                    roomBounds.Encapsulate(r.bounds);
                }
            }

            if (any)
            {
                roomBounds.Expand(12f);

                NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
                settings.agentRadius = 2.5f;
                settings.agentHeight = 12f;
                settings.agentClimb = 4f;
                settings.agentSlope = 45f;

                List<NavMeshBuildMarkup> markups = new List<NavMeshBuildMarkup>();
                List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
                NavMeshBuilder.CollectSources(room.transform, -1, NavMeshCollectGeometry.RenderMeshes, 0, markups, sources);

                navData = NavMeshBuilder.BuildNavMeshData(settings, sources, roomBounds, Vector3.zero, Quaternion.identity);

                if (navInstance.valid)
                {
                    NavMesh.RemoveNavMeshData(navInstance);
                }
                navInstance = NavMesh.AddNavMeshData(navData);

                Debug.Log("[BotSeek] NavMesh baked (no existing one found): sources=" + sources.Count +
                          " valid=" + navInstance.valid);
            }
        }
    }

    private void ResetBots()
    {
        ClearSpot();

        for (int i = 0; i < bots.Count; i++)
        {
            SeekBot b = bots[i];

            if (b.ragdoll != null)
            {
                b.ragdoll.Deactivate();
            }

            Vector3 home = ValidRoomHome(b.originPos, i);

            Collider cc = b.cc;
            if (cc != null) cc.enabled = false;

            if (b.tf != null)
            {
                b.tf.SetPositionAndRotation(home, b.originRot);
            }

            if (cc != null) cc.enabled = true;

            if (b.cc != null)
            {
                float scaleY = Mathf.Max(0.01f, b.tf.lossyScale.y);
                float stepFromClimb = botClimbStepHeight / scaleY;
                float heightBudget = scaleY * b.cc.height - 0.5f;
                float clamped = Mathf.Min(stepFromClimb, heightBudget);
                b.cc.stepOffset = stepFromClimb >= 0.1f ? clamped : 0.1f;
            }

            if (b.anim != null)
            {
                b.anim.SetFloat(AnimSpeedHash, 0f);
                b.anim.SetInteger(AnimPoseHash, 0);
            }

            if (b.smr != null)
            {
                Material mat = b.smr.material;
                mat.SetColor(BaseColorPropId, b.originColor);
            }

            // Reset transient AI/detection/patrol state.
            b.targetIdx = -1;
            b.corners = null;
            b.alerted = false;
            b.sightTimer = 0f;
            b.lostTimer = 0f;
            b.investigating = false;
            b.camo01 = 0f;
            b.bestDist = float.MaxValue;
            b.targetTimer = 0f;
            b.idleTimer = 0f;
            b.seenHasVel = false;
            b.seenVel = Vector3.zero;
            b.fireTimer = 0f;

            // NOTE: staggered per-bot so bots don't all refresh camo / "activate" on the same frame.
            // FIXED: raw is `if (count < 2) count = 1;`, NOT Mathf.Max(count, 2) — these only differ
            // at count 0 or 1 (draft would divide camoTimer's stagger by 2 there; raw divides by 1).
            int denom = bots.Count < 2 ? 1 : bots.Count;
            b.camoTimer = (camoRefresh / denom) * i;
            b.activateTimer = i * 0.12f;

            Vector3 pos = b.tf != null ? b.tf.position : Vector3.zero;
            b.idleAnchor = pos;
            b.progressAnchor = pos;
        }
    }

    private void AttachGunsToSeekers()
    {
        GameObject gunPrefab = FindInScene(gunObjectName);

        GunHolder holder = Object.FindFirstObjectByType<GunHolder>();

        Vector3 localPos = holder != null ? holder.localPosition : Vector3.zero;

        Vector3 localEuler = holder != null ? holder.localEuler : Vector3.zero;

        Vector3 localScale = holder != null ? holder.localScale : Vector3.one;

        foreach (SeekBot b in bots)
        {
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

            if (gunPrefab == null) continue;

            Transform handBone = null;
            Transform[] children = b.tf.GetComponentsInChildren<Transform>(true);
            foreach (Transform child in children)
            {
                if (child.name == handBoneName)
                {
                    handBone = child;
                    break;
                }
            }

            if (handBone == null) continue;

            Transform existing = handBone.Find("BotGun");
            if (existing != null)
            {
                b.gunTf = existing;
            }
            else
            {
                GameObject gunInstance = Object.Instantiate(gunPrefab, handBone, false);
                gunInstance.name = "BotGun";
                gunInstance.transform.localPosition = localPos;
                gunInstance.transform.localEulerAngles = localEuler;
                gunInstance.transform.localScale = localScale;
                gunInstance.SetActive(true);
                b.gunTf = gunInstance.transform;
            }
        }
    }

    private void BuildSearchPoints()
    {
        searchPoints.Clear();

        GameObject room = GameObject.Find(roomRootName);
        if (room != null)
        {
            // UNCONFIRMED: these are the excluded-substring string literals (StringLiteral_11123,
            // 4939, 3283, 11126, 11174, 4189, 8389, 3265) — no Dumpstringliteral.json was attached
            // this round, so these values are a best-effort guess at plausible names, not resolved
            // fact. Re-paste with the string table if you want these locked down.
            string[] excludedNames = { "Wall", "Floor", "Ceil", "Wallpaper", "Window", "Door", "Rug", "Cards" };

            MeshRenderer[] renderers = room.GetComponentsInChildren<MeshRenderer>();
            foreach (MeshRenderer mr in renderers)
            {
                Bounds b = mr.bounds;
                float footprint = Mathf.Max(b.extents.x * 2f, b.extents.z * 2f);
                float heightExtent = b.extents.y * 2f;

                // Only consider "furniture-sized" surfaces: not a huge structural surface (walls/floor),
                // not paper-thin (rugs/wallpaper), not a tiny prop.
                bool outOfRange = footprint > 22f || heightExtent < 3f || footprint < 2f;
                if (outOfRange) continue;

                bool excluded = false;
                foreach (string bad in excludedNames)
                {
                    if (mr.gameObject.name.Contains(bad)) { excluded = true; break; }
                }
                if (excluded) continue;

                Vector3 samplePos = new Vector3(b.center.x, floorY, b.center.z);
                float sampleRadius = Mathf.Max(footprint, 6f) + 6f;
                if (NavMesh.SamplePosition(samplePos, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas)
                    && hit.position.y <= floorY + 3f)
                {
                    searchPoints.Add(hit.position);
                }
            }
        }

        AddFloorGridPoints();

        if (searchPoints.Count == 0)
        {
            // FIXED: sincosf(angle, out sin, out cos) — the raw code uses the COSINE output for x
            // and the SINE output for z (confirmed against the standard sincosf signature and
            // cross-checked against ValidRoomHome's identical pattern, which already had this right).
            // The draft had x/z swapped.
            Vector3 origin = transform.position;
            for (int i = 0; i < 6; i++)
            {
                searchPoints.Add(new Vector3(
                    origin.x + Mathf.Cos(i) * 25f,
                    floorY,
                    origin.z + Mathf.Sin(i) * 25f));
            }
        }
    }

    public void StopMode()
    {
        Coroutine routine = _caughtRoutine;
        state = 0;
        if (routine != null)
        {
            StopCoroutine(routine);
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

    private void RestorePlayerRagdoll()
    {
        if (playerRagdoll == null && playerTf != null)
        {
            playerRagdoll = playerTf.GetComponent<Ragdoll>();
        }

        if (playerRagdoll != null)
        {
            playerRagdoll.Teardown();
            playerRagdoll = null;
        }
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

    public void SetStartButtonVisible(bool v)
    {
        if (!startBtnResolved)
        {
            ResolveStartButton();
        }

        if (startBtnGO == null) return;
        if (startBtnGO.activeSelf == v) return;

        startBtnGO.SetActive(v);
    }

    public void SkipGrace()
    {
        if (RootManager.Instance == null) return;

        RootManager.Instance.ShowInterAds_Native();

        if (state == 1 && chasePlayer)
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

    private GameObject FindInScene(string name)
    {
        GameObject found = GameObject.Find(name);
        if (found != null) return found;

        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        foreach (Transform t in all)
        {
            if (t.name == name && t.gameObject.scene.IsValid())
            {
                return t.gameObject;
            }
        }

        return null;
    }

    private void Update()
    {
        bool showStartBtn = chasePlayer && state == 1;
        SetStartButtonVisible(showStartBtn);

        if (state == 0 || state == 3) return;
        if (playerTf == null) return;

        playerBounds = PlayerBounds();

        if (state != 1)
        {
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

            foreach (SeekBot b in bots)
            {
                if (b.activateTimer > 0f)
                {
                    b.activateTimer -= Time.deltaTime;
                    if (b.anim != null)
                    {
                        b.anim.SetFloat(AnimSpeedHash, 0f);
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

                if (state == 3) return;
            }

            if (timeLeft <= 0f)
            {
                Win();
            }
            return;
        }

        // state == 1: hiding / grace phase.
        if (!chasePlayer)
        {
            if (gmm != null && gmm.SeekerHideCountdown)
            {
                return;
            }
        }
        else
        {
            graceLeft -= Time.deltaTime;
            AudioManager.Countdown(graceLeft);

            int sec = Mathf.FloorToInt(Mathf.Max(0f, graceLeft));
            if (statusText != null && sec != _lastGraceSec)
            {
                statusText.text = sec + "s";
                _lastGraceSec = sec;
            }

            if (graceLeft > 0f) return;
        }

        state = 2;
        if (statusText != null)
        {
            statusText.text = "";
        }

        if (chasePlayer)
        {
            AudioManager.ResetCountdown();
            AudioManager.PhaseSeek();
        }
    }

    private Bounds PlayerBounds()
    {
        if (playerRends != null && playerRends.Length != 0)
        {
            // NOTE: simplified from a hand-vectorised min/max merge (SIMD-style decompiler artifact)
            // to Bounds.Encapsulate, which computes the same union-of-AABBs result.
            Bounds b = playerRends[0].bounds;
            for (int i = 1; i < playerRends.Length; i++)
            {
                b.Encapsulate(playerRends[i].bounds);
            }
            return b;
        }

        // CONFIRMED: decoded the packed extents math — Vector3.one * 4.0 * 0.5 = extents (2,2,2),
        // i.e. a full-size (4,4,4) box centered on the player transform (or origin if no playerTf).
        Vector3 pos = playerTf != null ? playerTf.position : Vector3.zero;
        return new Bounds(pos, new Vector3(4f, 4f, 4f));
    }

    private void UpdateSpot()
    {
        _spotTimer += Time.deltaTime;

        if (_spotBot != null && _spotBot.tf != null && playerTf != null)
        {
            if (_spotBot.anim != null)
            {
                _spotBot.anim.SetFloat(AnimSpeedHash, 0f);
            }

            Vector3 toPlayer = playerTf.position - _spotBot.tf.position;
            float distSq = toPlayer.x * toPlayer.x + toPlayer.z * toPlayer.z;
            if (distSq > 0.01f)
            {
                float dist = Mathf.Sqrt(distSq);
                Vector3 dir = dist <= 1e-5f ? Vector3.forward : new Vector3(toPlayer.x / dist, 0f, toPlayer.z / dist);
                Quaternion target = Quaternion.LookRotation(dir);
                _spotBot.tf.rotation = Quaternion.Slerp(_spotBot.tf.rotation, target, Time.deltaTime * 8f);
            }

            if (camCtrl != null)
            {
                camCtrl.SetSpotView(_spotBot.tf, playerTf);
            }

            if (_spotBot.look != null)
            {
                _spotBot.look._hasTarget = true;
                _spotBot.look._lookPoint = playerBounds.center;
                _spotBot.look._aiming = true;
                _spotBot.look._looseAim = false;
                _spotBot.look._aimPoint = playerBounds.center;
                return;
            }
        }

        if (!_spotFired)
        {
            float delay = Mathf.Max(0.1f, spotShootDelay);
            if (_spotTimer >= delay)
            {
                _spotFired = true;
                if (_spotBot != null)
                {
                    _spotBot.fireTimer = 0f;
                    FireAtTarget(_spotBot, playerTf, true);
                }
            }
        }
        else if (state == 2 && _spotTimer >= spotShootDelay + 1.5f)
        {
            Lose(_spotBot);
        }
    }

    private float SearchHeat()
    {
        if (!searchEscalation || state != 2) return 0f;

        // CONFIRMED: same chasePlayer-based seekerHuntTime/roundTime pairing as StartSeeking
        // (independent corroboration of both offsets).
        float duration = Mathf.Max(1f, chasePlayer ? seekerHuntTime : roundTime);
        float elapsed = duration - timeLeft;
        float ratio = elapsed / Mathf.Max(1f, escalationFullTime);
        return Mathf.Clamp01(ratio);
    }

    // ====================== BOT MOVEMENT / PATROL ======================
    //
    // NOTE ON THIS REGION: StepBot is by far the densest state machine in the original dump
    // (gravity, idle/sniff/investigate short-circuits, stuck-escape, search-point patrol,
    // corner-by-corner path following, separation steering, sidestep-on-stuck, and a
    // teleport-recovery fallback for badly stuck bots). It has been reconstructed faithfully
    // to the observed control flow and formulas as closely as possible; a couple of specific
    // items are called out where I was able to fully confirm or correct them below, and one
    // (the sidestep blend ratio) is flagged as still not fully re-derivable from the raw
    // register-level arithmetic — playtest that one specifically if it looks off.

    private void StepBot(SeekBot b, float dt)
    {
        if (b.tf == null) return;

        // --- Gravity / falling ---
        if (b.cc != null && b.cc.enabled)
        {
            float gDt = Mathf.Min(dt, 0.05f);
            if (!b.cc.isGrounded || b.vy >= 0f)
            {
                b.vy -= gDt * botGravity;
            }
            else
            {
                b.vy = -2f;
            }

            if (Mathf.Abs(b.vy) > 0.001f)
            {
                b.cc.Move(Vector3.up * (b.vy * gDt));
            }
        }

        // --- Short-circuit states ---
        if (b.investigating)
        {
            b.idleAnchor = b.tf.position;
            b.idleTimer = 0f;
            StepInvestigate(b, dt);
            return;
        }

        if (b.sniffHold)
        {
            if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);
            LookAroundInPlace(b, dt);
            b.idleAnchor = b.tf.position;
            b.idleTimer = 0f;
            return;
        }

        if (stuckEscapeTime > 0f)
        {
            float wanderedFromAnchor = HorizDist(b.tf.position, b.idleAnchor);
            if (wanderedFromAnchor <= 2f)
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

        // --- Pick / resolve the current move target ---
        Vector3 moveTarget;

        if (!b.alerted)
        {
            if (b.targetIdx < 0)
            {
                int idx = PickSearchIndex(b);
                b.corners = null;
                b.targetIdx = idx;
                b.targetTimer = 0f;
                b.progressTimer = 0f;
                b.noProgressTimer = 0f;
                b.bestDist = float.MaxValue;
                b.progressAnchor = b.tf.position;

                if (idx < 0)
                {
                    if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);
                    return;
                }

                Vector3 pickedTarget = searchPoints[idx];
                float dist = Vector3.Distance(b.tf.position, pickedTarget);
                float speed = Mathf.Max(1f, walkSpeed);
                float budget = (dist / speed) * 2.2f + 2.5f;
                b.targetBudget = Mathf.Clamp(budget, 4f, 12f);
            }

            Vector3 target = searchPoints[b.targetIdx];
            float distToTarget = HorizDist(b.tf.position, target);

            if (distToTarget <= arriveDistance * 2f)
            {
                if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);
                b.scanTimer += dt;
                LookAroundInPlace(b, dt);
                if (b.scanTimer < scanTime) return;

                b.scanTimer = 0f;
                MarkChecked(b.targetIdx);
                b.targetIdx = -1;
                return;
            }

            moveTarget = target;
        }
        else
        {
            moveTarget = b.lastSeen;
        }

        // --- Path corners: reuse or repath ---
        Vector3[] corners = b.corners;
        b.repathTimer -= dt;

        bool haveUsablePath = corners != null && b.ci < corners.Length && b.repathTimer > 0f;
        if (!haveUsablePath)
        {
            corners = ComputePath(b.tf.position, moveTarget);
            b.corners = corners;
            b.ci = 0;
            b.repathTimer = repathInterval;

            if (corners == null)
            {
                GiveUpOnCurrentTarget(b);
                return;
            }

            if (!b.alerted)
            {
                Vector3 lastCorner = corners.Length > 0 ? corners[corners.Length - 1] : b.tf.position;
                float reach = HorizDist(lastCorner, moveTarget);
                if (reach > arriveDistance * 2f)
                {
                    GiveUpOnCurrentTarget(b);
                    return;
                }
            }
        }

        // --- Follow the path, corner by corner ---
        if (corners == null || corners.Length == 0)
        {
            GiveUpOnCurrentTarget(b);
            return;
        }

        if (b.ci >= corners.Length)
        {
            if (!b.alerted)
            {
                MarkChecked(b.targetIdx);
                b.targetIdx = -1;
            }
            b.corners = null;
            return;
        }

        Vector3 startPos = b.tf.position;
        Vector3 corner = corners[b.ci];
        float cdx = corner.x - startPos.x;
        float cdz = corner.z - startPos.z;
        float cdist = Mathf.Sqrt(cdx * cdx + cdz * cdz);

        if (cdist <= arriveDistance)
        {
            b.ci++;
            return;
        }

        float invDist = Mathf.Max(cdist, 0.001f);
        Vector3 dir = new Vector3(cdx / invDist, 0f, cdz / invDist);

        Vector3 separation = SeparationFrom(b);
        if (separation.sqrMagnitude > 1e-10f)
        {
            dir += separation * botAvoidStrength;
            dir = dir.normalized;
        }

        if (b.sidestepTimer > 0f)
        {
            b.sidestepTimer -= dt;
            // NOTE: could not fully re-derive the exact blend ratio between the forward direction
            // and the sideways (perpendicular-to-dir, scaled by sidestepDir) component from the
            // raw register-level arithmetic — the same float registers get reused/aliased heavily
            // in this block. The raw code clearly scales the forward part by 0.35 and blends in a
            // sideways component before renormalizing; the exact sideways weight is left as this
            // reasonable approximation rather than asserted as confirmed. Worth an in-editor check
            // if "stuck" sidestepping looks too timid or too aggressive.
            Vector3 perp = new Vector3(-dir.z, dir.y, dir.x) * b.sidestepDir;
            dir = (dir * 0.35f + perp * 0.65f).normalized;
        }

        float moveDist = walkSpeed * dt;
        if (b.cc != null && b.cc.enabled)
        {
            b.cc.Move(dir * moveDist);
        }
        else
        {
            b.tf.position += dir * moveDist;
        }

        if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 1f);

        if (dir.x * dir.x + dir.z * dir.z > 0.001f)
        {
            Quaternion look = Quaternion.LookRotation(dir);
            b.tf.rotation = Quaternion.Slerp(b.tf.rotation, look, dt * 10f);
        }

        b.progressTimer += dt;
        if (b.progressTimer >= 1.4f)
        {
            float progress = HorizDist(b.tf.position, b.progressAnchor);
            b.progressTimer = 0f;
            b.progressAnchor = b.tf.position;
            if (progress < 4f)
            {
                AbandonPursuit(b);
                return;
            }
        }

        // --- Stuck detection / recovery ---
        Vector3 afterMove = b.tf.position;
        float movedThisFrame = new Vector2(afterMove.x - startPos.x, afterMove.z - startPos.z).magnitude;

        if (movedThisFrame >= walkSpeed * dt * 0.35f)
        {
            b.stuckTimer = Mathf.Max(0f, b.stuckTimer - dt * 1.5f);
            HandleTargetProgressOrTimeout(b, moveTarget, dt);
            return;
        }

        b.stuckTimer += dt;
        if (b.stuckTimer > 0.3f && b.sidestepTimer <= 0f)
        {
            b.sidestepDir = Random.value < 0.5f ? 1 : -1;
            b.sidestepTimer = 0.5f;
            b.corners = null;
            b.repathTimer = 0f;
        }

        if (b.stuckTimer <= 1.5f)
        {
            HandleTargetProgressOrTimeout(b, moveTarget, dt);
            return;
        }

        // Badly stuck for >1.5s: try to teleport-recover onto the navmesh at the bot's own current
        // position (snapped to floorY).
        // FIXED: the raw code samples the bot's CURRENT position (x/z unmodified, y snapped to
        // floorY) — there is no "-forward * 8" backward offset here. That offset pattern does
        // appear elsewhere (AbandonPursuit), but not in this particular recovery branch.
        Vector3 curPos = b.tf.position;
        Vector3 probe = new Vector3(curPos.x, floorY, curPos.z);
        if (b.cc != null &&
            NavMesh.SamplePosition(probe, out NavMeshHit hit, 12f, NavMesh.AllAreas) &&
            hit.position.y <= floorY + 3f)
        {
            b.cc.enabled = false;
            Vector3 snapped = b.tf.position;
            snapped.x = hit.position.x;
            snapped.z = hit.position.z;
            b.tf.position = snapped;
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

        if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);
    }

    // Faces the bot smoothly toward a world point (used when close to a target but not yet arrived).
    private void FaceToward(SeekBot b, Vector3 worldPoint, float dt)
    {
        if (b.tf == null) return;

        Vector3 pos = b.tf.position;
        float dx = worldPoint.x - pos.x;
        float dz = worldPoint.z - pos.z;
        if (dx * dx + dz * dz <= 0.001f) return;

        Quaternion look = Quaternion.LookRotation(new Vector3(dx, 0f, dz));
        b.tf.rotation = Quaternion.Slerp(b.tf.rotation, look, dt * 10f);
    }

    // Shared "not improving toward the target" bookkeeping used by both the making-progress and
    // mildly-stuck branches of StepBot: face the target/lastSeen while a grace period runs out, then
    // give up on the current search point once the grace period or per-target time budget is spent.
    private void HandleTargetProgressOrTimeout(SeekBot b, Vector3 moveTarget, float dt)
    {
        if (b.alerted)
        {
            FaceToward(b, b.lastSeen, dt);
            return;
        }

        b.targetTimer += dt;
        float distToTarget = HorizDist(b.tf.position, moveTarget);
        bool improved = distToTarget < b.bestDist - 0.5f;

        if (!improved)
        {
            b.noProgressTimer += dt;
            if (b.noProgressTimer <= 2.5f)
            {
                FaceToward(b, moveTarget, dt);
                return;
            }
        }
        else
        {
            b.bestDist = distToTarget;
            b.noProgressTimer = 0f;
            if (b.targetTimer <= b.targetBudget)
            {
                FaceToward(b, moveTarget, dt);
                return;
            }
        }

        b.noProgressTimer = 0f;
        b.bestDist = float.MaxValue;
        b.targetTimer = 0f;
        b.corners = null;
        GiveUpOnCurrentTarget(b);
    }

    // Stops the walk animation and, if not currently alerted, releases the claimed search point so
    // another bot (or this one, later) can pick it again.
    private void GiveUpOnCurrentTarget(SeekBot b)
    {
        if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);
        if (b.alerted) return;

        MarkChecked(b.targetIdx);
        b.targetIdx = -1;
    }

    // ====================== DETECTION / ALERT ======================

    private void Detect(SeekBot b, float dt)
    {
        bool sawPlayer = CanSee(b);
        Transform nearestHider = NearestVisibleHiderBot(b);
        Transform target = sawPlayer ? playerTf : null;

        if (nearestHider != null)
        {
            bool preferPlayer = false;
            if (target != null)
            {
                float distToHider = HorizDist(b.tf.position, nearestHider.position);
                float distToPlayer = HorizDist(b.tf.position, target.position);
                preferPlayer = distToPlayer <= distToHider;
            }

            if (!preferPlayer)
            {
                sawPlayer = false;
                target = nearestHider;
            }
        }

        bool sniffed = false;
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
            target = ProximitySniff(b, dt, out sawPlayer, out sniffed);
        }

        if (target == null)
        {
            b.sightTimer = Mathf.Max(0f, b.sightTimer - dt * 0.7f);
            if (!b.alerted) return;

            b.lostTimer += dt;
            if (b.lostTimer < loseTrack) return;

            b.alerted = false;
            b.lostTimer = 0f;
            Vector3 predicted = PredictLostPoint(b);
            BeginInvestigate(b, predicted);
            if (packConverge)
            {
                CallForBackup(b, predicted);
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
        }
        else
        {
            b.investigating = false;
            if (dt > 0.0001f)
            {
                float vx = (target.position.x - b.lastSeen.x) / dt;
                float vz = (target.position.z - b.lastSeen.z) / dt;
                Vector3 newVel = new Vector3(vx, 0f, vz);
                b.seenVel = b.seenHasVel ? Vector3.Lerp(b.seenVel, newVel, 0.4f) : newVel;
                b.seenHasVel = true;
            }
        }
        b.alerted = true;

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
        if (b.tf == null) return;

        float dist = HorizDist(b.tf.position, target.position);
        float effectiveRange = catchRange;
        if (sawPlayer && camoAffectsVision)
        {
            float t = Mathf.Clamp01(b.camo01);
            effectiveRange = catchRange + (minCatchRange - catchRange) * t;
        }

        bool trigger = sniffed || spotHold <= b.sightTimer || dist < effectiveRange;
        if (!trigger) return;

        if (!sawPlayer || !chasePlayer)
        {
            FireAtTarget(b, target, sawPlayer);
            return;
        }

        if (!_spotting)
        {
            BeginSpot(b);
        }
    }

    private void UpdateLookTarget(SeekBot b, float dt)
    {
        if (b == null || b.look == null || b.tf == null) return;

        if (b.alerted)
        {
            // FIXED: raw packed constant is 3.0, not 2.0 — the aim/look point is lastSeen +
            // Vector3.up * 3f (confirmed: 0x40400000 = 3.0f). The "z scaled too, but it's a no-op
            // since up.z==0" observation still holds.
            Vector3 aimAt = b.lastSeen + Vector3.up * 3f;
            b.look._hasTarget = true;
            b.look._lookPoint = aimAt;
            b.look._aimPoint = aimAt;
            b.look._aiming = true;
            b.look._looseAim = false;
            return;
        }

        if (_spotting && _spotBot == b && playerTf != null)
        {
            b.look._hasTarget = true;
            b.look._lookPoint = playerBounds.center;
            b.look._aimPoint = playerBounds.center;
            b.look._aiming = true;
            b.look._looseAim = false;
            return;
        }

        Vector3 loosePoint;

        if (!b.sniffHold)
        {
            if (!b.investigating)
            {
                b.poiTimer -= dt;
                if (b.poiTimer <= 0f)
                {
                    b.poiPoint = PickPOI(b);
                    b.poiTimer = Random.Range(1f, 2.6f);
                }
                loosePoint = b.poiPoint;
            }
            else
            {
                // NOTE: original jitters investigatePoint by a small random offset while walking toward
                // it; the exact jitter magnitude/axis wasn't fully re-derived from the raw arithmetic.
                // This is cosmetic only (idle look-wander while investigating a noise, not pathing).
                loosePoint = b.investigatePoint + Vector3.up * Random.Range(-1.5f, 1.5f);
            }
        }
        else
        {
            loosePoint = (chasePlayer && playerTf != null)
                ? playerBounds.center
                : b.tf.position + b.tf.forward * 4f;
        }

        b.look._hasTarget = true;
        b.look._lookPoint = loosePoint;
        b.look._loosePoint = loosePoint;
        b.look._aiming = false;
        b.look._looseAim = true;
    }

    private void Win()
    {
        state = 3;
        StopAllBots();

        if (statusText != null) statusText.text = "";

        if (chasePlayer && resultUI != null)
        {
            SetStatusVisible(false);
            // "Survival seconds" == the full round duration, since Win() only fires once the round
            // timer has run out (i.e. the player survived the whole round).
            int survivalSecs = Mathf.RoundToInt(Mathf.Max(0f, roundTime));
            resultUI.ShowVictory("#1/8", survivalSecs, 100); // "#1/8" is an asset/sprite-frame key, kept as-is
            return;
        }

        if (resultText != null)
        {
            resultText.text = "YOU STAYED HIDDEN!\nYou win.";
        }
    }

    // ====================== INVESTIGATE / IDLE / PATROL HELPERS ======================

    private void StepInvestigate(SeekBot b, float dt)
    {
        if (b.tf == null) return;

        b.investigateTimer -= dt;

        Vector3 pos = b.tf.position;
        float dx = b.investigatePoint.x - pos.x;
        float dz = b.investigatePoint.z - pos.z;

        if (Mathf.Sqrt(dx * dx + dz * dz) <= arriveDistance * 2.5f || b.investigateTimer <= 0f)
        {
            if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);
            LookAroundInPlace(b, dt);
            if (b.investigateTimer > 0f) return;

            EndInvestigate(b);
            return;
        }

        Vector3[] corners = b.corners;
        b.repathTimer -= dt;

        if (corners == null || b.ci >= corners.Length || b.repathTimer <= 0f)
        {
            corners = ComputePath(pos, b.investigatePoint);
            b.corners = corners;
            b.ci = 0;
            b.repathTimer = repathInterval;

            if (corners == null)
            {
                EndInvestigate(b);
                return;
            }
        }

        if (corners.Length == 0)
        {
            EndInvestigate(b);
            return;
        }

        if (b.ci >= corners.Length)
        {
            b.corners = null;
            return;
        }

        Vector3 corner = corners[b.ci];
        float cdx = corner.x - pos.x;
        float cdz = corner.z - pos.z;
        float cdist = Mathf.Sqrt(cdx * cdx + cdz * cdz);

        if (cdist <= arriveDistance)
        {
            b.ci++;
            return;
        }

        float invDist = Mathf.Max(cdist, 0.001f);
        Vector3 dir = new Vector3(cdx / invDist, 0f, cdz / invDist);

        Vector3 separation = SeparationFrom(b);
        if (separation.sqrMagnitude > 1e-10f)
        {
            dir = (dir + separation * botAvoidStrength).normalized;
        }

        float moveDist = walkSpeed * dt;
        if (b.cc != null && b.cc.enabled)
        {
            b.cc.Move(dir * moveDist);
        }
        else
        {
            b.tf.position += dir * moveDist;
        }

        if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 1f);

        if (dir.x * dir.x + dir.z * dir.z > 0.001f)
        {
            Quaternion look = Quaternion.LookRotation(dir);
            b.tf.rotation = Quaternion.Slerp(b.tf.rotation, look, dt * 10f);
        }

        b.progressTimer += dt;
        if (b.progressTimer >= 1.4f)
        {
            float progress = HorizDist(b.tf.position, b.progressAnchor);
            b.progressTimer = 0f;
            b.progressAnchor = b.tf.position;
            if (progress < 4f)
            {
                EndInvestigate(b);
            }
        }
    }

    private void LookAroundInPlace(SeekBot b, float dt)
    {
        if (b == null || b.tf == null) return;

        b.glanceTimer -= dt;
        if (b.glanceTimer <= 0f)
        {
            float amp = Random.Range(45f, 115f);
            if (Random.value >= 0.5f) amp = -amp;
            b.glanceTargetYaw = b.tf.eulerAngles.y + amp;
            b.glanceTimer = Random.Range(0.5f, 1.2f);
        }

        Quaternion target = Quaternion.Euler(0f, b.glanceTargetYaw, 0f);
        float angle = Quaternion.Angle(b.tf.rotation, target);
        if (angle > 0f)
        {
            float t = Mathf.Clamp01((dt * 150f) / angle);
            b.tf.rotation = Quaternion.SlerpUnclamped(b.tf.rotation, target, t);
        }
    }

    private static float HorizDist(Vector3 a, Vector3 c)
    {
        float dx = a.x - c.x;
        float dz = a.z - c.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

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

            float dist = HorizDist(b.tf.position, home);
            float speed = Mathf.Max(1f, walkSpeed);
            float needed = dist / speed + 4f;
            b.investigateTimer = Mathf.Max(investigateTime, needed);

            b.idleAnchor = b.tf.position;
            b.idleTimer = 0f;
            return;
        }

        // Can't path there normally (likely wedged in geometry): find the nearest point on the main
        // navmesh island that can actually reach 'home' and teleport there directly.
        if (FindMainNavmeshNear(b.tf.position, home, out Vector3 landing))
        {
            if (b.cc != null) b.cc.enabled = false;
            Vector3 snapped = b.tf.position;
            snapped.x = landing.x;
            snapped.z = landing.z;
            b.tf.position = snapped;
            if (b.cc != null) b.cc.enabled = true;
        }
        else
        {
            if (b.cc != null) b.cc.enabled = false;
            b.tf.position = home;
            if (b.cc != null) b.cc.enabled = true;
        }

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
        b.vy = 0f;
        b.progressAnchor = b.tf.position;
        b.idleAnchor = b.tf.position;
        b.idleTimer = 0f;

        if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);

        Debug.Log("[BotSeek] " + b.tf.name + " deeply stranded — teleported to spawn.");
    }

    private int PickSearchIndex(SeekBot b)
    {
        if (searchPoints.Count == 0) return -1;

        if (homeInWhenLost && chasePlayer && playerTf != null)
        {
            float fraction = roundTime > 0.01f ? (roundTime - timeLeft) / roundTime : 0f;
            if (fraction >= homeInStartFraction && Random.value < homeInChance)
            {
                int nearest = NearestSearchPointTo(playerBounds.center);
                if (nearest >= 0)
                {
                    claimed[nearest] = true;
                    return nearest;
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

    private void MarkChecked(int idx)
    {
        if (idx < 0) return;
        if (checkedAt == null || idx >= checkedAt.Length) return;

        checkedAt[idx] = Time.time;
        if (claimed != null && idx < claimed.Length)
        {
            claimed[idx] = false;
        }
    }

    private Vector3[] ComputePath(Vector3 from, Vector3 to)
    {
        Vector3 start = NavMesh.SamplePosition(from, out NavMeshHit fromHit, 18f, NavMesh.AllAreas) ? fromHit.position : from;
        Vector3 end = NavMesh.SamplePosition(to, out NavMeshHit toHit, 18f, NavMesh.AllAreas) ? toHit.position : to;

        NavMeshPath path = new NavMeshPath();
        if (NavMesh.CalculatePath(start, end, NavMesh.AllAreas, path) && path.corners.Length > 0)
        {
            return path.corners;
        }
        return null;
    }

    private Vector3 SeparationFrom(SeekBot self)
    {
        Vector3 result = Vector3.zero;
        if (botAvoidRadius <= 0.01f) return result;

        foreach (SeekBot other in bots)
        {
            if (other == self || other.tf == null || self.tf == null) continue;

            float dx = self.tf.position.x - other.tf.position.x;
            float dz = self.tf.position.z - other.tf.position.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);

            if (dist > 0.01f && dist < botAvoidRadius)
            {
                float weight = 1f - dist / botAvoidRadius;
                result.x += (dx / dist) * weight;
                result.z += (dz / dist) * weight;
            }
        }
        return result;
    }

    private void AbandonPursuit(SeekBot b)
    {
        if (b.tf == null) return;

        Vector3 fwd = b.tf.forward;
        float mag = fwd.x * fwd.x + fwd.z * fwd.z;

        float bx, bz;
        if (mag >= 0.01f)
        {
            float len = Mathf.Sqrt(mag);
            bx = -fwd.x / len;
            bz = -fwd.z / len;
        }
        else
        {
            // FIXED: raw fallback (degenerate horizontal forward — bot looking straight up/down)
            // reads Vector3.forward.x/.z directly (0.0, 1.0), NOT a negated/back value. Preserved
            // as-is even though it's inconsistent with the normal (-forward) branch above — that
            // inconsistency appears to be a genuine quirk of the original code, not a translation
            // error, and this is an extremely rare edge case (bot looking straight up/down).
            bx = 0f;
            bz = 1f;
        }

        if (b.cc != null)
        {
            Vector3 probe = new Vector3(b.tf.position.x + bx * 8f, floorY, b.tf.position.z + bz * 8f);
            if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 14f, NavMesh.AllAreas) &&
                hit.position.y <= floorY + 3f)
            {
                b.cc.enabled = false;
                Vector3 snapped = b.tf.position;
                snapped.x = hit.position.x;
                snapped.z = hit.position.z;
                b.tf.position = snapped;
                b.cc.enabled = true;
            }
        }

        if (!b.alerted)
        {
            if (b.targetIdx >= 0) MarkChecked(b.targetIdx);
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

        if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);
    }

    // ====================== INVESTIGATE TARGETING / BACKUP ======================

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

    private void ReleaseClaim(int idx)
    {
        if (idx < 0) return;
        if (claimed != null && idx < claimed.Length)
        {
            claimed[idx] = false;
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

        if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);
    }

    private void CallForBackup(SeekBot caller, Vector3 point)
    {
        if (!packConverge) return;

        foreach (SeekBot b in bots)
        {
            if (b == caller || b.tf == null) continue;
            if (b.alerted || b.sniffHold || b.activateTimer > 0f) continue;

            float dist = HorizDist(b.tf.position, point);
            if (dist > convergeRadius) continue;

            if (b.investigating)
            {
                float distToCurrentInvestigate = HorizDist(b.investigatePoint, point);
                if (distToCurrentInvestigate < arriveDistance * 2f) continue; // already headed there
            }

            BeginInvestigate(b, point);

            float speed = Mathf.Max(1f, walkSpeed);
            float needed = dist / speed + 3f;
            b.investigateTimer = Mathf.Max(investigateTime, needed);
        }
    }

    private Vector3 PredictLostPoint(SeekBot b)
    {
        if (!predictiveSearch || !b.seenHasVel) return b.lastSeen;

        float speed = b.seenVel.magnitude;
        if (speed < predictMinSpeed) return b.lastSeen;

        Vector3 dir = speed <= 1e-5f ? Vector3.forward : b.seenVel / speed;
        float lead = Mathf.Min(predictMaxDist, speed * predictLead);

        Vector3 probe = new Vector3(b.lastSeen.x + dir.x * lead, floorY, b.lastSeen.z + dir.z * lead);
        if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 12f, NavMesh.AllAreas) &&
            hit.position.y <= floorY + 4f)
        {
            return new Vector3(hit.position.x, b.lastSeen.y, hit.position.z);
        }

        return b.lastSeen;
    }

    // Finds a NavMesh point on a ring around 'origin', at increasing radius, roughly `seed`-offset
    // in angle so different bots (or repeated calls) don't all pick the exact same spot.
    // CONFIRMED: radius loop 18..74 step 8 matches raw exactly; x uses cos, z uses sin (matches
    // the standard sincosf(angle, out sin, out cos) signature).
    private Vector3 ValidRoomHome(Vector3 origin, int seed)
    {
        Transform anchor = playerTf != null ? playerTf : transform;
        Vector3 anchorPos = anchor.position;

        for (float radius = 18f; radius <= 74f; radius += 8f)
        {
            float angle = seed * 1.3f + radius / 2f;
            Vector3 probe = new Vector3(
                anchorPos.x + Mathf.Cos(angle) * radius,
                floorY,
                anchorPos.z + Mathf.Sin(angle) * radius);

            if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 8f, NavMesh.AllAreas) &&
                hit.position.y <= floorY + 4f)
            {
                return new Vector3(hit.position.x, hit.position.y + 7f, hit.position.z);
            }
        }

        if (NavMesh.SamplePosition(new Vector3(anchorPos.x, floorY, anchorPos.z), out NavMeshHit anchorHit, 100f, NavMesh.AllAreas))
        {
            return new Vector3(anchorHit.position.x, anchorHit.position.y + 7f, anchorHit.position.z);
        }

        if (NavMesh.SamplePosition(new Vector3(origin.x, floorY, origin.z), out NavMeshHit originHit, 20f, NavMesh.AllAreas))
        {
            return new Vector3(originHit.position.x, origin.y + 7f, originHit.position.z);
        }

        return origin;
    }

    private bool PathReaches(Vector3 from, Vector3 to)
    {
        Vector3[] corners = ComputePath(from, to);
        if (corners == null || corners.Length == 0) return false;

        Vector3 last = corners[corners.Length - 1];
        return HorizDist(last, to) <= arriveDistance * 2f;
    }

    // Scans a ring of increasing radius around 'pos' for a NavMesh point that can actually path
    // back to 'mainRef' — used to rescue a bot that's wedged off the main navmesh island.
    private bool FindMainNavmeshNear(Vector3 pos, Vector3 mainRef, out Vector3 result)
    {
        result = pos;

        for (float radius = 3f; radius <= 22f; radius += 3f)
        {
            for (int angleDeg = 0; angleDeg <= 315; angleDeg += 45)
            {
                float rad = angleDeg * Mathf.Deg2Rad;
                // FIXED: x/z were swapped relative to the sincosf convention — raw uses the cosine
                // output for x and the sine output for z (confirmed against ValidRoomHome's
                // identical, already-correct pattern above).
                Vector3 probe = new Vector3(
                    pos.x + radius * Mathf.Cos(rad),
                    floorY,
                    pos.z + radius * Mathf.Sin(rad));

                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 2.5f, NavMesh.AllAreas) &&
                    hit.position.y <= floorY + 3f)
                {
                    if (PathReaches(hit.position, mainRef))
                    {
                        result = hit.position;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // ====================== VISION / SNIFF ======================
    //
    // NOTE ON THIS REGION: CanSee and NearestVisibleHiderBot involve the densest bit-level float
    // math in the whole dump (vision-cone dot products, camo-based range shrink, a 3-point vertical
    // sample against the player's bounding box). They've been reconstructed to match the observed
    // control flow and formulas as closely as possible, but a couple of specific constants (the
    // exact 0.6/0.5 sample-point offsets) are inferred rather than confirmed — flagged inline below.
    // The eye-height fallback constant (Vector3.up * 8f) IS confirmed (appears as a literal `* 8.0`
    // in the raw code at every one of its call sites, not via a packed/static-field lookup).

    private bool CanSee(SeekBot b)
    {
        if (!chasePlayer) return false;
        if (b.tf == null) return false;

        Vector3 eye = b.smr != null
            ? b.smr.bounds.center
            : b.tf.position + Vector3.up * 8f; // CONFIRMED: literal *8.0 in the raw code.

        float camo = 0f;
        if (camoAffectsVision && paint != null)
        {
            b.camoTimer -= Time.deltaTime;
            float distToPlayer = Vector3.Distance(eye, playerBounds.center);
            if (distToPlayer <= visionRange && b.camoTimer <= 0f)
            {
                b.camo01 = ComputeCamo01(eye);
                b.camoTimer = camoRefresh;
            }
            camo = b.camo01;
        }

        float erodedCamo = Mathf.Clamp01(camo * (1f - _searchHeat * escalationCamoErode));
        float effectiveRange = visionRange + (minVisionRange - visionRange) * erodedCamo;
        effectiveRange *= (_searchHeat * escalationRangeBoost + 1f);

        if (highExposureBoost && (playerBounds.center.y - floorY) > exposedHeightAboveFloor)
        {
            effectiveRange = Mathf.Max(effectiveRange, highVisionRange);
        }

        // Sample center, upper-body and lower-body points on the player's bounds rather than just
        // the exact center — offsets (0.6 / 0.5 of half-height) are inferred, not confirmed exactly.
        Vector3[] samplePoints =
        {
            playerBounds.center,
            playerBounds.center + Vector3.up * (playerBounds.extents.y * 0.6f),
            playerBounds.center - Vector3.up * (playerBounds.extents.y * 0.5f)
        };

        Vector3 forward = b.tf.forward;
        float fwdHorizMag = new Vector2(forward.x, forward.z).magnitude;
        Vector3 fwdDir = fwdHorizMag >= 1e-5f
            ? new Vector3(forward.x / fwdHorizMag, 0f, forward.z / fwdHorizMag)
            : Vector3.back;
        float cosHalfAngle = Mathf.Cos(visionAngle * 0.5f * Mathf.Deg2Rad);

        foreach (Vector3 point in samplePoints)
        {
            float dist = Vector3.Distance(eye, point);
            if (dist > effectiveRange) continue;

            float safeDist = Mathf.Max(dist, 0.001f);
            Vector3 toPoint = (point - eye) / safeDist;

            bool insideCone = false;
            Vector2 horiz = new Vector2(toPoint.x, toPoint.z);
            if (horiz.sqrMagnitude > 0.0001f)
            {
                Vector2 horizNorm = horiz.normalized;
                float dot = fwdDir.y * toPoint.y + fwdDir.x * horizNorm.x + fwdDir.z * horizNorm.y;
                insideCone = cosHalfAngle <= dot;
            }

            bool alwaysSeeClose = dist <= catchRange + (minCatchRange - catchRange) * camo;

            if ((alwaysSeeClose || insideCone) && LineClear(eye, toPoint, dist - 1f))
            {
                return true;
            }
        }

        return false;
    }

    private Transform NearestVisibleHiderBot(SeekBot b)
    {
        if (targets.Count == 0 || b.tf == null) return null;

        Vector3 eye = b.smr != null ? b.smr.bounds.center : b.tf.position + Vector3.up * 8f;

        Vector3 forward = b.tf.forward;
        float fwdHorizMag = new Vector2(forward.x, forward.z).magnitude;
        Vector3 fwdDir = fwdHorizMag >= 1e-5f
            ? new Vector3(forward.x / fwdHorizMag, 0f, forward.z / fwdHorizMag)
            : Vector3.back;
        float cosHalfAngle = Mathf.Cos(visionAngle * 0.5f * Mathf.Deg2Rad);

        Transform best = null;
        float bestDist = float.MaxValue;

        for (int i = targets.Count - 1; i >= 0; i--)
        {
            Transform t = targets[i];
            if (t == null)
            {
                targets.RemoveAt(i);
                continue;
            }
            if (!t.gameObject.activeInHierarchy || TargetIsCorpse(t))
            {
                targets.RemoveAt(i);
                continue;
            }

            Vector3 tPos = t.position + Vector3.up * 8f;
            float visRange = visionRange;
            if (highExposureBoost && (tPos.y - floorY) > exposedHeightAboveFloor)
            {
                visRange = Mathf.Max(visRange, highVisionRange);
            }

            float dist = Vector3.Distance(eye, tPos);
            if (dist > visRange || dist >= bestDist) continue;

            float safeDist = Mathf.Max(dist, 0.001f);
            Vector3 toPoint = (tPos - eye) / safeDist;

            bool insideRange = dist <= catchRange;
            bool insideCone = false;
            Vector2 horiz = new Vector2(toPoint.x, toPoint.z);
            if (horiz.sqrMagnitude > 0.0001f)
            {
                Vector2 horizNorm = horiz.normalized;
                float dot = fwdDir.y * toPoint.y + fwdDir.x * horizNorm.x + fwdDir.z * horizNorm.y;
                insideCone = cosHalfAngle <= dot;
            }

            if ((insideRange || insideCone) && LineClear(eye, toPoint, dist - 1.5f))
            {
                best = t;
                bestDist = dist;
            }
        }

        return best;
    }

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
        Vector3 targetPoint = isPlayer ? playerBounds.center : HiderAimPoint(t);

        Vector3 delta = targetPoint - eye;
        float dist = delta.magnitude;

        // "Has line of sight" (even if outside the detection cone/range) speeds up the sniff timer —
        // smelling something you can also partially see is quicker than pure blind smell-tracking.
        bool hasLineOfSight = dist >= 0.01f && LineClear(eye, delta / Mathf.Max(dist, 0.0001f), dist - 1f);

        float camoFactor = Mathf.Clamp01(b.camo01 * (1f - _searchHeat * escalationCamoErode));
        float baseTime = Mathf.Max(0.25f, sniffTime);

        if (isPlayer && camoAffectsVision)
        {
            float camoMul = Mathf.Max(0.05f, camoFactor * (sniffCamoMul - 1f) + 1f);
            baseTime /= camoMul;
        }

        if (hasLineOfSight)
        {
            baseTime /= Mathf.Max(1f, sniffExposedSpeedup);
        }

        float heat = Mathf.Clamp01(_searchHeat);
        float rate = dt / (baseTime * (heat * (escalationSniffSpeedup - 1f) + 1f));
        b.sniff01 += rate;

        if (b.sniff01 >= 1f)
        {
            sniffed = true;
            b.sniff01 = 1f;
        }
        else if (b.sniffHoldTimer >= sniffMaxHold)
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
            t = null;
        }

        return t;
    }

    private void BeginSpot(SeekBot b)
    {
        _spotting = true;
        _spotBot = b;
        _spotTimer = 0f;
        _spotFired = false;

        // TODO: GameModeManager isn't in the "fully reversed" list — writing its PlayerFrozen backing
        // field directly (as the dump does) rather than through a public setter. Paste GameModeManager
        // if you want this replaced with a proper API call.
        if (gmm != null) gmm.PlayerFrozen = true;

        if (b != null && b.tf != null && camCtrl != null && playerTf != null)
        {
            camCtrl.SetSpotView(b.tf, playerTf);
        }

        SetStatusVisible(false);

        string botName = (b != null && b.tf != null) ? b.tf.name : "?";
        Debug.Log("[BotSeek] Player SPOTTED by " + botName + " — shot in " + spotShootDelay + "s");
    }

    private void FireAtTarget(SeekBot b, Transform target, bool targetIsPlayer)
    {
        if (b == null || b.tf == null || target == null) return;
        if (b.fireTimer > 0f) return;

        b.fireTimer = Mathf.Max(0.05f, fireInterval);
        if (b.anim != null) b.anim.SetTrigger(AnimFireHash);

        AudioManager.WaterGunShot();

        Vector3 aimPoint = targetIsPlayer ? playerBounds.center : HiderAimPoint(target);
        Vector3 muzzle = SeekerMuzzle(b, aimPoint);

        Vector3 dir = aimPoint - muzzle;
        dir = dir.magnitude <= 1e-5f ? b.tf.forward : dir.normalized;

        SpawnSeekerBullet(b, muzzle, dir, target, targetIsPlayer);
    }

    private Vector3 PickPOI(SeekBot b)
    {
        if (b == null || b.tf == null) return Vector3.zero;

        Vector3 pos = b.tf.position;

        if (searchPoints.Count < 1 || Random.value >= 0.5f)
        {
            float yawOffset = Random.Range(-110f, 110f);
            Quaternion rot = Quaternion.Euler(0f, b.tf.eulerAngles.y + yawOffset, 0f);
            Vector3 dir = rot * Vector3.forward;
            float dist = Random.Range(6f, 20f);
            float yOffset = Random.Range(-4f, 12f);
            return new Vector3(pos.x + dir.x * dist, pos.y + yOffset, pos.z + dir.z * dist);
        }

        int idx = Random.Range(0, searchPoints.Count);
        Vector3 basePoint = searchPoints[idx];
        // FIXED: raw jitters along Vector3.up, NOT Vector3.forward (confirmed: reads the "up" entry
        // of the Vector3 static-field table for all three components, so only the y-component of the
        // jitter is actually nonzero).
        float jitterDist = Random.Range(0f, 7f);
        return basePoint + Vector3.up * jitterDist;
    }

    private Transform NearestSniffTarget(SeekBot b, out bool isPlayer)
    {
        isPlayer = false;

        float radius = Mathf.Max(0.5f, sniffRadius * (_searchHeat * escalationRangeBoost + 1f));

        Transform best = null;
        float bestDist = float.MaxValue;

        if (chasePlayer && playerTf != null && b.tf != null &&
            (playerBounds.center.y - floorY) <= sniffMaxHeight)
        {
            float dist = HorizDist(b.tf.position, playerBounds.center);
            if (dist < radius)
            {
                best = playerTf;
                isPlayer = true;
                bestDist = dist;
            }
        }

        for (int i = targets.Count - 1; i >= 0; i--)
        {
            Transform t = targets[i];
            if (t == null) { targets.RemoveAt(i); continue; }
            if (!t.gameObject.activeInHierarchy) { targets.RemoveAt(i); continue; }
            if (TargetIsCorpse(t)) continue;
            if ((t.position.y - floorY) > sniffMaxHeight) continue;
            if (b.tf == null) break;

            float dist = HorizDist(b.tf.position, t.position);
            if (dist < bestDist)
            {
                isPlayer = false;
                best = t;
                bestDist = dist;
            }
        }

        if (best == null) return null;

        Vector3 bestPos = isPlayer ? playerBounds.center : best.position;

        b.sniffPathTimer -= Time.deltaTime;
        if (b.sniffPathTimer <= 0f)
        {
            b.sniffReachable = SniffBarrierOK(b, bestPos);
            b.sniffPathTimer = 0.4f;
        }

        return b.sniffReachable ? best : null;
    }

    private Vector3 HiderAimPoint(Transform t)
    {
        if (t == null) return Vector3.zero;

        SkinnedMeshRenderer smr = t.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr != null) return smr.bounds.center;

        CharacterController cc = t.GetComponent<CharacterController>();
        if (cc != null) return cc.bounds.center;

        // CONFIRMED: literal *4.0 with the "up" table entry.
        return t.position + Vector3.up * 4f;
    }

    private bool LineClear(Vector3 origin, Vector3 dir, float dist)
    {
        if (dist <= 0f) return true;

        int count = Physics.RaycastNonAlloc(origin, dir, _rayHits, dist);
        for (int i = 0; i < count; i++)
        {
            Collider col = _rayHits[i].collider;
            if (col is CharacterController) continue; // ignore character capsules themselves
            if (!IsCharacter(col)) return false;       // a real obstacle blocks the line
        }
        return true;
    }

    private bool TargetIsCorpse(Transform t)
    {
        if (t == null) return false;

        if (!targetRagdoll.TryGetValue(t, out Ragdoll ragdoll))
        {
            ragdoll = t.GetComponent<Ragdoll>();
            targetRagdoll[t] = ragdoll;
        }

        // TODO: Ragdoll's public API beyond Deactivate()/Teardown()/Trigger() isn't confirmed. The
        // decompiled code reads a bool field on the Ragdoll instance here, most plausibly an
        // "is currently active/ragdolled" flag. Paste Ragdoll's class to confirm the real member name.
        return ragdoll != null && ragdoll.IsRagdolling;
    }

    private bool SniffBarrierOK(SeekBot b, Vector3 targetCenter)
    {
        if (b.tf == null) return false;

        if (SniffReachable(b.tf.position, targetCenter)) return true;
        if (!sniffThroughFurniture) return false;

        Vector3 eye = b.smr != null ? b.smr.bounds.center : b.tf.position + Vector3.up * 8f;
        Vector3 delta = targetCenter - eye;
        float dist = delta.magnitude;
        if (dist < 0.5f) return true;

        Vector3 dir = delta / dist;
        int count = Physics.RaycastNonAlloc(eye, dir, _rayHits, dist);
        for (int i = 0; i < count; i++)
        {
            Collider col = _rayHits[i].collider;
            if (col == null)
            {
                if (!IsCharacter(null)) return false;
                continue;
            }
            if (col is CharacterController) continue;
            if (IsCharacter(col)) continue;

            if (Bullet.BlocksShot(col.gameObject)) return false;
        }
        return true;
    }

    private bool SniffReachable(Vector3 from, Vector3 target)
    {
        Vector3[] corners = ComputePath(from, target);
        if (corners == null || corners.Length == 0) return false;

        Vector3 last = corners[corners.Length - 1];
        if (HorizDist(last, target) > arriveDistance * 2f) return false;

        float pathLen = 0f;
        for (int i = 1; i < corners.Length; i++)
        {
            pathLen += Vector3.Distance(corners[i - 1], corners[i]);
        }

        return pathLen <= sniffRadius * 2.2f;
    }

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

    // ====================== CATCH / LOSE FLOW ======================

    private void Lose(SeekBot finder)
    {
        state = 3;
        StopAllBots();

        string finderName = (finder != null && finder.tf != null) ? finder.tf.name : "a bot";
        Debug.Log("[BotSeek] Player was FOUND by " + finderName + "!");

        if (statusText != null) statusText.text = "";

        if (chasePlayer && resultUI != null)
        {
            GroundPlayer();
            RagdollPlayer(finder);

            _caughtTimeLeft = Mathf.FloorToInt(Mathf.Max(0f, timeLeft));
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
            resultText.text = "SPOTTED!\nFound by " + finderName + ".";
        }
    }

    private void ClearSpot()
    {
        _spotting = false;
        _spotBot = null;
        _spotTimer = 0f;
        _spotFired = false;

        if (gmm != null) gmm.PlayerFrozen = false;

        if (camCtrl != null)
        {
            camCtrl.ClearSpotView();
        }
    }

    public void FoundHider(SeekBot b, Transform hider)
    {
        if (hider != null)
        {
            targets.Remove(hider);

            Vector3 forward = (b != null && b.tf != null) ? b.tf.forward : hider.forward;

            SkinnedMeshRenderer smr = hider.GetComponentInChildren<SkinnedMeshRenderer>(true);
            // FIXED: the fallback (no SkinnedMeshRenderer found) is hider.position + Vector3.up * 6f
            // ONLY — no forward component at all, and the multiplier is 6.0, not 8.0. Confirmed via
            // the packed constant (broadcast 6.0) applied uniformly to the "up" Vector3 table entry.
            Vector3 ragdollOrigin = smr != null
                ? smr.bounds.center
                : hider.position + Vector3.up * 6f;

            Ragdoll ragdoll = Ragdoll.Trigger(hider, ragdollOrigin, forward, 1f, true);
            if (ragdoll == null)
            {
                hider.gameObject.SetActive(false);
            }

            if (botMgr != null)
            {
                botMgr.SetTagVisible(hider, false);
            }
        }

        if (b != null)
        {
            b.corners = null;
            b.alerted = false;
            b.sightTimer = 0f;
            b.lostTimer = 0f;
            b.targetIdx = -1;

            string seekerName = b.tf != null ? b.tf.name : "seeker";
            string hiderName = hider != null ? hider.name : "?";
            Debug.Log("[BotSeek] " + seekerName + " found " + hiderName + ".");
        }
    }

    private Vector3 SeekerMuzzle(SeekBot b, Vector3 aim)
    {
        Vector3 fallback = b.smr != null ? b.smr.bounds.center : b.tf.position + Vector3.up * 8f;

        if (b.gunTf != null && b.gunTf.gameObject.activeInHierarchy)
        {
            Vector3 muzzlePos = b.gunTf.position;
            Vector3 toAim = aim - muzzlePos;
            float dist = toAim.magnitude;

            if (dist > 0.5f && LineClear(muzzlePos, toAim / dist, dist - 1.5f))
            {
                return muzzlePos;
            }
        }

        return fallback;
    }

    // NOTE: the compiler-generated lambda passed to Bullet.InitShot wasn't included in the pasted
    // decompiled output (its body lives in a separate <>c__DisplayClass method that wasn't part of
    // the SpawnSeekerBullet dump). The Lose()/FoundHider() dispatch below is inferred from the
    // captured shooter/target/targetIsPlayer fields and the obvious game-design intent — it is a
    // best guess, not a decompiled fact. Paste the actual b__ method if you have it, to confirm.
    private void SpawnSeekerBullet(SeekBot shooter, Vector3 origin, Vector3 dir, Transform target, bool targetIsPlayer)
    {
        Quaternion rot = Quaternion.LookRotation(dir);
        GameObject bulletInstance;
        Bullet bullet;

        if (bulletPrefab != null)
        {
            bulletInstance = Object.Instantiate(bulletPrefab, origin, rot);
            bulletInstance.name = "BotBullet";

            bullet = bulletInstance.GetComponent<Bullet>();
            if (bullet == null)
            {
                bullet = bulletInstance.AddComponent<Bullet>();
            }

            if (bullet.speed <= 0f) bullet.speed = bulletSpeed;
            if (bullet.maxLife <= 0f) bullet.maxLife = bulletLife;
            if (bullet.radius <= 0f) bullet.radius = Mathf.Max(0.01f, bulletSize * 0.5f);

            if (bulletInstance.GetComponent<TrailRenderer>() == null)
            {
                AddTracer(bulletInstance);
            }
        }
        else
        {
            // No bullet prefab assigned: build a simple primitive-sphere bullet at runtime.
            bulletInstance = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bulletInstance.name = "BotBullet";
            Object.Destroy(bulletInstance.GetComponent<Collider>());

            bulletInstance.transform.localScale = Vector3.one * bulletSize;
            bulletInstance.transform.SetPositionAndRotation(origin, rot);

            MeshRenderer mr = bulletInstance.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            Material mat = mr.material;
            mat.color = bulletColor;
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", bulletColor);
            }
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", bulletColor * 2.2f);
            }

            AddTracer(bulletInstance);

            bullet = bulletInstance.AddComponent<Bullet>();
            bullet.speed = bulletSpeed;
            bullet.maxLife = bulletLife;
            bullet.radius = bulletSize * 0.5f;
        }

        if (shooter != null && shooter.tf != null)
        {
            bullet.InitShot(shooter.tf, hitGO =>
            {
                if (targetIsPlayer)
                {
                    Lose(shooter);
                }
                else
                {
                    FoundHider(shooter, target);
                }
            });
        }
    }

    private void AddTracer(GameObject go)
    {
        TrailRenderer trail = go.AddComponent<TrailRenderer>();
        trail.time = 0.25f;

        float widthFromScale = go.transform.localScale.x * 0.6f;
        trail.startWidth = Mathf.Max(0.1f, widthFromScale);
        trail.endWidth = 0f;
        trail.numCapVertices = 2;

        Material mat = Resources.Load<Material>("BulletTrail");
        if (mat == null)
        {
            Renderer r = go.GetComponentInChildren<MeshRenderer>();
            if (r != null) mat = r.sharedMaterial;
        }
        trail.sharedMaterial = mat;

        trail.startColor = bulletColor;
        trail.endColor = new Color(bulletColor.r, bulletColor.g, bulletColor.b, 0f);
    }

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

    private static void SetGunPose(Animator anim, bool on)
    {
        if (anim == null) return;

        int layerCount = anim.layerCount;
        for (int i = 0; i < layerCount; i++)
        {
            if (anim.GetLayerName(i) == "Gun")
            {
                anim.SetLayerWeight(i, on ? 1f : 0f);
                return;
            }
        }
    }

    // ====================== CAMOUFLAGE ======================

    private float ComputeCamo01(Vector3 eye)
    {
        Color bg = ColorBehindPlayer(eye);
        if (ColorDist(bg, Color.gray) < 1e-5f)
        {
            return 0f; // no real background sample (fell back to flat gray) -> no camo bonus
        }

        float colorDiff = ColorDist(playerAvgColor, bg);
        float tolerance = Mathf.Max(0.05f, camoMatchTolerance);
        float closeness = 1f - Mathf.Clamp01(colorDiff / tolerance);
        float smoothed = closeness * closeness * (3f - 2f * closeness); // smoothstep
        return Mathf.Clamp01(camoStrength * smoothed);
    }

    private Color ColorBehindPlayer(Vector3 eye)
    {
        Vector3 toPlayer = playerBounds.center - eye;
        float dist = toPlayer.magnitude;
        if (dist < 0.01f) return Color.gray;

        Vector3 dir = toPlayer / dist;
        float clearance = playerBounds.extents.magnitude + 1.5f;
        Vector3 rayOrigin = playerBounds.center + dir * clearance;

        int count = Physics.RaycastNonAlloc(rayOrigin, dir, _rayHits, visionRange);
        RaycastHit? found = null;
        for (int i = 0; i < count; i++)
        {
            Collider col = _rayHits[i].collider;
            if (col is CharacterController) continue;
            if (IsCharacter(col)) continue;

            found = _rayHits[i];
            break;
        }

        if (found == null) return Color.gray;

        RaycastHit hit = found.Value;
        Renderer r = hit.collider.GetComponent<Renderer>();
        if (r == null) r = hit.collider.GetComponentInParent<Renderer>();

        Vector2 uv = (hit.collider is MeshCollider) ? hit.textureCoord : Vector2.zero;
        if (uv == Vector2.zero)
        {
            uv = MeshUvCenter(r);
        }

        Material mat = r != null ? r.sharedMaterial : null;
        return SampleTexAtUV(mat, uv);
    }

    private static float ColorDist(Color a, Color b)
    {
        float dr = a.r - b.r;
        float dg = a.g - b.g;
        float db = a.b - b.b;
        return Mathf.Sqrt(dr * dr + dg * dg + db * db);
    }

    private Vector2 MeshUvCenter(Renderer obj)
    {
        if (obj == null) return new Vector2(0.5f, 0.5f);

        MeshFilter mf = obj.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) return new Vector2(0.5f, 0.5f);

        Vector2[] uvs = mf.sharedMesh.uv;
        if (uvs == null || uvs.Length == 0) return new Vector2(0.5f, 0.5f);

        Vector2 min = uvs[0], max = uvs[0];
        for (int i = 1; i < uvs.Length; i++)
        {
            min = Vector2.Min(min, uvs[i]);
            max = Vector2.Max(max, uvs[i]);
        }
        return (min + max) * 0.5f;
    }

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
            if (m.HasProperty("_BaseColor")) tint = m.GetColor("_BaseColor");
            if (m.HasProperty("_BaseMap")) tex = m.GetTexture("_BaseMap");
            if (tex == null && m.HasProperty("_MainTex")) tex = m.GetTexture("_MainTex");
            if (tex == null) tex = m.mainTexture;
        }

        Color result;
        if (tex == null)
        {
            result = tint;
        }
        else
        {
            int width = Mathf.Min(1024, tex.width);
            int height = Mathf.Min(1024, tex.height);

            RenderTexture prevActive = RenderTexture.active;
            RenderTexture temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.Default);
            Graphics.Blit(tex, temp);
            RenderTexture.active = temp;

            int px = Mathf.Clamp(Mathf.RoundToInt(Mathf.Repeat(uv.x, 1f) * (width - 1)) - 3, 0, Mathf.Max(0, width - 6));
            int py = Mathf.Clamp(Mathf.RoundToInt(Mathf.Repeat(uv.y, 1f) * (height - 1)) - 3, 0, Mathf.Max(0, height - 6));

            if (_camoBlock == null)
            {
                _camoBlock = new Texture2D(6, 6, TextureFormat.RGBA32, false);
            }

            _camoBlock.ReadPixels(new Rect(px, py, 6, 6), 0, 0);
            _camoBlock.Apply(false);

            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(temp);

            Color[] pixels = _camoBlock.GetPixels();
            float r = 0f, g = 0f, b = 0f;
            foreach (Color c in pixels) { r += c.r; g += c.g; b += c.b; }
            int n = Mathf.Max(1, pixels.Length);
            result = new Color(tint.r * (r / n), tint.g * (g / n), tint.b * (b / n), tint.a);
        }

        if (m != null)
        {
            _bgColorCache[m] = result;
        }
        return result;
    }

    // ====================== MISC / CLEANUP / REWARD-REVIVE ======================

    private void StopAllBots()
    {
        foreach (SeekBot b in bots)
        {
            if (b.anim != null) b.anim.SetFloat(AnimSpeedHash, 0f);
            if (b.look != null)
            {
                b.look._hasTarget = false;
                b.look._aiming = false;
            }
        }
    }

    private void GroundPlayer()
    {
        WallClimber wallClimber = Object.FindFirstObjectByType<WallClimber>();
        if (wallClimber != null)
        {
            wallClimber.DropToGround();
            return;
        }

        if (playerTf != null)
        {
            Grounding.ToFloor(playerTf, playerRends);
        }
    }

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

        Vector3 pushDir;
        if (finder != null && finder.tf != null)
        {
            Vector3 delta = playerTf.position - finder.tf.position;
            pushDir = new Vector3(delta.x, 0f, delta.z);
        }
        else
        {
            Vector3 fwd = playerTf.forward;
            pushDir = new Vector3(-fwd.x, 0f, -fwd.z);
        }

        playerRagdoll.Activate(playerBounds.center, pushDir, 1f, false);
    }

    // NOTE: this coroutine's body wasn't in the pasted dump (only its state-machine constructor,
    // which just wires up the captured 'this' reference, was shown). The wait-then-show-defeat
    // behavior is inferred from the field name (caughtPopupDelay) and ShowDefeatFromCaught's obvious
    // purpose — a reasonable, but not decompiled-confirmed, reconstruction.
    private IEnumerator ShowCaughtDelayed()
    {
        yield return new WaitForSeconds(caughtPopupDelay);
        ShowDefeatFromCaught();
    }

    public void RevivePlayer()
    {
        if (AdMgr.Instance == null) return;
        if (!AdMgr.Instance.IsRewardReady) return;

        AdMgr.Instance.OnRewardView(ResumeAfterRevive, RewardFail);
    }

    private void RewardFail()
    {
        // Intentionally empty in the source — reward ad declined/failed, nothing to undo.
    }

    // Was the anonymous method BotSeekController.<RevivePlayer>b__187_0 in the dump.
    private void ResumeAfterRevive()
    {
        if (hideUI == null) return;

        hideUI.HideAll();
        RestorePlayerRagdoll();
        GroundPlayer();
        ResetBots();

        _lastGraceSec = -1;
        state = 1;
        timeLeft = roundTime;
        graceLeft = graceTime;

        SetStatusVisible(chasePlayer);
        if (resultText != null) resultText.text = "";

        RootManager.Instance?.SetNumber(1);
        Debug.Log("[BotSeek] Player revived — hide again!");
    }

    private void ShowDefeatFromCaught()
    {
        if (resultUI != null)
        {
            resultUI.ShowDefeat("#5/8", _caughtTimeLeft, 0); // "#5/8" is an asset/sprite-frame key, kept as-is
        }
    }

    // ====================== SEARCH-POINT GRID / BOOKKEEPING ======================

    private void AddFloorGridPoints()
    {
        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        if (tri.vertices == null || tri.vertices.Length == 0) return;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        bool any = false;

        foreach (Vector3 v in tri.vertices)
        {
            if (v.y > floorY + 3f) continue;
            any = true;
            if (v.x < minX) minX = v.x;
            if (v.x > maxX) maxX = v.x;
            if (v.z < minZ) minZ = v.z;
            if (v.z > maxZ) maxZ = v.z;
        }
        if (!any) return;

        float cell = Mathf.Max(8f, searchGridCell);

        HashSet<long> existingCells = new HashSet<long>();
        foreach (Vector3 p in searchPoints)
        {
            existingCells.Add(GridKey(p, cell));
        }

        for (float x = minX; x <= maxX; x += cell)
        {
            for (float z = minZ; z <= maxZ; z += cell)
            {
                Vector3 probe = new Vector3(x, floorY, z);
                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, cell * 0.75f, NavMesh.AllAreas) &&
                    hit.position.y <= floorY + 3f)
                {
                    long key = GridKey(hit.position, cell);
                    if (existingCells.Add(key))
                    {
                        searchPoints.Add(hit.position);
                    }
                }
            }
        }
    }

    private static long GridKey(Vector3 p, float cell)
    {
        int gx = Mathf.FloorToInt(p.x / cell);
        int gz = Mathf.FloorToInt(p.z / cell);
        return (long)gx * 100000L + gz;
    }

    private int NearestSearchPointTo(Vector3 pos)
    {
        int best = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < searchPoints.Count; i++)
        {
            if (claimed != null && i < claimed.Length && claimed[i]) continue;

            float dist = Vector3.Distance(searchPoints[i], pos);
            if (dist < bestDist)
            {
                best = i;
                bestDist = dist;
            }
        }
        return best;
    }

    private int NearestFree(SeekBot b, bool respectChecked)
    {
        if (b == null || b.tf == null) return -1;

        int best = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < searchPoints.Count; i++)
        {
            if (claimed == null || i >= claimed.Length || claimed[i]) continue;

            if (respectChecked)
            {
                if (checkedAt == null || i >= checkedAt.Length) continue;
                if (Time.time - checkedAt[i] < recheckTime) continue;
            }

            float dist = Vector3.Distance(searchPoints[i], b.tf.position);
            if (dist < bestDist)
            {
                best = i;
                bestDist = dist;
            }
        }
        return best;
    }

    private int OldestFree(SeekBot b)
    {
        if (searchPoints.Count == 0) return -1;

        int best = -1;
        float oldest = float.MaxValue;

        for (int i = 0; i < searchPoints.Count; i++)
        {
            if (claimed == null || i >= claimed.Length || claimed[i]) continue;
            if (checkedAt == null || i >= checkedAt.Length) continue;

            if (checkedAt[i] < oldest)
            {
                best = i;
                oldest = checkedAt[i];
            }
        }
        return best;
    }

    // Resolves the shared UIFont and (re)builds the status/result UI without making it visible.
    // Useful to call ahead of time (e.g. from a lobby/loading screen) so the first real SetStatusVisible
    // call doesn't pay the UI-build cost.
    public void AuthorUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }

        BuildStatusUI();
        SetStatusVisible(false);
    }

    // ====================== STATIC INIT ======================

    static BotSeekController()
    {
        AnimSpeedHash = Animator.StringToHash("Speed");
        AnimPoseHash = Animator.StringToHash("Pose");
        AnimFireHash = Animator.StringToHash("GunFire");
        BaseColorPropId = Shader.PropertyToID("_BaseColor");
    }
}