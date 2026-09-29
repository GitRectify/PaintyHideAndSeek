using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Large new class this session. Movement/climb logic + a runtime-built on-screen up/down button
// UI (mirrors the SceneUI-based UI-building pattern confirmed in earlier sessions).
public class WallClimber : MonoBehaviour
{
    // ---------------- Inspector-tunable fields (defaults come from the decompiled ctor,
    // which is exactly the compiled form of C# field initializers — written here as field
    // initializers per the standing convention rather than as a custom constructor) ----------------
    public string characterRootName = "Player";
    public bool climbAnySurface = true;
    public string wallTag = "Wall";
    public string groundTag = "Ground";
    public float climbSpeed = 8f;
    public float freeMoveTurnSpeed = 12f;
    public float minWallSteepness = 50f;
    public float wallCheckDistance = 4f;
    public float groundCheckDistance = 9f;

    // NOTE: accessibility (public vs private) on the tunables above is inferred from the Unity
    // idiom of Inspector-exposed defaults set in a field initializer — not separately confirmed.
    public Sprite climbUpIcon;
    public Sprite climbDownIcon;

    // ---------------- internal state ----------------
    private Transform character;
    private Renderer[] charRends;
    private CharacterController cc;
    private Animator anim;
    private Joystick joystick;
    private Camera cam;
    private BotSeekController seek;
    private GameModeManager gmm;
    private CameraController camCtrl;
    private PoseSelectorUI poseSel;
    private Font font;
    private GameObject climbCanvas;

    // NOTE: typed as int (not bool) — confirmed via SetClimb assigning `(uint)held` into these,
    // and Update() doing integer subtraction (up - down) to get a -1/0/1 climb direction.
    private int up;
    private int down;

    // 0 = not climbing, 1 = on a wall. Confirmed via get_IsOnWall (`state == 1`).
    private int state;

    private readonly RaycastHit[] _hits = new RaycastHit[16];
    private readonly Vector3[] _probeDirs = new Vector3[4];
    private readonly Dictionary<Collider, bool> _isCharacterCache = new Dictionary<Collider, bool>();

    // ---------------- static (from .cctor) ----------------
    // NOTE: the actual 4 float values are embedded as a raw byte blob in the game's
    // <PrivateImplementationDetails> class (a compiler-generated array-initializer field), which
    // isn't in the string-literal dump and isn't otherwise recoverable from this decompile. These
    // are height fractions (0..1) sampled between the character capsule's bottom and top in
    // TryGetWall — placeholder values below, MUST be corrected from the real binary/game data.
    private static readonly float[] WallProbeHeightFractions = { 0.2f, 0.5f, 0.8f, 0.95f }; // TODO: unconfirmed — real values not recoverable from this dump

    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int PoseHash = Animator.StringToHash("Pose");

    // ================= properties =================

    public bool IsOnWall => state == 1;

    // True when not currently on a wall, but chasing the player and near a climbable one — used
    // to suppress some other system's rotation control while approaching a wall.
    public bool BlocksRotation
    {
        get
        {
            if (state == 1) return false;
            if (character == null) return false;
            if (seek == null) return false;
            if (!seek.Active) return false;
            if (!seek.chasePlayer) return false;
            return TryGetWall(out Vector3 _);
        }
    }

    public bool IsNearWall()
    {
        return TryGetWall(out Vector3 _);
    }

    public bool HasUserPose
    {
        get
        {
            if (poseSel == null) return false;
            // CONFIRMED: decoded the raw sign-bit bit-trick byte-by-byte — it is exactly
            // "activeSlot >= 0" (i.e. some pose slot is selected), not just a plausible guess.
            return poseSel.activeSlot >= 0;
        }
    }

    // ================= lifecycle =================

    // FIXED: this entire method was missing from the previous draft — it's a public API other
    // classes call directly (e.g. ChameleonPaint.GroundPlayer() calls wallClimber.DropToGround()).
    // Matches the raw WallClimber$$DropToGround exactly: unlike the two "fell off the wall" resets
    // inside Update(), this path does NOT gate on HasUserPose before forcing Pose back to 0 — that
    // asymmetry is confirmed in the raw code, not an inconsistency to "fix".
    public void DropToGround()
    {
        if (character == null) return;

        Grounding.ToFloor(character, charRends);
        up = 0;
        down = 0;
        state = 0;

        if (poseSel != null)
        {
            poseSel.ClearSelection();
            return;
        }
        if (anim == null) return;
        anim.SetInteger(PoseHash, 0);
    }

    public void Start()
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

        joystick = UnityEngine.Object.FindFirstObjectByType<Joystick>();
        cam = Camera.main;
        seek = UnityEngine.Object.FindFirstObjectByType<BotSeekController>();
        gmm = UnityEngine.Object.FindFirstObjectByType<GameModeManager>();
        camCtrl = UnityEngine.Object.FindFirstObjectByType<CameraController>();
        poseSel = UnityEngine.Object.FindFirstObjectByType<PoseSelectorUI>();
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
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;

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

    public void Update()
    {
        if (character == null) return;

        bool suppressed = gmm != null && gmm.PlayerFrozen;
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
            if (anim == null) return;
            anim.SetFloat(SpeedHash, 0f);
            return;
        }

        bool showClimbUI;
        bool notInteracting;
        if (seek != null && seek.Active && seek.chasePlayer)
        {
            if (state == 1)
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
            if (state != 1) return;

            // Was climbing, but the chase ended — drop back to the ground.
            Grounding.ToFloor(character, charRends);
            state = 0;
            if (poseSel != null)
            {
                poseSel.ClearSelection();
                return;
            }
            if (anim == null) return;
            // FIXED: raw does NOT gate this reset on HasUserPose — unlike the two "fell off the
            // wall" resets further down in this method (which both do check it), this path always
            // forces Pose back to 0. Confirmed by tracing all four Pose-reset call sites in the
            // raw dump; this asymmetry (always clear pose when the chase itself ends, but respect
            // a manually-selected pose only while actively climbing) appears intentional.
            anim.SetInteger(PoseHash, 0);
            return;
        }

        if (cam == null)
        {
            cam = Camera.main;
        }

        Vector2 joyDir = joystick != null ? joystick.Direction : Vector2.zero;

        if (state != 1)
        {
            // Not climbing yet — only start if the "up" button is held and a wall is in range.
            if (up < 1) return;
            if (!TryGetWall(out Vector3 _)) return;

            state = 1;
            if (anim == null) return;
            if (HasUserPose) return;
            anim.SetInteger(PoseHash, 1);
            return;
        }

        // Already climbing — per-frame movement/animation update.
        float dt = Time.deltaTime;
        int upBefore = up;
        int downBefore = down;

        Vector3 camHoriz = CameraHorizontal(joyDir);
        bool stillOnWall = TryGetWall(out Vector3 _);

        if (down < 1 || stillOnWall)
        {
            if (anim != null && !HasUserPose)
            {
                anim.SetInteger(PoseHash, stillOnWall ? 1 : 0);
            }

            if (!stillOnWall)
            {
                float moveMag = Mathf.Clamp01(camHoriz.magnitude);
                if (anim != null)
                {
                    anim.SetFloat(SpeedHash, moveMag);
                }
                if (moveMag > 0.01f)
                {
                    // LOW CONFIDENCE BLOCK: the decompile for this specific facing-rotation
                    // sub-step is genuinely ambiguous — the raw disassembly appears to reuse the
                    // character's OWN current rotation's y/z quaternion components as if they
                    // were vector components for the LookRotation "forward"/"upwards" args, which
                    // wouldn't make geometric sense. Two unnamed helper calls in this stretch
                    // (extracting camHoriz components) couldn't be fully resolved either. Given
                    // the overwhelming pattern elsewhere of pure register/stack-slot reuse causing
                    // exactly this kind of misleading appearance, this is implemented as the
                    // idiomatic version instead — face the movement direction, upright — but this
                    // block should be verified against actual game behavior rather than trusted.
                    Quaternion currentRot = character.rotation;
                    Quaternion targetRot = Quaternion.LookRotation(camHoriz, Vector3.up);
                    character.rotation = Quaternion.Slerp(currentRot, targetRot, dt * freeMoveTurnSpeed);
                }
            }
            else
            {
                if (anim != null)
                {
                    anim.SetFloat(SpeedHash, 0f);
                }
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
            state = 0;
            if (anim == null) return;
            if (HasUserPose) return;
            anim.SetInteger(PoseHash, 0);
            return;
        }
        else
        {
            // Holding "down" and no longer touching a wall — fell off while descending.
            Grounding.ToFloor(character, charRends);
            state = 0;
            up = 0;
            down = 0;
            if (anim == null) return;
            if (HasUserPose) return;
            anim.SetInteger(PoseHash, 0);
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
            if (hitT != null && hitT.IsChildOf(character)) continue;

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

        for (int h = 0; h < WallProbeHeightFractions.Length; h++)
        {
            float t = Mathf.Clamp01(WallProbeHeightFractions[h]);
            Vector3 probeOrigin = new Vector3(pos.x, bottomY + (topY - bottomY) * t, pos.z);

            for (int d = 0; d < _probeDirs.Length; d++)
            {
                int hitCount = Physics.RaycastNonAlloc(probeOrigin, _probeDirs[d], _hits, wallCheckDistance);
                for (int i = 0; i < hitCount; i++)
                {
                    Transform hitT = _hits[i].transform;
                    if (hitT == character) continue;
                    if (hitT != null && hitT.IsChildOf(character)) continue;

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

    public void SetClimb(int dir, bool held)
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
        if (col.CompareTag(groundTag)) return false;
        if (climbAnySurface)
        {
            return !IsCharacter(col);
        }
        return col.CompareTag(wallTag);
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

        // NOTE: the button's press/release wiring below is only partially decompiled. What's
        // confirmed: a compiler-generated closure capturing `this` and `dir` is constructed, and
        // a ClimbHold component is added to `go`. The two closure callback bodies
        // (<MakeButton>b__0 / <MakeButton>b__1) were never in the pasted dump — only their names
        // and the fact two System.Action instances get built and stashed somewhere on the
        // ClimbHold instance. Given the closure captures `this`+`dir`, the obvious inferred call
        // is SetClimb(dir, true/false) on press/release, but this is INFERRED, not confirmed —
        // left as TODOs rather than guessed field names.
        ClimbHold hold = SceneUI.GetOrAdd<ClimbHold>(go);
        // TODO: not decompiled — inferred shape only, exact field names on ClimbHold unconfirmed:
        // hold.onPress = () => SetClimb(dir, true);
        // hold.onRelease = () => SetClimb(dir, false);
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

    private Text NewText(string name, Transform parent, string content, int size, int anchor, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var txt = go.AddComponent<Text>();
        txt.font = font;
        txt.text = content;
        txt.fontSize = size;
        txt.alignment = (TextAnchor)anchor;
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