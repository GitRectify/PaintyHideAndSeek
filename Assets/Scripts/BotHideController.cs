using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output. Fields (names, order, attributes,
// offsets) and method accessibility match dump.cs (TypeDefIndex 9642). All string literals are
// confirmed against Dumpstringliteral.json.
//
// Decoded from libil2cpp.so (not in the paste): both sort callbacks (<>c.<AssignAndHideRoutine>
// b__65_0 and <>c.<AssignSpots>b__85_0) are `b.score.CompareTo(a.score)` (tail call to
// float.CompareTo, RVA 0x397DE70) - best score first. Bot, Cand and <>c have empty ctors.
//
// AssignSpots needed a Ghidra fix: 0x0233402c branches into an outlined fragment (0x04885e60) that
// jumps straight back to 0x02334030, so its flow was overridden to CALL and the fake function Ghidra
// had created at 0x02334030 was deleted.
//
// Convention: where raw jumps to the NullReferenceException stub, the C# just dereferences
// naturally; explicit null checks are kept only where raw really skips.
public class BotHideController : MonoBehaviour
{
    private class Bot
    {
        public Transform tf;                 // 0x10
        public Animator anim;                // 0x18
        public SkinnedMeshRenderer smr;      // 0x20
        public CharacterController cc;       // 0x28
        public Ragdoll ragdoll;              // 0x30
        public Vector3 originPos;            // 0x38
        public Quaternion originRot;         // 0x44
        public Color originColor;            // 0x54
        public Vector3 originScale;          // 0x64
        public float pivotToFeet;            // 0x70
    }

    private class Cand
    {
        public Vector3 spot;                 // 0x10
        public float score;                  // 0x1C
        public bool taken;                   // 0x20
    }

    [Header("Timing")]
    public float hideDelay = 5f;
    public float walkSpeed = 12f;
    public float walkTimeout = 8f;
    public float arriveDistance = 1.5f;

    [Header("Hide spread")]
    [Tooltip("Bots won't hide closer than this (world units) to another bot, so they spread across the room instead of clumping. If the room can't fit them all this far apart, each takes the farthest free spot available.")]
    public float minHideSeparation = 20f;
    [Tooltip("Random jitter added to each hiding-spot's score so the bots pick DIFFERENT spots every replay (0 = always the same best spots; higher = more varied). Comparable to the score range (~0-5); too high and they may use less-hidden spots.")]
    public float spotRandomness = 2.5f;

    [Header("Wall hiding (cling up a wall/surface, like the player climbing)")]
    [Tooltip("PLAY NOW: chance (0-1) each hider clings up a wall/tall surface at height instead of hiding on the floor. 0 = always floor.")]
    [Range(0f, 1f)]
    public float wallHideChance = 0.5f;
    [Tooltip("Lowest / highest a wall-hider clings ABOVE its normal standing height (world units).")]
    public float wallHideMinHeight = 6f;
    public float wallHideMaxHeight = 16f;
    [Tooltip("How far the bot's pivot stands OUT from the wall face (so it isn't buried in the surface).")]
    public float wallClingOffset = 2f;
    private readonly List<Renderer> climbSurfaces = new List<Renderer>();           // 0x48

    [Header("Scene")]
    public string roomRootName = "Room";
    public string playerName = "Player";
    public string[] botNames = new string[] { "Bot1", "Bot2", "Bot3", "Bot4" };
    private readonly List<Bot> bots = new List<Bot>();                              // 0x68
    private readonly List<Renderer> hideObjects = new List<Renderer>();             // 0x70
    private float timer;                                                            // 0x78
    private bool triggered;                                                         // 0x7C

    [Header("Seek phase (Mode 1 — after the bots have hidden)")]
    [Tooltip("Seconds the player has to find + shoot every hider once they're hidden. Hits 0 -> Time's Up.")]
    public float seekTime = 100f;
    private float seekTimer;                                                        // 0x84
    private bool seeking;                                                           // 0x88
    private bool over;                                                              // 0x89
    private int hiderTotal;                                                         // 0x8C
    private HideModeResultUI resultUI;                                              // 0x90

    [Header("Mode control")]
    [Tooltip("When NO GameModeManager is in the scene, auto-run the hide countdown on Start (standalone). With a manager present, the manager starts this mode on a button press instead.")]
    public bool autoStartIfStandalone = true;
    private bool armed;                                                             // 0x99
    public bool hideCountdown;                                                     // 0x9A

    [Header("Mode-1 bot size")]
    [Tooltip("Bots are scaled to this while Mode 1 is active (player seeks). Feet are auto-anchored to the floor so they don't float. 1 = no scaling.")]
    public float botScaleInMode1 = 0.5f;
    private bool scaleBots;                                                         // 0xA0
    private Text statusText;                                                        // 0xA8
    private GameObject statusCanvas;                                                // 0xB0
    private GameModeManager gmm;                                                    // 0xB8
    private Font font;                                                              // 0xC0
    private BotManager botMgr;                                                      // 0xC8
    private LevelManager levelMgr;
    private NavMeshData _navData;                                                   // 0xD0
    private NavMeshDataInstance _navInstance;                                       // 0xD8
    private readonly RaycastHit[] _rayHits = new RaycastHit[32];                    // 0xE0
    private readonly Dictionary<Collider, bool> _isCharacterCache = new Dictionary<Collider, bool>(); // 0xE8
    private readonly Dictionary<Material, Color> _bgColorCache = new Dictionary<Material, Color>();   // 0xF0
    private Texture2D _camoBlock;                                                   // 0xF8
    private int _lastHideSec = -1;                                                  // 0x100
    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int PoseHash = Animator.StringToHash("Pose");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    // Used only by the +30s reward callback (AddSeekTime's lambda) - separate from resultUI.
    public HideModeResultUI hideUI;                                                 // 0x108

    // ====================== PROPERTIES ======================

    public bool Active => armed;

    public bool HideCountdownActive => hideCountdown;

    public int TimerSeconds => (int)Mathf.Max(0f, seeking ? seekTimer : timer);

    public float TimerFraction
    {
        get
        {
            float total = seeking ? seekTime : hideDelay;
            float left = seeking ? seekTimer : timer;
            return Mathf.Clamp01(left / Mathf.Max(0.01f, total));
        }
    }

    public int FugitivesLeft => bots.Count;

    public int HuntersLeft => 1;

    // Live hiders: present, active, and not ragdolled (the Ragdoll is looked up and cached lazily).
    public int BotCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < bots.Count; i++)
            {
                Bot b = bots[i];
                if (b == null) continue;
                if (b.tf == null) continue;
                if (!b.tf.gameObject.activeSelf) continue;
                if (b.ragdoll == null)
                {
                    b.ragdoll = b.tf.GetComponent<Ragdoll>();
                }
                if (b.ragdoll != null && b.ragdoll.IsRagdolling) continue;
                n++;
            }
            return n;
        }
    }

    // ====================== SETUP ======================

    // Hiders never carry a gun: any "BotGun" left from seeker mode is destroyed and the "Gun"
    // animator layer is switched off.
    public void SetBots(string[] names)
    {
        StopAllCoroutines();
        botNames = names ?? new string[0];
        triggered = false;
        ResolveBots();

        foreach (Bot b in bots)
        {
            if (b == null) continue;
            if (b.tf == null) continue;

            Transform[] all = b.tf.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == "BotGun")
                {
                    Object.Destroy(all[i].gameObject);
                    break;
                }
            }

            if (b.anim == null) continue;
            for (int layer = 0; layer < b.anim.layerCount; layer++)
            {
                if (b.anim.GetLayerName(layer) == "Gun")
                {
                    b.anim.SetLayerWeight(layer, 0f);
                    break;
                }
            }
        }
    }

    private void Start()
    {
        botMgr = Object.FindFirstObjectByType<BotManager>();
        gmm = Object.FindFirstObjectByType<GameModeManager>();
        resultUI = Object.FindFirstObjectByType<HideModeResultUI>();
        levelMgr = Object.FindFirstObjectByType<LevelManager>(FindObjectsInactive.Include);

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
        Debug.Log("[BotHide] bots=" + bots.Count.ToString() + " hideObjects=" + hideObjects.Count.ToString() + " armed=" + armed.ToString());
    }

    private IEnumerator InitializeRoomWhenReady()
    {
        while (GetCurrentRoom() == null)
        {
            yield return null;
        }

        GameObject room = GetCurrentRoom();

        Debug.Log(
            $"[BotHide] Using room '{room.name}'."
        );

        BuildHideObjects();
        BuildClimbSurfaces();
        EnsureRoomColliders();
        BakeNavMesh();
    }

    private GameObject GetCurrentRoom()
    {
        if (levelMgr == null)
        {
            levelMgr = FindFirstObjectByType<LevelManager>(
                FindObjectsInactive.Include
            );
        }

        if (levelMgr != null &&
            levelMgr.CurrentRoomInstance != null)
        {
            return levelMgr.CurrentRoomInstance;
        }

        return null;
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

    // Hide countdown ("WAIT! Hiders are hiding... Ns"), then the seek phase: all hiders shot = win,
    // seekTimer out = time's up. The countdown audio only plays when the player is the seeker.
    private void Update()
    {
        if (!armed) return;

        if (hideCountdown)
        {
            timer -= Time.deltaTime;
            int sec = (int)Mathf.Max(0f, timer);
            if (statusText != null && sec != _lastHideSec)
            {
                statusText.text = "WAIT! Hiders are hiding... " + sec.ToString() + "s";
                _lastHideSec = sec;
            }
            if (timer <= 0f)
            {
                BeginSeekPhase();
            }
            return;
        }

        if (!seeking || over) return;

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

    public void StartHiding()
    {
        if (triggered) return;
        triggered = true;
        StartCoroutine(AssignAndHideRoutine());
    }

    private void BeginSeekPhase()
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

    // Decompiled from <AssignAndHideRoutine>d__65.MoveNext (+ <>m__Finally1, the foreach Dispose).
    // Scores every hide object (yielding a frame whenever ~4 ms have been spent), adds floor-grid
    // candidates, sorts best-first, assigns one spot per bot, then teleports each bot there - or,
    // with wallHideChance, up to 8 random climb surfaces are tried for a wall cling first.
    // pPos = the player, eye/fwd = the main camera (fallbacks: 4 above the player / the player's or
    // world forward), botY = the first bot's height (-1.1 when there are no bots).
    private IEnumerator AssignAndHideRoutine()
    {
        GameObject player = PlayerRef.Resolve(playerName);
        Vector3 pPos = player != null ? player.transform.position : Vector3.zero;
        Camera cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : pPos + Vector3.up * 4f;
        Vector3 fwd = cam != null ? cam.transform.forward
            : (player != null ? player.transform.forward : Vector3.forward);
        float botY = bots.Count < 1 ? -1.1f : bots[0].tf.position.y;

        List<Cand> cands = new List<Cand>();
        float frameStart = Time.realtimeSinceStartup;
        foreach (Renderer hideObj in hideObjects)
        {
            if (hideObj == null) continue;
            cands.Add(ScoreObject(hideObj, pPos, eye, fwd, botY));
            if (Time.realtimeSinceStartup - frameStart > 0.004f)
            {
                yield return null;
                frameStart = Time.realtimeSinceStartup;
            }
        }

        AddGridHideCands(cands, pPos, eye, fwd, botY);
        cands.Sort((a, b) => b.score.CompareTo(a.score));

        foreach (KeyValuePair<Bot, Vector3> kv in AssignFromCands(cands))
        {
            Bot bot = kv.Key;
            bool onWall = false;
            if (climbSurfaces.Count > 0 && Random.value < wallHideChance)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Renderer surf = climbSurfaces[Random.Range(0, climbSurfaces.Count)];
                    if (TryWallSpot(surf, pPos, botY, out Vector3 wallSpot, out Quaternion wallRot, out Color wallCamo))
                    {
                        TeleportAndHideWall(bot, wallSpot, wallRot, wallCamo);
                        onWall = true;
                        break;
                    }
                }
            }
            if (!onWall)
            {
                TeleportAndHide(bot, kv.Value);
            }
        }
    }

    // ====================== HIDING ======================

    private void TeleportAndHide(Bot bot, Vector3 spot)
    {
        if (bot == null) return;
        if (bot.tf == null) return;

        if (bot.cc != null) bot.cc.enabled = false;
        bot.tf.position = spot;
        if (bot.anim != null)
        {
            bot.anim.SetFloat(SpeedHash, 0f);
        }
        if (bot.cc != null) bot.cc.enabled = true;
        Camouflage(bot);
    }

    // Wall cling: placed and rotated, "Pose" 1, painted with the given wall colour, tag hidden.
    private void TeleportAndHideWall(Bot bot, Vector3 spot, Quaternion rot, Color camo)
    {
        if (bot == null) return;
        if (bot.tf == null) return;

        if (bot.cc != null) bot.cc.enabled = false;
        bot.tf.SetPositionAndRotation(spot, rot);
        if (bot.anim != null)
        {
            bot.anim.SetFloat(SpeedHash, 0f);
            bot.anim.SetInteger(PoseHash, 1);
        }
        if (bot.cc != null) bot.cc.enabled = true;

        if (bot.smr != null)
        {
            bot.smr.material.SetColor(BaseColorId, camo);
        }
        if (botMgr != null)
        {
            botMgr.SetTagVisible(bot.tf, false);
        }
    }

    // Room meshes at least (wallHideMinHeight + 3) tall that aren't floor/ceiling/rug/cards/roof.
    private void BuildClimbSurfaces()
    {
        climbSurfaces.Clear();
        GameObject room = GetCurrentRoom();
        if (room == null) return;

        string[] skip = new string[] { "Floor", "Ceil", "Rug", "Cards", "Roof" };
        MeshRenderer[] renderers = room.GetComponentsInChildren<MeshRenderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            MeshRenderer mr = renderers[i];
            Bounds bb = mr.bounds;
            if (bb.size.y < wallHideMinHeight + 3f) continue;

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
            climbSurfaces.Add(mr);
        }
    }

    // A cling spot on the side of `surf` facing the player: pick a height in
    // [wallHideMinHeight, wallHideMaxHeight] above botY (kept 2 units inside the surface), the box
    // face on the player's dominant axis, and a random point along it (±70% of the half-width).
    // Raycast back onto the face (8 units), stand wallClingOffset out along the hit normal, and
    // reject the spot if anything but a character is within 3 units in front of it. The bot faces
    // away from the wall; camo is sampled from the surface.
    private bool TryWallSpot(Renderer surf, Vector3 pPos, float botY, out Vector3 spot, out Quaternion rot, out Color camo)
    {
        spot = Vector3.zero;
        rot = Quaternion.identity;
        camo = Color.gray;
        if (surf == null) return false;

        Bounds bb = surf.bounds;
        float lo = Mathf.Max(wallHideMinHeight + botY, bb.min.y + 2f);
        float hi = Mathf.Min(wallHideMaxHeight + botY, bb.max.y - 2f);
        if (!(lo < hi)) return false;
        float y = Random.Range(lo, hi);

        Vector3 flat = new Vector3(pPos.x - bb.center.x, 0f, pPos.z - bb.center.z);
        Vector3 toPlayer = flat.sqrMagnitude >= 0.01f ? flat.normalized : Vector3.forward;
        float sx = toPlayer.x >= 0f ? 1f : -1f;
        float sz = toPlayer.z >= 0f ? 1f : -1f;
        Vector3 faceN = Mathf.Abs(toPlayer.z) <= Mathf.Abs(toPlayer.x) ? new Vector3(sx, 0f, 0f) : new Vector3(0f, 0f, sz);

        Vector3 tangent;
        float halfWidth;
        float halfDepth;
        if (Mathf.Abs(faceN.x) > 0.5f)
        {
            tangent = Vector3.forward;
            halfWidth = bb.extents.z;
            halfDepth = bb.extents.x;
        }
        else
        {
            tangent = Vector3.right;
            halfWidth = bb.extents.x;
            halfDepth = bb.extents.z;
        }

        float along = Random.Range(halfWidth * -0.7f, halfWidth * 0.7f);
        Vector3 origin = new Vector3(bb.center.x, y, bb.center.z) + faceN * 4f + faceN * halfDepth + tangent * along;
        int n = Physics.RaycastNonAlloc(origin, -faceN, _rayHits, 8f);

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
        if (!found) return false;

        Vector3 hn = hit.normal;
        Vector3 flatN = new Vector3(hn.x, 0f, hn.z);
        Vector3 outward = flatN.sqrMagnitude >= 0.01f ? flatN.normalized : -faceN;

        spot = hit.point + outward * wallClingOffset;
        spot.y = y;

        int m = Physics.RaycastNonAlloc(new Vector3(spot.x, y, spot.z), outward, _rayHits, 3f);
        for (int i = 0; i < m; i++)
        {
            Collider col = _rayHits[i].collider;
            if (col is CharacterController) continue;
            if (!IsCharacter(col)) return false;
        }

        rot = Quaternion.LookRotation(outward, Vector3.up);
        camo = SampleContactColor(spot, surf);
        return true;
    }

    // Seeker mode start: rebuild the hide/climb lists, reset + shrink the bots, run the hide
    // countdown and start hiding. The status canvas / countdown audio only apply when the player is
    // the seeker (or there's no GameModeManager).
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
            statusCanvas.SetActive(gmm == null || gmm.PlayerIsSeeker);
        }
        if (statusText != null)
        {
            statusText.text = "WAIT! Hiders are hiding... " + ((int)hideDelay).ToString() + "s";
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

        if (statusCanvas != null)
        {
            statusCanvas.SetActive(false);
        }
        if (statusText != null)
        {
            statusText.text = "";
        }
        if (resultUI != null)
        {
            resultUI.HideAll();
        }
    }

    // ====================== SHOOTING ======================

    // A bullet hit `hitGo`: if it's (part of) a live hider, kill it. Knockback goes along the main
    // camera's forward (or the hider's own forward without a camera).
    public bool TryShoot(GameObject hitGo)
    {
        if (!seeking || over) return false;
        if (hitGo == null) return false;

        Transform ht = hitGo.transform;
        foreach (Bot b in bots)
        {
            if (b == null) continue;
            if (b.tf == null) continue;
            if (!b.tf.gameObject.activeSelf) continue;
            if (b.ragdoll != null && b.ragdoll.IsRagdolling) continue;

            if (hitGo == b.tf.gameObject || ht.IsChildOf(b.tf))
            {
                Camera cam = Camera.main;
                Vector3 dir = cam != null ? cam.transform.forward : b.tf.forward;
                KillHider(b, dir);
                return true;
            }
        }
        return false;
    }

    // Aim-assisted hitscan: each live hider's bounds (skinned mesh, else a 4-unit box), padded by
    // aimAssist, is tested against the ray. The nearest candidate that is at least 1 unit ahead,
    // within maxRange, roughly in front (distance along the ray >= half the straight-line distance)
    // and not behind a wall is killed.
    public bool TryShootAim(Vector3 origin, Vector3 dir, float maxRange, float aimAssist)
    {
        if (!seeking || over) return false;
        if (dir.sqrMagnitude < 1e-6f) return false;

        dir = dir.normalized;
        Ray ray = new Ray(origin, dir);
        float pad = Mathf.Max(0f, aimAssist);

        Bot best = null;
        float bestAlong = float.MaxValue;
        foreach (Bot b in bots)
        {
            if (b == null) continue;
            if (b.tf == null) continue;
            if (!b.tf.gameObject.activeSelf) continue;
            if (b.ragdoll != null && b.ragdoll.IsRagdolling) continue;

            Bounds bb = b.smr != null ? b.smr.bounds : new Bounds(b.tf.position, Vector3.one * 4f);
            bb.Expand(pad);
            if (!bb.IntersectRay(ray)) continue;

            Vector3 to = bb.center - origin;
            float along = Vector3.Dot(to, dir);
            if (!(along > 1f && along <= maxRange)) continue;
            if (!(to.magnitude * 0.5f <= along)) continue;
            if (!(along < bestAlong)) continue;
            if (WallBetween(origin, bb.center)) continue;

            best = b;
            bestAlong = along;
        }

        if (best == null) return false;
        KillHider(best, dir);
        return true;
    }

    // Ragdolls the hider (or hides it if it has no ragdoll) and hides its name tag.
    private void KillHider(Bot b, Vector3 impulseDir)
    {
        Vector3 hitPoint = b.smr != null ? b.smr.bounds.center : b.tf.position + Vector3.up * 6f;
        b.ragdoll = Ragdoll.Trigger(b.tf, hitPoint, impulseDir, 1f, true);
        if (b.ragdoll == null)
        {
            b.tf.gameObject.SetActive(false);
        }
        if (botMgr != null)
        {
            botMgr.SetTagVisible(b.tf, false);
        }
        Debug.Log("[BotHide] Hider shot: " + b.tf.name + " (" + BotCount.ToString() + " left)");
    }

    // True if something that blocks shots (not a character) is between origin and target.
    private bool WallBetween(Vector3 origin, Vector3 target)
    {
        Vector3 d = target - origin;
        float dist = d.magnitude;
        if (dist < 0.5f) return false;

        int n = Physics.RaycastNonAlloc(origin, d / dist, _rayHits, dist - 0.5f);
        for (int i = 0; i < n; i++)
        {
            Collider col = _rayHits[i].collider;
            if (col is CharacterController) continue;
            if (IsCharacter(col)) continue;
            if (Bullet.BlocksShot(col.gameObject)) return true;
        }
        return false;
    }

    // ====================== ROUND END ======================

    private void Win()
    {
        seeking = false;
        over = true;
        if (statusText != null)
        {
            statusText.text = "";
        }

        if (gmm == null || gmm.PlayerIsSeeker)
        {
            if (resultUI != null)
            {
                resultUI.ShowVictorySeek("#1/8", (int)Mathf.Max(0f, seekTimer), hiderTotal);
            }
            else if (statusText != null)
            {
                statusText.text = "ALL FOUND!  You win.";
            }
        }
        Debug.Log("[BotHide] All hiders found — WIN.");
    }

    // Time's Up popup: +30s (rewarded ad) or give up.
    private void TimeUp()
    {
        seeking = false;
        over = true;
        seekTimer = 0f;
        if (statusText != null)
        {
            statusText.text = "";
        }

        if (gmm == null || gmm.PlayerIsSeeker)
        {
            if (resultUI != null)
            {
                resultUI.ShowTimeUp(AddSeekTime, ShowSeekDefeat);
            }
            else if (statusText != null)
            {
                statusText.text = "TIME'S UP!";
            }
        }
        Debug.Log("[BotHide] Time's up.");
    }

    // Rewarded ad for +30 s of hunting. A null AdMgr throws.
    private void AddSeekTime()
    {
        // if (!AdMgr.Instance.IsRewardReady) return;

        // <AddSeekTime>b__79_0
        // AdMgr.Instance.OnRewardView(() =>
        // {
            // hideUI.HideAll();
            // seeking = true;
            // over = false;
            // seekTimer += 30f;
            // RootManager.Instance?.SetNumber(1);
            // Debug.Log("[BotHide] +30s — keep hunting!");
        // }, RewardFail);
    }

    private void RewardFail()
    {
    }

    private void ShowSeekDefeat()
    {
        if (resultUI != null)
        {
            resultUI.ShowDefeatSeek("#5/8", 0, hiderTotal - BotCount);
        }
    }

    // ====================== BOTS ======================

    // Back to spawn: tags shown, ragdolls off, original scale/position/rotation/colour, Speed 0 and
    // Pose 0 (shrunk again when scaleBots is on).
    public void ResetBots()
    {
        if (botMgr != null)
        {
            botMgr.SetAllTagsVisible(true);
        }

        foreach (Bot b in bots)
        {
            if (b == null) continue;
            if (b.tf == null) continue;

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
                b.anim.SetFloat(SpeedHash, 0f);
                b.anim.SetInteger(PoseHash, 0);
            }
            if (b.smr != null)
            {
                b.smr.material.SetColor(BaseColorId, b.originColor);
            }
        }
    }

    // Shrinks the bot to botScaleInMode1 and lowers it so its feet stay on the floor.
    private void ApplyBotScale(Bot b)
    {
        if (botScaleInMode1 >= 0.999f) return;

        float feet = b.smr != null ? b.tf.position.y - b.smr.bounds.min.y : b.pivotToFeet;
        b.tf.localScale = b.originScale * botScaleInMode1;
        Vector3 p = b.tf.position;
        b.tf.position = new Vector3(p.x, p.y - feet * (1f - botScaleInMode1), p.z);
    }

    // ====================== SPOT SELECTION ======================

    // Same spot selection as AssignAndHideRoutine, all in one frame (used by DebugHideAllInstant).
    // FIX (confirmed): pPos is the PLAYER's position and botY the BOT's height - the earlier draft
    // had them swapped. The -1.1 default is DAT_00de4474 (0xBF8CCCCD), read at 0x02334018.
    private List<KeyValuePair<Bot, Vector3>> AssignSpots()
    {
        GameObject player = PlayerRef.Resolve(playerName);
        Vector3 pPos = player != null ? player.transform.position : Vector3.zero;
        Camera cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : pPos + Vector3.up * 4f;
        Vector3 fwd = cam != null ? cam.transform.forward
            : (player != null ? player.transform.forward : Vector3.forward);
        float botY = bots.Count > 0 ? bots[0].tf.position.y : -1.1f;

        List<Cand> cands = new List<Cand>();
        foreach (Renderer hideObj in hideObjects)
        {
            if (hideObj == null) continue;
            cands.Add(ScoreObject(hideObj, pPos, eye, fwd, botY));
        }
        AddGridHideCands(cands, pPos, eye, fwd, botY);
        cands.Sort((a, b) => b.score.CompareTo(a.score));
        return AssignFromCands(cands);
    }

    // Floor-grid candidates (30-unit cells over the lowest NavMesh level, 22-unit snap, at bot
    // height), only when there are fewer than bots + 3 candidates. They score 0.5 below an
    // equivalent object spot.
    private void AddGridHideCands(List<Cand> cands, Vector3 pPos, Vector3 eye, Vector3 fwd, float botY)
    {
        if (cands.Count >= bots.Count + 3) return;

        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        Vector3[] verts = tri.vertices;
        if (verts.Length == 0) return;

        float floor = float.MaxValue;
        for (int i = 0; i < verts.Length; i++)
        {
            floor = Mathf.Min(floor, verts[i].y);
        }

        float minX = float.MaxValue, maxX = -float.MaxValue;
        float minZ = float.MaxValue, maxZ = -float.MaxValue;
        bool any = false;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 v = verts[i];
            if (v.y > floor + 5f) continue;
            any = true;
            minX = Mathf.Min(minX, v.x);
            maxX = Mathf.Max(maxX, v.x);
            minZ = Mathf.Min(minZ, v.z);
            maxZ = Mathf.Max(maxZ, v.z);
        }
        if (!any || !(minX <= maxX)) return;

        for (float x = minX; x <= maxX; x += 30f)
        {
            for (float z = minZ; z <= maxZ; z += 30f)
            {
                if (NavMesh.SamplePosition(new Vector3(x, floor, z), out NavMeshHit hit, 22f, -1)
                    && hit.position.y <= floor + 5f)
                {
                    Vector3 spot = new Vector3(hit.position.x, botY, hit.position.z);
                    Color camo = ColorBehind(spot, eye);
                    Cand c = new Cand();
                    c.spot = spot;
                    c.score = ScoreSpot(spot, camo, pPos, eye, fwd) - 0.5f;
                    cands.Add(c);
                }
            }
        }
    }

    // One candidate per hide object: the spot behind it (from the player), its contact colour, the
    // spot score plus random jitter.
    private Cand ScoreObject(Renderer o, Vector3 pPos, Vector3 eye, Vector3 fwd, float botY)
    {
        Vector3 spot = HideSpotBehind(o, pPos, botY);
        Color camo = SampleContactColor(spot, o);
        float score = ScoreSpot(spot, camo, pPos, eye, fwd);
        float jitter = Random.value * Mathf.Max(0f, spotRandomness);
        Cand c = new Cand();
        c.spot = spot;
        c.score = score + jitter;
        return c;
    }

    // Candidates must already be sorted best-first. Each bot takes the first free candidate at least
    // minHideSeparation from every spot already taken; failing that, the free candidate farthest
    // from them. Stops early when no free candidate is left.
    private List<KeyValuePair<Bot, Vector3>> AssignFromCands(List<Cand> cands)
    {
        List<KeyValuePair<Bot, Vector3>> result = new List<KeyValuePair<Bot, Vector3>>();
        List<Vector3> used = new List<Vector3>();
        float minSep = Mathf.Max(0f, minHideSeparation);

        foreach (Bot bot in bots)
        {
            Cand pick = null;
            foreach (Cand c in cands)
            {
                if (c.taken) continue;
                if (MinDistanceTo(c.spot, used) >= minSep)
                {
                    pick = c;
                    break;
                }
            }

            if (pick == null)
            {
                float best = -1f;
                foreach (Cand c in cands)
                {
                    if (c.taken) continue;
                    float d = MinDistanceTo(c.spot, used);
                    if (d > best)
                    {
                        pick = c;
                        best = d;
                    }
                }
                if (pick == null) break;
            }

            pick.taken = true;
            used.Add(pick.spot);
            result.Add(new KeyValuePair<Bot, Vector3>(bot, pick.spot));
        }
        return result;
    }

    private static float MinDistanceTo(Vector3 p, List<Vector3> pts)
    {
        float min = float.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            min = Mathf.Min(min, Vector3.Distance(p, pts[i]));
        }
        return min;
    }

    // Higher = better hiding spot:
    //   0.6 × distance from the player (capped at 60 units)
    // + 0.8 if the spot is outside the camera's forward view (dot < 0.2)
    // + 1.5 if something non-character blocks the line from the eye to the bot's head (spot + 6 up)
    // + 2 × (1 - colour difference between the contact colour and what's behind it from the eye,
    //   capped at 0.9).
    private float ScoreSpot(Vector3 spot, Color camo, Vector3 pPos, Vector3 eye, Vector3 fwd)
    {
        Vector3 head = spot + Vector3.up * 6f;
        Vector3 d = head - eye;
        float dist = d.magnitude;

        float cover = 0f;
        int n = Physics.RaycastNonAlloc(eye, d / dist, _rayHits, Mathf.Max(0f, dist - 2f));
        for (int i = 0; i < n; i++)
        {
            Collider col = _rayHits[i].collider;
            if (col is CharacterController) continue;
            if (!IsCharacter(col))
            {
                cover = 1.5f;
                break;
            }
        }

        Vector3 f = fwd.normalized;
        Vector3 toSpot = (spot - eye).normalized;
        Color behind = ColorBehind(spot, eye);
        float distScore = Mathf.Min(1f, Vector3.Distance(spot, pPos) / 60f);
        float colorDiff = Mathf.Min(1f, ColorDist(camo, behind) / 0.9f);
        float offView = Vector3.Dot(f, toSpot) < 0.2f ? 0.8f : 0f;
        return distScore * 0.6f + offView + cover + (1f - colorDiff) + (1f - colorDiff);
    }

    // Colour of the first non-character surface behind the spot as seen from the eye (ray from
    // 1.5 units past the spot, 6 units up, 50 long). Gray = nothing found.
    private Color ColorBehind(Vector3 spot, Vector3 eye)
    {
        Vector3 flat = new Vector3(spot.x - eye.x, 0f, spot.z - eye.z);
        if (flat.sqrMagnitude < 0.01f) return Color.gray;

        Vector3 dir = flat.normalized;
        Vector3 origin = spot + dir * 1.5f + Vector3.up * 6f;
        int n = Physics.RaycastNonAlloc(origin, dir, _rayHits, 50f);

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

    // Decompiled from <HideBot>d__93.MoveNext. Walks the bot along a NavMesh path to `spot` for at
    // most walkTimeout seconds, then stops it and camouflages it. Unused - nothing calls HideBot
    // (the bots teleport). A null bot throws; a destroyed / inactive bot just ends the walk.
    private IEnumerator HideBot(Bot bot, Vector3 spot)
    {
        Vector3[] corners = ComputePath(bot.tf.position, spot);
        int ci = 0;
        float t = 0f;
        while (t < walkTimeout && ci < corners.Length)
        {
            if (bot.tf == null) yield break;
            if (!bot.tf.gameObject.activeInHierarchy) yield break;
            t += Time.deltaTime;

            Vector3 corner = corners[ci];
            Vector3 pos = bot.tf.position;
            float dx = corner.x - pos.x;
            float dz = corner.z - pos.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > arriveDistance)
            {
                float step = walkSpeed * Time.deltaTime;
                Vector3 delta = new Vector3(dx / dist * step, 0f, dz / dist * step);
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
                    bot.anim.SetFloat(SpeedHash, 1f);
                }
                bot.tf.rotation = Quaternion.Slerp(bot.tf.rotation, Quaternion.LookRotation(new Vector3(dx / dist, 0f, dz / dist)), Time.deltaTime * 12f);
                yield return null;
            }
            else
            {
                ci++;
            }
        }

        if (bot.anim != null)
        {
            bot.anim.SetFloat(SpeedHash, 0f);
        }
        Camouflage(bot);
    }

    // NavMesh path between the nearest NavMesh points (18-unit search). Unlike BotSeekController,
    // no path returns a single-corner path straight to `to`.
    private Vector3[] ComputePath(Vector3 from, Vector3 to)
    {
        if (NavMesh.SamplePosition(from, out NavMeshHit fromHit, 18f, -1))
        {
            from = fromHit.position;
        }
        Vector3 target = to;
        if (NavMesh.SamplePosition(to, out NavMeshHit toHit, 18f, -1))
        {
            target = toHit.position;
        }

        NavMeshPath path = new NavMeshPath();
        if (NavMesh.CalculatePath(from, target, -1, path) && path.corners != null && path.corners.Length != 0)
        {
            return path.corners;
        }
        return new Vector3[] { to };
    }

    // Paints the bot with the colour around it, picks a random hiding pose (1..4) and hides its tag.
    private void Camouflage(Bot bot)
    {
        Color c = SampleContactColor(bot.tf.position, null);
        if (bot.smr != null)
        {
            Material mat = bot.smr.material;

            if (mat.HasProperty(BaseColorId))
            {
                mat.SetColor(BaseColorId, c);
            }

            if (mat.HasProperty(ColorId))
            {
                mat.SetColor(ColorId, c);
            }
        }
        if (bot.anim != null)
        {
            bot.anim.SetInteger(PoseHash, Random.Range(1, 5));
        }
        if (botMgr != null)
        {
            botMgr.SetTagVisible(bot.tf, false);
        }
    }

    // Debug: every bot straight to its assigned spot (no wall hiding, no coroutine).
    public void DebugHideAllInstant()
    {
        foreach (KeyValuePair<Bot, Vector3> kv in AssignSpots())
        {
            Bot bot = kv.Key;
            CharacterController cc = bot.cc;
            if (cc != null) cc.enabled = false;
            bot.tf.position = kv.Value;
            if (cc != null) cc.enabled = true;
            Camouflage(bot);
        }
        triggered = true;
        if (statusText != null)
        {
            statusText.text = "";
        }
    }

    // The point just past `obj` on the far side from the player (its larger horizontal half-size
    // + 2), at bot height.
    private Vector3 HideSpotBehind(Renderer obj, Vector3 playerPos, float botY)
    {
        Vector3 c = obj.transform.position;
        Vector3 flat = new Vector3(c.x - playerPos.x, 0f, c.z - playerPos.z);
        Vector3 dir = (flat.sqrMagnitude >= 0.01f ? flat : Vector3.forward).normalized;
        Bounds bb = obj.bounds;
        float r = Mathf.Max(bb.extents.x, bb.extents.z);
        return new Vector3(c.x + dir.x * (r + 2f), botY, c.z + dir.z * (r + 2f));
    }

    private void ResolveBots()
    {
        bots.Clear();
        for (int i = 0; i < botNames.Length; i++)
        {
            string name = botNames[i];
            GameObject go = GameObject.Find(name);
            if (go == null)
            {
                Debug.LogWarning("[BotHide] '" + name + "' not found.");
                continue;
            }

            SkinnedMeshRenderer smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Color originColor = Color.white;
            if (smr != null && smr.sharedMaterial != null && smr.sharedMaterial.HasProperty(BaseColorId))
            {
                originColor = smr.sharedMaterial.GetColor(BaseColorId);
            }
            float pivotToFeet = 0f;
            if (smr != null)
            {
                pivotToFeet = go.transform.position.y - smr.bounds.min.y;
            }

            Ragdoll ragdoll = go.GetComponent<Ragdoll>();
            Bot b = new Bot();
            b.tf = go.transform;
            b.anim = go.GetComponentInChildren<Animator>(true);
            b.smr = smr;
            b.cc = go.GetComponent<CharacterController>();
            b.ragdoll = ragdoll;
            b.originPos = go.transform.position;
            b.originRot = go.transform.rotation;
            b.originColor = originColor;
            b.originScale = go.transform.localScale;
            b.pivotToFeet = pivotToFeet;
            bots.Add(b);
        }
    }

    // Furniture-sized room meshes (2..22 wide, at least 3 tall, not a wall/floor/etc. by name).
    private void BuildHideObjects()
    {
        hideObjects.Clear();
        GameObject room = GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[BotHide] room '" + roomRootName + "' not found.");
            return;
        }

        string[] skip = new string[] { "Wall", "Floor", "Ceil", "Wallpaper", "Window", "Door", "Rug", "Cards" };
        MeshRenderer[] renderers = room.GetComponentsInChildren<MeshRenderer>(true);
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
            hideObjects.Add(mr);
        }
    }

    // ====================== COLOUR SAMPLING ======================

    // Colour of the nearest non-character renderer hit within 16 units of (botPos + 1.5 up), probing
    // down, forward, back, left and right. Falls back to the given renderer's colour, then gray.
    private Color SampleContactColor(Vector3 botPos, Renderer fallback)
    {
        Vector3 origin = botPos + Vector3.up * 1.5f;
        Vector3[] dirs = new Vector3[] { Vector3.down, Vector3.forward, Vector3.back, Vector3.left, Vector3.right };

        bool found = false;
        float best = float.MaxValue;
        RaycastHit bestHit = default(RaycastHit);
        for (int d = 0; d < dirs.Length; d++)
        {
            int n = Physics.RaycastNonAlloc(origin, dirs[d], _rayHits, 16f);
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = _rayHits[i];
                Collider col = h.collider;
                if (col is CharacterController) continue;
                if (IsCharacter(col)) continue;

                Renderer r = col.GetComponent<Renderer>();
                if (r == null)
                {
                    r = col.GetComponentInParent<Renderer>();
                }
                if (r == null) continue;

                if (h.distance < best)
                {
                    best = h.distance;
                    bestHit = h;
                    found = true;
                }
            }
        }

        if (found)
        {
            Renderer r = bestHit.collider.GetComponent<Renderer>();
            if (r == null)
            {
                r = bestHit.collider.GetComponentInParent<Renderer>();
            }
            Vector2 uv = bestHit.collider is MeshCollider ? bestHit.textureCoord : Vector2.zero;
            if (uv == Vector2.zero)
            {
                uv = MeshUvCenter(r);
            }
            Material m = r != null ? r.sharedMaterial : null;
            return SampleTexAtUV(m, uv);
        }
        return fallback != null ? SampleObjectColor(fallback, botPos) : Color.gray;
    }

    // Colour of `obj` where a ray from 2 units above botPos toward the object's centre hits its own
    // (ensured) MeshCollider; the mesh UV centre otherwise.
    private Color SampleObjectColor(Renderer obj, Vector3 botPos)
    {
        MeshCollider mc = EnsureObjCollider(obj);
        Vector2 uv = MeshUvCenter(obj);
        if (mc != null)
        {
            Bounds bb = obj.bounds;
            Vector3 origin = botPos + Vector3.up + Vector3.up;
            Vector3 d = bb.center - origin;
            float dist = d.magnitude;
            int n = Physics.RaycastNonAlloc(origin, d / dist, _rayHits, dist + 6f);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (_rayHits[i].collider == mc && _rayHits[i].distance < best)
                {
                    best = _rayHits[i].distance;
                    uv = _rayHits[i].textureCoord;
                }
            }
        }
        return SampleTexAtUV(obj.sharedMaterial, uv);
    }

    // The object's MeshCollider, adding one when it has a readable, non-empty mesh; else null.
    private MeshCollider EnsureObjCollider(Renderer obj)
    {
        MeshCollider mc = obj.GetComponent<MeshCollider>();
        if (mc != null) return mc;

        MeshFilter mf = obj.GetComponent<MeshFilter>();
        if (mf == null) return null;
        if (mf.sharedMesh == null) return null;
        if (!mf.sharedMesh.isReadable) return null;
        if (mf.sharedMesh.vertexCount < 1) return null;
        return obj.gameObject.AddComponent<MeshCollider>();
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

    // Average colour of a 6x6 texel block at uv, tinted by _BaseColor, cached per material (same
    // algorithm as BotSeekController.SampleTexAtUV). No texture = the tint, alpha 1.
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

    // ====================== ROOM / NAVMESH ======================

    // Adds MeshColliders to readable room meshes that have no collider, then ramps.
    private void EnsureRoomColliders()
    {
        GameObject room = GetCurrentRoom();
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

    // Always bakes a runtime NavMesh over the room (agent r 2.5, h 12, climb 4, slope 45; colliders
    // only; bounds expanded by 12), replacing any previous one.
    private void BakeNavMesh()
    {
        GameObject room = GetCurrentRoom();
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

        _navData = NavMeshBuilder.BuildNavMeshData(settings, sources, bb, Vector3.zero, Quaternion.identity);
        if (_navInstance.valid)
        {
            NavMesh.RemoveNavMeshData(_navInstance);
        }
        _navInstance = NavMesh.AddNavMeshData(_navData);

        Debug.Log("[BotHide] NavMesh baked: sources=" + sources.Count.ToString() + " valid=" + _navInstance.valid.ToString());
    }

    private void OnDestroy()
    {
        if (_navInstance.valid)
        {
            NavMesh.RemoveNavMeshData(_navInstance);
        }
        if (_camoBlock != null)
        {
            Object.Destroy(_camoBlock);
        }
    }

    // ====================== STATUS UI ======================

    // "HideStatusCanvas" (sort 9, 1080x1920) with the top-centre "HideStatus" countdown text.
    private void BuildStatusUI()
    {
        statusCanvas = SceneUI.GetOrCreateCanvas("HideStatusCanvas", out bool canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(statusCanvas);
        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9;
            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(statusCanvas);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(statusCanvas);

        GameObject textGO = SceneUI.GetOrCreate("HideStatus", statusCanvas.transform, out bool textCreated);
        statusText = SceneUI.GetOrAdd<Text>(textGO);
        statusText.font = font;
        statusText.raycastTarget = false;
        if (!textCreated) return;

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
        rt.anchoredPosition = new Vector2(0f, -90f);
        rt.sizeDelta = new Vector2(800f, 60f);
    }

    // Adds the new Input System's UI module when that package is present, otherwise the legacy
    // StandaloneInputModule.
    private void EnsureEventSystem()
    {
        if (Object.FindFirstObjectByType<EventSystem>() != null) return;

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        System.Type inputModuleType = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (inputModuleType != null)
        {
            go.AddComponent(inputModuleType);
        }
        else
        {
            go.AddComponent<StandaloneInputModule>();
        }
    }
}
