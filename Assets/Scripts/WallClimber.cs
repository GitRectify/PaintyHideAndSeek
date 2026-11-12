using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Movement/climb logic + a runtime-built on-screen up/down button UI.
// Re-verified method by method against raw Ghidra output. Fields (names, order, offsets,
// attributes), the nested State enum, and every method's accessibility match dump.cs
// (TypeDefIndex 9724). All string literals are confirmed against Dumpstringliteral.json.
public class WallClimber : MonoBehaviour
{
    private enum State
    {
        Grounded = 0,
        OnWall = 1
    }

    [Header("Character")]
    public string characterRootName = "Player";
    public float climbSpeed = 8f;
    [Tooltip("How fast the chameleon turns to face its travel direction while free-roaming OFF the wall (open space, no wall nearby). 0 = keep facing wherever it was.")]
    public float freeMoveTurnSpeed = 12f;

    [Header("Detection")]
    [Tooltip("Climb ANY solid object you lean against — wall, table, chair, props… The floor (Ground) and characters (player/bots) are always excluded. Turn OFF to climb only Wall-tagged surfaces.")]
    public bool climbAnySurface = true;
    [Tooltip("Used only when 'Climb Any Surface' is OFF: only colliders with this tag are climbable.")]
    public string wallTag = "Wall";
    public string groundTag = "Ground";
    public float wallCheckDistance = 4f;
    public float groundCheckDistance = 9f;
    [Tooltip("Only surfaces steeper than this (degrees the surface normal is tilted from straight up) count as climbable WALLS. Walkable ramps / staircases at or below this are walked up normally — never treated as a wall to cling to. Keep above the player's slopeLimit (45°).")]
    public float minWallSteepness = 50f;

    [Header("Climb button icons (Assets/Texture2D)")]
    [Tooltip("Up / climb button icon (top.png). Falls back to a generated triangle when unset.")]
    public Sprite climbUpIcon;
    [Tooltip("Down button icon (doww.png). Falls back to a generated triangle when unset.")]
    public Sprite climbDownIcon;

    private State state;
    private Transform character;
    private Renderer[] charRends;
    // int, not bool: SetClimb stores (uint)held, and Update computes up - down as -1/0/1.
    private int up;
    private int down;

    // Read by the online input stream: the server reproduces climbing from these two buttons.
    public bool ClimbUpHeld => up > 0;
    public bool ClimbDownHeld => down > 0;
    private Font font;
    private CharacterController cc;
    private Joystick joystick;
    private Camera cam;
    private Animator anim;
    private GameModeManager gmm;
    private CameraController camCtrl;
    private BotSeekController seek;
    private PoseSelectorUI poseSel;
    private BotHideController hide;
    private Mode1FirstPersonGun gun;
    private bool clinging;
    private float lastTouchedWallAt;

    // Walls have gaps (shelves, window frames) and the probes see straight through them. Crossing one
    // is still the same climb: the body must not let go and take hold again, on anybody's screen.
    private const float LetGoSeconds = 1f;
    private GameObject climbCanvas;
    private readonly RaycastHit[] _hits = new RaycastHit[16];
    private readonly Vector3[] _probeDirs = new Vector3[4];

    // CONFIRMED: .cctor fills this from the metadata blob
    // <PrivateImplementationDetails>.1EA60047B9BF4176B2692BA4F93A56901549CDBC4B01F11DD8D16E697454CF72
    // (dump.cs: "Metadata offset 0x70D588"). The 16 bytes there in global-metadata.dat, read as
    // little-endian floats, are 0.15, 0.45, 0.7, 0.92: the heights (as a fraction of the capsule,
    // bottom to top) that TryGetWall probes at.
    private static readonly float[] _probeHeights = { 0.15f, 0.45f, 0.7f, 0.92f };

    private readonly Dictionary<Collider, bool> _isCharacterCache = new Dictionary<Collider, bool>();


    // ================= properties =================

    public bool IsOnWall => state == State.OnWall;

    /// <summary>On a wall and touching it, not hanging in the open past its top.</summary>
    public bool IsClinging => state == State.OnWall && clinging;

    // The walls are open to the player in any round, on either side: online, hunted by the seeker
    // bots in Single Play, or hunting the hider bots. They close with the round.
    private bool WallsOpen
    {
        get
        {
            if (gmm == null || !gmm.RoundActive) return false;
            if (gmm.NetworkRound) return true;
            if (gmm.PlayerIsSeeker) return hide != null && hide.Active;
            return seek != null && seek.Active && seek.chasePlayer;
        }
    }

    private void LeaveWall()
    {
        state = State.Grounded;
        clinging = false;
        BodyAnimation.SetClimbing(anim, false);
    }

    // True when not currently on a wall, but chasing the player and near a climbable one — used
    // to suppress some other system's rotation control while approaching a wall.
    public bool BlocksRotation
    {
        get
        {
            if (state == State.OnWall) return false;
            if (character == null) return false;
            if (!WallsOpen) return false;
            return TryGetWall(out Vector3 _);
        }
    }

    private bool IsNearWall()
    {
        return TryGetWall(out Vector3 _);
    }

    // ================= lifecycle =================

    // Public API called from other classes (BotSeekController.GroundPlayer calls it).
    // Matches the raw WallClimber$$DropToGround exactly: unlike the two "fell off the wall" resets
    // inside Update(), this path does NOT gate on HasUserPose before forcing Pose back to 0 — that
    // asymmetry is confirmed in the raw code, not an inconsistency to "fix".
    public void DropToGround()
    {
        if (character == null) return;

        Grounding.ToFloor(character, charRends);
        up = 0;
        down = 0;
        LeaveWall();

        if (poseSel != null)
        {
            poseSel.ClearSelection();
            return;
        }
        BodyAnimation.SetPose(anim, 0);
    }

    // A new character's body (CharacterSelect): the one that climbs from now on. It starts off
    // any wall (the swap happens at Home).
    private void HandlePlayerChanged(Transform body)
    {
        character = body;
        charRends = body != null ? body.GetComponentsInChildren<Renderer>() : new Renderer[0];
        cc = body != null ? body.GetComponent<CharacterController>() : null;
        anim = body != null ? body.GetComponentInChildren<Animator>(true) : null;
        up = 0;
        down = 0;
        state = State.Grounded;
        clinging = false;
    }

    private void OnDestroy()
    {
        PlayerRef.Changed -= HandlePlayerChanged;
    }

    /// <summary>A new round: off any wall, the climb buttons released (RoundUiReset).</summary>
    public void ResetControls()
    {
        up = 0;
        down = 0;
        if (state == State.OnWall) DropToGround();
    }

    private void Start()
    {
        GameObject resolved = PlayerRef.Resolve(characterRootName);
        if (resolved != null)
        {
            character = resolved.transform;
            charRends = resolved.GetComponentsInChildren<Renderer>();
            cc = resolved.GetComponent<CharacterController>();
            anim = resolved.GetComponentInChildren<Animator>(true);
        }
        else
        {
            Debug.LogWarning("[WallClimber] Character '" + characterRootName + "' not found.");
        }
        PlayerRef.Changed += HandlePlayerChanged;

        joystick = UnityEngine.Object.FindAnyObjectByType<Joystick>(FindObjectsInactive.Include);
        cam = Camera.main;
        seek = UnityEngine.Object.FindAnyObjectByType<BotSeekController>();
        gmm = UnityEngine.Object.FindAnyObjectByType<GameModeManager>();
        camCtrl = UnityEngine.Object.FindAnyObjectByType<CameraController>();
        poseSel = UnityEngine.Object.FindAnyObjectByType<PoseSelectorUI>();
        hide = UnityEngine.Object.FindAnyObjectByType<BotHideController>(FindObjectsInactive.Include);
        gun = UnityEngine.Object.FindAnyObjectByType<Mode1FirstPersonGun>(FindObjectsInactive.Include);
        font = UIFont.Default;

        EnsureEventSystem();
        BuildButtons();

        // NOTE: the decompile computes `character != null` here but never uses the result — the
        // log message always concatenates a hardcoded literal `true`, not this check. Preserved
        // exactly; this may be a bug in the original source (a `$"...{character != null}..."`
        // that got typo'd or optimized away into a constant).
        bool _unusedCharacterCheck = character != null;
        Debug.Log("[WallClimber] Ready (character=" + true + ").");
    }

    // Confirmed as a genuine instance method (decompiled with an unused __this parameter) rather
    // than static, even though it never touches instance state.
    private void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null) return;

        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();

        Type inputModuleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (inputModuleType != null)
        {
            go.AddComponent(inputModuleType);
        }
        else
        {
            go.AddComponent<StandaloneInputModule>();
        }
    }

    private void BuildButtons()
    {
        GameObject canvasGO = SceneUI.GetOrCreateCanvas("ClimbButtonsCanvas", out bool created);
        climbCanvas = canvasGO;

        Canvas canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);
        if (created)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 7;

            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f); // portrait — consistent with other portrait-canvas UIs confirmed in earlier sessions
            scaler.matchWidthOrHeight = 1f;
        }

        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);

        Sprite triangle = MakeTriangle(0x60);
        Sprite circle = MakeCircle(0x60);

        Transform canvasTransform = canvasGO.transform;
        MakeButton(canvasTransform, "ClimbUp", triangle, circle, false, new Vector2(-70f, 340f), 1, climbUpIcon);
        MakeButton(canvasTransform, "ClimbDown", triangle, circle, true, new Vector2(-70f, 160f), -1, climbDownIcon);
    }

    private void Update()
    {
        if (character == null) return;

        // Frozen, or a seeker waiting for the hiders to hide: nobody climbs.
        bool suppressed = gmm != null && (gmm.PlayerFrozen || gmm.SeekerHideCountdown);
        if (!suppressed)
        {
            suppressed = camCtrl != null && camCtrl._freeLook;
        }

        if (suppressed)
        {
            up = 0;
            down = 0;
            if (climbCanvas != null && climbCanvas.activeSelf)
            {
                climbCanvas.SetActive(false);
            }
            BodyAnimation.SetSpeed(anim, 0f);
            return;
        }

        bool showClimbUI;
        bool notInteracting;
        if (WallsOpen)
        {
            if (state == State.OnWall)
            {
                showClimbUI = true;
            }
            else
            {
                showClimbUI = TryGetWall(out Vector3 _);
            }
            notInteracting = false;
        }
        else
        {
            showClimbUI = false;
            notInteracting = true;
        }

        if (climbCanvas != null)
        {
            bool currentlyActive = climbCanvas.activeSelf;
            if (showClimbUI != currentlyActive)
            {
                climbCanvas.SetActive(showClimbUI);
            }
        }

        if (!showClimbUI)
        {
            up = 0;
            down = 0;
        }

        if (notInteracting)
        {
            if (state != State.OnWall) return;

            // Was climbing, but the round ended — drop back to the ground.
            Grounding.ToFloor(character, charRends);
            LeaveWall();
            if (poseSel != null)
            {
                poseSel.ClearSelection();
                return;
            }
            BodyAnimation.SetPose(anim, 0);
            return;
        }

        if (cam == null)
        {
            cam = Camera.main;
        }

        Vector2 joyDir = joystick != null ? joystick.Direction : Vector2.zero;

        if (state != State.OnWall)
        {
            // Not climbing yet — only start if the "up" button is held and a wall is in range.
            if (up < 1) return;
            if (!TryGetWall(out Vector3 _)) return;

            state = State.OnWall;
            clinging = true;
            lastTouchedWallAt = Time.time;
            if (gun != null) gun.StandUp();     // nobody climbs lying down
            BodyAnimation.SetClimbing(anim, true);
            return;
        }

        // Already climbing — per-frame movement/animation update.
        float dt = Time.deltaTime;
        int upBefore = up;
        int downBefore = down;

        // A seeker aims with their body, so off the ground as on it the stick moves them the way
        // they face and never turns them. The camera sits over their shoulder, pulled about by
        // the wall they are on, and going by it sent them off at an angle to where they looked.
        bool facingLed = camCtrl != null && camCtrl.IsFirstPerson;
        Vector3 camHoriz = facingLed ? FacingHorizontal(joyDir) : CameraHorizontal(joyDir);
        bool stillOnWall = TryGetWall(out Vector3 _);
        if (stillOnWall) lastTouchedWallAt = Time.time;
        clinging = Time.time - lastTouchedWallAt < LetGoSeconds;

        if (down < 1 || stillOnWall)
        {
            BodyAnimation.SetClimbing(anim, clinging);

            if (!stillOnWall)
            {
                float moveMag = Mathf.Clamp01(camHoriz.magnitude);
                BodyAnimation.SetSpeed(anim, moveMag);
                if (moveMag > 0.01f && !facingLed)
                {
                    // CONFIRMED by decoding the two unnamed helpers' ARM64 bytes in libil2cpp.so:
                    // FUN_02349bd8 (RVA 0x2249BD8) takes a Vector3* and normalizes it
                    // (Vector3.Normalize), and FUN_02349a84 (RVA 0x2249A84) returns the Vector3
                    // statics at +0x18 (Vector3.up). Ghidra showed the rotation's y/z as the
                    // arguments only because it lost track of the helpers' float return registers.
                    Quaternion currentRot = character.rotation;
                    Quaternion targetRot = Quaternion.LookRotation(Vector3.Normalize(camHoriz), Vector3.up);
                    character.rotation = Quaternion.Slerp(currentRot, targetRot, dt * freeMoveTurnSpeed);
                }
            }
            else
            {
                BodyAnimation.SetSpeed(anim, 0f);
            }

            int climbDir = upBefore - downBefore;
            // NOTE: decompiled as three separate scalar computations against Vector3.up's raw
            // fields — algebraically exact to this simplified form (Vector3.up has no x/z, so
            // this reduces cleanly).
            Vector3 motion = (camHoriz + Vector3.up * climbDir) * (dt * climbSpeed);

            if (motion.sqrMagnitude > 0f)
            {
                if (cc != null && cc.enabled)
                {
                    cc.Move(motion);
                }
                else if (character != null)
                {
                    character.position += motion;
                }
            }

            if (climbDir >= 0 || !IsGrounded()) return;

            // Climbing down and touched ground — drop off the wall.
            Grounding.ToFloor(character, charRends);
            LeaveWall();
            return;
        }
        else
        {
            // Holding "down" and no longer touching a wall — fell off while descending.
            Grounding.ToFloor(character, charRends);
            LeaveWall();
            up = 0;
            down = 0;
        }
    }

    // ================= movement helpers =================

    private Vector3 CameraHorizontal(Vector2 inp)
    {
        float inputSqrMag = inp.x * inp.x + inp.y * inp.y;
        if (inputSqrMag < 0.0001f)
        {
            return Vector3.zero;
        }

        Vector3 forwardFlat;
        Vector3 rightFlat;

        if (cam != null)
        {
            Transform camT = cam.transform;
            forwardFlat = FlattenHorizontal(camT.forward, Vector3.forward);
            rightFlat = FlattenHorizontal(camT.right, Vector3.right);
        }
        else
        {
            forwardFlat = Vector3.forward;
            rightFlat = Vector3.right;
        }

        Vector3 result = forwardFlat * inp.y + rightFlat * inp.x;

        // FIXED: raw compares the SQUARED magnitude against 0.0001 before ever taking a square
        // root — the previous draft computed result.magnitude first and compared that to 0.0001,
        // a threshold ~100x more permissive, which would normalize (and thus redirect) small
        // vectors that the original leaves as-is. Matches the same squared-magnitude-first pattern
        // already correctly used in FlattenHorizontal below.
        float sqrMag = result.x * result.x + result.y * result.y + result.z * result.z;
        if (sqrMag > 0.0001f)
        {
            float mag = Mathf.Sqrt(sqrMag);
            result = mag <= 1e-5f ? Vector3.zero : result / mag;
        }

        float inputMag = Mathf.Clamp01(Mathf.Sqrt(inputSqrMag));
        return result * inputMag;
    }

    // The stick's direction by the way the character faces, level: forward on the stick is forward
    // for the body, as JoystickMover walks a seeker on the ground.
    private Vector3 FacingHorizontal(Vector2 inp)
    {
        float inputMag = Mathf.Clamp01(inp.magnitude);
        if (inputMag < 0.01f) return Vector3.zero;

        Vector3 forwardFlat = FlattenHorizontal(character.forward, Vector3.forward);
        Vector3 rightFlat = FlattenHorizontal(character.right, Vector3.right);
        Vector3 result = forwardFlat * inp.y + rightFlat * inp.x;
        return result.sqrMagnitude > 0.0001f ? result.normalized * inputMag : Vector3.zero;
    }

    // NOTE: pulled out for clarity — the decompile inlines this (zero the Y component, normalize,
    // fall back to the given world axis when nearly vertical) twice, once for camera-forward and
    // once for camera-right.
    private static Vector3 FlattenHorizontal(Vector3 v, Vector3 fallbackAxis)
    {
        float sqrMag = v.x * v.x + v.z * v.z;
        if (sqrMag < 0.0001f)
        {
            return fallbackAxis;
        }
        float mag = Mathf.Sqrt(sqrMag);
        if (mag <= 1e-5f)
        {
            return Vector3.zero;
        }
        return new Vector3(v.x / mag, 0f, v.z / mag);
    }

    private bool IsGrounded()
    {
        if (character == null) return false;

        Vector3 origin = character.position + Vector3.up;
        int hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, groundCheckDistance + 1f);

        for (int i = 0; i < hitCount; i++)
        {
            Transform hitT = _hits[i].transform;
            if (hitT == character) continue;
            if (hitT.IsChildOf(character)) continue;

            Collider col = _hits[i].collider;
            if (col is CharacterController) continue;
            if (col.isTrigger) continue;
            if (col.GetComponentInParent<Animator>() != null) continue;

            return true;
        }

        return false;
    }

    // Probes outward from the character (forward/back/right/left) at several heights along its
    // capsule, looking for the nearest climbable wall hit within wallCheckDistance.
    private bool TryGetWall(out Vector3 normal)
    {
        normal = -character.forward; // fallback value even on failure — matches decompile

        _probeDirs[0] = character.forward;
        _probeDirs[1] = -character.forward;
        _probeDirs[2] = character.right;
        _probeDirs[3] = -character.right;

        Vector3 pos = character.position;
        float bottomY = pos.y;
        float topY = pos.y;
        if (cc != null)
        {
            float scaleY = Mathf.Max(0.0001f, character.lossyScale.y);
            float halfHeight = scaleY * cc.height * 0.5f;
            float centerY = pos.y + scaleY * cc.center.y;
            bottomY = centerY - halfHeight;
            topY = centerY + halfHeight;
        }

        bool found = false;
        float bestDist = float.MaxValue;

        for (int h = 0; h < _probeHeights.Length; h++)
        {
            float t = Mathf.Clamp01(_probeHeights[h]);
            Vector3 probeOrigin = new Vector3(pos.x, bottomY + (topY - bottomY) * t, pos.z);

            for (int d = 0; d < _probeDirs.Length; d++)
            {
                int hitCount = Physics.RaycastNonAlloc(probeOrigin, _probeDirs[d], _hits, wallCheckDistance);
                for (int i = 0; i < hitCount; i++)
                {
                    Transform hitT = _hits[i].transform;
                    if (hitT == character) continue;
                    if (hitT.IsChildOf(character)) continue;

                    Collider col = _hits[i].collider;
                    if (!IsClimbable(col)) continue;

                    Vector3 hitNormal = _hits[i].normal;
                    // NOTE: decompiled as a manual dot/acos with a 1e-15 epsilon guard — confirmed
                    // equivalent to Vector3.Angle (same pattern as SampleSurface elsewhere).
                    float steepness = Vector3.Angle(hitNormal, Vector3.up);
                    if (steepness < minWallSteepness) continue;

                    float dist = _hits[i].distance;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        found = true;
                        normal = hitNormal;
                    }
                }
            }
        }

        return found;
    }

    private void SetClimb(int dir, bool held)
    {
        if (dir < 1)
        {
            down = held ? 1 : 0;
        }
        else
        {
            up = held ? 1 : 0;
        }
    }

    private bool IsClimbable(Collider col)
    {
        if (col == null) return false;
        if (col.isTrigger) return false;
        if (SafeTags.Has(col, groundTag)) return false;
        if (climbAnySurface)
        {
            return !IsCharacter(col);
        }
        return SafeTags.Has(col, wallTag);
    }

    private bool IsCharacter(Collider col)
    {
        if (col is CharacterController) return true; // NOTE: inlined type check, confirmed equivalent to `is CharacterController`

        if (_isCharacterCache.TryGetValue(col, out bool cached))
        {
            return cached;
        }

        bool isChar = col != null && col.GetComponentInParent<Animator>() != null;
        _isCharacterCache[col] = isChar;
        return isChar;
    }

    // ================= UI building =================

    public void AuthorUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }
        EnsureEventSystem();
        BuildButtons();
    }

    private static Sprite MakeTriangle(int s)
    {
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < s; y++)
        {
            float v = (float)y / (s - 1);
            for (int x = 0; x < s; x++)
            {
                float u = (float)x / (s - 1);
                bool inTriangleBand = Mathf.Abs(u - 0.5f) <= (1f - v) * 0.5f;

                bool nearOrPastTip;
                bool exactlyAtTip;
                if (inTriangleBand)
                {
                    exactlyAtTip = v == 0.96f;
                    nearOrPastTip = v >= 0.96f;
                }
                else
                {
                    exactlyAtTip = false;
                    nearOrPastTip = true;
                }

                bool blackPixel = v < 0.12f || (nearOrPastTip && !exactlyAtTip);
                float value = blackPixel ? 0f : 1f;
                tex.SetPixel(x, y, new Color(value, value, value, value));
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, s, s), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite MakeCircle(int s)
    {
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        float radius = s / 2f;
        for (int y = 0; y < s; y++)
        {
            float dy = (y + 0.5f) - radius;
            for (int x = 0; x < s; x++)
            {
                float dx = (x + 0.5f) - radius;
                float edge = radius - Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(edge);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, s, s), new Vector2(0.5f, 0.5f), 100f);
    }

    private void MakeButton(Transform parent, string name, Sprite triangle, Sprite circle, bool flip, Vector2 anchoredPos, int dir, Sprite iconSprite)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        Image img = SceneUI.GetOrAdd<Image>(go);

        if (iconSprite != null)
        {
            img.sprite = null;
            img.color = new Color(0f, 0f, 0f, 0f);
        }
        else
        {
            img.color = new Color(0.2f, 0.22f, 0.28f, 0.9f);
            img.sprite = circle;
        }

        if (created)
        {
            RectTransform rt = img.rectTransform;
            rt.anchorMax = new Vector2(1f, 0f);
            rt.anchorMin = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = new Vector2(150f, 150f);
        }

        Transform buttonTransform = go.transform;
        GameObject iconGO = SceneUI.GetOrCreate("Arrow", buttonTransform, out bool _);
        Image iconImg = SceneUI.GetOrAdd<Image>(iconGO);

        iconImg.color = Color.white;
        iconImg.raycastTarget = false;
        bool hasCustomIcon = iconSprite != null;
        iconImg.sprite = hasCustomIcon ? iconSprite : triangle;
        iconImg.preserveAspect = hasCustomIcon;

        // NOTE: unlike the outer button's RectTransform above (only touched when freshly
        // created), the icon's RectTransform is set up unconditionally every call. Confirmed via
        // trace, not a simplification on my part.
        RectTransform iconRt = iconImg.rectTransform;
        iconRt.anchorMin = new Vector2(0.5f, 0.5f);
        iconRt.anchorMax = new Vector2(0.5f, 0.5f);
        iconRt.pivot = new Vector2(0.5f, 0.5f);
        iconRt.anchoredPosition = Vector2.zero;
        iconRt.sizeDelta = hasCustomIcon ? new Vector2(120f, 110f) : new Vector2(74f, 74f);
        iconRt.localEulerAngles = (flip && !hasCustomIcon) ? new Vector3(0f, 0f, 180f) : Vector3.zero;

        // CONFIRMED: raw stores two Actions at ClimbHold +0x20 and +0x28, which dump.cs names onDown
        // and onUp. Their bodies, WallClimber.<>c__DisplayClass51_0.<MakeButton>b__0/b__1
        // (RVA 0x2287044 / 0x2287068, closure fields <>4__this and dir), were decoded from their
        // ARM64 bytes: each loads (this, dir), sets held = 1 / 0, and branches straight to SetClimb
        // (file offset 0x2281DE4). So press = SetClimb(dir, true), release = SetClimb(dir, false).
        ClimbHold hold = SceneUI.GetOrAdd<ClimbHold>(go);
        hold.onDown = () => SetClimb(dir, true);
        hold.onUp = () => SetClimb(dir, false);
    }

    private Image NewImage(string name, Transform parent, Color color, Sprite sprite)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        img.sprite = sprite;
        return img;
    }

    private Text NewText(string name, Transform parent, string content, int size, TextAnchor anchor, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var txt = go.AddComponent<Text>();
        txt.font = font;
        txt.text = content;
        txt.fontSize = size;
        txt.alignment = anchor;
        txt.color = color;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        txt.verticalOverflow = VerticalWrapMode.Overflow;
        return txt;
    }

    // Confirmed static (no __this in the decompiled signature, unlike EnsureEventSystem/HasInternet-style methods).
    private static void Stretch(Component c)
    {
        RectTransform rt = c.transform as RectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}

/// <summary>
/// CompareTag for a tag the project may not define. Unity logs "Tag: Ground is not defined" on
/// every CompareTag with an undefined tag, and the wall probes ask on every hit, every frame: no
/// "Ground" or "Wall" tag exists in TagManager, so standing by a wall logged thousands of errors a
/// minute. Whether a tag exists is found out once (FindWithTag throws for an undefined one).
/// </summary>
public static class SafeTags
{
    private static readonly Dictionary<string, bool> defined = new Dictionary<string, bool>(StringComparer.Ordinal);

    public static bool Has(Component component, string tag) => component != null && Defined(tag) && component.CompareTag(tag);

    public static bool Defined(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        if (defined.TryGetValue(tag, out bool known)) return known;

        bool exists;
        try
        {
            GameObject.FindWithTag(tag);
            exists = true;
        }
        catch (UnityException)
        {
            exists = false;
        }

        defined[tag] = exists;
        return exists;
    }
}
