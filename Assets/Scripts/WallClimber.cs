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
    private Vector3 lastWallNormal = Vector3.back;   // the face last climbed, for going over its top

    // Walls have gaps (shelves, window frames) and the probes see straight through them. Crossing one
    // is still the same climb: the body must not let go and take hold again, on anybody's screen.
    private const float LetGoSeconds = 1f;
    private GameObject climbCanvas;
    private readonly RaycastHit[] _hits = new RaycastHit[16];
    private readonly Vector3[] _probeDirs = new Vector3[4];

    // The probes run up the body from 0.15 to 0.92 of its height (the original game's lowest and
    // highest of its four), no further apart than ProbeSpacing world units. Four were too few for a
    // ladder (2026-11-24, Room4): its rungs are 1.25 thick with 4.25 of air between them, and at
    // many heights all four looked through the gaps, so a player who stopped there let go of it
    // after LetGoSeconds and fell.
    private const float LowestProbe = 0.15f;
    private const float HighestProbe = 0.92f;
    private const float ProbeSpacing = 0.9f;

    // How far past the feet and the head the climb looks for more of what it is on, as a share of
    // the body's height: wall just under the feet is the top of it reached, wall over the head is a
    // gap in it being crossed.
    private const float NearReach = 0.5f;

    // The walls the probes last touched, one per direction: the stick does not push into them.
    private readonly Vector3[] _touchNormals = new Vector3[4];
    private int _touchCount;

    private readonly Dictionary<Collider, bool> _isCharacterCache = new Dictionary<Collider, bool>();


    // ================= properties =================

    public bool IsOnWall => state == State.OnWall;

    /// <summary>The Climb (up) button is held down now (its colour shows it, 2026-11-25).</summary>
    public bool UpHeld => up > 0;

    /// <summary>The Down button is held down now.</summary>
    public bool DownHeld => down > 0;

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

        // The stick and the movement keys together, as JoystickMover reads them on the ground: on a
        // wall the keys used to do nothing, since only the on-screen stick was read here.
        Vector2 joyDir = KeyBindings.WithKeys(joystick != null ? joystick.Direction : Vector2.zero);

        if (state != State.OnWall)
        {
            // Not climbing yet — only start if the "up" button is held and a wall is in range.
            if (up < 1) return;
            if (!TryGetWall(out Vector3 _)) return;
            // Nobody climbs lying down, and a seeker lying under a table stays there (BodyCapsule).
            if (gun != null && !gun.TryStandUp()) return;

            state = State.OnWall;
            clinging = true;
            lastTouchedWallAt = Time.time;
            BodyAnimation.SetClimbing(anim, true);
            return;
        }

        // Already climbing — per-frame movement/animation update.
        float dt = Time.deltaTime;
        int climbDir = up - down;

        // A seeker aims with their body, so off the ground as on it the stick moves them the way
        // they face and never turns them. The camera sits over their shoulder, pulled about by
        // the wall they are on, and going by it sent them off at an angle to where they looked.
        bool facingLed = camCtrl != null && camCtrl.IsFirstPerson;
        Vector3 camHoriz = facingLed ? FacingHorizontal(joyDir) : CameraHorizontal(joyDir);

        // Where the thing climbed is: against the body; or, with nothing against it, just under the
        // feet (its top reached) or just over the head (a gap in it being crossed). Any of them is
        // still holding on to it.
        bool touching = TryGetWall(out Vector3 wallNormal);
        if (touching) lastWallNormal = wallNormal;
        bool above = !touching && WallNear(true);
        bool below = !touching && !above && WallNear(false);
        if (touching || above || below) lastTouchedWallAt = Time.time;
        clinging = Time.time - lastTouchedWallAt < LetGoSeconds;

        // Off the wall for good: over the top of what was climbed with something under the feet
        // to stand on, or nothing of it near for LetGoSeconds (moved off its side). The climb ends
        // and JoystickMover's gravity has the body again: it used to stay "on the wall" with no
        // wall, and the stick walked it about in the air.
        if (!touching && (!clinging || StandsOnTop()))
        {
            LeaveWall();
            BodyAnimation.SetSpeed(anim, 0f);
            return;
        }

        BodyAnimation.SetClimbing(anim, clinging);

        if (touching)
        {
            // Against it, the stick moves the body along it, never into it: into a solid wall
            // that went nowhere anyway, but into a ladder it pushed the body in between two
            // rungs, wedged under the upper one (2026-11-24).
            camHoriz = AlongWalls(camHoriz);
            BodyAnimation.SetSpeed(anim, 0f);
        }
        else
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

        // Up only while there is more of it at the body or over the head. At the top of a ladder
        // or a wall the body stops there, holding on (the stick takes it over the top, or Down
        // back down), where it used to rise on into the air for LetGoSeconds and then drop to the
        // floor (2026-11-24).
        int vertical = climbDir > 0 && !touching && !above ? 0 : climbDir;
        Vector3 motion = (camHoriz + Vector3.up * vertical) * (dt * climbSpeed);

        // Climb held at the top (the face ends just under the feet): over it and onto the top, as a
        // player holding Climb expects. It used to stop there, hanging, until the stick pushed it
        // over, and a player holding Climb alone saw the climb freeze (2026-11-25). The stick, when
        // it is pushed, still says which way; a little rise clears the edge.
        if (climbDir > 0 && !touching && !above && below)
        {
            Vector3 over = camHoriz.sqrMagnitude > 0.01f
                ? camHoriz
                : -new Vector3(lastWallNormal.x, 0f, lastWallNormal.z).normalized;
            motion = (over + Vector3.up * 0.35f) * (dt * climbSpeed);
        }

        CollisionFlags flags = CollisionFlags.None;
        if (motion.sqrMagnitude > 0f)
        {
            if (cc != null && cc.enabled)
            {
                flags = cc.Move(motion);
            }
            else if (character != null)
            {
                character.position += motion;
            }
        }

        if (vertical >= 0) return;

        // Climbing down, the body comes off it standing once the feet meet the floor (or anything
        // else under them). It used to drop the last few units to the floor in one frame as soon
        // as the floor was in reach, and to drop all the way whenever its probes looked through a
        // gap on the way down.
        if (cc != null && cc.enabled)
        {
            if ((flags & CollisionFlags.Below) != 0) LeaveWall();
            return;
        }

        if (!IsGrounded()) return;
        Grounding.ToFloor(character, charRends);
        LeaveWall();
    }

    // The stick's way with the part of it that pushes into the walls touched taken out.
    private Vector3 AlongWalls(Vector3 move)
    {
        for (int i = 0; i < _touchCount; i++)
        {
            Vector3 n = _touchNormals[i];
            n.y = 0f;
            if (n.sqrMagnitude < 0.0001f) continue;
            n.Normalize();
            float into = Vector3.Dot(move, n);
            if (into < 0f) move -= n * into;
        }
        return move;
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

    // How far under the feet a top still counts as stood on, as a share of the body's height.
    private const float TopOutReach = 0.15f;

    // A walkable surface right under the feet (not a wall, not a character): the climb has come
    // over the top of what it went up, or down to a floor or a shelf.
    private bool StandsOnTop()
    {
        if (cc == null || character == null) return false;

        float scaleY = Mathf.Max(0.0001f, character.lossyScale.y);
        float halfHeight = scaleY * cc.height * 0.5f;
        Vector3 center = character.position + Vector3.up * (scaleY * cc.center.y);
        float reach = halfHeight * (1f + 2f * TopOutReach);

        int hitCount = Physics.RaycastNonAlloc(center, Vector3.down, _hits, reach, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hitCount; i++)
        {
            Transform hitT = _hits[i].transform;
            if (hitT == character || hitT.IsChildOf(character)) continue;
            if (IsCharacter(_hits[i].collider)) continue;
            if (Vector3.Angle(_hits[i].normal, Vector3.up) >= minWallSteepness) continue;
            return true;
        }
        return false;
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

    // Probes outward from the character (forward/back/right/left) at heights all the way up its
    // capsule, looking for the nearest climbable wall hit within wallCheckDistance.
    private bool TryGetWall(out Vector3 normal)
    {
        normal = -character.forward; // fallback value even on failure — matches decompile
        BodySpan(out float bottomY, out float topY);
        float height = topY - bottomY;
        return ProbeWalls(bottomY + height * LowestProbe, bottomY + height * HighestProbe, true, ref normal);
    }

    // More of the climbable thing just over the head (upward) or just under the feet, within
    // NearReach of the body's height: the climb holds on to it there.
    private bool WallNear(bool upward)
    {
        BodySpan(out float bottomY, out float topY);
        float reach = (topY - bottomY) * NearReach;
        Vector3 normal = Vector3.zero;
        return upward
            ? ProbeWalls(topY, topY + reach, false, ref normal)
            : ProbeWalls(bottomY - reach, bottomY + (topY - bottomY) * LowestProbe, false, ref normal);
    }

    // The bottom and top of the body, in world units.
    private void BodySpan(out float bottomY, out float topY)
    {
        Vector3 pos = character.position;
        bottomY = pos.y;
        topY = pos.y;
        if (cc == null) return;

        float scaleY = Mathf.Max(0.0001f, character.lossyScale.y);
        float halfHeight = scaleY * cc.height * 0.5f;
        float centerY = pos.y + scaleY * cc.center.y;
        bottomY = centerY - halfHeight;
        topY = centerY + halfHeight;
    }

    // Horizontal rays from the body's axis, at heights from fromY to toY no further apart than
    // ProbeSpacing, in the four directions: the nearest climbable wall among them. With remember,
    // the nearest wall each way is kept for AlongWalls.
    private bool ProbeWalls(float fromY, float toY, bool remember, ref Vector3 normal)
    {
        _probeDirs[0] = character.forward;
        _probeDirs[1] = -character.forward;
        _probeDirs[2] = character.right;
        _probeDirs[3] = -character.right;
        if (remember) _touchCount = 0;

        Vector3 pos = character.position;
        int steps = Mathf.Max(1, Mathf.CeilToInt((toY - fromY) / ProbeSpacing));
        bool found = false;
        float bestDist = float.MaxValue;

        for (int d = 0; d < _probeDirs.Length; d++)
        {
            float dirBest = float.MaxValue;
            Vector3 dirNormal = Vector3.zero;

            for (int h = 0; h <= steps; h++)
            {
                Vector3 probeOrigin = new Vector3(pos.x, Mathf.Lerp(fromY, toY, h / (float)steps), pos.z);
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
                    if (dist < dirBest)
                    {
                        dirBest = dist;
                        dirNormal = hitNormal;
                    }
                }
            }

            if (dirBest == float.MaxValue) continue;
            found = true;
            if (remember) _touchNormals[_touchCount++] = dirNormal;
            if (dirBest < bestDist)
            {
                bestDist = dirBest;
                normal = dirNormal;
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
