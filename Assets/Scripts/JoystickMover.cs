using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output. Fields (names, order, attributes,
// offsets) and method accessibility match dump.cs (TypeDefIndex 9682). All string literals are
// confirmed against Dumpstringliteral.json.
//
// Decoded helpers: FUN_022f7090 = Vector3.zero, FUN_02349ca4 = Vector2.magnitude,
// FUN_02349ac8 = Vector3.magnitude, FUN_023701ec = Vector3.Normalize() (in place, not inlined; the
// .normalized property is what gets inlined in CameraRelative). The 0.05 (0x3D4CCCCD) fminnm is
// Mathf.Min(Time.deltaTime, 0.05f).
//
// Inlined in raw: CameraController.FreeLook / IsFirstPerson (private _freeLook / firstPerson reads),
// GameModeManager.PlayerFrozen (backing field), Vector3.MoveTowards, Mathf.Clamp / Clamp01 / Max.
// Ghidra printed the Ready log's bools as Boolean.ToString(true); they are the null checks.
// fpTurnSpeed is declared but not used by any method.
//
// Convention: where raw jumps to the NullReferenceException stub, the C# just dereferences
// naturally; explicit null checks are kept only where raw really skips.
public class JoystickMover : MonoBehaviour
{
    [Header("Character")]
    public string characterRootName = "Player";
    public float moveSpeed = 20f;
    [Tooltip("Acceleration/deceleration (u/s²) for smooth movement: pushing the joystick gradually accelerates to `moveSpeed`, while releasing it gradually decelerates to zero (avoiding jerky stops). Lower values ​​result in smoother, more fluid motion; higher values ​​provide snappier, more responsive control. Setting this to a very high value (e.g., 999) results in instantaneous response, mimicking the original behavior.")]
    public float acceleration = 80f;
    private Vector3 _moveVel;
    public bool moveRelativeToCamera = true;
    public bool faceMovement = true;
    public float turnSpeed = 12f;
    [Tooltip("Tallest stair step (world units) the player can walk up. Kept CONSTANT in world space across the Play Now 0.5x scaling by compensating the CharacterController.stepOffset for the player's scale — Room2's staircases have ~4u risers, which the default stepOffset (0.3) can't climb.")]
    public float climbStepHeight = 4f;
    [Tooltip("Downward acceleration (world u/s²). A CharacterController has NO gravity of its own — without this the player FLOATS when walking DOWN stairs / off a ledge. Big because the world is large (player ~14u tall).")]
    public float gravity = 70f;
    private float _vy;
    [Tooltip("Jump height in WORLD UNITS for the Seeker-mode 'Jump' button. The character rises to about this height then falls back under gravity (no jump animation needed). Tune this for a higher/lower hop — do NOT change 'gravity' (it's shared with normal falling, so lowering it would make walking DOWN stairs float too). At gravity 70, height 8 ≈ a 1-second hop.")]
    public float jumpHeight = 8f;
    [Tooltip("Stuck recovery: if the player pushes to move but makes ~no progress (wedged inside a concave furniture/plant collider) for this long, snap it back to the last spot it moved freely. Concave room colliders can TRAP the CharacterController with no way to push out, so a teleport-back is the reliable escape. 0 = disable.")]
    public float unstickAfter = 0.6f;
    private Vector3 _lastFreePos;
    private bool _haveFreePos;
    private float _stuckTimer;
    private bool _joyHidden;
    [Tooltip("First-person (Mode 1) turn speed in degrees/sec when pushing the joystick left/right.")]
    public float fpTurnSpeed = 120f;

    [Header("Joystick Pack")]
    [Tooltip("Drag a Joystick Pack joystick here (Fixed/Floating/Dynamic/Variable). Auto-found if left empty.")]
    public Joystick joystick;

    [Header("Character rotation buttons (Btn_XoayNhanVat toggles the two rotation buttons)")]
    [Tooltip("On/Off button; two rotary knobs (left/right)—push to reveal, push again to hide. Search by name.")]
    public string rotateButtonName = "Btn_XoayNhanVat";
    [Tooltip("Hold the button to rotate the character to the left (from right to left). Search by name.")]
    public string rotateLeftButtonName = "ButtonRotationLeft";
    [Tooltip("Hold the button to rotate the character to the RIGHT (from left to right). Search by name.")]
    public string rotateRightButtonName = "ButtonRotationRight";
    [Tooltip("Rotation speed when holding the button (degrees/second).")]
    public float rotateButtonSpeed = 120f;
    private GameObject rotateButtonGo;
    private GameObject rotLeftGo;
    private GameObject rotRightGo;
    private bool _rotBtnsShown;
    private int _rotHeldDir;

    [Header("Sprite Btn_XoayNhanVat based on state (similar to Btn_DoiDang)")]
    [Tooltip("Sprite when ON (the two rotary controls are visible) — e.g., 'Rounded Rectangle 5 copy 8'.")]
    public Sprite rotOnSprite;
    [Tooltip("Sprite when OFF (2 hidden dials). Leave blank = keep the button's original sprite.")]
    public Sprite rotOffSprite;
    private Image _rotBtnImg;
    private Transform character;
    private Camera cam;
    private WallClimber climber;
    private CameraController camCtrl;
    private GameModeManager gmm;
    private CharacterController cc;
    private Animator anim;
    private static readonly int SpeedHash = Animator.StringToHash("Speed");

    private void Start()
    {
        GameObject player = PlayerRef.Resolve(characterRootName);
        if (player != null)
        {
            character = player.transform;
        }
        else
        {
            Debug.LogWarning("[JoystickMover] Character '" + characterRootName + "' not found.");
        }
        if (character != null)
        {
            cc = character.GetComponent<CharacterController>();
            anim = character.GetComponent<Animator>();
        }
        cam = Camera.main;
        if (joystick == null)
        {
            joystick = FindFirstObjectByType<Joystick>(FindObjectsInactive.Include);
        }
        if (joystick == null)
        {
            Debug.LogWarning("[JoystickMover] No Joystick (Joystick Pack) assigned or found in the scene.");
        }
        climber = FindFirstObjectByType<WallClimber>(FindObjectsInactive.Include);
        camCtrl = FindFirstObjectByType<CameraController>(FindObjectsInactive.Include);
        gmm = FindFirstObjectByType<GameModeManager>(FindObjectsInactive.Include);
        WireRotateButton();
        Debug.Log("[JoystickMover] Ready (character=" + (character != null) + ", joystick=" + (joystick != null) + ").");
    }

    private void Update()
    {
        // Keep the climbable step constant in world units whatever the player's scale.
        if (cc != null && character != null)
        {
            float s = Mathf.Max(0.1f, character.lossyScale.y);
            float h = cc.height;
            cc.stepOffset = Mathf.Clamp(climbStepHeight / s, 0.1f, s * h - 0.5f);
        }

        // The rotate pair hides itself when its toggle button is not on screen.
        if (_rotBtnsShown && rotateButtonGo != null && !rotateButtonGo.activeInHierarchy)
        {
            ShowRotateButtons(false);
        }
        if (_rotHeldDir != 0 && character != null && (climber == null || !climber.BlocksRotation))
        {
            character.Rotate(0f, rotateButtonSpeed * _rotHeldDir * Time.deltaTime, 0f, Space.World);
        }

        if (character == null) return;
        if (joystick == null) return;

        // Frozen player: joystick hidden; frozen or seeker hide countdown: no movement at all.
        bool frozen = gmm != null && gmm.PlayerFrozen;
        if (joystick != null && _joyHidden != frozen)
        {
            joystick.gameObject.SetActive(!frozen);
            _joyHidden = frozen;
        }
        bool countdown = gmm != null && gmm.SeekerHideCountdown;
        if (countdown || frozen)
        {
            SetSpeed(0f);
            _vy = 0f;
            _moveVel = Vector3.zero;
            return;
        }

        // Free-look scout: the joystick pans the camera; the player only falls.
        if (camCtrl != null && camCtrl.FreeLook)
        {
            camCtrl.PanFree(joystick.Direction, Time.deltaTime);
            if (climber != null && climber.IsOnWall)
            {
                _vy = 0f;
            }
            else
            {
                MoveWithGravity(Vector3.zero);
            }
            SetSpeed(0f);
            _moveVel = Vector3.zero;
            return;
        }
        // On a wall the WallClimber moves the player.
        if (climber != null && climber.IsOnWall)
        {
            _vy = 0f;
            _moveVel = Vector3.zero;
            return;
        }

        Vector3 target = Vector3.zero;
        if (camCtrl != null && camCtrl.IsFirstPerson)
        {
            // Mode 1: strafe / walk relative to the character's level facing, no turning.
            Vector2 d = joystick.Direction;
            if (Mathf.Clamp01(d.magnitude) >= 0.01f)
            {
                Vector3 fwd = character.forward;
                fwd.y = 0f;
                if (fwd.x * fwd.x + fwd.z * fwd.z > 0.001f)
                {
                    fwd.Normalize();
                }
                Vector3 right = character.right;
                right.y = 0f;
                if (right.x * right.x + right.z * right.z > 0.001f)
                {
                    right.Normalize();
                }
                target = (fwd * d.y + right * d.x) * moveSpeed;
            }
        }
        else
        {
            // Third person: camera-relative direction, speed by stick deflection, turn to face it.
            Vector2 inp = joystick.Direction;
            if (inp.sqrMagnitude >= 0.0001f)
            {
                Vector3 dir = new Vector3(inp.x, 0f, inp.y);
                if (moveRelativeToCamera && cam != null)
                {
                    dir = CameraRelative(inp);
                }
                if (dir.sqrMagnitude >= 0.0001f)
                {
                    float mag = Mathf.Clamp01(inp.magnitude);
                    dir.Normalize();
                    target = dir * (mag * moveSpeed);
                    if (faceMovement)
                    {
                        character.rotation = Quaternion.Slerp(character.rotation, Quaternion.LookRotation(dir), turnSpeed * Time.deltaTime);
                    }
                }
            }
        }

        _moveVel = Vector3.MoveTowards(_moveVel, target, acceleration * Mathf.Min(Time.deltaTime, 0.05f));
        Vector3 posBefore = character.position;
        MoveWithGravity(_moveVel);
        float speed = 0f;
        if (moveSpeed > 0.01f)
        {
            speed = Mathf.Clamp01(_moveVel.magnitude / moveSpeed);
        }
        SetSpeed(speed);
        UnstickIfWedged(_moveVel, posBefore, speed);
    }

    // While pushing, a frame that covers less than a quarter of the expected distance counts as
    // stuck; after unstickAfter seconds of that the player is teleported back to the last free spot.
    private void UnstickIfWedged(Vector3 horizVel, Vector3 posBefore, float speedAnim)
    {
        if (unstickAfter <= 0f) return;
        if (cc == null || !cc.enabled) return;
        if (character == null) return;
        if (speedAnim > 0.1f)
        {
            Vector3 p = character.position;
            float moved = new Vector3(p.x - posBefore.x, 0f, p.z - posBefore.z).magnitude;
            float expected = new Vector3(horizVel.x, 0f, horizVel.z).magnitude * Mathf.Min(Time.deltaTime, 0.05f);
            if (expected <= 0.001f || moved >= expected * 0.25f)
            {
                _stuckTimer = 0f;
                _lastFreePos = character.position;
                _haveFreePos = true;
                return;
            }
            _stuckTimer += Time.deltaTime;
            if (_stuckTimer < unstickAfter) return;
            if (!_haveFreePos) return;
            cc.enabled = false;
            character.position = _lastFreePos;
            cc.enabled = true;
            _vy = 0f;
        }
        _stuckTimer = 0f;
    }

    // Stick direction on the ground plane relative to the camera.
    private Vector3 CameraRelative(Vector2 inp)
    {
        Vector3 fwd = cam.transform.forward;
        fwd = new Vector3(fwd.x, 0f, fwd.z).normalized;
        Vector3 right = cam.transform.right;
        right = new Vector3(right.x, 0f, right.z).normalized;
        return fwd * inp.y + right * inp.x;
    }

    // CharacterController move with gravity (grounded: small downward stick); without an enabled
    // controller the transform is moved directly.
    private void MoveWithGravity(Vector3 horizVel)
    {
        if (cc != null && cc.enabled)
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (cc.isGrounded && _vy < 0f)
            {
                _vy = -2f;
            }
            _vy -= dt * gravity;
            cc.Move((horizVel + Vector3.up * _vy) * dt);
            return;
        }
        if (character != null)
        {
            character.position += horizVel * Time.deltaTime;
        }
    }

    // Seeker-mode jump: only when grounded, not on a wall, not scouting, not frozen / counting down.
    public void Jump()
    {
        if (cc == null || !cc.enabled || !cc.isGrounded) return;
        if (climber != null && climber.IsOnWall) return;
        if (camCtrl != null && camCtrl.FreeLook) return;
        if (gmm != null && (gmm.PlayerFrozen || gmm.SeekerHideCountdown)) return;
        _vy = Mathf.Sqrt(2f * gravity * Mathf.Max(0f, jumpHeight));
    }

    private void SetSpeed(float v)
    {
        if (anim != null)
        {
            anim.SetFloat(SpeedHash, v);
        }
    }

    // Finds the scene's rotate toggle and the left / right hold buttons (their events are authored in
    // the scene and call the public Start/Stop/Evt methods).
    private void WireRotateButton()
    {
        rotateButtonGo = FindInScene(rotateButtonName);
        rotLeftGo = FindInScene(rotateLeftButtonName);
        rotRightGo = FindInScene(rotateRightButtonName);
        _rotBtnImg = rotateButtonGo != null ? rotateButtonGo.GetComponent<Image>() : null;
        ShowRotateButtons(false);
        if (rotateButtonGo == null)
        {
            Debug.LogWarning("[JoystickMover] '" + rotateButtonName + "' not found.");
        }
        if (rotLeftGo == null || rotRightGo == null)
        {
            Debug.LogWarning("[JoystickMover] ButtonRotationLeft/Right not found.");
        }
    }

    public void ToggleRotateButtons()
    {
        ShowRotateButtons(!_rotBtnsShown);
    }

    private void ShowRotateButtons(bool on)
    {
        _rotBtnsShown = on;
        if (rotLeftGo != null)
        {
            rotLeftGo.SetActive(on);
        }
        if (rotRightGo != null)
        {
            rotRightGo.SetActive(on);
        }
        if (_rotBtnImg != null)
        {
            Sprite s = on ? rotOnSprite : rotOffSprite;
            if (s != null)
            {
                _rotBtnImg.sprite = s;
            }
        }
        if (!on)
        {
            _rotHeldDir = 0;
        }
    }

    public void StartRotateLeft()
    {
        _rotHeldDir = 1;
    }

    public void StartRotateRight()
    {
        _rotHeldDir = -1;
    }

    public void StopRotate()
    {
        _rotHeldDir = 0;
    }

    public void EvtRotateLeftDown(BaseEventData e)
    {
        _rotHeldDir = 1;
    }

    public void EvtRotateRightDown(BaseEventData e)
    {
        _rotHeldDir = -1;
    }

    public void EvtRotateUp(BaseEventData e)
    {
        _rotHeldDir = 0;
    }

    // GameObject.Find, else any scene object of that name (also inactive ones).
    private GameObject FindInScene(string n)
    {
        GameObject go = GameObject.Find(n);
        if (go != null)
        {
            return go;
        }
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
}
