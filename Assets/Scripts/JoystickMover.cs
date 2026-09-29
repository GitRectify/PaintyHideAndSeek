using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// NOTE: Drives character movement from the on-screen Joystick (Joystick Pack asset).
// Supports third-person (optionally camera-relative) and first-person strafing, wall-climbing
// hand-off via WallClimber, free-look camera pass-through via CameraController, and a "wedged"
// recovery system that teleports the player back to their last known-free position if they get
// stuck against geometry.
public class JoystickMover : MonoBehaviour
{
    [SerializeField] private string characterRootName = "Player";
    [SerializeField] private Joystick joystick;
    [SerializeField] private bool moveRelativeToCamera = true;
    [SerializeField] private bool faceMovement = true;
    [SerializeField] private float moveSpeed = 12f;
    [SerializeField] private float acceleration = 80f;
    [SerializeField] private float gravity = 70f;
    [SerializeField] private float turnSpeed = 12f;
    [SerializeField] private float climbStepHeight = 4f;
    // NOTE: confirmed field (default 120) but not referenced by any method pasted so far —
    // likely used by a first-person camera/look script not yet decompiled. Unused here.
    [SerializeField] private float fpTurnSpeed = 120f;
    [SerializeField] private float jumpHeight = 8f;
    [SerializeField] private float unstickAfter = 0.6f;

    [SerializeField] private string rotateButtonName = "Btn_XoayNhanVat"; // NOTE: Vietnamese, "rotate character button"
    [SerializeField] private string rotateLeftButtonName = "ButtonRotationLeft";
    [SerializeField] private string rotateRightButtonName = "ButtonRotationRight";
    [SerializeField] private float rotateButtonSpeed = 120f;

    // NOTE: these two Sprite fields are confirmed to exist (read via fixed field offsets 0xc0
    // and 0xc8 in ShowRotateButtons to swap _rotBtnImg's sprite based on the `on` state) but
    // their real names weren't in the paste — named here by inferred purpose, unconfirmed.
    [SerializeField] private Sprite rotateActiveSprite = null;
    [SerializeField] private Sprite rotateInactiveSprite = null;

    private Transform character;
    private CharacterController cc;
    private Animator anim;
    private Camera cam;
    private WallClimber climber;
    private CameraController camCtrl;
    private GameModeManager gmm;

    private GameObject rotateButtonGo;
    private GameObject rotLeftGo;
    private GameObject rotRightGo;
    private Image _rotBtnImg;
    private bool _rotBtnsShown;
    private int _rotHeldDir;

    private float _vy;
    private Vector3 _moveVel;
    private float _stuckTimer;
    private Vector3 _lastFreePos;
    private bool _haveFreePos;
    private bool _joyHidden;

    private static readonly int SpeedHash = Animator.StringToHash("Speed");

    private void Start()
    {
        GameObject characterGo = PlayerRef.Resolve(characterRootName);
        if (characterGo != null)
        {
            character = characterGo.transform;
        }
        else
        {
            Debug.LogWarning($"[JoystickMover] Character '{characterRootName}' not found.");
        }

        if (character != null)
        {
            cc = character.GetComponent<CharacterController>();
            anim = character.GetComponent<Animator>();
        }

        cam = Camera.main;

        if (joystick == null)
        {
            joystick = FindFirstObjectByType<Joystick>(
                FindObjectsInactive.Include
            );
        }
        if (joystick == null)
        {
            Debug.LogWarning("[JoystickMover] No Joystick (Joystick Pack) assigned or found in the scene.");
        }

        climber = FindFirstObjectByType<WallClimber>();
        camCtrl = FindFirstObjectByType<CameraController>();
        gmm = FindFirstObjectByType<GameModeManager>();

        WireRotateButton();

        Debug.Log($"[JoystickMover] Ready (character={character != null}, joystick={joystick != null}).");
    }

    private void WireRotateButton()
    {
        rotateButtonGo = FindInScene(rotateButtonName);
        rotLeftGo = FindInScene(rotateLeftButtonName);
        rotRightGo = FindInScene(rotateRightButtonName);

        _rotBtnImg = (rotateButtonGo != null) ? rotateButtonGo.GetComponent<Image>() : null;

        ShowRotateButtons(false);

        if (rotateButtonGo == null)
        {
            Debug.LogWarning($"[JoystickMover] '{rotateButtonName}' not found.");
        }
        if (rotLeftGo == null || rotRightGo == null)
        {
            Debug.LogWarning("[JoystickMover] ButtonRotationLeft/Right not found.");
        }
    }

    private void Update()
    {
        // Keep the character controller's step offset in proportion to the character's scale
        // and height, so stairs/ledges up to climbStepHeight (in world units) remain steppable
        // after non-uniform scaling.
        if (character == null || !character.gameObject.activeInHierarchy || joystick == null)
        {
            return;
        }

        if (cc != null && character != null)
        {
            float scaleY = Mathf.Max(0.1f, character.lossyScale.y);
            float raw = climbStepHeight / scaleY;
            float scaledHeightMargin = scaleY * cc.height - 0.5f;
            cc.stepOffset = (raw >= 0.1f) ? Mathf.Min(raw, scaledHeightMargin) : 0.1f;
        }

        if (_rotBtnsShown && rotateButtonGo != null && !rotateButtonGo.activeInHierarchy)
        {
            ShowRotateButtons(false);
        }

        if (_rotHeldDir != 0 && character != null)
        {
            if (climber == null || !climber.BlocksRotation)
            {
                character.Rotate(0f, rotateButtonSpeed * _rotHeldDir * Time.deltaTime, 0f);
            }
        }

        if (character == null || joystick == null)
        {
            return;
        }

        bool playerFrozen = gmm != null && gmm.PlayerFrozen;
        if (_joyHidden != playerFrozen)
        {
            joystick.gameObject.SetActive(!playerFrozen);
            _joyHidden = playerFrozen;
        }

        bool seekerHideCountdown = gmm != null && gmm.SeekerHideCountdown;
        if (seekerHideCountdown || playerFrozen)
        {
            SetSpeed(0f);
            _vy = 0f;
            _moveVel = Vector3.zero;
            return;
        }

        // NOTE: CameraController._freeLook and .firstPerson are accessed directly here, matching
        // the raw field reads in the pseudocode — assumes those are public/internal on
        // CameraController (delivered in an earlier session); not re-verified against that file here.
        if (camCtrl != null && camCtrl._freeLook)
        {
            Vector2 dir = joystick.Direction;
            float dt = Time.deltaTime;
            camCtrl.PanFree(dir, dt);

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

        // NOTE (genuinely ambiguous): raw shares one code block between this branch and the
        // freeLook branch above for setting _moveVel before returning. The x-component there
        // comes from an unresolved helper (FUN_022f7090) and the y/z components come from
        // register state whose provenance wasn't fully traceable. Both scenarios mean "no
        // horizontal movement this frame," so Vector3.zero is the sensible reading, but the
        // exact raw values weren't independently confirmed.
        if (climber != null && climber.IsOnWall)
        {
            _vy = 0f;
            _moveVel = Vector3.zero;
            return;
        }

        Vector3 targetVel = Vector3.zero;

        if (camCtrl != null && camCtrl.firstPerson)
        {
            Vector2 dir = joystick.Direction;
            float speedScale = Mathf.Clamp01(dir.magnitude);
            if (speedScale >= 0.01f)
            {
                Vector3 fwd = character.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.001f)
                {
                    fwd.Normalize();
                }

                Vector3 right = character.right;
                right.y = 0f;
                if (right.sqrMagnitude > 0.001f)
                {
                    right.Normalize();
                }

                targetVel = (fwd * dir.y + right * dir.x) * moveSpeed;
            }
        }
        else
        {
            Vector2 dir = joystick.Direction;
            if (dir.sqrMagnitude >= 0.0001f)
            {
                Vector3 moveDir;
                if (moveRelativeToCamera && cam != null)
                {
                    Vector3 camRel = CameraRelative(dir);
                    moveDir = new Vector3(camRel.x, 0f, camRel.z);
                }
                else
                {
                    moveDir = new Vector3(dir.x, 0f, dir.y);
                }

                if (moveDir.sqrMagnitude >= 0.0001f)
                {
                    float speedScale = Mathf.Clamp01(dir.magnitude);
                    moveDir = moveDir.normalized * speedScale * moveSpeed;
                    targetVel = new Vector3(moveDir.x, 0f, moveDir.z);

                    if (faceMovement)
                    {
                        Quaternion look = Quaternion.LookRotation(moveDir);
                        character.rotation = Quaternion.Slerp(character.rotation, look, turnSpeed * Time.deltaTime);
                    }
                }
            }
        }

        _moveVel = Vector3.MoveTowards(_moveVel, targetVel, acceleration * Mathf.Min(Time.deltaTime, 0.05f));

        Vector3 posBefore = character.position;
        MoveWithGravity(_moveVel);

        float speedAnim = 0f;
        if (moveSpeed > 0.01f)
        {
            speedAnim = Mathf.Clamp01(_moveVel.magnitude / moveSpeed);
        }

        SetSpeed(speedAnim);
        UnstickIfWedged(_moveVel, posBefore, speedAnim);
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
            Sprite candidate = on ? rotateActiveSprite : rotateInactiveSprite;
            if (candidate != null)
            {
                _rotBtnImg.sprite = candidate;
            }
        }

        if (!on)
        {
            _rotHeldDir = 0;
        }
    }

    private void SetSpeed(float v)
    {
        if (anim != null)
        {
            anim.SetFloat(SpeedHash, v);
        }
    }

    private void MoveWithGravity(Vector3 horizVel)
    {
        if (cc != null && cc.enabled && cc.gameObject.activeInHierarchy)
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);

            if (cc.isGrounded && _vy < 0f)
            {
                _vy = -2f;
            }
            _vy -= dt * gravity;

            Vector3 delta = (horizVel + Vector3.up * _vy) * dt;
            cc.Move(delta);
            return;
        }

        if (character != null)
        {
            float dt = Time.deltaTime;
            character.position += horizVel * dt;
        }
    }

    // FIX (confirmed): raw computes an actual SQRT and compares that linear magnitude to
    // 1e-05 (`fVar8 = SQRT(fwd.x^2 + fwd.z^2); if (fVar8 <= 1e-05) {...}`) - confirming the
    // original source used `.magnitude`, not `.sqrMagnitude`. A prior draft compared
    // `sqrMagnitude <= 1e-5f` directly, which is the wrong conversion: the correctly-squared
    // threshold is (1e-05)^2 = 1e-10, not 1e-5. The prior threshold was therefore about 316x
    // too permissive, incorrectly zeroing out the forward/right direction (falling back to
    // Vector3.zero) for camera angles with a small-but-genuinely-nonzero horizontal component
    // (e.g. looking nearly straight up or down) where raw would still normalize and use it.
    // Fixed to the correctly-squared 1e-10f for both the forward and right vectors.
    private Vector3 CameraRelative(Vector2 inp)
    {
        Transform camT = cam.transform;

        Vector3 fwd = camT.forward;
        fwd.y = 0f;
        fwd = (fwd.sqrMagnitude <= 1e-10f) ? Vector3.zero : fwd.normalized;

        Vector3 right = camT.right;
        right.y = 0f;
        right = (right.sqrMagnitude <= 1e-10f) ? Vector3.zero : right.normalized;

        return fwd * inp.y + right * inp.x;
    }

    private void UnstickIfWedged(Vector3 horizVel, Vector3 posBefore, float speedAnim)
    {
        if (unstickAfter <= 0f) return;
        if (cc == null || !cc.enabled || !cc.gameObject.activeInHierarchy) return;
        if (character == null) return;

        if (speedAnim > 0.1f)
        {
            Vector3 posAfter = character.position;
            Vector2 moved = new Vector2(posAfter.x - posBefore.x, posAfter.z - posBefore.z);
            float movedDist = moved.magnitude;

            Vector2 expectedXZ = new Vector2(horizVel.x, horizVel.z);
            float expectedSpeed = expectedXZ.magnitude;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);

            if (expectedSpeed * dt <= 0.001f || expectedSpeed * dt * 0.25f <= movedDist)
            {
                // Moved roughly as far as expected (or barely tried to move) - not wedged.
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

    private void Jump()
    {
        if (cc == null || !cc.enabled || !cc.gameObject.activeInHierarchy) return;
        if (!cc.isGrounded) return;
        if (climber != null && climber.IsOnWall) return;
        if (camCtrl != null && camCtrl._freeLook) return;
        if (gmm != null)
        {
            if (gmm.PlayerFrozen) return;
            if (gmm.SeekerHideCountdown) return;
        }

        float h = Mathf.Max(0f, jumpHeight);
        _vy = Mathf.Sqrt((gravity + gravity) * h);
    }

    // NOTE: confirmed cross-cutting scene-lookup helper pattern (GameObject.Find first, fallback
    // to scanning Resources.FindObjectsOfTypeAll<Transform>() filtered by scene.IsValid()) —
    // also appears in GameModeManager.FindInSceneByName, HideModeResultUI.FindGO, and
    // HomeUI.FindInSceneIncludingInactive. Confirmed static like those other occurrences (body
    // never touches instance state; call sites pass reused/garbage register values as "this").
    private static GameObject FindInScene(string n)
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

    private void ToggleRotateButtons()
    {
        ShowRotateButtons(!_rotBtnsShown);
    }

    private void StartRotateLeft()
    {
        _rotHeldDir = 1;
    }

    private void StartRotateRight()
    {
        _rotHeldDir = -1;
    }

    private void StopRotate()
    {
        _rotHeldDir = 0;
    }

    // NOTE: these Evt* variants have bodies identical to StartRotateLeft/StartRotateRight/StopRotate
    // above but take a BaseEventData parameter — kept as separate methods since they're almost
    // certainly wired to a different UI hookup (e.g. EventTrigger callbacks vs a plain Button
    // OnClick), not a true exact-duplicate in the "same signature" sense.
    private void EvtRotateLeftDown(BaseEventData e)
    {
        _rotHeldDir = 1;
    }

    private void EvtRotateRightDown(BaseEventData e)
    {
        _rotHeldDir = -1;
    }

    private void EvtRotateUp(BaseEventData e)
    {
        _rotHeldDir = 0;
    }
}