using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class CameraController : MonoBehaviour
{
    // ====================== TUNABLES (from .ctor defaults) ======================

    public string characterRootName = "Player";

    [Header("Third-person orbit")]
    public float distance = 33f;
    public float followLerp = 8f;
    public float targetHeight = 2f;
    public float pitch = 22f;
    public float yaw;
    public float minPitch = 5f;
    public float maxPitch = 80f;
    public float orbitSpeed = 80f;
    public float orbitDragSpeed = 0.22f;
    public float zoomScrollSpeed = 5f;
    public float zoomPinchSpeed = 0.08f;
    public float minDistance = 12f;
    public float maxDistance = 60f;

    [Header("First-person")]
    public bool firstPerson;
    public float tpsSide = 2.5f;
    public float tpsLookAhead = 11.5f;
    public float tpsBack = 10f;
    public float tpsHeight = 5.5f;
    public float tpsLookHeight = 1.7f;
    public float tpsLookSide = 0.5f;
    public bool tpsAvoidWalls = true;
    public float aimPitchDeg;

    [Header("Prone")]
    public float proneBack = 0.12f;
    public float proneLerpSpeed = 6f;
    public float proneHeightDrop = 2.5f;
    public float proneLookDrop = 1.8f;

    [Header("Stair smoothing")]
    public float stairCamSmooth = 6f;

    [Header("Home / menu camera")]
    public bool staticCameraInMenu = true;
    public string homeCameraName = "Camera";
    public bool swapHomeCamera = true;

    [Header("Free look / scout")]
    public bool scoutClampToRoom = true;
    public float freePanSpeed = 40f;
    public float scoutFloorMargin = 2f;
    public float scoutCeilMargin = 3f;

    [Header("Spot (caught) view")]
    public float spotFocusHeight = 3f;
    public float spotLerp = 6f;
    public float spotZoomOutBy = 30f;
    public float spotZoomTime = 3f;

    // ====================== RUNTIME STATE ======================

    private Transform character;
    private Camera cam;
    private ChameleonPaint paint;
    private GameModeManager gmm;

    private float _defPitch;
    private float _defDistance;
    private float _defYaw;

    private Vector3 homePos;
    private Quaternion homeRot;
    private bool homeCaptured;
    private Camera _homeCam;
    private bool _prevInMenu;
    private bool _wasInMenu = true;

    public bool _freeLook;
    private Vector3 _freePivot;
    private float _scoutFloorY;
    private float _scoutRoofY;

    private float _smoothY;
    private bool _smoothYInit;

    public bool _proneOn;
    private float _proneAmt;

    private Transform _camProfileFor;
    private SeekerCamProfile _camProfile;

    private float prevPinchDist = -1f;

    public GameObject orbitButtons;
    public int orbitDir;
    public int pitchDir;

    public bool spotView;
    public Transform spotBot;
    public Transform spotTarget;
    private float _spotZoomT;
    private Vector3 _spotDir3;
    private float _spotStartDist;

    public bool FreeLook => _freeLook;
    public bool IsFirstPerson => firstPerson;
    public bool IsSpotView => spotView;

    // ====================== LIFECYCLE ======================

    private void Start()
    {
        GameObject playerGO = PlayerRef.Resolve(characterRootName) as GameObject;
        if (playerGO != null)
        {
            character = playerGO.transform;
        }

        cam = Camera.main;
        paint = Object.FindFirstObjectByType<ChameleonPaint>();
        gmm = Object.FindFirstObjectByType<GameModeManager>();

        _defPitch = pitch;
        _defDistance = distance;
        _defYaw = yaw;

        EnsureEventSystem();

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
        _prevInMenu = !InMenu();
    }

    // Creates a scene EventSystem if one doesn't already exist, preferring the new Input System's
    // UI module when it's available (via reflection, so this compiles fine without the package too)
    // and falling back to the legacy StandaloneInputModule otherwise.
    private void EnsureEventSystem()
    {
        if (Object.FindFirstObjectByType<EventSystem>() != null) return;

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();

        System.Type inputModuleType = System.Type.GetType(
            "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");

        if (inputModuleType != null)
        {
            go.AddComponent(inputModuleType);
        }
        else
        {
            go.AddComponent<StandaloneInputModule>();
        }
    }

    public bool InMenu()
    {
        if (!staticCameraInMenu) return false;
        if (gmm == null) return false;

        return !gmm.RoundActive;
    }

    private void ApplyCameraSwap(bool inMenu)
    {
        if (!swapHomeCamera) return;

        bool homeCamExists = _homeCam != null;

        if (cam != null)
        {
            cam.enabled = !(homeCamExists && inMenu);
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
            _freeLook = false;
            _wasInMenu = true;

            if (orbitButtons != null && orbitButtons.activeSelf)
            {
                orbitButtons.SetActive(false);
            }

            if (!homeCaptured) return;

            // Smoothly drift the camera back toward its original "home" pose while in the menu.
            float decay = Mathf.Exp(-followLerp * Time.deltaTime);
            float t = Mathf.Clamp01(1f - decay);

            Transform camTf = cam.transform;
            camTf.position = Vector3.Lerp(camTf.position, homePos, t);
            camTf.rotation = Quaternion.Slerp(camTf.rotation, homeRot, t);
            return;
        }

        if (character == null) return;

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

        bool drawing = paint != null && paint.drawingOn;

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

    private void HandleZoomInput()
    {
        if (Mouse.current != null)
        {
            Vector2 scroll = Mouse.current.scroll.ReadValue();
            if (Mathf.Abs(scroll.y) > 0.01f)
            {
                float delta = scroll.y >= 0f ? zoomScrollSpeed : -zoomScrollSpeed;
                distance = Mathf.Clamp(distance - delta, minDistance, maxDistance);
            }
        }

        if (Touchscreen.current == null) return;

        var touches = Touchscreen.current.touches;
        Vector2 t0 = Vector2.zero;
        Vector2 t1 = Vector2.zero;
        int pressedCount = 0;

        if (touches.Count > 0)
        {
            for (int i = 0; i < touches.Count && pressedCount < 2; i++)
            {
                if (!touches[i].press.isPressed) continue;

                Vector2 pos = touches[i].position.ReadValue();
                if (pressedCount == 0) t0 = pos; else t1 = pos;
                pressedCount++;
            }

            if (pressedCount > 1)
            {
                float dist = Vector2.Distance(t0, t1);
                if (prevPinchDist > 0f)
                {
                    float deltaDist = dist - prevPinchDist;
                    distance = Mathf.Clamp(distance - deltaDist * zoomPinchSpeed, minDistance, maxDistance);
                }
                prevPinchDist = dist;
                return;
            }
        }

        prevPinchDist = -1f;
    }

    // Smooths the character's Y position for the camera follow-point, so stair-stepping doesn't make
    // the camera bob every frame. Snaps instead of smoothing on big jumps (teleports, respawns).
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
            float t = Mathf.Clamp01(1f - Mathf.Exp(-stairCamSmooth * Time.deltaTime));
            _smoothY = Mathf.Lerp(_smoothY, y, t);
        }

        return new Vector3(character.position.x, _smoothY, character.position.z);
    }

    // Caches the SeekerCamProfile on the currently-active player, re-resolving only when the active
    // player changes (so this is cheap to call every frame).
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

    // ====================== CAMERA UPDATE (orbit / first-person / spot / free-look) ======================
    //
    // NOTE ON THIS REGION: this was one giant method in the decompiled source. It's been split into
    // UpdateCam (dispatch) + UpdateSpotCam / UpdateFirstPersonCam / ApplyOrbitCam (the three distinct
    // camera behaviors) for readability — a structural refactor, not a behavior change. The underlying
    // math is a faithful, cross-checked reconstruction (the Vector3.back/up/down/zero fallbacks were
    // verified against the same static-field offset table used successfully elsewhere in this
    // project).

    private void UpdateCam(bool snap)
    {
        if (spotView && spotTarget != null && cam != null)
        {
            UpdateSpotCam(snap);
            return;
        }

        if (!_freeLook)
        {
            Vector3 pivot = FollowPivot(snap);

            if (firstPerson)
            {
                UpdateFirstPersonCam(pivot, snap);
                return;
            }

            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 rotatedOffset = rot * (Vector3.back * distance);
            ApplyOrbitCam(pivot, rotatedOffset, snap);
        }
        else
        {
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 rotatedOffset = rot * (Vector3.back * distance);

            float pivotY;
            if (!scoutClampToRoom)
            {
                pivotY = _freePivot.y;
            }
            else
            {
                // CORRECTION: re-derived this term by term against the raw decompile. It is NOT a
                // decompiler artifact — it's a real, meaningful calculation. `camHeightAbovePivot`
                // is how much higher the CAMERA sits above the pivot once ApplyOrbitCam applies
                // `targetHeight` and this same `rotatedOffset` (both are reused for the actual
                // position calc below), and it's subtracted from the roof bound so the clamp keeps
                // the CAMERA under the ceiling, not just the pivot. The earlier draft dropped this
                // subtraction entirely, which would let the camera poke through the ceiling at
                // steep orbit angles.
                float camHeightAbovePivot = Mathf.Max(0f, targetHeight + rotatedOffset.y);
                float floor = _scoutFloorY + scoutFloorMargin;
                float roofClamp = Mathf.Max(floor, (_scoutRoofY - scoutCeilMargin) - camHeightAbovePivot);
                pivotY = (floor <= _freePivot.y) ? Mathf.Min(roofClamp, _freePivot.y) : floor;
                _freePivot.y = pivotY;
            }

            Vector3 pivot = new Vector3(_freePivot.x, pivotY, _freePivot.z);
            ApplyOrbitCam(pivot, rotatedOffset, snap);
        }
    }

    // Cinematic "you were caught" camera: orbits around a point between the catching bot and the
    // target, slowly zooming out over spotZoomTime.
    private void UpdateSpotCam(bool snap)
    {
        Vector3 targetPos = spotTarget.position;
        Vector3 focusPoint = targetPos + Vector3.up * spotFocusHeight;

        _spotZoomT += Time.deltaTime;
        float zoomT01 = 1f;
        if (spotZoomTime > 0.01f)
        {
            float raw = Mathf.Clamp01(_spotZoomT / spotZoomTime);
            zoomT01 = raw * raw * (3f - 2f * raw); // smoothstep
        }

        float lerpT = snap ? 1f : Mathf.Clamp01(1f - Mathf.Exp(-spotLerp * Time.deltaTime));

        float dist = _spotStartDist + zoomT01 * spotZoomOutBy;
        Vector3 desiredPos = focusPoint + _spotDir3 * dist;

        Transform camTf = cam.transform;
        camTf.position = Vector3.Lerp(camTf.position, desiredPos, lerpT);

        Vector3 lookDir = focusPoint - camTf.position;
        float lookMag = lookDir.magnitude;
        lookDir = lookMag <= 1e-5f ? Vector3.zero : lookDir / lookMag;

        Quaternion desiredRot = Quaternion.LookRotation(lookDir, Vector3.up);
        camTf.rotation = Quaternion.Slerp(camTf.rotation, desiredRot, lerpT);
    }

    // First-person / near-shoulder camera: offsets from the follow pivot by height/back/side (with
    // per-player SeekerCamProfile overrides and a prone blend), optionally pulled in by a wall-avoidance
    // raycast, then looks slightly ahead of the character (with an adjustable aim-pitch).
    private void UpdateFirstPersonCam(Vector3 pivot, bool snap)
    {
        SeekerCamProfile profile = ActiveCamProfile();

        float tpsHeightV = profile != null ? profile.tpsHeight : tpsHeight;
        float tpsBackV = profile != null ? profile.tpsBack : tpsBack;
        float tpsSideV = profile != null ? profile.tpsSide : tpsSide;
        float tpsLookAheadV = profile != null ? profile.tpsLookAhead : tpsLookAhead;
        float tpsLookHeightV = profile != null ? profile.tpsLookHeight : tpsLookHeight;
        float tpsLookSideV = profile != null ? profile.tpsLookSide : tpsLookSide;

        float proneTarget = _proneOn ? 1f : 0f;
        _proneAmt = Mathf.MoveTowards(_proneAmt, proneTarget, proneLerpSpeed * Time.deltaTime);

        if (_proneAmt > 0f)
        {
            float proneHeightDropV = profile != null ? profile.proneHeightDrop : proneHeightDrop;
            float proneLookDropV = profile != null ? profile.proneLookDrop : proneLookDrop;
            float proneBackV = profile != null ? profile.proneBack : proneBack;

            tpsLookHeightV -= proneLookDropV * _proneAmt;
            tpsHeightV -= proneHeightDropV * _proneAmt;
            tpsBackV = Mathf.Lerp(tpsBackV, proneBackV, Mathf.Clamp01(_proneAmt));
        }

        if (character == null) return;

        Vector3 charForward = character.forward;
        Vector3 flatForward = (charForward.x * charForward.x + charForward.z * charForward.z) >= 0.001f
            ? new Vector3(charForward.x, 0f, charForward.z).normalized
            : Vector3.back;

        Vector3 right = Vector3.Cross(Vector3.up, flatForward);

        Vector3 raisedPivot = pivot + Vector3.up * tpsHeightV;
        Vector3 desiredCamPos = raisedPivot + right * tpsSideV - flatForward * tpsBackV;

        if (tpsAvoidWalls)
        {
            Vector3 toDesired = desiredCamPos - raisedPivot;
            float dist = toDesired.magnitude;
            if (dist > 0.01f)
            {
                Vector3 dir = toDesired / dist;
                if (Physics.Raycast(raisedPivot, dir, out RaycastHit hit, dist + 0.5f))
                {
                    bool isCharacter = hit.collider is CharacterController ||
                                        hit.collider.GetComponentInParent<Animator>() != null;
                    if (!isCharacter)
                    {
                        desiredCamPos = hit.point - dir * 0.5f;
                    }
                }
            }
        }

        float lerpT = snap ? 1f : Mathf.Clamp01(1f - Mathf.Exp(-followLerp * Time.deltaTime));

        Transform camTf = cam.transform;
        camTf.position = Vector3.Lerp(camTf.position, desiredCamPos, lerpT);

        Vector3 lookTarget = raisedPivot + right * tpsLookSideV + flatForward * tpsLookAheadV + Vector3.up * tpsLookHeightV;
        Vector3 lookDir = lookTarget - camTf.position;

        if (Mathf.Abs(aimPitchDeg) > 0.01f)
        {
            Quaternion pitchRot = Quaternion.AngleAxis(-aimPitchDeg, right);
            lookDir = pitchRot * lookDir;
        }

        lookDir = lookDir.magnitude <= 1e-5f ? Vector3.zero : lookDir.normalized;

        Quaternion desiredRot = Quaternion.LookRotation(lookDir, Vector3.up);
        camTf.rotation = Quaternion.Slerp(camTf.rotation, desiredRot, lerpT);
    }

    // Shared tail for the normal third-person orbit and free-look cameras: moves toward
    // pivot+targetHeight+offset, and always looks back at pivot+targetHeight (the character's
    // head/chest height) regardless of orbit angle.
    private void ApplyOrbitCam(Vector3 pivot, Vector3 rotatedOffset, bool snap)
    {
        if (cam == null) return;

        Vector3 raisedPivot = pivot + Vector3.up * targetHeight;
        Vector3 desiredCamPos = raisedPivot + rotatedOffset;

        Transform camTf = cam.transform;
        float lerpT = snap ? 1f : Mathf.Clamp01(1f - Mathf.Exp(-followLerp * Time.deltaTime));
        camTf.position = Vector3.Lerp(camTf.position, desiredCamPos, lerpT);

        Vector3 lookDir = raisedPivot - camTf.position;
        Quaternion desiredRot = Quaternion.LookRotation(lookDir, Vector3.up);
        camTf.rotation = Quaternion.Slerp(camTf.rotation, desiredRot, lerpT);
    }

    // ====================== HOME CAMERA ======================

    private Camera ResolveHomeCamera()
    {
        if (string.IsNullOrEmpty(homeCameraName)) return null;

        Camera[] all = Resources.FindObjectsOfTypeAll<Camera>();
        foreach (Camera c in all)
        {
            if (c == cam) continue;
            if (c.name == homeCameraName && c.gameObject.scene.IsValid())
            {
                return c;
            }
        }
        return null;
    }

    // ====================== INPUT / EXTERNAL API ======================

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

    public void SetAimPitch(float deg)
    {
        aimPitchDeg = deg;
    }

    public void SetSpotView(Transform bot, Transform target)
    {
        _freeLook = false;

        if (!spotView && target != null)
        {
            _spotZoomT = 0f;

            Vector3 focusPoint = target.position + Vector3.up * spotFocusHeight;
            Vector3 camPos = cam != null ? cam.transform.position : focusPoint;
            Vector3 offset = camPos - focusPoint;

            float distSq = offset.sqrMagnitude;
            float dist = Mathf.Sqrt(distSq);
            _spotStartDist = Mathf.Max(6f, dist);

            if (distSq <= 0.01f)
            {
                // CORRECTION: Y and Z were swapped (and sign-flipped) in the earlier draft. Traced
                // the exact field assignment order in the decompile (x=fVar7, y=fVar9, z=fVar10) —
                // the real degenerate fallback is (0, +0.7071, -0.7071), not (0, -0.7071, +0.7071).
                _spotDir3 = new Vector3(0f, 0.70710677f, -0.70710677f);
            }
            else
            {
                _spotDir3 = offset / dist;
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

    public void SetFreeLook(bool on)
    {
        if (_freeLook == on) return;
        _freeLook = on;
        if (!on) return;
        if (character == null) return;

        _freePivot = character.position;
        CacheScoutBounds();
    }

    public void ToggleFreeLook()
    {
        SetFreeLook(!_freeLook);
    }

    // Probes straight down (to find the floor) and straight up (to find the ceiling) from the
    // character's position, ignoring character colliders, so free-look can be clamped to stay
    // inside the room.
    private void CacheScoutBounds()
    {
        float charY = character != null ? character.position.y : 0f;
        _scoutFloorY = charY;
        _scoutRoofY = charY + 60f;

        if (character == null) return;

        Vector3 pos = character.position;
        Vector3 probeOrigin = pos + Vector3.up * 2f;

        if (Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit floorHit, 80f))
        {
            if (floorHit.collider.GetComponentInParent<Animator>() == null)
            {
                _scoutFloorY = floorHit.point.y;
            }
        }

        Vector3 upStart = new Vector3(pos.x, _scoutFloorY + 1f, pos.z);
        RaycastHit[] hits = Physics.RaycastAll(upStart, Vector3.up, 300f);

        float highestY = float.MinValue;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.GetComponentInParent<Animator>() != null) continue; // skip characters
            if (hit.point.y > highestY)
            {
                highestY = hit.point.y;
            }
        }

        if (highestY > _scoutFloorY + 5f)
        {
            _scoutRoofY = highestY;
        }
    }

    public void PanFree(Vector2 input, float dt)
    {
        if (!_freeLook) return;
        if (cam == null) return;

        Vector3 camForward = cam.transform.forward;
        Vector3 camRight = cam.transform.right;

        Vector3 rightFlat;
        float rightMagSq = camRight.x * camRight.x + camRight.z * camRight.z;
        if (rightMagSq > 0.001f)
        {
            float rightMag = Mathf.Sqrt(rightMagSq);
            rightFlat = new Vector3(camRight.x / rightMag, 0f, camRight.z / rightMag);
        }
        else
        {
            rightFlat = Vector3.zero; // degenerate (camera looking straight up/down): no side-pan
        }

        float speed = freePanSpeed * dt;
        _freePivot.x += (camForward.x * input.y + rightFlat.x * input.x) * speed;
        _freePivot.y += (camForward.y * input.y + rightFlat.y * input.x) * speed;
        _freePivot.z += (camForward.z * input.y + rightFlat.z * input.x) * speed;
    }

    public void AuthorUI()
    {
        // Intentionally empty in the source.
    }

    // ====================== ORBIT BUTTONS UI ======================

    private void BuildOrbitButtons()
    {
        bool canvasCreated, orbitCreated, labelCreated;

        GameObject canvasGO = SceneUI.GetOrCreateCanvas("CameraOrbitCanvas", out canvasCreated);
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

        orbitButtons = SceneUI.GetOrCreate("OrbitButtons", canvasGO.transform, out orbitCreated);

        RectTransform orbitRt = SceneUI.GetOrAdd<RectTransform>(orbitButtons);
        orbitRt.anchorMax = new Vector2(1f, 0f);
        orbitRt.anchorMin = new Vector2(1f, 0f);
        orbitRt.pivot = new Vector2(1f, 0f);
        orbitRt.anchoredPosition = new Vector2(-30f, 40f);
        orbitRt.sizeDelta = new Vector2(330f, 330f);

        GameObject labelGO = SceneUI.GetOrCreate("OrbitLbl", orbitButtons.transform, out labelCreated);
        Text label = SceneUI.GetOrAdd<Text>(labelGO);
        label.font = UIFont.Default;
        label.raycastTarget = false;

        if (labelCreated)
        {
            label.text = "Rotate view";
            label.fontSize = 26;
            label.alignment = TextAnchor.LowerCenter;
            label.color = new Color(1f, 1f, 1f, 0.8f);
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;

            RectTransform labelRt = label.rectTransform;
            labelRt.anchorMin = new Vector2(0f, 1f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.pivot = new Vector2(0.5f, 0f);
            labelRt.anchoredPosition = new Vector2(0f, 6f);
            labelRt.sizeDelta = new Vector2(0f, 34f);
        }

        Sprite circle = MakeCircle(96);

        // A diamond D-pad: left/right orbit yaw, up/down orbit pitch.
        MakeHoldButton(orbitButtons.transform, "<", circle, new Vector2(0f, 110f), -1, false);
        MakeHoldButton(orbitButtons.transform, ">", circle, new Vector2(220f, 110f), 1, false);
        MakeHoldButton(orbitButtons.transform, "\u25B2", circle, new Vector2(110f, 220f), -1, true); // "▲"
        MakeHoldButton(orbitButtons.transform, "\u25BC", circle, new Vector2(110f, 0f), 1, true);    // "▼"

        orbitButtons.SetActive(false);
    }

    // Draws a soft anti-aliased circle mask into a runtime texture, used as the D-pad button icon.
    private Sprite MakeCircle(int s)
    {
        Texture2D tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
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

    // Builds one round hold-to-orbit button. Holding it sets orbitDir/pitchDir (consumed in
    // LateUpdate); releasing it clears back to 0.
    private void MakeHoldButton(Transform parent, string label, Sprite circle, Vector2 pos, int dir, bool isPitch)
    {
        GameObject go = SceneUI.GetOrCreate("Orbit_" + label, parent, out bool _);

        Image img = SceneUI.GetOrAdd<Image>(go);
        img.color = new Color(0.18f, 0.19f, 0.24f, 0.9f);
        img.sprite = circle;

        RectTransform rt = img.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(110f, 110f);

        GameObject labelGO = SceneUI.GetOrCreate("L", go.transform, out bool labelCreated);
        Text text = SceneUI.GetOrAdd<Text>(labelGO);
        text.font = UIFont.Default;
        text.raycastTarget = false;
        text.text = label;

        if (labelCreated)
        {
            text.fontSize = 60;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            RectTransform labelRt = text.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
        }

        // NOTE: the two press/release callback bodies (<MakeHoldButton>b__0 / b__1 in the dump)
        // weren't included in the pasted assembly — only the closure wiring (capturing 'this',
        // isPitch, dir) was shown. The implementation below is inferred from how orbitDir/pitchDir
        // are consumed in LateUpdate (nonzero while held, zero once released); JoyDrag's own field
        // names (onPointerDown/onPointerUp) are guessed too, since JoyDrag hasn't been reversed —
        // paste it if you have it, to confirm both.
        JoyDrag joyDrag = SceneUI.GetOrAdd<JoyDrag>(go);

        joyDrag.onPointer = _ =>
        {
            if (isPitch)
                pitchDir = dir;
            else
                orbitDir = dir;
        };

        joyDrag.onUp = _ =>
        {
            if (isPitch)
                pitchDir = 0;
            else
                orbitDir = 0;
        };
    }

    // ====================== SMALL UI HELPERS ======================
    // (Unused by BuildOrbitButtons, which goes through SceneUI instead — kept since they were
    // present in the dump; possibly leftover from an earlier version of this class.)

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

        Text text = go.AddComponent<Text>();
        text.font = UIFont.Default;
        text.text = content;
        text.fontSize = size;
        text.alignment = anchor;
        text.color = color;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private void Stretch(Component c)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}