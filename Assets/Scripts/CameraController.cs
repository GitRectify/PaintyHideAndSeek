using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output. Fields (names, order, attributes,
// offsets) and method accessibility match dump.cs (TypeDefIndex 9653). All string literals are
// confirmed against Dumpstringliteral.json.
//
// Decoded from libil2cpp.so (not in the paste): <>c__DisplayClass101_0 (isPitch 0x10, this 0x18,
// dir 0x20).
//  - <MakeHoldButton>b__0 (RVA 0x224E82C): isPitch ? pitchDir = dir : orbitDir = dir.
//  - <MakeHoldButton>b__1 (RVA 0x224E864): isPitch ? pitchDir = 0 : orbitDir = 0.
// They go into JoyDrag.onPointer (0x20) and JoyDrag.onUp (0x28).
//
// Inlined in raw: ChameleonPaint.DrawingActive (drawingOn), Stretch (in MakeHoldButton's label),
// Mathf.SmoothStep / MoveTowards / Lerp / Clamp, Vector3.normalized / magnitude / Cross,
// Quaternion.Euler (Internal_FromEulerRad of deg * Deg2Rad), CanvasScaler.uiScaleMode /
// matchWidthOrHeight.
//
// Convention: where raw jumps to the NullReferenceException stub, the C# just dereferences
// naturally; explicit null checks are kept only where raw really skips.
public class CameraController : MonoBehaviour
{
    [Header("Target")]
    public string characterRootName = "Player";
    public float targetHeight = 2f;

    [Header("Framing (angled top-down, not too high)")]
    [Range(5f, 70f)]
    public float pitch = 22f;
    public float distance = 33f;
    public float followLerp = 8f;
    [Tooltip("How fast the camera's VERTICAL follow catches up. Lower = smoother over stairs — absorbs the CharacterController's per-step Y 'pops' (~4u) that otherwise make the camera jitter while climbing. X/Z follow stays snappy.")]
    public float stairCamSmooth = 6f;
    private float _smoothY;
    private bool _smoothYInit;

    [Header("Orbit (only while drawing)")]
    public float orbitSpeed = 80f;
    public float orbitDragSpeed = 0.22f;
    [Tooltip("Up/down camera tilt limits when orbiting (pitch).")]
    public float minPitch = 5f;
    public float maxPitch = 80f;

    [Header("Zoom (mouse wheel + 2-finger pinch)")]
    [Tooltip("Closest the camera may get to the character.")]
    public float minDistance = 12f;
    [Tooltip("Farthest the camera may get from the character.")]
    public float maxDistance = 60f;
    [Tooltip("World units zoomed per mouse-wheel notch.")]
    public float zoomScrollSpeed = 5f;
    [Tooltip("World units zoomed per screen-pixel of pinch.")]
    public float zoomPinchSpeed = 0.08f;

    [Header("Mode 1 camera (over-the-shoulder)")]
    [Tooltip("How far BEHIND the character the Mode-1 camera sits.")]
    public float tpsBack = 10f;
    [Tooltip("Camera height above the character pivot (hips).")]
    public float tpsHeight = 5.5f;
    [Tooltip("Sideways offset over the right shoulder (+) so the gun stays in view.")]
    public float tpsSide = 2.5f;
    [Tooltip("How far AHEAD of the character the camera aims (keeps the room in view for seeking).")]
    public float tpsLookAhead = 11.5f;
    [Tooltip("Height of the aim point above the pivot (lower = look down more).")]
    public float tpsLookHeight = 1.7f;
    [Tooltip("Sideways offset of the aim point.")]
    public float tpsLookSide = 0.5f;
    [Tooltip("Pull the camera in front of walls so it never clips through them.")]
    public bool tpsAvoidWalls = true;

    [Header("Prone (btnnam / pose_nam) — lower camera when lying down")]
    [Tooltip("Fallback when the character does NOT have a SeekerCamProfile: how many world units the camera drops when prone. If the character has a profile, the profile's `proneHeightDrop` is used.")]
    public float proneHeightDrop = 2.5f;
    [Tooltip("Fallback: When crouching, the aiming point drops by a corresponding amount (and the view lowers along with it).")]
    public float proneLookDrop = 1.8f;
    [Tooltip("Fallback: camera pullback distance when prone (absolute value, replacing tpsBack). Negative value = moves to the front.")]
    public float proneBack = 0.12f;
    [Tooltip("Speed ​​of lowering/raising the camera when toggling the lying-down state (expressed as a rate of 0–1 per second). A higher value means faster/snappier movement.")]
    public float proneLerpSpeed = 6f;
    private bool _proneOn;
    private float _proneAmt;
    public bool firstPerson;
    private float aimPitchDeg;
    private SeekerCamProfile _camProfile;
    private Transform _camProfileFor;

    [Header("Home / menu camera (static — does NOT follow the character)")]
    [Tooltip("On the Home screen (no round active) the camera holds the Main Camera's authored scene pose — position the Main Camera in the scene to frame the Home shot. It starts following the character only once a round begins.")]
    public bool staticCameraInMenu = true;

    [Header("Home camera swap")]
    [Tooltip("Name of the camera INSIDE the home model. On the Home menu the Main Camera is turned OFF and this one ON; in-game it's the reverse. Blank / not found = no swap (Main Camera always on).")]
    public string homeCameraName = "Camera";
    [Tooltip("Turn the Main Camera OFF + the home-model camera ON while on the Home menu (and swap back in-game).")]
    public bool swapHomeCamera = true;

    [Header("Spot cam (Play Now: caught) — ZOOM OUT from the character (bot not framed)")]
    [Tooltip("Extra distance (world units) the camera pulls straight back FROM the character over the caught moment — a zoom-out reveal. It keeps the angle it was already at; the bot is NOT shown.")]
    public float spotZoomOutBy = 30f;
    [Tooltip("Seconds the zoom-out takes. Set it near BotSeekController.spotShootDelay so it finishes pulling back around when the shot lands.")]
    public float spotZoomTime = 3f;
    [Tooltip("Look-point height above the character's pivot (≈ its body centre).")]
    public float spotFocusHeight = 3f;
    [Tooltip("How fast the camera eases as it pulls back / tracks the character.")]
    public float spotLerp = 6f;
    private Transform spotBot;
    private Transform spotTarget;
    private bool spotView;
    private float _spotZoomT;
    private Vector3 _spotDir3;
    private float _spotStartDist;

    [Header("Free-look scout camera (ButtonCamera): pan the camera to watch the room / enemies")]
    [Tooltip("World units/sec the joystick pans the free-look camera when scouting (camera detached from the character). Zoom (wheel/pinch) still works to see more/less of the room.")]
    public float freePanSpeed = 40f;
    [Tooltip("Keep the scout camera INSIDE the room — it can't fly up through the roof or down through the floor.")]
    public bool scoutClampToRoom = true;
    [Tooltip("Min height (world units) the scout look-point stays above the floor.")]
    public float scoutFloorMargin = 2f;
    [Tooltip("How far (world units) below the roof the scout camera stops, so it doesn't poke through the ceiling.")]
    public float scoutCeilMargin = 3f;
    public bool _freeLook;
    private Vector3 _freePivot;
    private float _scoutFloorY;
    private float _scoutRoofY;
    private Transform character;
    private Camera cam;
    private ChameleonPaint paint;
    private GameModeManager gmm;
    private Vector3 homePos;
    private Quaternion homeRot;
    private bool homeCaptured;
    private Camera _homeCam;
    private bool _prevInMenu;
    public float yaw;
    private int orbitDir;
    private int pitchDir;
    private float prevPinchDist = -1f;
    private GameObject orbitButtons;
    private bool _wasInMenu = true;
    private float _defPitch;
    private float _defDistance;
    private float _defYaw;

    public bool FreeLook => _freeLook;
    public bool IsFirstPerson => firstPerson;
    public bool IsSpotView => spotView;

    private void Start()
    {
        GameObject player = PlayerRef.Resolve(characterRootName);
        if (player != null)
        {
            character = player.transform;
        }
        cam = Camera.main;
        paint = FindFirstObjectByType<ChameleonPaint>();
        gmm = FindFirstObjectByType<GameModeManager>();
        _defPitch = pitch;
        _defDistance = distance;
        _defYaw = yaw;
        EnsureEventSystem();

        // The authored Main Camera pose is the Home shot.
        if (cam != null)
        {
            homePos = cam.transform.position;
            homeRot = cam.transform.rotation;
            homeCaptured = true;
        }
        if (character != null && cam != null && !InMenu())
        {
            UpdateCam(true);
        }
        _homeCam = ResolveHomeCamera();
        // Opposite of the current state, so the first LateUpdate applies the camera swap.
        _prevInMenu = !InMenu();
    }

    // Home screen = no round running (only when staticCameraInMenu).
    private bool InMenu()
    {
        return staticCameraInMenu && gmm != null && !gmm.RoundActive;
    }

    // The camera named homeCameraName (other than the Main Camera), even if inactive, as long as it
    // is a scene object.
    private Camera ResolveHomeCamera()
    {
        if (string.IsNullOrEmpty(homeCameraName)) return null;
        Camera[] all = Resources.FindObjectsOfTypeAll<Camera>();
        for (int i = 0; i < all.Length; i++)
        {
            Camera c = all[i];
            if (c != cam && c.name == homeCameraName && c.gameObject.scene.IsValid())
            {
                return c;
            }
        }
        return null;
    }

    // Menu: home camera on, Main Camera off (only if a home camera exists). In-game: the reverse.
    private void ApplyCameraSwap(bool inMenu)
    {
        if (!swapHomeCamera) return;
        bool hasHome = _homeCam != null;
        if (cam != null)
        {
            cam.enabled = !(hasHome && inMenu);
        }
        if (_homeCam != null)
        {
            _homeCam.enabled = inMenu;
        }
    }

    private void LateUpdate()
    {
        bool inMenu = InMenu();
        if (_prevInMenu != inMenu)
        {
            _prevInMenu = inMenu;
            ApplyCameraSwap(inMenu);
        }
        if (cam == null) return;

        if (InMenu())
        {
            // Home screen: ease back to the authored pose.
            _freeLook = false;
            _wasInMenu = true;
            if (orbitButtons != null && orbitButtons.activeSelf)
            {
                orbitButtons.SetActive(false);
            }
            if (!homeCaptured) return;
            float t = 1f - Mathf.Exp(-followLerp * Time.deltaTime);
            cam.transform.position = Vector3.Lerp(cam.transform.position, homePos, t);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, homeRot, t);
            return;
        }

        if (character == null) return;

        // Entering a round: restore the default framing (the seeker keeps its own).
        if (_wasInMenu)
        {
            _wasInMenu = false;
            if (gmm == null || !gmm.PlayerIsSeeker)
            {
                yaw = _defYaw;
                pitch = _defPitch;
                distance = _defDistance;
            }
        }

        // Orbit buttons exist only while drawing.
        bool drawing = paint != null && paint.DrawingActive;
        if (orbitButtons != null && orbitButtons.activeSelf != drawing)
        {
            orbitButtons.SetActive(drawing);
        }
        if (!drawing)
        {
            orbitDir = 0;
            pitchDir = 0;
        }
        else if (orbitDir != 0)
        {
            yaw += orbitSpeed * orbitDir * Time.deltaTime;
        }
        if (pitchDir != 0)
        {
            pitch = Mathf.Clamp(pitch + orbitSpeed * pitchDir * Time.deltaTime, minPitch, maxPitch);
        }

        HandleZoomInput();
        UpdateCam(false);
    }

    // Character position with a smoothed Y (stairs); snaps on first use, on request, or on a jump
    // of more than 20 units.
    private Vector3 FollowPivot(bool snap)
    {
        if (character == null) return Vector3.zero;
        float y = character.position.y;
        if (snap || !_smoothYInit || Mathf.Abs(y - _smoothY) > 20f)
        {
            _smoothY = y;
            _smoothYInit = true;
        }
        else
        {
            _smoothY = Mathf.Lerp(_smoothY, y, 1f - Mathf.Exp(-(stairCamSmooth * Time.deltaTime)));
        }
        return new Vector3(character.position.x, _smoothY, character.position.z);
    }

    // SeekerCamProfile of PlayerRef.Active, looked up again only when the active player changes.
    private SeekerCamProfile ActiveCamProfile()
    {
        Transform active = PlayerRef.Active;
        if (active != _camProfileFor)
        {
            _camProfileFor = active;
            _camProfile = active != null ? active.GetComponent<SeekerCamProfile>() : null;
        }
        return _camProfile;
    }

    private void UpdateCam(bool snap)
    {
        // Spot view (caught): pull straight back from the focus point along _spotDir3, eased in
        // over spotZoomTime, always looking at the focus point.
        if (spotView && spotTarget != null)
        {
            Vector3 focus = spotTarget.position + Vector3.up * spotFocusHeight;
            _spotZoomT += Time.deltaTime;
            float zoom = 1f;
            if (spotZoomTime > 0.01f)
            {
                zoom = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_spotZoomT / spotZoomTime));
            }
            float dist = _spotStartDist + zoom * spotZoomOutBy;
            float k = 1f;
            if (!snap)
            {
                k = 1f - Mathf.Exp(-(spotLerp * Time.deltaTime));
            }
            cam.transform.position = Vector3.Lerp(cam.transform.position, focus + _spotDir3 * dist, k);
            Quaternion look = Quaternion.LookRotation((focus - cam.transform.position).normalized, Vector3.up);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, look, k);
            return;
        }

        Vector3 pivot;
        Quaternion rot;
        if (!_freeLook)
        {
            pivot = FollowPivot(snap);
            if (firstPerson)
            {
                // Mode-1 over-the-shoulder camera; the character's SeekerCamProfile overrides the
                // fallback values here.
                SeekerCamProfile p = ActiveCamProfile();
                float height = p != null ? p.tpsHeight : tpsHeight;
                float back = p != null ? p.tpsBack : tpsBack;
                float side = p != null ? p.tpsSide : tpsSide;
                float lookAhead = p != null ? p.tpsLookAhead : tpsLookAhead;
                float lookHeight = p != null ? p.tpsLookHeight : tpsLookHeight;
                float lookSide = p != null ? p.tpsLookSide : tpsLookSide;

                _proneAmt = Mathf.MoveTowards(_proneAmt, _proneOn ? 1f : 0f, proneLerpSpeed * Time.deltaTime);
                if (_proneAmt > 0f)
                {
                    height -= (p != null ? p.proneHeightDrop : proneHeightDrop) * _proneAmt;
                    lookHeight -= (p != null ? p.proneLookDrop : proneLookDrop) * _proneAmt;
                    back = Mathf.Lerp(back, p != null ? p.proneBack : proneBack, _proneAmt);
                }

                // Level facing (forward when the character looks straight up/down).
                Vector3 fwd = character.forward;
                fwd.y = 0f;
                fwd = fwd.x * fwd.x + fwd.z * fwd.z < 0.001f ? Vector3.forward : fwd.normalized;
                Vector3 right = Vector3.Cross(Vector3.up, fwd);

                Vector3 eye = pivot + Vector3.up * height;
                Vector3 camPos = eye - fwd * back + right * side;
                if (tpsAvoidWalls)
                {
                    // Pull in front of any non-character wall between the eye point and the camera.
                    Vector3 off = camPos - eye;
                    float len = off.magnitude;
                    if (len > 0.01f)
                    {
                        Vector3 dir = off / len;
                        if (Physics.Raycast(eye, dir, out RaycastHit hit, len + 0.5f)
                            && !(hit.collider is CharacterController)
                            && hit.collider.GetComponentInParent<Animator>() == null)
                        {
                            camPos = hit.point - dir * 0.5f;
                        }
                    }
                }

                float k = 1f;
                if (!snap)
                {
                    k = 1f - Mathf.Exp(-(followLerp * Time.deltaTime));
                }
                cam.transform.position = Vector3.Lerp(cam.transform.position, camPos, k);

                // Look ahead of the character, tilted by the Mode-1 aim pitch.
                Vector3 lookDir = pivot + Vector3.up * lookHeight + fwd * lookAhead + right * lookSide - cam.transform.position;
                if (Mathf.Abs(aimPitchDeg) > 0.01f)
                {
                    lookDir = Quaternion.AngleAxis(-aimPitchDeg, right) * lookDir;
                }
                Quaternion look = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
                cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, look, k);
                return;
            }
            rot = Quaternion.Euler(pitch, yaw, 0f);
        }
        else
        {
            // Free-look scout: orbit around the panned pivot, kept between floor and roof.
            rot = Quaternion.Euler(pitch, yaw, 0f);
            if (scoutClampToRoom)
            {
                float camAbove = Mathf.Max(0f, targetHeight + (rot * (Vector3.back * distance)).y);
                float floor = _scoutFloorY + scoutFloorMargin;
                float ceil = _scoutRoofY - scoutCeilMargin - camAbove;
                _freePivot.y = Mathf.Clamp(_freePivot.y, floor, Mathf.Max(floor, ceil));
            }
            pivot = _freePivot;
        }

        // Orbit: distance back along the yaw/pitch rotation, looking at pivot + targetHeight.
        Vector3 target = pivot + Vector3.up * targetHeight;
        Vector3 orbitPos = target + rot * (Vector3.back * distance);
        cam.transform.position = snap
            ? orbitPos
            : Vector3.Lerp(cam.transform.position, orbitPos, 1f - Mathf.Exp(-(followLerp * Time.deltaTime)));
        cam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);
    }

    public void OrbitYaw(float deltaPixels)
    {
        yaw += orbitDragSpeed * deltaPixels;
    }

    public void OrbitPitch(float deltaPixels)
    {
        pitch = Mathf.Clamp(pitch - orbitDragSpeed * deltaPixels, minPitch, maxPitch);
    }

    public void Zoom(float deltaDistance)
    {
        distance = Mathf.Clamp(distance + deltaDistance, minDistance, maxDistance);
    }

    public void SetFirstPerson(bool v)
    {
        firstPerson = v;
        if (!v)
        {
            aimPitchDeg = 0f;
            _proneOn = false;
            _proneAmt = 0f;
        }
    }

    public void SetProne(bool v)
    {
        _proneOn = v;
    }

    // Caught view on target. The pull-back direction and start distance (at least 6) are taken
    // from where the camera is when the view starts.
    public void SetSpotView(Transform bot, Transform target)
    {
        _freeLook = false;
        if (!spotView && target != null)
        {
            _spotZoomT = 0f;
            Vector3 focus = target.position + Vector3.up * spotFocusHeight;
            Vector3 away = (cam != null ? cam.transform : transform).position - focus;
            _spotStartDist = Mathf.Max(6f, away.magnitude);
            if (away.sqrMagnitude <= 0.01f)
            {
                _spotDir3 = new Vector3(0f, 0.70710677f, -0.70710677f);
            }
            else
            {
                _spotDir3 = away.normalized;
            }
        }
        spotBot = bot;
        spotTarget = target;
        spotView = target != null;
    }

    public void ClearSpotView()
    {
        spotView = false;
        spotBot = null;
        spotTarget = null;
    }

    // Turning free-look on starts the scout pivot at the character and measures the room.
    public void SetFreeLook(bool on)
    {
        if (_freeLook == on) return;
        _freeLook = on;
        if (!on) return;
        if (character != null)
        {
            _freePivot = character.position;
            CacheScoutBounds();
        }
    }

    // Floor = first non-character hit straight down (from 2 above the character, 80 range);
    // roof = highest non-character hit straight up from the floor, if more than 5 above it.
    // Defaults: character Y and Y + 60.
    private void CacheScoutBounds()
    {
        _scoutFloorY = character != null ? character.position.y : 0f;
        _scoutRoofY = _scoutFloorY + 60f;
        if (character == null) return;

        Vector3 p = character.position;
        if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 80f)
            && hit.collider.GetComponentInParent<Animator>() == null)
        {
            _scoutFloorY = hit.point.y;
        }

        p.y = _scoutFloorY + 1f;
        RaycastHit[] hits = Physics.RaycastAll(p, Vector3.up, 300f);
        float roof = float.MinValue;
        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].collider.GetComponentInParent<Animator>() == null && hits[i].point.y > roof)
            {
                roof = hits[i].point.y;
            }
        }
        if (roof > _scoutFloorY + 5f)
        {
            _scoutRoofY = roof;
        }
    }

    public void ToggleFreeLook()
    {
        SetFreeLook(!_freeLook);
    }

    // Joystick pan of the scout pivot: y along the camera forward, x along the level camera right.
    public void PanFree(Vector2 input, float dt)
    {
        if (!_freeLook) return;
        if (cam == null) return;
        Vector3 fwd = cam.transform.forward;
        Vector3 right = cam.transform.right;
        right.y = 0f;
        if (right.sqrMagnitude > 0.001f)
        {
            right = right.normalized;
        }
        _freePivot += (fwd * input.y + right * input.x) * (freePanSpeed * dt);
    }

    public void SetAimPitch(float deg)
    {
        aimPitchDeg = deg;
    }

    // Mouse wheel (one zoomScrollSpeed step per notch) and two-finger pinch.
    private void HandleZoomInput()
    {
        if (Mouse.current != null)
        {
            Vector2 scroll = Mouse.current.scroll.ReadValue();
            if (Mathf.Abs(scroll.y) > 0.01f)
            {
                distance = Mathf.Clamp(distance - Mathf.Sign(scroll.y) * zoomScrollSpeed, minDistance, maxDistance);
            }
        }

        Touchscreen ts = Touchscreen.current;
        if (ts == null) return;
        var touches = ts.touches;
        Vector2 a = Vector2.zero;
        Vector2 b = Vector2.zero;
        int pressed = 0;
        for (int i = 0; i < touches.Count && pressed < 2; i++)
        {
            if (touches[i].press.isPressed)
            {
                if (pressed == 0)
                {
                    a = touches[i].position.ReadValue();
                }
                else
                {
                    b = touches[i].position.ReadValue();
                }
                pressed++;
            }
        }
        if (pressed > 1)
        {
            float d = Vector2.Distance(a, b);
            if (prevPinchDist > 0f)
            {
                distance = Mathf.Clamp(distance - (d - prevPinchDist) * zoomPinchSpeed, minDistance, maxDistance);
            }
            prevPinchDist = d;
            return;
        }
        prevPinchDist = -1f;
    }

    public void AuthorUI()
    {
    }

    // "CameraOrbitCanvas" (sort 6, 1080x1920): bottom-right "OrbitButtons" pad with a "Rotate view"
    // caption and four hold buttons (< > yaw, ▲ ▼ pitch). Starts hidden.
    private void BuildOrbitButtons()
    {
        GameObject canvasGO = SceneUI.GetOrCreateCanvas("CameraOrbitCanvas", out bool canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);
        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 6;
            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);

        orbitButtons = SceneUI.GetOrCreate("OrbitButtons", canvasGO.transform, out bool padCreated);
        RectTransform rt = SceneUI.GetOrAdd<RectTransform>(orbitButtons);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.anchorMin = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-30f, 40f);
        rt.sizeDelta = new Vector2(330f, 330f);

        GameObject lblGO = SceneUI.GetOrCreate("OrbitLbl", orbitButtons.transform, out bool lblCreated);
        Text lbl = SceneUI.GetOrAdd<Text>(lblGO);
        lbl.font = UIFont.Default;
        lbl.raycastTarget = false;
        if (lblCreated)
        {
            lbl.text = "Rotate view";
            lbl.fontSize = 26;
            lbl.alignment = TextAnchor.LowerCenter;
            lbl.color = new Color(1f, 1f, 1f, 0.8f);
            lbl.horizontalOverflow = HorizontalWrapMode.Overflow;
            lbl.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0f, 1f);
            lrt.anchorMax = new Vector2(1f, 1f);
            lrt.pivot = new Vector2(0.5f, 0f);
            lrt.anchoredPosition = new Vector2(0f, 6f);
            lrt.sizeDelta = new Vector2(0f, 34f);
        }

        Sprite circle = MakeCircle(96);
        MakeHoldButton(orbitButtons.transform, "<", circle, new Vector2(0f, 110f), -1, false);
        MakeHoldButton(orbitButtons.transform, ">", circle, new Vector2(220f, 110f), 1, false);
        MakeHoldButton(orbitButtons.transform, "▲", circle, new Vector2(110f, 220f), -1, true);
        MakeHoldButton(orbitButtons.transform, "▼", circle, new Vector2(110f, 0f), 1, true);
        orbitButtons.SetActive(false);
    }

    // 110x110 round button "Orbit_<label>": holding it sets orbitDir / pitchDir to dir,
    // releasing clears it.
    private void MakeHoldButton(Transform parent, string label, Sprite circle, Vector2 pos, int dir, bool isPitch)
    {
        GameObject go = SceneUI.GetOrCreate("Orbit_" + label, parent, out bool created);
        Image img = SceneUI.GetOrAdd<Image>(go);
        img.color = new Color(0.18f, 0.19f, 0.24f, 0.9f);
        img.sprite = circle;
        RectTransform rt = img.rectTransform;
        rt.anchorMax = new Vector2(0f, 0f);
        rt.anchorMin = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(110f, 110f);

        GameObject txtGO = SceneUI.GetOrCreate("L", go.transform, out bool txtCreated);
        Text txt = SceneUI.GetOrAdd<Text>(txtGO);
        txt.font = UIFont.Default;
        txt.raycastTarget = false;
        txt.text = label;
        if (txtCreated)
        {
            txt.fontSize = 60;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.white;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            Stretch(txt);
        }

        JoyDrag drag = SceneUI.GetOrAdd<JoyDrag>(go);
        drag.onPointer = e =>
        {
            if (isPitch)
            {
                pitchDir = dir;
            }
            else
            {
                orbitDir = dir;
            }
        };
        drag.onUp = e =>
        {
            if (isPitch)
            {
                pitchDir = 0;
            }
            else
            {
                orbitDir = 0;
            }
        };
    }

    // Creates an EventSystem if none exists: new Input System UI module when that package is
    // present (looked up by name), else StandaloneInputModule.
    private void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        Type inputModule = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (inputModule != null)
        {
            go.AddComponent(inputModule);
            return;
        }
        go.AddComponent<StandaloneInputModule>();
    }

    private Image NewImage(string name, Transform parent, Color color, Sprite sprite)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = color;
        img.sprite = sprite;
        return img;
    }

    private Text NewText(string name, Transform parent, string content, int size, TextAnchor anchor, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Text txt = go.AddComponent<Text>();
        txt.font = UIFont.Default;
        txt.text = content;
        txt.fontSize = size;
        txt.alignment = anchor;
        txt.color = color;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        txt.verticalOverflow = VerticalWrapMode.Overflow;
        return txt;
    }

    private static void Stretch(Component c)
    {
        RectTransform rt = c.transform as RectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // White s x s circle, alpha = clamped distance inside the edge (1-pixel soft rim).
    private static Sprite MakeCircle(int s)
    {
        Texture2D tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float r = s / 2f;
        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                float dx = x + 0.5f - r;
                float dy = y + 0.5f - r;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy))));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, s, s), new Vector2(0.5f, 0.5f), 100f);
    }
}
