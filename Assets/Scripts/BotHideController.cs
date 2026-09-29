using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public class BotHideController : MonoBehaviour
{
    // ====================== NESTED TYPES ======================

    [Serializable]
    public class Bot
    {
        public Transform tf;
        public Animator anim;
        public SkinnedMeshRenderer smr;
        public CharacterController cc;
        public Ragdoll ragdoll;
        public Vector3 originPos;
        public Quaternion originRot;
        public Color originColor;
        public Vector3 originScale;
        public float pivotToFeet;
    }

    private class Cand
    {
        public Vector3 spot;
        public float score;
    }

    // ====================== TUNABLES ======================

    public float walkTimeout = 8f;
    public float arriveDistance = 1.5f;
    public float hideDelay = 30f;
    public float walkSpeed = 10f;
    public float wallHideChance = 0.5f;
    public float wallHideMinHeight = 6f;
    public float minHideSeparation = 20f;
    public float spotRandomness = 2.5f;
    public float wallHideMaxHeight = 16f;
    public float wallClingOffset = 2f;
    public float seekTime = 60f;
    public bool autoStartIfStandalone = true;
    public float botScaleInMode1 = 0.5f;

    public string roomRootName = "Room";
    public string playerName = "Player";
    public string[] botNames = { "Bot1", "Bot2", "Bot3", "Bot4" };

    // ====================== RUNTIME STATE ======================

    public bool armed;
    public bool hideCountdown;
    public bool triggered;
    public bool seeking;
    public bool over;
    public bool scaleBots;
    public float timer;
    public float seekTimer;
    public int hiderTotal;
    private int _lastHideSec = -1;

    public List<Bot> bots;
    public List<Renderer> hideObjects;
    public List<Renderer> climbSurfaces;

    private LevelManager levelMgr;
    private BotManager botMgr;
    private GameModeManager gmm;
    private HideModeResultUI resultUI;
    public HideModeResultUI hideUI; // used by AddSeekTime's reward callback

    private Font font;
    private GameObject statusCanvas;
    private Text statusText;

    private RaycastHit[] _rayHits;
    private Dictionary<Collider, bool> _isCharacterCache;
    private Dictionary<Material, Color> _bgColorCache;
    private Texture2D _camoBlock;

    private NavMeshData _navData;
    private NavMeshDataInstance _navInstance;

    // State used across the AssignAndHideRoutine coroutine (mirrors the compiler's
    // <>d__65 state-machine fields _pPos_5__2 / _eye_5__3 / _fwd_5__4 / _botY_5__5 / _cands_5__6).
    private Vector3 _pPos, _eye, _fwd;
    private float _botY;
    private List<Cand> _cands;

    // Animator/shader hashes, resolved once in the static constructor.
    private static int s_speedHash;
    private static int s_poseHash;
    private static int s_baseColorPropId;

    static BotHideController()
    {
        s_speedHash = Animator.StringToHash("Speed");
        s_poseHash = Animator.StringToHash("Pose");
        s_baseColorPropId = Shader.PropertyToID("_BaseColor");
    }

    public BotHideController()
    {
        climbSurfaces = new List<Renderer>();
        bots = new List<Bot>();
        hideObjects = new List<Renderer>();
        _rayHits = new RaycastHit[32];
        _isCharacterCache = new Dictionary<Collider, bool>();
        _bgColorCache = new Dictionary<Material, Color>();
    }

    // ====================== PROPERTIES ======================

    public bool Active => armed;

    public int TimerSeconds
    {
        get
        {
            float t = seeking ? seekTimer : timer;
            return (int)Math.Max(0f, t);
        }
    }

    public float TimerFraction
    {
        get
        {
            float total = seeking ? seekTime : hideDelay;
            float t = seeking ? seekTimer : timer;
            return Mathf.Clamp01(t / Mathf.Max(total, 0.01f));
        }
    }

    public int FugitivesLeft => bots.Count;

    public int HuntersLeft => 1;

    public int BotCount
    {
        get
        {
            int count = 0;
            foreach (var b in bots)
            {
                if (b?.tf == null) break;
                GameObject go = b.tf.gameObject;
                if (!go.activeSelf) continue;

                if (b.ragdoll == null)
                {
                    b.ragdoll = go.GetComponent<Ragdoll>();
                }

                if (b.ragdoll == null || !b.ragdoll.IsRagdolling)
                {
                    count++;
                }
            }
            return count;
        }
    }

    // ====================== SETUP ======================

    public void SetBots(string[] names)
    {
        StopAllCoroutines();
        botNames = names ?? Array.Empty<string>();
        triggered = false;
        ResolveBots();

        foreach (var bot in bots)
        {
            if (bot?.tf == null) continue;

            var children = bot.tf.GetComponentsInChildren<Transform>(true);
            foreach (var child in children)
            {
                if (child.name == "Cards")
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                    break;
                }
            }

            if (bot.anim != null)
            {
                for (int i = 0; i < bot.anim.layerCount; i++)
                {
                    if (bot.anim.GetLayerName(i) == "Wall")
                    {
                        bot.anim.SetLayerWeight(i, 0f);
                        break;
                    }
                }
            }
        }
    }

    public void ResolveBots()
    {
        bots.Clear();

        if (botNames == null) return;

        foreach (var name in botNames)
        {
            GameObject go = GameObject.Find(name);
            if (go == null)
            {
                Debug.LogWarning("[BotHide] room '" + name + "' not found.");
                continue;
            }

            SkinnedMeshRenderer smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);

            Color originColor;
            if (smr != null && smr.sharedMaterial != null && smr.sharedMaterial.HasProperty(s_baseColorPropId))
            {
                originColor = smr.sharedMaterial.GetColor(s_baseColorPropId);
            }
            else
            {
                originColor = Color.white;
            }

            float pivotToFeet = 0f;
            if (smr != null)
            {
                pivotToFeet = go.transform.position.y - (smr.bounds.center.y - smr.bounds.extents.y);
            }

            var bot = new Bot
            {
                tf = go.transform,
                anim = go.GetComponentInChildren<Animator>(true),
                smr = smr,
                cc = go.GetComponent<CharacterController>(),
                ragdoll = go.GetComponent<Ragdoll>(),
                originPos = go.transform.position,
                originRot = go.transform.rotation,
                originColor = originColor,
                originScale = go.transform.localScale,
                pivotToFeet = pivotToFeet,
            };

            bots.Add(bot);
        }
    }

    void Start()
    {
        botMgr = UnityEngine.Object.FindFirstObjectByType<BotManager>();
        gmm = UnityEngine.Object.FindFirstObjectByType<GameModeManager>();
        resultUI = UnityEngine.Object.FindFirstObjectByType<HideModeResultUI>();
        levelMgr = UnityEngine.Object.FindFirstObjectByType<LevelManager>();

        if (botMgr == null)
        {
            ResolveBots();
        }

        StartCoroutine(InitializeRoomWhenReady());

        font = UIFont.Default;
        EnsureEventSystem();
        BuildStatusUI();

        timer = hideDelay;
        armed = autoStartIfStandalone && gmm == null;

        if (statusCanvas != null)
        {
            statusCanvas.SetActive(armed);
        }

        Debug.Log(
            "[BotHide] bots=" + bots.Count +
            " hideObjects=" + hideObjects.Count +
            " armed=" + armed.ToString());
    }

    // ====================== SCENE BUILDING ======================

    private IEnumerator InitializeRoomWhenReady()
    {
        while (levelMgr == null ||
            levelMgr.CurrentRoomInstance == null)
        {
            if (levelMgr == null)
            {
                levelMgr =
                    UnityEngine.Object.FindFirstObjectByType<LevelManager>();
            }

            yield return null;
        }

        BuildHideObjects();
        BuildClimbSurfaces();
        EnsureRoomColliders();
        BakeNavMesh();

        Debug.Log(
            "[BotHide] Room initialization complete: " +
            levelMgr.CurrentRoomInstance.name
        );
    }

    private GameObject GetCurrentRoom()
    {
        if (levelMgr == null)
        {
            levelMgr =
                UnityEngine.Object.FindFirstObjectByType<LevelManager>();
        }

        if (levelMgr != null &&
            levelMgr.CurrentRoomInstance != null)
        {
            return levelMgr.CurrentRoomInstance;
        }

        return null;
    }

    public void BuildHideObjects()
    {
        hideObjects.Clear();

        GameObject room = GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[BotHide] room '" + roomRootName + "' not found.");
            return;
        }

        string[] excludedNames = { "Wallpaper", "Floor", "Cards", "Window", "Door", "Rug", "Ceil", "Wall" };

        var renderers = room.GetComponentsInChildren<MeshRenderer>();
        foreach (var r in renderers)
        {
            Bounds b = r.bounds;
            float largestHorizontalExtent = Mathf.Max(b.extents.x * 2f, b.extents.z * 2f);

            bool isLargeSurface = largestHorizontalExtent > 22f
                || (b.extents.y * 2f < 3f);

            bool skip = largestHorizontalExtent < 2f || isLargeSurface;
            if (!skip)
            {
                bool nameMatches = false;
                foreach (var n in excludedNames)
                {
                    if (r.gameObject.name.Contains(n))
                    {
                        nameMatches = true;
                        break;
                    }
                }
                if (!nameMatches)
                {
                    hideObjects.Add(r);
                }
            }
        }
    }

    public void BuildClimbSurfaces()
    {
        climbSurfaces.Clear();

        GameObject room = GetCurrentRoom();
        if (room == null) return;

        string[] excludedNames = { "Floor", "Ceil", "Rug", "Cards", "Roof" };

        var renderers = room.GetComponentsInChildren<MeshRenderer>();
        foreach (var r in renderers)
        {
            Bounds b = r.bounds;
            if (b.extents.y * 2f >= wallHideMinHeight + 3f)
            {
                bool nameMatches = false;
                foreach (var n in excludedNames)
                {
                    if (r.gameObject.name.Contains(n))
                    {
                        nameMatches = true;
                        break;
                    }
                }
                if (!nameMatches)
                {
                    climbSurfaces.Add(r);
                }
            }
        }
    }

    public void EnsureRoomColliders()
    {
        GameObject room = GetCurrentRoom();
        if (room == null) return;

        var renderers = room.GetComponentsInChildren<MeshRenderer>();
        foreach (var r in renderers)
        {
            if (r.GetComponent<Collider>() != null) continue;

            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            if (!mf.sharedMesh.isReadable) continue;
            if (mf.sharedMesh.vertexCount <= 0) continue;

            r.gameObject.AddComponent<MeshCollider>();
        }

        StairRampBuilder.EnsureRampsForRoom(room);
    }

    public void BakeNavMesh()
    {
        GameObject room = GetCurrentRoom();
        if (room == null) return;

        Vector3 roomPos = room.transform.position;
        Bounds bounds = new Bounds(roomPos, Vector3.zero);

        var renderers = room.GetComponentsInChildren<Renderer>();
        bool any = false;
        foreach (var r in renderers)
        {
            if (!any)
            {
                bounds = r.bounds;
                any = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }

        bounds.Expand(12f);

        NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
        settings.agentRadius = 2.5f;
        settings.agentHeight = 12f;
        settings.agentClimb = 4f;
        settings.agentSlope = 45f;

        var markups = new List<NavMeshBuildMarkup>();
        var sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(room.transform, -1, NavMeshCollectGeometry.RenderMeshes, 0, markups, sources);

        NavMeshData navData = NavMeshBuilder.BuildNavMeshData(
            settings, sources, bounds, Vector3.zero, Quaternion.identity);

        _navData = navData;

        if (_navInstance.valid)
        {
            NavMesh.RemoveNavMeshData(_navInstance);
        }
        _navInstance = NavMesh.AddNavMeshData(_navData);

        Debug.Log("[BotHide] NavMesh baked: sources=" + sources.Count + " valid=" + _navInstance.valid.ToString());
    }

    public void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null)
        {
            return;
        }

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();

        Type inputSystemUiType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (inputSystemUiType != null)
        {
            go.AddComponent(inputSystemUiType);
        }
        else
        {
            go.AddComponent<StandaloneInputModule>();
        }
    }

    public void BuildStatusUI()
    {
        bool createdCanvas;
        bool createdText;

        statusCanvas = SceneUI.GetOrCreateCanvas("HideStatusCanvas", out createdCanvas);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(statusCanvas);

        if (createdCanvas)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9;

            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(statusCanvas);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }

        SceneUI.GetOrAdd<GraphicRaycaster>(statusCanvas);

        Transform parent = statusCanvas.transform;
        GameObject textGo = SceneUI.GetOrCreate("HideStatus", parent, out createdText);
        statusText = SceneUI.GetOrAdd<Text>(textGo);

        statusText.font = font;
        statusText.raycastTarget = false;

        if (createdText)
        {
            statusText.text = "";
            statusText.fontSize = 40;
            statusText.alignment = TextAnchor.MiddleCenter;
            statusText.color = Color.white;
            statusText.horizontalOverflow = HorizontalWrapMode.Overflow;
            statusText.verticalOverflow = VerticalWrapMode.Overflow;

            RectTransform rt = statusText.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, -90f);
            rt.sizeDelta = new Vector2(600f, 60f);
        }
    }

    public void AuthorUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }
        EnsureEventSystem();
        BuildStatusUI();
    }

    // ====================== MAIN LOOP ======================

    void Update()
    {
        if (!armed) return;

        if (hideCountdown)
        {
            timer -= Time.deltaTime;
            int shownSeconds = (int)Math.Max(0f, timer);

            if (statusText != null && shownSeconds != _lastHideSec)
            {
                statusText.text = "WAIT! Hiders are hiding... " + shownSeconds + "s";
                _lastHideSec = shownSeconds;
            }

            if (timer <= 0f)
            {
                BeginSeekPhase();
            }
        }
        else if (seeking && !over)
        {
            if (BotCount < 1)
            {
                Win();
                return;
            }

            seekTimer -= Time.deltaTime;

            if (gmm != null && gmm.PlayerIsSeeker)
            {
                AudioManager.Countdown(seekTimer);
            }

            if (seekTimer <= 0f)
            {
                TimeUp();
            }
        }
    }

    public void BeginSeekPhase()
    {
        hideCountdown = false;
        seeking = true;
        over = false;
        seekTimer = seekTime;

        hiderTotal = bots.Count;

        if (gmm != null && gmm.PlayerIsSeeker)
        {
            AudioManager.PhaseSeek();
        }

        if (statusText != null)
        {
            statusText.text = "";
        }
    }

    public void Win()
    {
        seeking = false;
        over = true;

        if (statusText != null)
        {
            statusText.text = "";
        }

        if (gmm == null || !gmm.PlayerIsSeeker)
        {
            if (resultUI != null)
            {
                int timeLeft = (int)Math.Max(0f, seekTimer);
                resultUI.ShowVictorySeek("#1/8", timeLeft, hiderTotal);
            }
        }
        else
        {
            if (statusText != null)
            {
                statusText.text = "ALL FOUND!  You win.";
            }
        }

        Debug.Log("[BotHide] All hiders found — WIN.");
    }

    public void TimeUp()
    {
        seeking = false;
        over = true;
        seekTimer = 0f;

        if (statusText != null)
        {
            statusText.text = "";
        }

        if (gmm == null || !gmm.PlayerIsSeeker)
        {
            if (resultUI != null)
            {
                resultUI.ShowTimeUp(AddSeekTime, ShowSeekDefeat);
            }
        }
        else
        {
            if (statusText != null)
            {
                statusText.text = "TIME'S UP!";
            }
        }

        Debug.Log("[BotHide] Time's up.");
    }

    // ====================== HIDE-PHASE COROUTINE ======================

    public void StartHiding()
    {
        if (triggered) return;
        triggered = true;
        StartCoroutine(AssignAndHideRoutine());
    }

    private IEnumerator AssignAndHideRoutine()
    {
        GameObject player = PlayerRef.Resolve(playerName);
        _pPos = player != null ? player.transform.position : Vector3.zero;

        Camera cam = Camera.main;
        _eye = cam != null ? cam.transform.position : _pPos + Vector3.up * 4f;
        _fwd = cam != null ? cam.transform.forward
            : (player != null ? player.transform.forward : Vector3.forward);

        _botY = bots.Count > 0 && bots[0]?.tf != null ? bots[0].tf.position.y : -1.1f;

        _cands = new List<Cand>();

        // Score all fixed hide-objects, budgeting ~4ms of work per frame so this doesn't
        // cause a hitch on rooms with a lot of renderers.
        float frameStart = Time.realtimeSinceStartup;
        foreach (var hideObj in hideObjects)
        {
            if (hideObj == null) continue;

            Cand c = ScoreObject(hideObj, _pPos, _eye, _fwd, _botY);
            _cands.Add(c);

            if (Time.realtimeSinceStartup - frameStart > 0.004f)
            {
                yield return null;
                frameStart = Time.realtimeSinceStartup;
            }
        }

        AddGridHideCands(_cands, _pPos, _eye, _fwd, _botY);

        // Sort descending by score (best spots first) — the actual comparison delegate
        // body (<AssignAndHideRoutine>b__65_0) wasn't in the pasted pseudocode, so the
        // sort direction is inferred from AssignFromCands' "take the first qualifying
        // candidate" behavior; flag if bots end up in poor spots first.
        _cands.Sort((a, b) => b.score.CompareTo(a.score));

        var assignments = AssignFromCands(_cands);

        foreach (var kv in assignments)
        {
            Bot bot = kv.Key;
            Vector3 groundSpot = kv.Value;

            bool hidOnWall = false;

            if (climbSurfaces.Count > 0 && UnityEngine.Random.value < wallHideChance)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    int idx = UnityEngine.Random.Range(0, climbSurfaces.Count);
                    Renderer surf = climbSurfaces[idx];

                    if (TryWallSpot(surf, _pPos, _botY, out Vector3 wallSpot, out Quaternion wallRot, out Color wallCamo))
                    {
                        TeleportAndHideWall(bot, wallSpot, wallRot, wallCamo);
                        hidOnWall = true;
                        break;
                    }
                }
            }

            if (!hidOnWall)
            {
                TeleportAndHide(bot, groundSpot);
            }
        }
    }

    // ====================== TELEPORT / CAMOUFLAGE ======================

    public void TeleportAndHide(Bot bot, Vector3 spot)
    {
        if (bot?.tf == null) return;

        if (bot.cc != null) bot.cc.enabled = false;
        bot.tf.position = spot;
        if (bot.anim != null) bot.anim.SetFloat(s_speedHash, 0f);
        if (bot.cc != null) bot.cc.enabled = true;

        Camouflage(bot);
    }

    public void Camouflage(Bot bot)
    {
        if (bot?.tf == null) return;

        Vector3 botPos = bot.tf.position;
        Color camo = SampleContactColor(botPos, null);

        if (bot.smr != null)
        {
            Material mat = bot.smr.material;
            mat.SetColor(s_baseColorPropId, camo);
        }

        if (bot.anim != null)
        {
            int pose = UnityEngine.Random.Range(1, 5);
            bot.anim.SetInteger(s_poseHash, pose);
        }

        if (botMgr != null)
        {
            botMgr.SetTagVisible(bot.tf, false);
        }
    }

    public void TeleportAndHideWall(Bot bot, Vector3 spot, Quaternion rot, Color camo)
    {
        if (bot?.tf == null) return;

        if (bot.cc != null) bot.cc.enabled = false;
        bot.tf.SetPositionAndRotation(spot, rot);

        if (bot.anim != null)
        {
            bot.anim.SetFloat(s_speedHash, 0f);
            bot.anim.SetInteger(s_poseHash, 1);
        }

        if (bot.cc != null) bot.cc.enabled = true;

        if (bot.smr != null)
        {
            Material mat = bot.smr.material;
            mat.SetColor(s_baseColorPropId, camo);
        }

        if (botMgr != null)
        {
            botMgr.SetTagVisible(bot.tf, false);
        }
    }

    // ====================== CHARACTER / RAYCAST HELPERS ======================

    public bool IsCharacter(Collider col)
    {
        if (col == null) return false;

        if (_isCharacterCache.TryGetValue(col, out bool cached))
        {
            return cached;
        }

        bool isChar = col.GetComponentInParent<Animator>() != null;
        _isCharacterCache[col] = isChar;
        return isChar;
    }

    // ====================== WALL / CLIMB-SURFACE HIDE SPOT SEARCH ======================

    public bool TryWallSpot(Renderer surf, Vector3 pPos, float botY,
        out Vector3 spot, out Quaternion rot, out Color camo)
    {
        spot = Vector3.zero;
        rot = Quaternion.identity;
        camo = Color.gray;

        if (surf == null) return false;

        Bounds b = surf.bounds;
        float minY = Mathf.Max(wallHideMinHeight + botY, b.center.y - b.extents.y + 2f);
        float maxY = Mathf.Min(wallHideMaxHeight + botY, b.center.y + b.extents.y - 2f);
        if (minY >= maxY) return false;

        float y = UnityEngine.Random.Range(minY, maxY);

        Vector2 toWall = new Vector2(pPos.x - b.center.x, pPos.z - b.center.z);
        Vector2 dirToWall = toWall.sqrMagnitude >= 0.01f ? toWall.normalized : Vector2.right;

        bool useZAxis = Mathf.Abs(dirToWall.y) > 0.5f;
        float faceHalfWidth = useZAxis ? b.extents.z : b.extents.x;
        Vector3 rightAxis = useZAxis ? Vector3.forward : Vector3.right;

        float offsetAlongFace = UnityEngine.Random.Range(faceHalfWidth * -0.7f, faceHalfWidth * 0.7f);

        Vector3 rayOrigin = new Vector3(
            b.center.x + dirToWall.x * 4f + dirToWall.x * faceHalfWidth + rightAxis.x * offsetAlongFace,
            y + rightAxis.y * offsetAlongFace,
            b.center.z + dirToWall.y * 4f + dirToWall.y * faceHalfWidth + rightAxis.z * offsetAlongFace);
        Vector3 rayDir = new Vector3(-dirToWall.x, 0f, -dirToWall.y);

        int hitCount = Physics.RaycastNonAlloc(rayOrigin, rayDir, _rayHits, 8f);
        if (!TryGetClosestNonCharacterHit(hitCount, out RaycastHit hit)) return false;

        Vector3 hitNormal = hit.normal;
        Vector2 flatNormal = new Vector2(hitNormal.x, hitNormal.z);
        Vector2 clingDir = flatNormal.sqrMagnitude >= 0.01f ? flatNormal.normalized : Vector2.zero;

        Vector3 hitPoint = hit.point;
        spot = new Vector3(
            hitPoint.x + clingDir.x * wallClingOffset,
            y,
            hitPoint.z + clingDir.y * wallClingOffset);

        int confirmHits = Physics.RaycastNonAlloc(
            new Vector3(spot.x, y, spot.z),
            new Vector3(clingDir.x, 0f, clingDir.y),
            _rayHits, 3f);

        if (confirmHits > 0)
        {
            for (int i = 0; i < confirmHits; i++)
            {
                Collider c = _rayHits[i].collider;
                if (c is CharacterController) continue;
                if (!IsCharacter(c))
                {
                    if (!Bullet.BlocksShot(c.gameObject)) continue;
                }
            }
        }

        rot = Quaternion.LookRotation(new Vector3(clingDir.x, 0f, clingDir.y), Vector3.up);
        camo = SampleContactColor(spot, surf);
        return true;
    }

    private bool TryGetClosestNonCharacterHit(int hitCount, out RaycastHit result)
    {
        result = default;
        if (hitCount <= 0) return false;

        float closest = float.MaxValue;
        bool found = false;

        for (int i = 0; i < hitCount; i++)
        {
            Collider c = _rayHits[i].collider;
            if (c is CharacterController) continue;
            if (IsCharacter(c)) continue;

            float d = _rayHits[i].distance;
            if (d < closest)
            {
                closest = d;
                result = _rayHits[i];
                found = true;
            }
        }

        return found;
    }

    // ====================== SEEKING (BEING SHOT AT) ======================

    public bool TryShoot(GameObject hitGo)
    {
        if (!seeking || over) return false;
        if (hitGo == null) return false;

        Transform hitTf = hitGo.transform;

        foreach (var b in bots)
        {
            if (b?.tf == null) continue;
            if (!b.tf.gameObject.activeSelf) continue;
            if (b.ragdoll != null && b.ragdoll.IsRagdolling) continue;

            bool matches = b.tf.gameObject == hitGo || hitTf.IsChildOf(b.tf);
            if (!matches) continue;

            Vector3 impulseDir;
            Camera cam = Camera.main;
            if (cam != null)
            {
                impulseDir = cam.transform.forward;
            }
            else
            {
                impulseDir = b.tf.forward;
            }

            KillHider(b, impulseDir);
            return true;
        }

        return false;
    }

    public void KillHider(Bot b, Vector3 impulseDir)
    {
        if (b == null) return;

        Bounds bounds;
        Vector3 ragdollOrigin;
        if (b.smr != null)
        {
            bounds = b.smr.bounds;
            ragdollOrigin = bounds.center;
        }
        else if (b.tf != null)
        {
            ragdollOrigin = b.tf.position + Vector3.up * 6f;
        }
        else
        {
            return;
        }

        b.ragdoll = Ragdoll.Trigger(b.tf, ragdollOrigin, impulseDir, 1f, true);

        if (b.ragdoll == null)
        {
            if (b.tf != null)
            {
                b.tf.gameObject.SetActive(false);
            }
        }

        if (botMgr != null && b.tf != null)
        {
            botMgr.SetTagVisible(b.tf, false);
        }

        int remaining = BotCount;
        Debug.Log("[BotHide] Hider shot: " + (b.tf != null ? b.tf.name : "") + " (" + remaining + " left)");
    }

    public bool TryShootAim(Vector3 origin, Vector3 dir, float maxRange, float aimAssist)
    {
        if (!seeking || over) return false;
        if (dir.sqrMagnitude < 1e-6f) return false;

        Vector3 aimDir = dir.normalized;
        float assist = Mathf.Max(0f, aimAssist);

        Bot best = null;
        float bestT = float.MaxValue;

        foreach (var b in bots)
        {
            if (b?.tf == null) continue;
            if (!b.tf.gameObject.activeSelf) continue;
            if (b.ragdoll != null && b.ragdoll.IsRagdolling) continue;

            Bounds box;
            if (b.smr != null)
            {
                box = b.smr.bounds;
            }
            else
            {
                box = new Bounds(b.tf.position, new Vector3(4f, 8f, 4f));
            }
            box.Expand(assist);

            if (!box.IntersectRay(new Ray(origin, aimDir), out float _)) continue;

            Vector3 toCenter = box.center - origin;
            float t = Vector3.Dot(aimDir, toCenter);
            if (t <= 1f || t > maxRange) continue;
            if (toCenter.magnitude * 0.5f > t) continue;
            if (t >= bestT) continue;

            if (WallBetween(origin, box.center)) continue;

            best = b;
            bestT = t;
        }

        if (best != null)
        {
            KillHider(best, aimDir);
            return true;
        }

        return false;
    }

    public bool WallBetween(Vector3 origin, Vector3 target)
    {
        Vector3 toTarget = target - origin;
        float dist = toTarget.magnitude;
        if (dist < 0.5f) return false;

        Vector3 dir = toTarget / dist;
        int hitCount = Physics.RaycastNonAlloc(origin, dir, _rayHits, dist - 0.5f);

        for (int i = 0; i < hitCount; i++)
        {
            Collider c = _rayHits[i].collider;
            if (c is CharacterController) continue;
            if (IsCharacter(c)) continue;

            if (Bullet.BlocksShot(c.gameObject))
            {
                return true;
            }
        }

        return false;
    }

    // ====================== REWARDED-AD SEEK-TIME EXTENSION ======================

    public void AddSeekTime()
    {
        if (AdMgr.Instance == null) return;
        if (!AdMgr.Instance.IsRewardReady) return;

        AdMgr.Instance.OnRewardView(OnAddSeekTimeRewardSuccess, RewardFail);
    }

    // CORRECTION: the decompile wraps this entire body in `if (hideUI != null) { ... } else
    // throw;` — the earlier draft only null-checked the HideAll() call and let everything else
    // (seeking/over/seekTimer/RootManager.SetNumber/the log) run unconditionally even when
    // hideUI was null. Natural crash-on-null on the first line reproduces the real behavior.
    private void OnAddSeekTimeRewardSuccess()
    {
        hideUI.HideAll(); // throws naturally if null — matches the decompile's explicit throw
        seeking = true;
        over = false;
        seekTimer += 30f;
        RootManager.Instance?.SetNumber(1);
        Debug.Log("[BotHide] +30s — keep hunting!");
    }

    public void RewardFail()
    {
    }

    // CORRECTION: uses a different label string than Win() — confirmed via string literal
    // ("#5/8" here vs "#1/8" in Win()). The earlier draft used "#1/8" for both.
    public void ShowSeekDefeat()
    {
        if (resultUI != null)
        {
            int found = hiderTotal - BotCount;
            resultUI.ShowDefeatSeek("#5/8", 0, found);
        }
    }

    // ====================== SCALE-DOWN MODE ======================

    public void ApplyBotScale(Bot b)
    {
        if (botScaleInMode1 >= 0.999f) return;
        if (b == null) return;

        float pivotToFeet;
        if (b.smr != null && b.tf != null)
        {
            Bounds bounds = b.smr.bounds;
            pivotToFeet = b.tf.position.y - (bounds.center.y - bounds.extents.y);
        }
        else
        {
            pivotToFeet = b.pivotToFeet;
        }

        if (b.tf == null) return;

        b.tf.localScale = b.originScale * botScaleInMode1;

        Vector3 pos = b.tf.position;
        pos.y -= pivotToFeet * (1f - botScaleInMode1);
        b.tf.position = pos;
    }

    // ====================== RESET / STOP ======================

    public void ResetBots()
    {
        if (botMgr != null)
        {
            botMgr.SetAllTagsVisible(true);
        }

        foreach (var b in bots)
        {
            if (b?.tf == null) continue;

            if (b.ragdoll != null)
            {
                b.ragdoll.Deactivate();
            }

            if (b.cc != null) b.cc.enabled = false;

            b.tf.localScale = b.originScale;
            b.tf.SetPositionAndRotation(b.originPos, b.originRot);

            if (scaleBots)
            {
                ApplyBotScale(b);
            }

            if (b.cc != null) b.cc.enabled = true;

            if (b.anim != null)
            {
                b.anim.SetFloat(s_speedHash, 0f);
                b.anim.SetInteger(s_poseHash, 0);
            }

            if (b.smr != null)
            {
                Material mat = b.smr.material;
                mat.SetColor(s_baseColorPropId, b.originColor);
            }
        }
    }

    public void BeginCountdown()
    {
        StopAllCoroutines();
        BuildHideObjects();
        BuildClimbSurfaces();
        scaleBots = true;
        ResetBots();

        seeking = false;
        over = false;
        armed = true;
        hideCountdown = true;
        triggered = false;
        timer = hideDelay;

        if (statusCanvas != null)
        {
            bool showCanvas = gmm == null || gmm.PlayerIsSeeker;
            statusCanvas.SetActive(showCanvas);
        }

        if (statusText != null)
        {
            int shown = (int)Math.Max(0f, hideDelay);
            statusText.text = "WAIT! Hiders are hiding... " + shown + "s";
        }

        if (gmm != null && gmm.PlayerIsSeeker)
        {
            AudioManager.ResetCountdown();
            AudioManager.PhaseStart();
        }

        StartHiding();
    }

    public void StopMode()
    {
        StopAllCoroutines();
        armed = false;
        hideCountdown = false;
        triggered = false;
        seeking = false;
        over = false;
        scaleBots = false;
        ResetBots();

        if (statusCanvas != null) statusCanvas.SetActive(false);
        if (statusText != null) statusText.text = "";
        if (resultUI != null) resultUI.HideAll();
    }

    // ====================== SPOT-PICKING SYSTEM ======================

    public List<KeyValuePair<Bot, Vector3>> AssignSpots()
    {
        GameObject player = PlayerRef.Resolve(playerName);
        float botY = player != null ? player.transform.position.y : 0f;

        Camera cam = Camera.main;
        // CORRECTION: confirmed via trace — when there's no main camera, the decompile's fallback
        // for `eye` is NOT simply zero. It reuses a leftover Y value from the player-position
        // lookup above (botY) and produces (x=0, y=botY, z=4) via variable-slot reuse. Almost
        // certainly an unintentional quirk in the original source (the "*4" strongly suggests the
        // intent was something like "player position + forward*4"), but kept exact since that's
        // what actually runs. The earlier draft had a plain (0,0,0) fallback.
        Vector3 eye = cam != null ? cam.transform.position : new Vector3(0f, botY, 4f);
        Vector3 fwd = cam != null ? cam.transform.forward
            : (player != null ? player.transform.forward : Vector3.forward);

        Vector3 pPos = bots.Count > 0 && bots[0]?.tf != null ? bots[0].tf.position : Vector3.zero;

        var cands = new List<Cand>();

        foreach (var hideObj in hideObjects)
        {
            Cand c = ScoreObject(hideObj, pPos, eye, fwd, botY);
            if (c != null) cands.Add(c);
        }
        AddGridHideCands(cands, pPos, eye, fwd, botY);

        cands.Sort((a, b) => b.score.CompareTo(a.score));

        return AssignFromCands(cands);
    }

    private Cand ScoreObject(Renderer o, Vector3 pPos, Vector3 eye, Vector3 fwd, float botY)
    {
        Vector3 spot = HideSpotBehind(o, pPos, botY);
        Color camo = SampleContactColor(spot, o);
        float score = ScoreSpot(spot, camo, pPos, eye, fwd);
        float jitter = UnityEngine.Random.value * Mathf.Max(0f, spotRandomness);

        return new Cand { spot = spot, score = score + jitter };
    }

    private void AddGridHideCands(List<Cand> cands, Vector3 pPos, Vector3 eye, Vector3 fwd, float botY)
    {
        if (cands.Count >= bots.Count + 3) return;

        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        if (tri.vertices == null || tri.vertices.Length == 0) return;

        float minY = float.MaxValue;
        foreach (var v in tri.vertices)
        {
            if (v.y < minY) minY = v.y;
        }

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        bool any = false;
        foreach (var v in tri.vertices)
        {
            if (v.y > minY + 5f) continue;
            any = true;
            if (v.x < minX) minX = v.x;
            if (v.x > maxX) maxX = v.x;
            if (v.z < minZ) minZ = v.z;
            if (v.z > maxZ) maxZ = v.z;
        }
        if (!any) return;

        for (float x = minX; x <= maxX; x += 30f)
        {
            for (float z = minZ; z <= maxZ; z += 30f)
            {
                Vector3 sample = new Vector3(x, minY, z);
                if (!NavMesh.SamplePosition(sample, out NavMeshHit hit, 22f, -1)) continue;
                if (hit.position.y > minY + 5f) continue;

                Vector3 spot = new Vector3(hit.position.x, botY, hit.position.z);
                Color camo = ColorBehind(spot, eye);
                float score = ScoreSpot(spot, camo, pPos, eye, fwd) - 0.5f;

                cands.Add(new Cand { spot = spot, score = score });
            }
        }
    }

    private List<KeyValuePair<Bot, Vector3>> AssignFromCands(List<Cand> cands)
    {
        var result = new List<KeyValuePair<Bot, Vector3>>();
        var usedSpots = new List<Vector3>();
        float minSeparation = Mathf.Max(0f, minHideSeparation);

        foreach (var b in bots)
        {
            Cand chosen = null;

            foreach (var c in cands)
            {
                if (c.score < 0f) continue; // "used" flag stored in-place in original (sign bit reuse)
                if (MinDistanceTo(c.spot, usedSpots) < minSeparation) continue;
                chosen = c;
                break;
            }

            if (chosen == null)
            {
                float bestDist = -1f;
                foreach (var c in cands)
                {
                    if (c.score < 0f) continue;
                    float d = MinDistanceTo(c.spot, usedSpots);
                    if (d > bestDist)
                    {
                        bestDist = d;
                        chosen = c;
                    }
                }
                if (chosen == null) break;
            }

            chosen.score = -1f; // mark used
            usedSpots.Add(chosen.spot);
            result.Add(new KeyValuePair<Bot, Vector3>(b, chosen.spot));
        }

        return result;
    }

    // ====================== COLOR / RAYCAST SAMPLING HELPERS ======================

    public Color ColorBehind(Vector3 spot, Vector3 eye)
    {
        Vector3 toEye = new Vector3(spot.x - eye.x, 0f, spot.z - eye.z);
        if (toEye.sqrMagnitude < 0.01f) return Color.gray;

        Vector3 dir = toEye.normalized;
        Vector3 origin = spot + dir * 1.5f + Vector3.up * 6f;

        int hitCount = Physics.RaycastNonAlloc(origin, dir, _rayHits, 50f);
        if (!TryGetClosestNonCharacterHit(hitCount, out RaycastHit hit)) return Color.gray;

        return SampleHitColor(hit);
    }

    public Color SampleContactColor(Vector3 botPos, Renderer fallback)
    {
        // NOTE: the decompile builds this direction set as {down, forward, back, left, right} —
        // a different order than listed here — but since this method searches for the CLOSEST
        // hit across every direction rather than short-circuiting on the first, the order has no
        // effect on the result. Verified the set itself is identical.
        Vector3[] directions =
        {
            Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.down,
        };

        RaycastHit? best = null;
        float bestDist = float.MaxValue;

        foreach (var dir in directions)
        {
            Vector3 origin = botPos + Vector3.up * 1.5f;
            int hitCount = Physics.RaycastNonAlloc(origin, dir, _rayHits, 16f);
            if (!TryGetClosestNonCharacterHit(hitCount, out RaycastHit hit)) continue;

            if (hit.distance < bestDist)
            {
                bestDist = hit.distance;
                best = hit;
            }
        }

        if (best.HasValue)
        {
            return SampleHitColor(best.Value);
        }

        if (fallback != null)
        {
            return SampleObjectColor(fallback, botPos);
        }

        return Color.gray;
    }

    private Color SampleHitColor(RaycastHit hit)
    {
        Renderer r = hit.collider.GetComponent<Renderer>();
        if (r == null) r = hit.collider.GetComponentInParent<Renderer>();
        if (r == null) return Color.gray;

        Vector2 uv;
        if (hit.collider is MeshCollider)
        {
            uv = hit.textureCoord;
        }
        else
        {
            uv = Vector2.zero;
        }

        if ((uv - Vector2.zero).sqrMagnitude < 1e-10f)
        {
            uv = MeshUvCenter(r);
        }

        Material m = r.sharedMaterial;
        return SampleTexAtUV(m, uv);
    }

    public float ScoreSpot(Vector3 spot, Color camo, Vector3 pPos, Vector3 eye, Vector3 fwd)
    {
        Vector3 toSpot = spot + Vector3.up * 6f - eye;
        float dist = toSpot.magnitude;
        float visRange = Mathf.Max(0f, dist - 2f);

        Vector3 dir = toSpot / dist;
        int hitCount = Physics.RaycastNonAlloc(eye, dir, _rayHits, visRange);

        float occlusionScore = 0f;
        if (hitCount > 0)
        {
            for (int i = 0; i < hitCount; i++)
            {
                Collider c = _rayHits[i].collider;
                if (c is CharacterController) continue;
                if (!IsCharacter(c))
                {
                    occlusionScore = 1.5f;
                    break;
                }
            }
        }

        Vector3 fwdN = fwd.sqrMagnitude > 1e-10f ? fwd.normalized : Vector3.forward;
        Vector3 toSpotFlat = spot - eye;
        Vector3 toSpotN = toSpotFlat.sqrMagnitude > 1e-10f ? toSpotFlat.normalized : Vector3.forward;

        Color behindColor = ColorBehind(spot, eye);
        float colorDist = Mathf.Sqrt(
            (camo.r - behindColor.r) * (camo.r - behindColor.r) +
            (camo.g - behindColor.g) * (camo.g - behindColor.g) +
            (camo.b - behindColor.b) * (camo.b - behindColor.b)) / 0.9f;

        float distFromPlayer = Vector3.Distance(spot, pPos) / 60f;

        float distScore = Mathf.Clamp01(distFromPlayer);
        float colorScore = Mathf.Clamp01(colorDist);

        float facingDot = Vector3.Dot(fwdN, toSpotN);
        float facingPenalty = facingDot >= 0.2f ? 0f : 0.8f;

        return distScore * 0.6f + facingPenalty + occlusionScore + (1f - colorScore) * 2f;
    }

    public Vector3 HideSpotBehind(Renderer obj, Vector3 playerPos, float botY)
    {
        if (obj == null)
            return Vector3.zero;

        Transform t = obj.transform;

        if (t == null)
            return Vector3.zero;

        Vector3 objPos = t.position;

        Vector2 away = new Vector2(
            objPos.x - playerPos.x,
            objPos.z - playerPos.z);

        Vector2 dir = away.sqrMagnitude >= 0.01f
            ? away.normalized
            : Vector2.up;

        Bounds b = obj.bounds;

        float halfWidth =
            Mathf.Max(b.extents.x, b.extents.z);

        return new Vector3(
            objPos.x + dir.x * (halfWidth + 2f),
            botY,
            objPos.z + dir.y * (halfWidth + 2f));
    }

    private float MinDistanceTo(Vector3 p, List<Vector3> pts)
    {
        if (pts == null || pts.Count < 1) return float.MaxValue;

        float min = float.MaxValue;
        foreach (var pt in pts)
        {
            float d = Vector3.Distance(p, pt);
            if (d < min) min = d;
        }
        return min;
    }

    public float ColorDist(Color a, Color b)
    {
        float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
        return Mathf.Sqrt(dr * dr + dg * dg + db * db);
    }

    public Vector2 MeshUvCenter(Renderer obj)
    {
        if (obj == null) return new Vector2(0.5f, 0.5f);

        MeshFilter mf = obj.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable)
        {
            return new Vector2(0.5f, 0.5f);
        }

        Vector2[] uvs = mf.sharedMesh.uv;
        if (uvs == null || uvs.Length == 0) return new Vector2(0.5f, 0.5f);

        Vector2 min = uvs[0];
        Vector2 max = uvs[0];
        for (int i = 1; i < uvs.Length; i++)
        {
            min = Vector2.Min(min, uvs[i]);
            max = Vector2.Max(max, uvs[i]);
        }

        return (min + max) * 0.5f;
    }

    public Color SampleTexAtUV(Material m, Vector2 uv)
    {
        if (m != null && _bgColorCache.TryGetValue(m, out Color cached))
        {
            return cached;
        }

        Color tint = Color.white;
        Texture source = null;

        if (m != null)
        {
            if (m.HasProperty("_BaseColor"))
            {
                tint = m.GetColor("_BaseColor");
            }
            if (m.HasProperty("_BaseMap"))
            {
                source = m.GetTexture("_BaseMap");
            }
            if (source == null && m.HasProperty("_MainTex"))
            {
                source = m.GetTexture("_MainTex");
            }
            if (source == null)
            {
                source = m.mainTexture;
            }
        }

        Color result;

        if (source == null)
        {
            result = tint;
        }
        else
        {
            int width = Mathf.Min(source.width, 1024);
            int height = Mathf.Min(source.height, 1024);

            RenderTexture prevActive = RenderTexture.active;
            RenderTexture temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, temp);
            RenderTexture.active = temp;

            float u = uv.x - Mathf.Floor(uv.x);
            float v = uv.y - Mathf.Floor(uv.y);
            int px = Mathf.Clamp(Mathf.RoundToInt(u * (width - 1)) - 3, 0, Mathf.Max(0, width - 6));
            int py = Mathf.Clamp(Mathf.RoundToInt(v * (height - 1)) - 3, 0, Mathf.Max(0, height - 6));

            if (_camoBlock == null)
            {
                _camoBlock = new Texture2D(6, 6, TextureFormat.RGBA32, false);
            }

            _camoBlock.ReadPixels(new Rect(px, py, 6f, 6f), 0, 0);
            _camoBlock.Apply(false);

            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(temp);

            Color[] pixels = _camoBlock.GetPixels();
            float sumR = 0f, sumG = 0f, sumB = 0f;
            foreach (var p in pixels)
            {
                sumR += p.r;
                sumG += p.g;
                sumB += p.b;
            }
            int n = Mathf.Max(1, pixels.Length);

            result = new Color(tint.r * (sumR / n), tint.g * (sumG / n), tint.b * (sumB / n), tint.a);
        }

        if (m != null)
        {
            _bgColorCache[m] = result;
        }

        return result;
    }

    // ====================== WALK-TO-SPOT COROUTINE ======================
    // General-purpose: walks a bot along a computed NavMesh path toward `spot`, then
    // stops its walk animation and camouflages it. Not called from AssignAndHideRoutine
    // (which teleports instead) — likely invoked elsewhere (BotManager or an AI script
    // not yet reversed).

    public IEnumerator HideBot(Bot bot, Vector3 spot)
    {
        if (bot?.tf == null) yield break;

        Vector3[] corners = ComputePath(bot.tf.position, spot);
        int cornerIndex = 0;
        float elapsed = 0f;

        while (elapsed < walkTimeout && cornerIndex < corners.Length)
        {
            if (bot.tf == null) yield break;
            if (!bot.tf.gameObject.activeInHierarchy) yield break;

            elapsed += Time.deltaTime;

            Vector3 corner = corners[cornerIndex];
            Vector3 pos = bot.tf.position;
            float dx = corner.x - pos.x;
            float dz = corner.z - pos.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);

            if (dist > arriveDistance)
            {
                float step = walkSpeed * Time.deltaTime;
                Vector3 delta = new Vector3((dx / dist) * step, 0f, (dz / dist) * step);

                if (bot.cc != null && bot.cc.enabled)
                {
                    bot.cc.Move(delta);
                }
                else
                {
                    bot.tf.position += delta;
                }

                if (bot.anim != null)
                {
                    bot.anim.SetFloat(s_speedHash, 1f);
                }

                Vector3 facing = new Vector3(dx / dist, 0f, dz / dist);
                Quaternion targetRot = Quaternion.LookRotation(facing);
                bot.tf.rotation = Quaternion.Slerp(bot.tf.rotation, targetRot, Time.deltaTime * 12f);

                yield return null;
            }
            else
            {
                cornerIndex++;
            }
        }

        if (bot.anim != null)
        {
            bot.anim.SetFloat(s_speedHash, 0f);
        }

        Camouflage(bot);
    }

    public Vector3[] ComputePath(Vector3 from, Vector3 to)
    {
        Vector3 start = from, end = to;

        if (NavMesh.SamplePosition(from, out NavMeshHit fromHit, 18f, -1))
        {
            start = fromHit.position;
        }
        if (NavMesh.SamplePosition(to, out NavMeshHit toHit, 18f, -1))
        {
            end = toHit.position;
        }

        NavMeshPath path = new NavMeshPath();
        if (NavMesh.CalculatePath(start, end, -1, path) && path.corners != null && path.corners.Length > 0)
        {
            return path.corners;
        }

        return new[] { to };
    }

    public void DebugHideAllInstant()
    {
        var assignments = AssignSpots();
        foreach (var kv in assignments)
        {
            Bot bot = kv.Key;
            Vector3 spot = kv.Value;

            if (bot.cc != null) bot.cc.enabled = false;
            bot.tf.position = spot;
            if (bot.cc != null) bot.cc.enabled = true;

            Camouflage(bot);
        }

        triggered = true;
        if (statusText != null)
        {
            statusText.text = "";
        }
    }

    public Color SampleObjectColor(Renderer obj, Vector3 botPos)
    {
        MeshCollider ensuredCollider = EnsureObjCollider(obj);
        Vector2 uv = MeshUvCenter(obj);

        if (ensuredCollider != null && obj != null)
        {
            Bounds b = obj.bounds;
            Vector3 origin = botPos + Vector3.up * 2f;
            Vector3 toCenter = b.center - origin;
            float dist = toCenter.magnitude;
            Vector3 dir = toCenter / dist;

            int hitCount = Physics.RaycastNonAlloc(origin, dir, _rayHits, dist + 6f);
            float closest = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                if (_rayHits[i].collider == (Collider)ensuredCollider)
                {
                    if (_rayHits[i].distance < closest)
                    {
                        closest = _rayHits[i].distance;
                        uv = _rayHits[i].textureCoord;
                    }
                }
            }
        }

        Material m = obj != null ? obj.sharedMaterial : null;
        return SampleTexAtUV(m, uv);
    }

    public MeshCollider EnsureObjCollider(Renderer obj)
    {
        if (obj == null) return null;

        MeshCollider existing = obj.GetComponent<MeshCollider>();
        if (existing != null) return existing;

        MeshFilter mf = obj.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return null;
        if (!mf.sharedMesh.isReadable) return null;
        if (mf.sharedMesh.vertexCount < 1) return null;

        return obj.gameObject.AddComponent<MeshCollider>();
    }

    // ====================== CLEANUP ======================

    void OnDestroy()
    {
        if (_navInstance.valid)
        {
            NavMesh.RemoveNavMeshData(_navInstance);
        }

        if (_camoBlock != null)
        {
            UnityEngine.Object.Destroy(_camoBlock);
        }
    }
}