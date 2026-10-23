using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output. Fields (names, order, attributes,
// offsets), the nested Mode enum and method accessibility match dump.cs (TypeDefIndex 9666). All
// string literals are confirmed against Dumpstringliteral.json.
//
// Decoded from libil2cpp.so with capstone (not in the paste):
//  - <>c.<BuildUI>b__168_2/4/6: c => new Color(c,0,0,1) / (0,c,0,1) / (0,0,c,1).
//  - <>c__DisplayClass168_0 (wrt, this, vrt): b__0 wheel drag -> SetHS(atan2(y,x)/2PI, min(r,1)) with
//    x/y the local point relative to the wheel centre over the half size; b__1 V-slider drag ->
//    SetV(clamp01((local.y - rect.yMin) / rect.height)); b__3/5/7 -> SetChannel(0/1/2, v);
//    b__8 / b__9 -> metallic / roughness = clamp01(v), ApplyMaterialParams, RefreshMaterialUI.
//  - DisplayClass177_0 (MakeBar) / 179_0 (MakeSliderBox): onVal(clamp01((local.x - rect.xMin) /
//    rect.width)); DisplayClass178_0 (MakeSizeSlider): SetBrushSize of the same value.
//  - <ReadAtEndOfFrame>d__148.MoveNext: see ReadAtEndOfFrame.
//  - The scene catcher's UIDrag gets cb / onMove / onUp / onExit = OnScenePointer / Move / Up /
//    Exit (checked in BuildUI's code).
//  - Ghidra's "x - (float)(int)x" in SetHS / StampAtUV / MakeWheel / TryRaycastAlbedo is frintm
//    (floor), i.e. the inlined Mathf.Repeat(x, 1f).
//  - SetMode colour tables (0xCE3xxx): eye button (0.95,0.70,0.20) on / (0.25,0.27,0.32) off;
//    erase button (0.90,0.45,0.40) on / (0.32,0.22,0.22) off; cursor pivot y 0.16 (paint) / 0.85.
//
// Inlined in raw: Singleton<RootManager>.Instance, HitsCharacter (in OnScenePointer),
// CameraController.OrbitYaw/OrbitPitch, Color -> Color32, Mathf.Clamp/Clamp01/Lerp/RoundToInt,
// UniversalAdditionalCameraData property setters, Image.sprite (Ghidra prints m_Corners).
// brMark / brVal are declared but never used.
//
// Convention: where raw jumps to the NullReferenceException stub, the C# just dereferences
// naturally; explicit null checks are kept only where raw really skips.
public class ChameleonPaint : MonoBehaviour
{
    private enum Mode
    {
        Paint = 0,
        Eyedrop = 1,
        Erase = 2
    }

    [Header("Character")]
    public string characterRootName = "Player";

    [Header("UI")]
    [Range(0.7f, 2.2f)]
    public float panelScale = 1.4f;

    [Header("PickerPanel slide animation")]
    [Tooltip("Duration (seconds) of the PickerPanel open/close slide. Set to 0 for instant movement.")]
    [Min(0f)]
    public float panelSlideDuration = 0.25f;
    [Tooltip("How far left (UI units) the PickerPanel slides when hidden.")]
    [Min(0f)]
    public float panelSlideDistance = 1000f;

    [Header("Icons (Assets/Texture2D)")]
    public Sprite pickerIcon;
    public Sprite eraserIcon;
    public Sprite paintIcon;
    [Tooltip("On-screen size (px) of the mouse cursor brush/eraser/picker icon shown while drawing. Bigger = more visible.")]
    public float cursorIconSize = 110f;

    [Header("Size slider (Assets/Texture2D) — track/fill/handle + preview ring/dot + brush icon")]
    public Sprite sizeTrackSprite;
    public Sprite sizeFillSprite;
    public Sprite sizeHandleSprite;
    public Sprite sizeDotSprite;
    public Sprite sizeRingSprite;
    public Sprite brushIconSprite;

    [Header("Material slider appearance")]
    [Tooltip("Diameter of Metallic/Roughness handles; uses the same sprite as SizeHandle. Smaller than SizeHandle to fit the existing two rows.")]
    [Range(24f, 80f)]
    public float materialSliderHandleSize = 50f;
    [Tooltip("Diameter of SizeDot (UI units) with the minimum brush size.")]
    public float sizePreviewDotMin = 20f;
    [Tooltip("Diameter of SizeDot (UI units) with the maximum brush size.")]
    public float sizePreviewDotMax = 70f;

    [Header("Horizontal color picker")]
    [Tooltip("Optional custom rainbow image. Leave empty to generate the rainbow automatically.")]
    public Sprite hueBarSprite;
    [Tooltip("Optional custom black-to-white image. Leave empty to generate it automatically.")]
    public Sprite lightnessBarSprite;
    [Tooltip("Optional white triangular pointer image (points UP). Leave empty to generate it automatically.")]
    public Sprite colorPointerSprite;

    [Header("Fill button")]
    [Tooltip("Optional icon for FillBtn. The button instantly paints the whole character with the selected brush color.")]
    public Sprite fillIconSprite;
    [Tooltip("Seconds to show FillBtn/Icon after each click. Uses real time, even if the game is paused.")]
    [Range(0.05f, 1f)]
    public float fillIconFlashDuration = 0.2f;

    [Header("Paint splatter (GoopSpray prefab) — bắn hạt khi tô, màu = màu brush")]
    [Tooltip("Prefab GoopSpray — instantiate 1 bản lúc runtime (KHÔNG để trong scene). BẮT BUỘC gán ở đây; để trống → không có splatter (prefab không nằm trong Resources/ nên fallback Resources.Load chỉ là dự phòng).")]
    public GameObject goopSprayPrefab;
    [Tooltip("Số hạt bắn mỗi nhịp (throttle theo goopInterval).")]
    public int goopEmitCount = 1;
    [Tooltip("Giãn cách tối thiểu giữa 2 lần bắn (giây) khi kéo tô — tránh spam hạt.")]
    public float goopInterval = 0.04f;
    [Tooltip("Tốc độ toé của hạt goop (u/s) — tạo độ tản + độ dài cho render Stretch. 0 = đứng yên (Stretch sẽ vô hình).")]
    public float goopSpraySpeed = 1.5f;
    private ParticleSystem goopSpray;
    private ParticleSystem goopGlobs;
    private float _goopTimer;
    [Tooltip("Shift the cursor icon UP (px) from the touch/finger point so it isn't hidden under the thumb on mobile. The paint still lands exactly where you touch. 0 = icon at the touch point (old behaviour).")]
    public float cursorTouchLift = 120f;

    [Header("Tool buttons")]
    // Tool selection is shown by enabling/disabling each button's child Icon GameObject.
    [Tooltip("Close (X) button sprite (x.png). Falls back to a red box + \"X\" text when unset.")]
    public Sprite closeIcon;

    [Header("Scene UI")]
    [Tooltip("Paint on/off gate button authored in the SCENE. Tap it to open the picker / enable painting; it auto-hides while drawing and reappears when you close. If left unset, auto-binds to a scene GameObject named \"PaintGateButton\".")]
    public Button paintGateButton;

    [Header("Gate button state icons (Assets/Texture2D)")]
    [Tooltip("Background while painting is ON (Rounded Rectangle 5 copy 8). The gate stays visible.")]
    public Sprite gateOnSprite;
    [Tooltip("Background while painting is OFF (Rounded Rectangle 5 copy 6).")]
    public Sprite gateOffSprite;

    [Header("Eyedropper")]
    [Tooltip("Read the surface's TRUE albedo texture via raycast — fully ignores lighting/shadows, so the same wall gives the same colour everywhere. Falls back to screen sampling on a miss.")]
    public bool eyedropTrueAlbedo = true;
    [Tooltip("Root object whose meshes get colliders so the albedo eyedropper can hit them.")]
    public string environmentRootName = "Room";
    [Tooltip("Fallback only (when true-albedo misses): lift the screen sample out of shadow.")]
    public bool eyedropIgnoreShadow = true;
    [Range(0.4f, 1f)]
    public float eyedropBrightness = 0.85f;

    [Header("Eyedropper magnifier (loupe)")]
    [Tooltip("Show a circular magnifier (a SECONDARY camera zoomed onto the sample point) while picking color with the eyedropper, so you can place the sample point precisely.")]
    public bool loupeEnabled = true;
    [Tooltip("Magnification of the loupe (the 'ZOOM xN' label).")]
    public float loupeZoom = 5f;
    [Tooltip("On-screen diameter (px) of the loupe circle.")]
    public float loupeSize = 240f;
    [Tooltip("Loupe position anchored to the screen's RIGHT-CENTER (x<0 = inset from the right edge).")]
    public Vector2 loupePos = new Vector2(-210f, 120f);

    private LevelManager levelMgr;
    private Color brush = Color.white;
    private float metallic;
    private float roughness = 1f;
    private float H;
    private float S;
    private float V;
    // Stored independently so the hue pointer does not jump when brush becomes black/white.
    private float selectedHue;
    // HSL saturation is remembered even at pure black or white, where RGB cannot
    // encode hue/saturation. Sliding back from either end restores the original color.
    private float selectedColorSaturation = 1f;
    private float selectedHueBarPosition = 1f; // Both ends of the rainbow are red; remember the exact end clicked.
    private RectTransform huePointer;
    private RectTransform lightnessPointer;
    private int brushPx = 22;
    private Mode mode;
    private Transform characterTf;
    private Renderer[] bodyRenderers;
    private Camera cam;
    private SkinnedMeshRenderer[] skinnedRenderers;
    private MeshCollider[] skinnedColliders;
    private Animator charAnimator;
    private Mesh[] bakedColliderMeshes;
    private Texture2D paintTex;
    private Color32[] paintBuf;
    private const int TexSize = 512;
    private bool dirty;
    private Color _avgBody = Color.white;
    private bool camoDirty = true;
    [Tooltip("Stride when averaging the body paint for bots' camo check (higher = cheaper, slightly less precise).")]
    [Range(1, 32)]
    public int bodyColorSampleStride = 8;
    private Texture2D readTex;
    private Texture2D albedoPix;
    private bool readPending;
    private Vector2 readPos;
    private Transform uiRoot;
    private Font font;
    private Image preview;
    private Image vSliderImg;
    private GameObject eyeBtnIcon;
    private GameObject eraseBtnIcon;
    private GameObject brushBtnIcon;
    private GameObject fillBtnIcon;
    private Coroutine fillIconFlashCoroutine;
    private Image cursorIcon;
    private Image paintGateImg;
    private Image mFill;
    private Image roFill;
    private RectTransform metallicHandleRt;
    private RectTransform roughnessHandleRt;
    private GameObject panelGO;
    private RectTransform panelRect;
    private Vector2 panelOpenPosition;
    private Coroutine panelSlideCoroutine;
    private readonly HashSet<GameObject> _reusedUi = new HashSet<GameObject>();
    private bool drawingOn;
    private CameraController camCtrl;
    private Camera loupeCam;
    private RenderTexture loupeRT;
    private GameObject loupeGO;
    private RawImage loupeImg;
    private Text loupeZoomText;
    private bool sceneGestureActive;
    private bool sceneGestureOrbit;
    private RectTransform wheelMarker;
    private RectTransform vMarker;
    private RectTransform rMark;
    private RectTransform gMark;
    private RectTransform bMark;
    private RectTransform brMark;
    private Text rVal;
    private Text gVal;
    private Text bVal;
    private Text mVal;
    private Text roVal;
    private Text brVal;
    private Text hexVal;
    private Text hsvVal;
    private Text modeText;
    private Texture2D vSliderTex;
    private Image _sizeFill;
    private RectTransform _sizeHandleRt;
    private Image _sizeHandleImg;
    private RectTransform _sizeDotRt;
    private Image _sizeDotImg;
    private float _sizeTrackW;
    private float wheelRadius = 100f;
    private Sprite solidSprite;
    private Sprite circleSprite;
    private Sprite ringSprite;
    private Sprite wheelSprite;
    private Sprite lineSprite;
    private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorID = Shader.PropertyToID("_Color");
    private static readonly int BaseMapID = Shader.PropertyToID("_BaseMap");
    private static readonly int MainTexID = Shader.PropertyToID("_MainTex");
    private static readonly int MetallicID = Shader.PropertyToID("_Metallic");
    private static readonly int SmoothnessID = Shader.PropertyToID("_Smoothness");

    public bool DrawingActive => drawingOn;

    private void Start()
    {
        font = UIFont.Default;
        cam = Camera.main;
        camCtrl = FindAnyObjectByType<CameraController>();
        readTex = new Texture2D(5, 5, TextureFormat.RGBA32, false);
        solidSprite = MakeSolid();
        circleSprite = MakeCircle(96);
        ringSprite = MakeRing(96, 0.7f);
        lineSprite = MakeSolid();
        FindCharacter();
        ResolveGoop();
        EnsureCollider();
        StartCoroutine(EnsureRoomCollidersWhenReady());
        InitPaintCanvas();
        EnsureEventSystem();
        BuildUI();
        SetBrush(Color.white);
        RefreshMaterialUI();
        RefreshBrushUI();
        SetMode(Mode.Paint);
        // Initial startup must hide the panel instantly; only user actions animate it.
        CloseDrawing(true);
    }

    // Body renderers of the player (anything under a "gun" is excluded), its skinned parts (stale
    // MeshColliders on them are removed) and its animator.
    private void FindCharacter()
    {
        GameObject go = PlayerRef.Resolve(characterRootName);
        if (go == null)
        {
            Debug.LogWarning("[ChameleonPaint] Character '" + characterRootName + "' not found.");
            bodyRenderers = new Renderer[0];
            return;
        }
        characterTf = go.transform;

        Renderer[] all = go.GetComponentsInChildren<Renderer>();
        List<Renderer> list = new List<Renderer>(all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            Renderer r = all[i];
            if (r != null && !IsUnderGun(r.transform))
            {
                list.Add(r);
            }
        }
        bodyRenderers = list.ToArray();

        skinnedRenderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            SkinnedMeshRenderer smr = skinnedRenderers[i];
            if (smr == null) continue;
            MeshCollider mc = smr.GetComponent<MeshCollider>();
            if (mc != null)
            {
                Destroy(mc);
            }
        }
        skinnedColliders = null;
        bakedColliderMeshes = null;
        charAnimator = go.GetComponentInChildren<Animator>(true);

        Debug.Log("[ChameleonPaint] Found '" + characterRootName + "' with " + bodyRenderers.Length
            + " body renderer(s) (gun excluded); skinnedParts=" + (skinnedRenderers != null ? skinnedRenderers.Length : 0)
            + " animator=" + (charAnimator != null) + ".");
    }

    private static bool IsUnderGun(Transform t)
    {
        for (; t != null; t = t.parent)
        {
            if (t.name == "gun")
            {
                return true;
            }
        }
        return false;
    }

    // While drawing, every skinned part gets a MeshCollider fed with a baked copy of its current
    // pose, so the brush raycast lands on the posed body.
    private void BeginPaintCollider()
    {
        if (skinnedRenderers == null || skinnedRenderers.Length == 0) return;
        int n = skinnedRenderers.Length;
        if (skinnedColliders == null || skinnedColliders.Length != n)
        {
            skinnedColliders = new MeshCollider[n];
        }
        if (bakedColliderMeshes == null || bakedColliderMeshes.Length != n)
        {
            bakedColliderMeshes = new Mesh[n];
        }
        for (int i = 0; i < n; i++)
        {
            if (skinnedRenderers[i] == null) continue;
            if (bakedColliderMeshes[i] == null)
            {
                bakedColliderMeshes[i] = new Mesh();
            }
            if (skinnedColliders[i] == null)
            {
                skinnedColliders[i] = skinnedRenderers[i].gameObject.AddComponent<MeshCollider>();
            }
        }
        RebakePaintCollider();
    }

    private void RebakePaintCollider()
    {
        if (skinnedRenderers == null || skinnedColliders == null || bakedColliderMeshes == null) return;
        int n = Mathf.Min(skinnedRenderers.Length, Mathf.Min(skinnedColliders.Length, bakedColliderMeshes.Length));
        for (int i = 0; i < n; i++)
        {
            if (skinnedRenderers[i] == null || skinnedColliders[i] == null) continue;
            if (bakedColliderMeshes[i] == null)
            {
                bakedColliderMeshes[i] = new Mesh();
            }
            skinnedRenderers[i].BakeMesh(bakedColliderMeshes[i], true);
            // Reassign through null so the collider picks up the re-baked mesh.
            skinnedColliders[i].sharedMesh = null;
            skinnedColliders[i].sharedMesh = bakedColliderMeshes[i];
        }
    }

    private void EndPaintCollider()
    {
        if (skinnedColliders == null) return;
        for (int i = 0; i < skinnedColliders.Length; i++)
        {
            if (skinnedColliders[i] != null)
            {
                Destroy(skinnedColliders[i]);
                skinnedColliders[i] = null;
            }
        }
    }

    // A non-skinned character root with no collider gets a MeshCollider from its MeshFilter.
    private void EnsureCollider()
    {
        if (characterTf == null) return;
        if (characterTf.GetComponent<Collider>() != null) return;
        MeshFilter mf = characterTf.GetComponent<MeshFilter>();
        MeshCollider mc = characterTf.gameObject.AddComponent<MeshCollider>();
        if (mf != null && mf.sharedMesh != null)
        {
            mc.sharedMesh = mf.sharedMesh;
        }
    }

    // Readable environment meshes without a collider get a MeshCollider (for the albedo eyedropper).
    private void EnsureRoomColliders(GameObject root)
    {
        if (root == null)
        {
            Debug.LogWarning("[ChameleonPaint] Environment room is null; " + "albedo eyedropper limited.");

            return;
        }

        int added = 0;

        MeshRenderer[] mrs = root.GetComponentsInChildren<MeshRenderer>(true);

        for (int i = 0; i < mrs.Length; i++)
        {
            MeshRenderer mr = mrs[i];

            if (mr.GetComponent<Collider>() != null)
            {
                continue;
            }

            MeshFilter mf = mr.GetComponent<MeshFilter>();

            if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable)
            {
                continue;
            }

            MeshCollider mc =mr.gameObject.AddComponent<MeshCollider>();

            mc.sharedMesh = mf.sharedMesh;
            added++;
        }

        Debug.Log($"[ChameleonPaint] Environment '{root.name}' ready. " + $"Added {added} colliders for albedo eyedropper.");
    }

    private IEnumerator EnsureRoomCollidersWhenReady()
    {
        GameObject root = GetCurrentRoom();

        while (root == null)
        {
            yield return null;
            root = GetCurrentRoom();
        }

        Debug.Log($"[ChameleonPaint] Environment ready: {root.name}");

        EnsureRoomColliders(root);
    }

    private GameObject GetCurrentRoom()
    {
        if (levelMgr == null)
        {
            levelMgr = FindAnyObjectByType<LevelManager>(FindObjectsInactive.Include);
        }

        if (levelMgr != null && levelMgr.CurrentRoomInstance != null)
        {
            return levelMgr.CurrentRoomInstance;
        }

        // Optional fallback for test/standalone scenes
        return GameObject.Find(environmentRootName);
    }

    private void InitPaintCanvas()
    {
        paintTex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false);
        paintTex.wrapMode = TextureWrapMode.Clamp;
        paintBuf = new Color32[TexSize * TexSize];
        ClearCanvas(Color.white);
        ApplyCanvasToBody();
    }

    private void ClearCanvas(Color c)
    {
        Color32 c32 = c;
        for (int i = 0; i < paintBuf.Length; i++)
        {
            paintBuf[i] = c32;
        }
        if (paintTex != null)
        {
            paintTex.SetPixels32(paintBuf);
            paintTex.Apply(false);
        }
        camoDirty = true;
    }

    // Average colour of the painted body (used by the bots' camouflage check). False (white) before
    // the canvas exists.
    public bool TryGetAverageBodyColor(out Color avg)
    {
        if (paintBuf == null)
        {
            avg = Color.white;
            return false;
        }
        if (camoDirty)
        {
            RecomputeAverageBodyColor();
        }
        avg = _avgBody;
        return true;
    }

    // Averages every stride-th texel that is not near-white (painted); if nothing is painted the
    // average is over all sampled texels.
    private void RecomputeAverageBodyColor()
    {
        int step = Mathf.Max(1, bodyColorSampleStride);
        Color32[] buf = paintBuf;
        long sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int y = 0; y < TexSize; y += step)
        {
            for (int x = 0; x < TexSize; x += step)
            {
                Color32 c = buf[y * TexSize + x];
                if (c.r < 250 || c.g < 250 || c.b < 250)
                {
                    sr += c.r;
                    sg += c.g;
                    sb += c.b;
                    n++;
                }
            }
        }
        if (n == 0)
        {
            for (int y = 0; y < TexSize; y += step)
            {
                for (int x = 0; x < TexSize; x += step)
                {
                    Color32 c = buf[y * TexSize + x];
                    sr += c.r;
                    sg += c.g;
                    sb += c.b;
                    n++;
                }
            }
        }
        Color avg = Color.white;
        if (n > 0)
        {
            float d = n * 255f;
            avg = new Color(sr / d, sg / d, sb / d, 1f);
        }
        _avgBody = avg;
        camoDirty = false;
    }

    // Body materials show the paint canvas untinted, with the current metallic / smoothness.
    private void ApplyCanvasToBody()
    {
        if (bodyRenderers == null) return;
        for (int i = 0; i < bodyRenderers.Length; i++)
        {
            Renderer r = bodyRenderers[i];
            if (r == null) continue;
            Material m = r.material;
            m.SetColor(BaseColorID, Color.white);
            m.SetColor(ColorID, Color.white);
            m.SetTexture(BaseMapID, paintTex);
            m.SetTexture(MainTexID, paintTex);
            m.SetFloat(MetallicID, metallic);
            m.SetFloat(SmoothnessID, 1f - roughness);
        }
    }

    private void ApplyMaterialParams()
    {
        if (bodyRenderers == null) return;
        for (int i = 0; i < bodyRenderers.Length; i++)
        {
            Renderer r = bodyRenderers[i];
            if (r == null) continue;
            Material m = r.material;
            m.SetFloat(MetallicID, metallic);
            m.SetFloat(SmoothnessID, 1f - roughness);
        }
    }

    public void ResetPaint()
    {
        if (paintBuf == null) return;
        metallic = 0f;
        roughness = 1f;
        ClearCanvas(Color.white);
        ApplyCanvasToBody();
        SetBrush(Color.white);
        RefreshMaterialUI();
    }

    // Soft round stamp (radius brushPx texels, full strength in the inner ~40%) at the UV.
    private void StampAtUV(Vector2 uv, Color col)
    {
        if (paintBuf == null) return;
        int cx = Mathf.RoundToInt(Mathf.Repeat(uv.x, 1f) * (TexSize - 1));
        int cy = Mathf.RoundToInt(Mathf.Repeat(uv.y, 1f) * (TexSize - 1));
        int r = brushPx;
        int x0 = Mathf.Max(0, cx - r);
        int x1 = Mathf.Min(TexSize - 1, cx + r);
        int y0 = Mathf.Max(0, cy - r);
        int y1 = Mathf.Min(TexSize - 1, cy + r);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > r) continue;
                float a = Mathf.Clamp01((1f - d / r) * 1.7f);
                float inv = 1f - a;
                int idx = y * TexSize + x;
                Color32 o = paintBuf[idx];
                paintBuf[idx] = new Color32(
                    (byte)(col.r * 255f * a + o.r * inv),
                    (byte)(col.g * 255f * a + o.g * inv),
                    (byte)(col.b * 255f * a + o.b * inv),
                    255);
            }
        }
        dirty = true;
        camoDirty = true;
    }

    // Uploads the paint buffer once per frame after stamping.
    private void LateUpdate()
    {
        if (dirty && paintTex != null)
        {
            paintTex.SetPixels32(paintBuf);
            paintTex.Apply(false);
            dirty = false;
        }
    }

    // Scene drag. Drawing: eyedrop samples; otherwise a gesture that starts on the character paints
    // and one that starts off it orbits the camera. Not drawing: always orbits.
    private void OnScenePointer(PointerEventData e)
    {
        if (!drawingOn)
        {
            if (camCtrl == null) return;
        }
        else
        {
            MoveCursorIcon(e.position);
            if (mode == Mode.Eyedrop)
            {
                UpdateLoupe(e.position);
                if (eyedropTrueAlbedo && TryRaycastAlbedo(e.position, out Color albedo))
                {
                    SetBrush(albedo);
                    return;
                }
                RequestRead(e.position);
                return;
            }
            if (!sceneGestureActive)
            {
                sceneGestureActive = true;
                sceneGestureOrbit = !HitsCharacter(e.position);
            }
            if (!sceneGestureOrbit)
            {
                StampOnCharacter(e.position);
                return;
            }
            if (camCtrl == null) return;
        }
        camCtrl.OrbitYaw(e.delta.x);
        camCtrl.OrbitPitch(e.delta.y);
    }

    // Nearest hit on the character itself (CharacterControllers ignored), after re-baking the posed
    // colliders.
    private bool TryRaycastCharacter(Vector2 screenPos, out RaycastHit best)
    {
        best = default(RaycastHit);
        if (cam == null)
        {
            cam = Camera.main;
        }
        if (cam == null) return false;
        if (characterTf == null) return false;
        RebakePaintCollider();
        RaycastHit[] hits = Physics.RaycastAll(cam.ScreenPointToRay(screenPos), 10000f);
        bool found = false;
        float bestDist = float.MaxValue;
        for (int i = 0; i < hits.Length; i++)
        {
            Transform t = hits[i].transform;
            if (t != characterTf && !t.IsChildOf(characterTf)) continue;
            if (hits[i].collider is CharacterController) continue;
            if (hits[i].distance < bestDist)
            {
                bestDist = hits[i].distance;
                best = hits[i];
                found = true;
            }
        }
        return found;
    }

    private bool HitsCharacter(Vector2 screenPos)
    {
        return TryRaycastCharacter(screenPos, out RaycastHit hit);
    }

    // Paints (or erases to white) at the hit UV, plays the stroke sound and sprays goop.
    private void StampOnCharacter(Vector2 screenPos)
    {
        if (!TryRaycastCharacter(screenPos, out RaycastHit hit)) return;
        StampAtUV(hit.textureCoord, mode == Mode.Erase ? Color.white : brush);
        AudioManager.PaintStroke();
        if (mode != Mode.Erase)
        {
            SprayGoop(hit.point, brush);
        }
    }

    // The splatter comes from the first child ParticleSystem of a GoopSpray instance (the prefab,
    // else Resources "GoopSpray", else a scene object of that name).
    private void ResolveGoop()
    {
        if (goopSpray == null)
        {
            GameObject prefab = goopSprayPrefab != null ? goopSprayPrefab : Resources.Load<GameObject>("GoopSpray");
            if (prefab != null)
            {
                GameObject inst = Instantiate(prefab);
                inst.name = "GoopSpray";
                goopSpray = inst.GetComponent<ParticleSystem>();
            }
            else
            {
                GameObject found = GameObject.Find("GoopSpray");
                if (found == null)
                {
                    Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        Transform t = all[i];
                        if (t.name == "GoopSpray" && t.gameObject.scene.IsValid())
                        {
                            found = t.gameObject;
                            break;
                        }
                    }
                }
                if (found != null)
                {
                    goopSpray = found.GetComponent<ParticleSystem>();
                }
            }
        }
        if (goopSpray != null)
        {
            ParticleSystem[] systems = goopSpray.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] != goopSpray)
                {
                    goopGlobs = systems[i];
                    break;
                }
            }
        }
        if (goopGlobs != null && !goopGlobs.isPlaying)
        {
            goopGlobs.Play();
        }
        if (goopGlobs == null)
        {
            Debug.LogWarning("[ChameleonPaint] GoopSpray chưa resolve được — gán 'goopSprayPrefab' trong Inspector; splatter khi tô đang TẮT.");
        }
    }

    // Emits goopEmitCount brush-coloured particles at the hit point, at most once per goopInterval.
    private void SprayGoop(Vector3 worldPos, Color col)
    {
        if (goopGlobs == null || Time.time - _goopTimer < goopInterval) return;
        _goopTimer = Time.time;
        if (!goopGlobs.isPlaying)
        {
            goopGlobs.Play();
        }
        ParticleSystem.EmitParams ep = new ParticleSystem.EmitParams();
        ep.startColor = new Color(col.r, col.g, col.b, 1f);
        int n = Mathf.Max(1, goopEmitCount);
        for (int i = 0; i < n; i++)
        {
            ep.position = worldPos;
            ep.velocity = UnityEngine.Random.insideUnitSphere * goopSpraySpeed;
            goopGlobs.Emit(ep, 1);
        }
    }

    private void OnScenePointerMove(PointerEventData e)
    {
        if (!drawingOn) return;
        MoveCursorIcon(e.position);
    }

    // Releasing ends the gesture; the eyedropper is one-shot and returns to painting.
    private void OnScenePointerUp(PointerEventData e)
    {
        sceneGestureActive = false;
        if (drawingOn && mode == Mode.Eyedrop)
        {
            SetMode(Mode.Paint);
        }
    }

    private void OnScenePointerExit(PointerEventData e)
    {
        if (cursorIcon != null)
        {
            cursorIcon.gameObject.SetActive(false);
        }
        HideLoupe();
    }

    private void MoveCursorIcon(Vector2 screenPos)
    {
        if (cursorIcon == null || !drawingOn) return;
        if (!cursorIcon.gameObject.activeSelf)
        {
            cursorIcon.gameObject.SetActive(true);
        }
        cursorIcon.rectTransform.position = new Vector3(screenPos.x, screenPos.y + cursorTouchLift, 0f);
    }

    // True albedo under the pointer: material tint x base texture texel at the hit UV (tiling and
    // offset applied), read back through a temporary RenderTexture. Only MeshCollider hits count.
    private bool TryRaycastAlbedo(Vector2 screenPos, out Color result)
    {
        result = Color.white;
        if (cam == null)
        {
            cam = Camera.main;
        }
        if (cam == null) return false;
        if (!Physics.Raycast(cam.ScreenPointToRay(screenPos), out RaycastHit hit, 10000f)) return false;
        if (!(hit.collider is MeshCollider)) return false;
        Renderer rend = hit.collider != null ? hit.collider.GetComponent<Renderer>() : null;
        if (rend == null) return false;
        if (rend.sharedMaterial == null) return false;
        Material mat = rend.sharedMaterial;

        Color tint;
        if (mat.HasProperty(BaseColorID))
        {
            tint = mat.GetColor(BaseColorID);
        }
        else if (mat.HasProperty(ColorID))
        {
            tint = mat.GetColor(ColorID);
        }
        else
        {
            tint = Color.white;
        }

        Texture tex = mat.HasProperty(BaseMapID) ? mat.GetTexture(BaseMapID) : null;
        if (tex == null && mat.HasProperty(MainTexID))
        {
            tex = mat.GetTexture(MainTexID);
        }
        if (tex == null)
        {
            result = new Color(tint.r, tint.g, tint.b, 1f);
            return true;
        }

        Vector4 st = mat.HasProperty("_BaseMap_ST") ? mat.GetVector("_BaseMap_ST") : new Vector4(1f, 1f, 0f, 0f);
        float u = Mathf.Repeat(st.z + st.x * hit.textureCoord.x, 1f);
        float v = Mathf.Repeat(st.w + st.y * hit.textureCoord.y, 1f);
        int px = Mathf.Clamp((int)(u * tex.width), 0, tex.width - 1);
        int py = Mathf.Clamp((int)(v * tex.height), 0, tex.height - 1);

        RenderTexture prev = RenderTexture.active;
        RenderTexture tmp = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(tex, tmp);
        RenderTexture.active = tmp;
        if (albedoPix == null)
        {
            albedoPix = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        }
        albedoPix.ReadPixels(new Rect(px, py, 1f, 1f), 0, 0, false);
        albedoPix.Apply(false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(tmp);

        Color s = albedoPix.GetPixel(0, 0);
        result = new Color(Mathf.Clamp01(tint.r * s.r), Mathf.Clamp01(tint.g * s.g), Mathf.Clamp01(tint.b * s.b), 1f);
        return true;
    }

    // Screen-sample fallback: read on the next end of frame (only one read in flight).
    private void RequestRead(Vector2 screenPos)
    {
        readPos = screenPos;
        if (readPending) return;
        readPending = true;
        StartCoroutine(ReadAtEndOfFrame());
    }

    // Averages a 5x5 screen patch around readPos (clamped to the screen).
    private IEnumerator ReadAtEndOfFrame()
    {
        yield return new WaitForEndOfFrame();
        int x = Mathf.Clamp(Mathf.RoundToInt(readPos.x) - 2, 0, Screen.width - 5);
        int y = Mathf.Clamp(Mathf.RoundToInt(readPos.y) - 2, 0, Screen.height - 5);
        readTex.ReadPixels(new Rect(x, y, 5f, 5f), 0, 0, false);
        readTex.Apply(false);
        Color[] px = readTex.GetPixels();
        float r = 0f, g = 0f, b = 0f;
        for (int i = 0; i < px.Length; i++)
        {
            r += px[i].r;
            g += px[i].g;
            b += px[i].b;
        }
        float n = px.Length;
        SetBrush(NormalizeAlbedo(new Color(r / n, g / n, b / n, 1f)));
        readPending = false;
    }

    // With eyedropIgnoreShadow, scales the sample so its brightest channel reaches
    // eyedropBrightness (gain 1..6); alpha is always 1.
    private Color NormalizeAlbedo(Color c)
    {
        float r = c.r, g = c.g, b = c.b;
        if (eyedropIgnoreShadow)
        {
            float max = Mathf.Max(r, Mathf.Max(g, b));
            if (max >= 0.004f)
            {
                float k = Mathf.Clamp(eyedropBrightness / max, 1f, 6f);
                r = Mathf.Clamp01(r * k);
                g = Mathf.Clamp01(g * k);
                b = Mathf.Clamp01(b * k);
            }
        }
        return new Color(r, g, b, 1f);
    }

    private void SetBrush(Color c)
    {
        brush = new Color(c.r, c.g, c.b, 1f);
        Color.RGBToHSV(brush, out H, out S, out V);
        // Black and white do not encode hue or saturation. Keep the last meaningful
        // selection so the lightness slider can restore it when moved back inward.
        RGBToHSL(brush, out float h, out float saturation, out float lightness);
        if (lightness > 0.00001f && lightness < 0.99999f)
        {
            selectedColorSaturation = saturation;
        }
        if (S > 0.00001f && lightness > 0.00001f && lightness < 0.99999f)
        {
            // Keep whichever red endpoint was clicked rather than snapping to the other.
            float hueDistance = Mathf.Abs(Mathf.Repeat(H - selectedHue + 0.5f, 1f) - 0.5f);
            if (hueDistance > 0.001f) selectedHueBarPosition = 1f - H;
            selectedHue = H;
        }
        RefreshColorUI();
    }

    // The rainbow runs red -> magenta -> blue -> cyan -> green -> yellow -> red,
    // matching the supplied reference. Clicking hue makes fully saturated paint.
    private void SetHueFromBar(float t)
    {
        selectedHueBarPosition = Mathf.Clamp01(t);
        selectedHue = Mathf.Repeat(1f - selectedHueBarPosition, 1f);
        float h, s, l;
        RGBToHSL(brush, out h, out s, out l);
        // A white/black starting brush otherwise hides the newly selected hue.
        if (l < 0.02f || l > 0.98f) l = 0.5f;
        selectedColorSaturation = 1f;
        SetBrush(HSLToRGB(selectedHue, selectedColorSaturation, l));
    }

    // Lightness: left=black, middle=vivid selected hue, right=white.
    // Preserve existing saturation when the eyedropper sampled a muted color.
    private void SetLightnessFromBar(float t)
    {
        // Do NOT recalculate saturation from brush here: at black/white it is zero.
        // Use the last selected HSL saturation instead.
        SetBrush(HSLToRGB(selectedHue, selectedColorSaturation, Mathf.Clamp01(t)));
    }

    private static void RGBToHSL(Color color, out float h, out float s, out float l)
    {
        float hsvS, hsvV;
        Color.RGBToHSV(color, out h, out hsvS, out hsvV);
        float min = hsvV * (1f - hsvS);
        l = (hsvV + min) * 0.5f;
        float chroma = hsvV - min;
        float denom = 1f - Mathf.Abs(2f * l - 1f);
        s = denom > 0.00001f ? Mathf.Clamp01(chroma / denom) : 0f;
    }

    private static Color HSLToRGB(float h, float s, float l)
    {
        l = Mathf.Clamp01(l);
        s = Mathf.Clamp01(s);
        float chroma = (1f - Mathf.Abs(2f * l - 1f)) * s;
        float v = l + chroma * 0.5f;
        float hsvS = v > 0.00001f ? chroma / v : 0f;
        Color color = Color.HSVToRGB(Mathf.Repeat(h, 1f), Mathf.Clamp01(hsvS), v);
        color.a = 1f;
        return color;
    }

    // Slider 0..1 -> brush radius 5..48 texels.
    private void SetBrushSize(float v)
    {
        brushPx = Mathf.RoundToInt(Mathf.Lerp(5f, 48f, Mathf.Clamp01(v)));
        RefreshBrushUI();
    }

    // Show only the selected tool's Icon child; keep the button backgrounds unchanged.
    private void SetMode(Mode m)
    {
        mode = m;
        if (m != Mode.Eyedrop)
        {
            HideLoupe();
        }

        if (eyeBtnIcon != null)
        {
            eyeBtnIcon.SetActive(m == Mode.Eyedrop);
        }
        if (eraseBtnIcon != null)
        {
            eraseBtnIcon.SetActive(m == Mode.Erase);
        }
        if (brushBtnIcon != null)
        {
            brushBtnIcon.SetActive(m == Mode.Paint);
        }
        // Fill is an immediate action, not a persistent editing mode.
        if (fillBtnIcon != null)
        {
            fillBtnIcon.SetActive(false);
        }
        if (modeText != null)
        {
            modeText.text = m == Mode.Eyedrop ? "EYEDROPPER: click a surface to sample"
                : (m == Mode.Erase ? "ERASER: drag over the character to erase" : "PAINT: click / drag on the character");
        }
        if (cursorIcon == null) return;
        Sprite icon = m == Mode.Eyedrop ? pickerIcon : (m == Mode.Erase ? eraserIcon : paintIcon);
        if (icon != null)
        {
            cursorIcon.sprite = icon;
        }
        // The pivot sits on the tool's tip (brush tip low, picker/eraser tip high).
        cursorIcon.rectTransform.pivot = new Vector2(0.12f, m == Mode.Paint ? 0.16f : 0.85f);
        cursorIcon.gameObject.SetActive(false);
    }

    public void ToggleDrawing()
    {
        // RootManager.Instance?.ShowInterAds_Native();
        if (drawingOn)
        {
            CloseDrawing();
        }
        else
        {
            OpenDrawing();
        }
    }

    public void OpenDrawing()
    {
        drawingOn = true;
        BeginPaintCollider();
        SlidePickerPanel(true);
        if (paintGateImg != null && gateOnSprite != null)
        {
            paintGateImg.sprite = gateOnSprite;
        }
        if (modeText != null)
        {
            modeText.gameObject.SetActive(true);
        }
        SetMode(Mode.Paint);
    }

    public void CloseDrawing()
    {
        CloseDrawing(false);
    }

    private void CloseDrawing(bool immediately)
    {
        drawingOn = false;
        EndPaintCollider();
        SetMode(Mode.Paint);
        if (immediately)
        {
            StopPickerPanelSlide();
            if (panelRect != null)
            {
                panelRect.anchoredPosition = panelOpenPosition + Vector2.left * panelSlideDistance;
            }
            if (panelGO != null)
            {
                panelGO.SetActive(false);
            }
        }
        else
        {
            // Keep PickerPanel active until it has finished sliding offscreen.
            SlidePickerPanel(false);
        }
        if (paintGateImg != null && gateOffSprite != null)
        {
            paintGateImg.sprite = gateOffSprite;
        }
        if (modeText != null)
        {
            modeText.gameObject.SetActive(false);
        }
        if (cursorIcon != null)
        {
            cursorIcon.gameObject.SetActive(false);
        }
        HideLoupe();
    }

    private void StopPickerPanelSlide()
    {
        if (panelSlideCoroutine != null)
        {
            StopCoroutine(panelSlideCoroutine);
            panelSlideCoroutine = null;
        }
    }

    // Slides horizontally on the RectTransform's X axis, leaving Y unchanged.
    // Open: X -1000 -> 0; Close: X 0 -> -1000 (by default).
    private void SlidePickerPanel(bool open)
    {
        if (panelGO == null || panelRect == null) return;

        StopPickerPanelSlide();
        Vector2 target = panelOpenPosition + Vector2.left * (open ? 0f : panelSlideDistance);

        if (open && !panelGO.activeSelf)
        {
            // If the panel was fully hidden, start from the left.
            panelRect.anchoredPosition = panelOpenPosition + Vector2.left * panelSlideDistance;
            panelGO.SetActive(true);
        }
        if (panelSlideDuration <= 0f || !isActiveAndEnabled)
        {
            panelRect.anchoredPosition = target;
            if (!open) panelGO.SetActive(false);
            return;
        }
        panelSlideCoroutine = StartCoroutine(AnimatePickerPanel(target, open));
    }

    private IEnumerator AnimatePickerPanel(Vector2 target, bool opening)
    {
        Vector2 from = panelRect.anchoredPosition;
        float elapsed = 0f;
        while (elapsed < panelSlideDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / panelSlideDuration);
            t = t * t * (3f - 2f * t); // smooth ease-in/ease-out
            panelRect.anchoredPosition = Vector2.LerpUnclamped(from, target, t);
            yield return null;
        }
        panelRect.anchoredPosition = target;
        if (!opening && panelGO != null)
        {
            panelGO.SetActive(false);
        }
        panelSlideCoroutine = null;
    }

    public void ToggleEyedrop()
    {
        SetMode(mode == Mode.Eyedrop ? Mode.Paint : Mode.Eyedrop);
    }

    public void ToggleErase()
    {
        SetMode(mode == Mode.Erase ? Mode.Paint : Mode.Erase);
    }

    // The brush button explicitly selects Paint mode.
    public void SelectBrush()
    {
        SetMode(Mode.Paint);
    }

    // One-click fill of the entire character: every pixel in the shared paint texture
    // changes to the current brush color immediately, without changing the active tool.
    public void FillBody()
    {
        // Also works when this method is wired directly through the Inspector's OnClick.
        FlashFillButtonIcon();
        if (paintBuf == null) return;
        ClearCanvas(brush);
        dirty = false; // The texture was already uploaded by ClearCanvas.
        ApplyCanvasToBody();
        AudioManager.PaintStroke();
    }

    // Briefly show FillBtn/Icon as click feedback, then hide it automatically.
    // Restarting the timer on a rapid second click avoids the first click hiding it early.
    private void FlashFillButtonIcon()
    {
        if (fillBtnIcon == null) return;
        if (fillIconFlashCoroutine != null)
        {
            StopCoroutine(fillIconFlashCoroutine);
            fillIconFlashCoroutine = null;
        }
        fillBtnIcon.SetActive(true);
        fillIconFlashCoroutine = StartCoroutine(HideFillIconAfterDelay());
    }

    private IEnumerator HideFillIconAfterDelay()
    {
        yield return new WaitForSecondsRealtime(fillIconFlashDuration);
        if (fillBtnIcon != null)
        {
            fillBtnIcon.SetActive(false);
        }
        fillIconFlashCoroutine = null;
    }

    private void RefreshColorUI()
    {
        if (preview != null) preview.color = brush;
        if (_sizeDotImg != null) _sizeDotImg.color = brush;
        if (_sizeHandleImg != null) _sizeHandleImg.color = brush;

        float h, saturation, lightness;
        RGBToHSL(brush, out h, out saturation, out lightness);
        // The rainbow is drawn in reverse HSV order, red at both ends.
        PositionColorPointer(huePointer, selectedHueBarPosition);
        PositionColorPointer(lightnessPointer, lightness);

        // Keep the optional hex readout synchronized if one already exists.
        if (hexVal != null) hexVal.text = "#" + ColorUtility.ToHtmlStringRGB(brush);
    }

    // Position the *entire* triangle inside its bar, including at 0% and 100%.
    // The bar is the pointer's parent, so all math is in that bar's local UI units.
    private static void PositionColorPointer(RectTransform pointer, float t)
    {
        if (pointer == null) return;
        RectTransform bar = pointer.parent as RectTransform;
        if (bar == null) return;
        t = Mathf.Clamp01(t);
        float halfPointer = Mathf.Min(pointer.rect.width * 0.2f, bar.rect.width * 0.2f);
        float x = Mathf.Lerp(bar.rect.xMin + halfPointer,
            bar.rect.xMax - halfPointer, t);
        pointer.anchorMin = new Vector2(0f, 0f);
        pointer.anchorMax = new Vector2(0f, 0f);
        pointer.pivot = new Vector2(0.5f, 1f);
        pointer.anchoredPosition = new Vector2(x - bar.rect.xMin, 40f);
    }

    private void RefreshMaterialUI()
    {
        if (mFill != null)
        {
            mFill.fillAmount = metallic;
        }
        if (roFill != null)
        {
            roFill.fillAmount = roughness;
        }
        PositionMaterialHandle(metallicHandleRt, metallic);
        PositionMaterialHandle(roughnessHandleRt, roughness);
        if (mVal != null)
        {
            mVal.text = metallic.ToString("0.00");
        }
        if (roVal != null)
        {
            roVal.text = roughness.ToString("0.00");
        }
    }

    private static void PositionMaterialHandle(RectTransform handle, float value)
    {
        if (handle == null) return;
        RectTransform track = handle.parent as RectTransform;
        if (track == null) return;
        handle.anchoredPosition = new Vector2(
            Mathf.Lerp(track.rect.xMin, track.rect.xMax, Mathf.Clamp01(value)) - track.rect.xMin,
            0f);
    }

    // Map brushPx (5..48) to the FULL track length and to a 5..75 preview dot.
    private void RefreshBrushUI()
    {
        float t = Mathf.Clamp01((brushPx - 5f) / 43f);
        if (_sizeHandleRt != null)
        {
            // The handle's center reaches BOTH actual endpoints of SizeTrack.
            _sizeHandleRt.anchoredPosition = new Vector2(_sizeTrackW * t, 0f);
        }
        if (_sizeFill != null)
        {
            _sizeFill.fillAmount = t;
        }
        if (_sizeDotRt != null)
        {
            float d = Mathf.Lerp(sizePreviewDotMin, sizePreviewDotMax, t);
            _sizeDotRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, d);
            _sizeDotRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, d);
        }
    }

    // Vertical 6px line at x = v across the bar.
    private static void SetMarkerX(RectTransform m, float v)
    {
        if (m == null) return;
        v = Mathf.Clamp01(v);
        m.anchorMin = new Vector2(v, 0f);
        m.anchorMax = new Vector2(v, 1f);
        m.pivot = new Vector2(0.5f, 0.5f);
        m.anchoredPosition = Vector2.zero;
        m.sizeDelta = new Vector2(6f, 0f);
    }

    // Horizontal 6px line at y = v across the bar.
    private static void SetMarkerY(RectTransform m, float v)
    {
        if (m == null) return;
        v = Mathf.Clamp01(v);
        m.anchorMin = new Vector2(0f, v);
        m.anchorMax = new Vector2(1f, v);
        m.pivot = new Vector2(0.5f, 0.5f);
        m.anchoredPosition = Vector2.zero;
        m.sizeDelta = new Vector2(0f, 6f);
    }

    // Creates an EventSystem if none exists: new Input System UI module when that package is
    // present (looked up by name), else StandaloneInputModule.
    private void EnsureEventSystem()
    {
        if (FindAnyObjectByType<EventSystem>() != null) return;

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

    public void AuthorUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }
        if (solidSprite == null)
        {
            solidSprite = MakeSolid();
            circleSprite = MakeCircle(96);
            ringSprite = MakeRing(96, 0.7f);
                lineSprite = MakeSolid();
        }
        EnsureEventSystem();
        BuildUI();
        SetMode(Mode.Paint);
    }

    // "ChameleonPaintCanvas" (1080x1920): a full-screen invisible "Scene" catcher for painting /
    // orbiting, and the "PickerPanel" (860x520 top-left, scaled by panelScale) with the close
    // button, horizontal rainbow + lightness bars, preview, PICK / ERASE / BRUSH buttons,
    // brush size slider and Metallic / Roughness boxes. Objects that already existed keep their
    // authored layout (_reusedUi).
    private void BuildUI()
    {
        _reusedUi.Clear();

        GameObject canvasGO = SceneUI.GetOrCreateCanvas("ChameleonPaintCanvas", out bool canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);
        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);
        uiRoot = canvas.transform;

        Image scene = NewImage("Scene", uiRoot, new Color(1f, 1f, 1f, 0f), null);
        Stretch(scene, 0f);
        scene.raycastTarget = true;
        UIDrag sceneDrag = SceneUI.GetOrAdd<UIDrag>(scene.gameObject);
        sceneDrag.cb = OnScenePointer;
        sceneDrag.onMove = OnScenePointerMove;
        sceneDrag.onUp = OnScenePointerUp;
        sceneDrag.onExit = OnScenePointerExit;

        Image panel = NewImage("PickerPanel", uiRoot, new Color(0.1f, 0.11f, 0.13f, 0f), solidSprite);
        // Additional bottom row holds the 600px size slider without overlapping other controls.
        // Existing/custom-authored panel layouts are not overwritten by Place().
        Place(panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -30f), new Vector2(860f, 652f));
        if (!_reusedUi.Contains(panel.gameObject))
        {
            panel.rectTransform.localScale = Vector3.one * Mathf.Clamp(panelScale, 0.5f, 3f);
        }
        Transform p = panel.transform;
        panelGO = panel.gameObject;
        // Open ends at X = 0; keep the original Y position and UI anchoring/layout.
        if (panelRect != panel.rectTransform)
        {
            panelRect = panel.rectTransform;
            panelOpenPosition = new Vector2(0f, panelRect.anchoredPosition.y);
        }

        bool hasClose = closeIcon != null;
        Image close = NewImage("CloseBtn", p, Color.white, hasClose ? closeIcon : solidSprite);
        if (!hasClose)
        {
            close.color = new Color(0.5f, 0.2f, 0.2f, 0.95f);
        }
        close.preserveAspect = true;
        Place(close, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(930f, -120f), new Vector2(60f, 60f));
        Button closeButton = SceneUI.GetOrAdd<Button>(close.gameObject);
        closeButton.targetGraphic = close;
        // Respect Inspector callbacks; auto-wire runtime-created CloseBtn only when needed.
        if (closeButton.onClick.GetPersistentEventCount() == 0)
        {
            closeButton.onClick.RemoveListener(CloseDrawing);
            closeButton.onClick.AddListener(CloseDrawing);
        }
        NewTextIn("X", close.transform, "X", 32, TextAnchor.MiddleCenter, Color.white).gameObject.SetActive(!hasClose);

        // Replace the old HSV wheel, V-slider and R/G/B bars with two horizontal bars.
        HideLegacyColorPicker(p);
        BuildHorizontalColorPicker(p);

        Image previewBack = NewImage("PreviewBack", p, new Color(0f, 0f, 0f, 0.6f), solidSprite);
        Place(previewBack, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -288f), new Vector2(310f, 46f));
        preview = NewImage("Preview", previewBack.transform, Color.white, solidSprite);
        Stretch(preview, 4f);
        preview.raycastTarget = false;

        // Preserve original button images on authored scene objects. Their BaseIcon
        // stays visible; only the selected-state Icon is enabled/disabled.
        Image eye = NewToolButtonImage("EyeBtn", p, new Color(0.25f, 0.27f, 0.32f, 1f));
        Place(eye, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(680f, -800f), new Vector2(150f, 150f));
        Button eyeButton = SceneUI.GetOrAdd<Button>(eye.gameObject);
        eyeButton.targetGraphic = eye;
        if (eyeButton.onClick.GetPersistentEventCount() == 0)
        {
            eyeButton.onClick.RemoveListener(ToggleEyedrop);
            eyeButton.onClick.AddListener(ToggleEyedrop);
        }
        eyeBtnIcon = AddToolButtonContent(eye.transform, pickerIcon, "PICK");

        Image erase = NewToolButtonImage("EraseBtn", p, new Color(0.32f, 0.22f, 0.22f, 1f));
        Place(erase, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(80f, -800f), new Vector2(150f, 150f));
        Button eraseButton = SceneUI.GetOrAdd<Button>(erase.gameObject);
        eraseButton.targetGraphic = erase;
        if (eraseButton.onClick.GetPersistentEventCount() == 0)
        {
            eraseButton.onClick.RemoveListener(ToggleErase);
            eraseButton.onClick.AddListener(ToggleErase);
        }
        eraseBtnIcon = AddToolButtonContent(erase.transform, eraserIcon, "ERASE");

        Image brushButtonImage = NewToolButtonImage("BrushBtn", p, new Color(0.25f, 0.35f, 0.29f, 1f));
        Place(brushButtonImage, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(480f, -800f), new Vector2(150f, 150f));
        Button brushButton = SceneUI.GetOrAdd<Button>(brushButtonImage.gameObject);
        brushButton.targetGraphic = brushButtonImage;
        if (brushButton.onClick.GetPersistentEventCount() == 0)
        {
            brushButton.onClick.RemoveListener(SelectBrush);
            brushButton.onClick.AddListener(SelectBrush);
        }
        brushBtnIcon = AddToolButtonContent(brushButtonImage.transform,
            brushIconSprite != null ? brushIconSprite : paintIcon, "BRUSH");

        // Fill is a one-click action. Put its default layout in a second row so
        // existing Eye/Erase/Brush buttons and authored positions remain unchanged.
        Image fillButtonImage = NewToolButtonImage("FillBtn", p, new Color(0.25f, 0.34f, 0.42f, 1f));
        Place(fillButtonImage, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(280f, -800f), new Vector2(150f, 150f));
        Button fillButton = SceneUI.GetOrAdd<Button>(fillButtonImage.gameObject);
        fillButton.targetGraphic = fillButtonImage;
        // Keep Inspector-assigned listeners. Hook FillBody unless the Inspector
        // already calls this exact method on this component.
        bool inspectorCallsFillBody = false;
        for (int i = 0; i < fillButton.onClick.GetPersistentEventCount(); i++)
        {
            if (fillButton.onClick.GetPersistentTarget(i) == this
                && fillButton.onClick.GetPersistentMethodName(i) == nameof(FillBody))
            {
                inspectorCallsFillBody = true;
                break;
            }
        }
        fillButton.onClick.RemoveListener(FillBody);
        if (!inspectorCallsFillBody)
        {
            fillButton.onClick.AddListener(FillBody);
        }
        fillBtnIcon = AddToolButtonContent(fillButtonImage.transform, fillIconSprite, "FILL");
        fillBtnIcon.SetActive(false); // Initially off; clicking FillBody flashes this selected-state Icon.

        // Upgrade the two-button default layout when adding BrushBtn. Custom
        // scene-authored layouts are left untouched.
        if (!_reusedUi.Contains(brushButtonImage.gameObject)
            && _reusedUi.Contains(eye.gameObject) && _reusedUi.Contains(erase.gameObject)
            && Mathf.Abs(eye.rectTransform.anchoredPosition.x - 22f) < 0.1f
            && Mathf.Abs(erase.rectTransform.anchoredPosition.x - 182f) < 0.1f
            && Mathf.Abs(eye.rectTransform.sizeDelta.x - 150f) < 0.1f
            && Mathf.Abs(erase.rectTransform.sizeDelta.x - 150f) < 0.1f)
        {
            eye.rectTransform.sizeDelta = new Vector2(96f, 62f);
            erase.rectTransform.anchoredPosition = new Vector2(128f, -340f);
            erase.rectTransform.sizeDelta = new Vector2(96f, 62f);
        }

        // The old wheel, RGB bars, and their readouts are hidden rather than destroyed.

        MakeSizeSlider(p, 100f, 660f);
        MakeSliderBox(p, "Metallic", 420f, v =>
        {
            metallic = Mathf.Clamp01(v);
            ApplyMaterialParams();
            RefreshMaterialUI();
        }, out mFill, out mVal, out metallicHandleRt);
        MakeSliderBox(p, "Roughness", 540f, v =>
        {
            roughness = Mathf.Clamp01(v);
            ApplyMaterialParams();
            RefreshMaterialUI();
        }, out roFill, out roVal, out roughnessHandleRt);

        cursorIcon = NewImage("CursorIcon", uiRoot, Color.white, pickerIcon);
        cursorIcon.raycastTarget = false;
        cursorIcon.preserveAspect = true;
        cursorIcon.rectTransform.sizeDelta = new Vector2(cursorIconSize, cursorIconSize);
        cursorIcon.rectTransform.pivot = new Vector2(0.12f, 0.16f);
        cursorIcon.gameObject.SetActive(false);

        BuildLoupeUI();
        BindSceneGateButton();
    }

    // "EyedropLoupe" at the right-centre: circular rim, masked RawImage of the loupe camera,
    // crosshair bars and a "ZOOM xN" pill. Starts hidden.
    private void BuildLoupeUI()
    {
        if (uiRoot == null) return;
        Sprite circle = Resources.Load<Sprite>("LoupeCircle");
        if (circle == null)
        {
            circle = circleSprite;
        }

        loupeGO = SceneUI.GetOrCreate("EyedropLoupe", uiRoot, out bool created);
        RectTransform rt = SceneUI.GetOrAdd<RectTransform>(loupeGO);
        if (created)
        {
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.anchorMin = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = loupePos;
            rt.sizeDelta = new Vector2(loupeSize, loupeSize);
        }

        GameObject rim = SceneUI.GetOrCreate("Rim", loupeGO.transform, out created);
        Image rimImg = SceneUI.GetOrAdd<Image>(rim);
        rimImg.sprite = circle;
        rimImg.color = Color.white;
        rimImg.raycastTarget = false;
        if (created)
        {
            Stretch(rimImg, 0f);
        }

        GameObject maskGO = SceneUI.GetOrCreate("Mask", rim.transform, out created);
        Image maskImg = SceneUI.GetOrAdd<Image>(maskGO);
        maskImg.sprite = circle;
        maskImg.color = Color.white;
        if (created)
        {
            Stretch(maskImg, 8f);
        }
        SceneUI.GetOrAdd<Mask>(maskGO).showMaskGraphic = false;

        GameObject mag = SceneUI.GetOrCreate("Mag", maskGO.transform, out created);
        loupeImg = SceneUI.GetOrAdd<RawImage>(mag);
        loupeImg.color = Color.white;
        loupeImg.raycastTarget = false;
        if (loupeRT != null)
        {
            loupeImg.texture = loupeRT;
        }
        if (created)
        {
            Stretch(loupeImg, 0f);
        }

        MakeCrossBar(loupeGO.transform, "CrossH", new Vector2(46f, 3f));
        MakeCrossBar(loupeGO.transform, "CrossV", new Vector2(3f, 46f));

        GameObject pill = SceneUI.GetOrCreate("ZoomPill", loupeGO.transform, out created);
        Image pillImg = SceneUI.GetOrAdd<Image>(pill);
        pillImg.sprite = null;
        pillImg.color = new Color(0.08f, 0.09f, 0.11f, 0.9f);
        pillImg.raycastTarget = false;
        if (created)
        {
            RectTransform prt = pillImg.rectTransform;
            prt.anchorMax = new Vector2(0.5f, 0f);
            prt.anchorMin = new Vector2(0.5f, 0f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = new Vector2(0f, 4f);
            prt.sizeDelta = new Vector2(150f, 44f);
        }

        GameObject txt = SceneUI.GetOrCreate("ZoomTxt", pill.transform, out created);
        loupeZoomText = SceneUI.GetOrAdd<Text>(txt);
        loupeZoomText.font = font;
        loupeZoomText.raycastTarget = false;
        loupeZoomText.text = "ZOOM x" + Mathf.RoundToInt(loupeZoom);
        if (created)
        {
            loupeZoomText.alignment = TextAnchor.MiddleCenter;
            loupeZoomText.color = Color.white;
            loupeZoomText.fontSize = 26;
            Stretch(loupeZoomText, 0f);
        }
        loupeGO.SetActive(false);
    }

    private void MakeCrossBar(Transform parent, string name, Vector2 size)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        Image img = SceneUI.GetOrAdd<Image>(go);
        img.sprite = null;
        img.color = new Color(1f, 1f, 1f, 0.92f);
        img.raycastTarget = false;
        if (created)
        {
            RectTransform rt = img.rectTransform;
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = size;
        }
    }

    // Disabled camera (copy of the main one, rendered manually) drawing into a 256x256 RT, with URP
    // camera data matching the main camera's.
    private void EnsureLoupeCamera()
    {
        if (loupeCam != null) return;
        if (cam == null)
        {
            cam = Camera.main;
        }
        if (loupeRT == null)
        {
            loupeRT = new RenderTexture(256, 256, 16);
            loupeRT.Create();
        }
        GameObject go = new GameObject("EyedropLoupeCam");
        loupeCam = go.AddComponent<Camera>();
        if (cam != null)
        {
            loupeCam.CopyFrom(cam);
        }
        loupeCam.targetTexture = loupeRT;
        loupeCam.aspect = 1f;
        loupeCam.enabled = false;

        UniversalAdditionalCameraData src = cam != null ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
        UniversalAdditionalCameraData dst = loupeCam.GetComponent<UniversalAdditionalCameraData>();
        if (dst == null)
        {
            dst = loupeCam.gameObject.AddComponent<UniversalAdditionalCameraData>();
        }
        dst.renderType = CameraRenderType.Base;
        dst.renderPostProcessing = true;
        if (src != null)
        {
            dst.renderPostProcessing = src.renderPostProcessing;
            dst.volumeLayerMask = src.volumeLayerMask;
            dst.volumeTrigger = src.volumeTrigger;
            dst.renderShadows = src.renderShadows;
            dst.antialiasing = src.antialiasing;
            dst.antialiasingQuality = src.antialiasingQuality;
        }
        if (loupeImg != null)
        {
            loupeImg.texture = loupeRT;
        }
    }

    // Aims the loupe camera from the main camera at the point under the pointer (or 30 units along
    // the ray) with the FOV narrowed by loupeZoom, renders it and shows the loupe.
    private void UpdateLoupe(Vector2 screenPos)
    {
        if (!loupeEnabled) return;
        if (cam == null)
        {
            cam = Camera.main;
        }
        if (cam == null) return;
        EnsureLoupeCamera();
        if (loupeCam == null) return;

        Ray ray = cam.ScreenPointToRay(screenPos);
        Vector3 target = Physics.Raycast(ray, out RaycastHit hit, 10000f) ? hit.point : ray.GetPoint(30f);
        loupeCam.transform.position = cam.transform.position;
        loupeCam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, cam.transform.up);
        float zoom = loupeZoom;
        float fov = cam.fieldOfView;
        loupeCam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, zoom)) * Mathf.Rad2Deg;
        loupeCam.Render();
        if (loupeGO != null && !loupeGO.activeSelf)
        {
            loupeGO.SetActive(true);
        }
    }

    private void HideLoupe()
    {
        if (loupeGO != null && loupeGO.activeSelf)
        {
            loupeGO.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (loupeRT != null)
        {
            loupeRT.Release();
            Destroy(loupeRT);
            loupeRT = null;
        }
        if (loupeCam != null)
        {
            Destroy(loupeCam.gameObject);
            loupeCam = null;
        }
    }

    // The scene gate button (field, else "PaintGateButton"); only its Image is kept for the on/off
    // sprite swap.
    private void BindSceneGateButton()
    {
        if (paintGateButton == null)
        {
            GameObject go = GameObject.Find("PaintGateButton");
            if (go != null)
            {
                paintGateButton = go.GetComponent<Button>();
            }
        }
        if (paintGateButton == null)
        {
            Debug.LogWarning("[ChameleonPaint] No scene Paint gate button is assigned (field 'paintGateButton') and none named 'PaintGateButton' was found — painting cannot be opened.");
            return;
        }
        paintGateImg = paintGateButton.GetComponent<Image>();
    }

    // Unlike NewImage, this deliberately does not overwrite scene-authored Image
    // sprites or colors. Existing EyeBtn/EraseBtn/BrushBtn/FillBtn artwork is preserved.
    private Image NewToolButtonImage(string name, Transform parent, Color fallbackColor)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        if (!created)
        {
            _reusedUi.Add(go);
        }
        Image img = go.GetComponent<Image>();
        bool needsDefaults = created || img == null;
        if (img == null)
        {
            img = SceneUI.GetOrAdd<Image>(go);
        }
        if (needsDefaults)
        {
            img.sprite = solidSprite;
            img.color = fallbackColor;
        }
        return img;
    }

    // BaseIcon: the normal tool image, always visible (including inactive states).
    // Icon: the selection overlay, enabled only in this tool's mode.
    // Scene-authored Icon and BaseIcon objects keep their images and layouts.
    private GameObject AddToolButtonContent(Transform btn, Sprite defaultIcon, string fallbackText)
    {
        Transform selected = btn.Find("Icon");
        Image selectedImage = selected != null ? selected.GetComponent<Image>() : null;
        // For projects made with the old script, Icon WAS the only tool image.
        // Copy that image into BaseIcon so deselecting no longer leaves a blank button.
        Sprite baseSprite = selectedImage != null && selectedImage.sprite != null
            ? selectedImage.sprite : defaultIcon;

        GameObject baseGO = SceneUI.GetOrCreate("BaseIcon", btn, out bool baseCreated);
        Image baseImage = SceneUI.GetOrAdd<Image>(baseGO);
        if (baseCreated)
        {
            baseImage.sprite = baseSprite;
            // Legacy Icon is also the selected image: keep a softly faded copy
            // underneath so the tool remains identifiable when not selected.
            Color baseColor = selectedImage != null ? selectedImage.color : Color.white;
            if (selectedImage != null) baseColor.a *= 0.65f;
            baseImage.color = baseColor;
            baseImage.raycastTarget = false;
            baseImage.preserveAspect = true;
            if (selectedImage != null)
            {
                // Match the original tool image's authored position/size.
                RectTransform from = selectedImage.rectTransform;
                RectTransform to = baseImage.rectTransform;
                to.anchorMin = from.anchorMin;
                to.anchorMax = from.anchorMax;
                to.pivot = from.pivot;
                to.sizeDelta = from.sizeDelta;
                to.anchoredPosition = from.anchoredPosition;
                to.localRotation = from.localRotation;
                to.localScale = from.localScale;
            }
            else
            {
                Stretch(baseImage, 12f);
            }
            baseGO.transform.SetAsFirstSibling();
        }
        // The base artwork is never hidden by switching between tools.
        baseGO.SetActive(true);

        if (baseImage.sprite == null && btn.Find("Label") == null)
        {
            NewTextIn("Label", btn, fallbackText, 24, TextAnchor.MiddleCenter, Color.white);
        }

        GameObject selectedGO = SceneUI.GetOrCreate("Icon", btn, out bool selectedCreated);
        if (selectedCreated)
        {
            // New UI: a small highlight bar is the selected-state Icon.
            // The regular tool graphic remains in BaseIcon underneath it.
            Image selectedIndicator = SceneUI.GetOrAdd<Image>(selectedGO);
            selectedIndicator.sprite = solidSprite;
            selectedIndicator.color = new Color(1f, 0.82f, 0.22f, 1f);
            selectedIndicator.raycastTarget = false;
            RectTransform rt = selectedIndicator.rectTransform;
            rt.anchorMin = new Vector2(0.12f, 0f);
            rt.anchorMax = new Vector2(0.88f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 3f);
            rt.sizeDelta = new Vector2(0f, 4f);
        }
        return selectedGO;
    }

    // Horizontal rainbow + black-to-white picker from the first reference image.
    // Existing HueBar / LightnessBar layout and artwork are preserved when authored in the scene.
    private void BuildHorizontalColorPicker(Transform parent)
    {
        Sprite hueDefault = MakeRoundedColorBar(t => Color.HSVToRGB(Mathf.Repeat(1f - t, 1f), 1f, 1f));
        Sprite lightDefault = MakeRoundedColorBar(t => new Color(t, t, t, 1f));
        Sprite pointerDefault = colorPointerSprite != null ? colorPointerSprite : MakeUpTrianglePointer();

        Image hue = NewColorBarImage("HueBar", parent, hueBarSprite != null ? hueBarSprite : hueDefault);
        Place(hue, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(50f, -80f), new Vector2(800f, 100f));
        AttachHorizontalColorDrag(hue, SetHueFromBar);
        huePointer = MakeColorPointer(hue.transform, pointerDefault);

        Image lightness = NewColorBarImage("LightnessBar", parent,
            lightnessBarSprite != null ? lightnessBarSprite : lightDefault);
        Place(lightness, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(50f, -230f), new Vector2(800f, 100f));
        AttachHorizontalColorDrag(lightness, SetLightnessFromBar);
        lightnessPointer = MakeColorPointer(lightness.transform, pointerDefault);

        RefreshColorUI();
    }

    // Unlike NewImage, do not replace existing scene-authored bar images.
    private Image NewColorBarImage(string name, Transform parent, Sprite fallback)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        if (!created) _reusedUi.Add(go);
        Image img = SceneUI.GetOrAdd<Image>(go);
        if (created || img.sprite == null) img.sprite = fallback;
        img.color = Color.white;
        img.type = Image.Type.Simple;
        img.raycastTarget = true;
        go.SetActive(true);
        return img;
    }

    private void AttachHorizontalColorDrag(Image bar, Action<float> onValue)
    {
        RectTransform rt = bar.rectTransform;
        SceneUI.GetOrAdd<UIDrag>(bar.gameObject).cb = e =>
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rt, e.position, e.pressEventCamera, out Vector2 lp)) return;

            // Match the pointer's safe center-to-center travel. Touching the left
            // or right edge reaches the endpoint without the triangle spilling out.
            RectTransform pointer = rt.Find("Pointer") as RectTransform;
            float halfPointer = pointer != null
                ? Mathf.Min(pointer.rect.width * 0.2f, rt.rect.width * 0.2f) : 0f;
            float minX = rt.rect.xMin + halfPointer;
            float maxX = rt.rect.xMax - halfPointer;
            if (maxX <= minX) return;
            onValue(Mathf.Clamp01((lp.x - minX) / (maxX - minX)));
        };
    }

    private RectTransform MakeColorPointer(Transform bar, Sprite fallback)
    {
        GameObject go = SceneUI.GetOrCreate("Pointer", bar, out bool created);
        Image img = SceneUI.GetOrAdd<Image>(go);
        if (created || img.sprite == null) img.sprite = fallback;
        img.color = Color.white;
        img.preserveAspect = true;
        img.raycastTarget = false; // Allow clicks/drags through the pointer to the bar.
        RectTransform rt = img.rectTransform;
        if (created)
        {
            rt.sizeDelta = new Vector2(60f, 60f);
        }
        go.SetActive(true);
        return rt;
    }

    // Deactivate old UI elements rather than destroying scene-authored artwork.
    private static void HideLegacyColorPicker(Transform parent)
    {
        string[] names = {
            "Wheel", "VSlider", "RLbl", "RBar", "RVal",
            "GLbl", "GBar", "GVal", "BLbl", "BBar", "BVal", "HSV", "Hex"
        };
        foreach (string name in names)
        {
            Transform old = parent.Find(name);
            if (old != null) old.gameObject.SetActive(false);
        }
    }

    // Runtime sprite with dark rounded border and a smoothly interpolated interior.
    private static Sprite MakeRoundedColorBar(Func<float, Color> gradient)
    {
        const int width = 512, height = 64;
        const float radius = 10f, border = 3f;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dx = Mathf.Max(Mathf.Abs(x + 0.5f - width * 0.5f) - (width * 0.5f - radius), 0f);
                float dy = Mathf.Max(Mathf.Abs(y + 0.5f - height * 0.5f) - (height * 0.5f - radius), 0f);
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                Color color = distance > radius ? Color.clear
                    : (distance > radius - border ? new Color(0.08f, 0.1f, 0.14f, 1f)
                        : gradient(x / (float)(width - 1)));
                texture.SetPixel(x, y, color);
            }
        }
        texture.Apply(false);
        return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
    }

    // White upward triangle, dark outline; rendered below the chosen bar.
    private static Sprite MakeUpTrianglePointer()
    {
        const int width = 44, height = 32;
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < height; y++)
        {
            float down = (height - 1f - y) / (height - 1f);
            float outerHalf = 18f * down;
            for (int x = 0; x < width; x++)
            {
                float dx = Mathf.Abs(x + 0.5f - width * 0.5f);
                bool inside = down >= 0.09f && down <= 0.94f && dx <= outerHalf;
                bool inner = down >= 0.21f && down <= 0.80f && dx <= outerHalf - 3.2f;
                texture.SetPixel(x, y, inside ? (inner ? Color.white : new Color(0.08f, 0.1f, 0.13f, 1f)) : Color.clear);
            }
        }
        texture.Apply(false);
        return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
    }

    // "<label>Lbl" + draggable gradient "<label>Bar" (with a marker, returned) + "<label>Val" text.
    private RectTransform MakeBar(Transform parent, string label, float x0, float y, float barW, Sprite grad, Action<float> onVal, out Text valText)
    {
        RectTransform brt;
        Text lbl = NewText(label + "Lbl", parent, label, 22, TextAnchor.MiddleLeft, Color.white);
        Place(lbl, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0, -y), new Vector2(82f, 28f));
        lbl.raycastTarget = false;

        Image bar = NewImage(label + "Bar", parent, Color.white, grad);
        Place(bar, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0 + 90f, -y), new Vector2(barW, 26f));
        brt = bar.rectTransform;
        SceneUI.GetOrAdd<UIDrag>(bar.gameObject).cb = e =>
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(brt, e.position, null, out Vector2 lp);
            onVal(Mathf.Clamp01((lp.x - brt.rect.xMin) / brt.rect.width));
        };
        RectTransform mark = MakeMarker(bar.transform);

        valText = NewText(label + "Val", parent, "0.00", 22, TextAnchor.MiddleLeft, Color.white);
        Place(valText, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0 + 90f + barW + 8f, -y), new Vector2(90f, 28f));
        valText.raycastTarget = false;
        return mark;
    }

    // 600x30 size track with an 80x80 draggable handle. The handle's CENTER can
    // reach both ends of the track. SizeRing is 80x80; SizeDot grows from 5 to 75
    // and always matches the current brush/Preview color.
    private void MakeSizeSlider(Transform parent, float x0, float y)
    {
        const float trackWidth = 600f;
        const float trackHeight = 30f;
        const float handleSize = 80f;
        const float ringSize = 80f;
        // Upgrade the old script's serialized 12..44 dot range to the new 5..75 range.
        // Other custom Inspector ranges remain configurable.
        if (Mathf.Approximately(sizePreviewDotMin, 20f)
            && Mathf.Approximately(sizePreviewDotMax, 70f))
        {
            sizePreviewDotMin = 20f;
            sizePreviewDotMax = 70f;
        }
        _sizeTrackW = trackWidth;

        Image track = NewImage("SizeTrack", parent, Color.white, sizeTrackSprite);
        Place(track, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(x0, -y), new Vector2(trackWidth, trackHeight));
        RectTransform trackRt = track.rectTransform;
        // Force the requested dimensions EVEN when a SizeTrack already exists in the scene.
        trackRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, trackWidth);
        trackRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, trackHeight);
        if (track.sprite == null) track.sprite = solidSprite;

        Image fill = NewImage("SizeFill", track.transform, new Color(0.1f, 0.11f, 0.13f, 0f), sizeFillSprite);
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = 0;
        fill.fillAmount = 0f;
        fill.raycastTarget = false;
        Stretch(fill, 0f);
        _sizeFill = fill;

        Image handle = NewImage("SizeHandle", track.transform, Color.white, sizeHandleSprite);
        if (handle.sprite == null) handle.sprite = circleSprite;
        handle.raycastTarget = true; // Lets the user drag the actual 80x80 thumb.
        handle.preserveAspect = true;
        RectTransform hrt = handle.rectTransform;
        hrt.anchorMin = new Vector2(0f, 0.5f);
        hrt.anchorMax = new Vector2(0f, 0.5f);
        hrt.pivot = new Vector2(0.5f, 0.5f);
        hrt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, handleSize);
        hrt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, handleSize);
        _sizeHandleRt = hrt;
        _sizeHandleImg = handle;
        _sizeHandleImg.color = brush;

        // Shared handler: clicking the track OR grabbing/dragging the handle works.
        UIDrag trackDrag = SceneUI.GetOrAdd<UIDrag>(track.gameObject);
        trackDrag.cb = e =>
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                trackRt, e.position, e.pressEventCamera, out Vector2 lp);
            float normalized = Mathf.InverseLerp(trackRt.rect.xMin, trackRt.rect.xMax, lp.x);
            SetBrushSize(normalized);
        };
        SceneUI.GetOrAdd<UIDrag>(handle.gameObject).cb = trackDrag.cb;

        Image icon = NewImage("SizeIcon", parent, Color.white, brushIconSprite);
        icon.raycastTarget = false;
        icon.preserveAspect = true;
        float rowY = -(y + 70f); // Keep label below the 80px handle (no overlap).
        Place(icon, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0, rowY), new Vector2(30f, 30f));

        Text lbl = NewText("SizeLbl", parent, "Size", 24, TextAnchor.MiddleLeft, Color.white);
        Place(lbl, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0 + 38f, rowY), new Vector2(160f, 30f));
        lbl.raycastTarget = false;

        Image ring = NewImage("SizeRing", parent, Color.white, sizeRingSprite);
        if (ring.sprite == null) ring.sprite = ringSprite;
        ring.raycastTarget = false;
        ring.preserveAspect = true;
        // In newly created layouts the ring sits beside the 600px track, centered on it.
        Place(ring, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0 + trackWidth + ringSize / 2, -(y - ringSize / 2)), new Vector2(ringSize, ringSize));
        ring.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, ringSize);
        ring.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, ringSize);

        Image dot = NewImage("SizeDot", ring.transform, brush, sizeDotSprite);
        if (dot.sprite == null) dot.sprite = circleSprite;
        dot.color = brush;
        dot.raycastTarget = false;
        dot.preserveAspect = true;
        RectTransform drt = dot.rectTransform;
        drt.anchorMin = new Vector2(0.5f, 0.5f);
        drt.anchorMax = new Vector2(0.5f, 0.5f);
        drt.pivot = new Vector2(0.5f, 0.5f);
        drt.anchoredPosition = Vector2.zero;
        _sizeDotImg = dot;
        _sizeDotRt = drt;

        // One-time upgrade of the old auto-generated 320px layout (NOT custom layouts).
        // Without this, a reused 860x520 panel puts the newly widened track offscreen.
        if (_reusedUi.Contains(track.gameObject)
            && Mathf.Abs(trackRt.anchoredPosition.x - 384f) < 0.1f
            && Mathf.Abs(trackRt.anchoredPosition.y + 276f) < 0.1f
            && Mathf.Abs(ring.rectTransform.anchoredPosition.x - 740f) < 0.1f
            && Mathf.Abs(ring.rectTransform.anchoredPosition.y + 263f) < 0.1f
            && panelRect != null
            && Mathf.Abs(panelRect.rect.width - 860f) < 0.1f
            && Mathf.Abs(panelRect.rect.height - 520f) < 0.1f)
        {
            panelRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 652f);
            trackRt.anchoredPosition = new Vector2(36f, -548f);
            ring.rectTransform.anchoredPosition = new Vector2(660f, -523f);
            if (_reusedUi.Contains(icon.gameObject))
                icon.rectTransform.anchoredPosition = new Vector2(36f, -618f);
            if (_reusedUi.Contains(lbl.gameObject))
                lbl.rectTransform.anchoredPosition = new Vector2(74f, -618f);
        }

        RefreshBrushUI();
    }

    // Metallic/Roughness reuse SizeTrack / SizeFill / SizeHandle artwork, so all three
    // sliders look like one set. Retains the existing Box object and authored position.
    private void MakeSliderBox(Transform parent, string label, float y,
        Action<float> onVal, out Image fill, out Text valText, out RectTransform handleRt)
    {
        Text lbl = NewText(label + "Lbl", parent, label, 28, TextAnchor.MiddleLeft, Color.white);
        Place(lbl, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(24f, -y), new Vector2(196f, 40f));
        lbl.raycastTarget = false;

        // Use the exact same sprites as SizeTrack, even when those sprites were
        // authored directly onto SizeTrack/SizeHandle instead of assigned in Inspector.
        Transform sizeTrack = parent.Find("SizeTrack");
        Sprite trackSkin = sizeTrackSprite != null ? sizeTrackSprite
            : (sizeTrack != null ? sizeTrack.GetComponent<Image>()?.sprite : null);
        Sprite fillSkin = sizeFillSprite != null ? sizeFillSprite
            : (_sizeFill != null ? _sizeFill.sprite : null);
        Sprite handleSkin = sizeHandleSprite != null ? sizeHandleSprite
            : (_sizeHandleImg != null ? _sizeHandleImg.sprite : null);

        // "MetallicBox" / "RoughnessBox" are retained for scene compatibility.
        Image track = NewImage(label + "Box", parent, Color.white,
            trackSkin != null ? trackSkin : solidSprite);
        Place(track, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(100f, -y), new Vector2(600f, 30f));
        RectTransform trackRt = track.rectTransform;
        trackRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 30f);
        track.type = Image.Type.Simple;
        track.preserveAspect = false;
        track.raycastTarget = true;

        fill = NewImage(label + "Fill", track.transform, Color.white,
            fillSkin != null ? fillSkin : solidSprite);
        fill.color = _sizeFill != null ? _sizeFill.color : new Color(0f, 0f, 0f, 0f);
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = 0;
        fill.fillAmount = 0f;
        fill.raycastTarget = false;
        // Replace the old box inset with the same full-track fill layout as SizeFill.
        RectTransform fillRt = fill.rectTransform;
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;

        valText = NewText(label + "Val", track.transform, "0.00", 26, TextAnchor.MiddleCenter, Color.white);
        Stretch(valText, 0f);
        valText.raycastTarget = false;

        Image handle = NewImage(label + "Handle", track.transform, Color.white,
            handleSkin != null ? handleSkin : circleSprite);
        handle.preserveAspect = true;
        handle.raycastTarget = true;
        handleRt = handle.rectTransform;
        handleRt.anchorMin = new Vector2(0f, 0.5f);
        handleRt.anchorMax = new Vector2(0f, 0.5f);
        handleRt.pivot = new Vector2(0.5f, 0.5f);
        handleRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, materialSliderHandleSize);
        handleRt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, materialSliderHandleSize);
        handle.transform.SetAsLastSibling();

        // The track and the handle are both draggable, like SizeTrack/SizeHandle.
        UIDrag drag = SceneUI.GetOrAdd<UIDrag>(track.gameObject);
        drag.cb = e =>
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                trackRt, e.position, e.pressEventCamera, out Vector2 lp)) return;
            if (trackRt.rect.width <= 0f) return;
            onVal(Mathf.InverseLerp(trackRt.rect.xMin, trackRt.rect.xMax, lp.x));
        };
        SceneUI.GetOrAdd<UIDrag>(handle.gameObject).cb = drag.cb;
    }

    // 6px white "Mark" line with a darker "MarkO" outline behind it.
    private RectTransform MakeMarker(Transform bar)
    {
        Image mark = NewImage("Mark", bar, Color.white, lineSprite);
        mark.raycastTarget = false;
        RectTransform rt = mark.rectTransform;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(6f, 0f);

        Image outline = NewImage("MarkO", mark.transform, new Color(0f, 0f, 0f, 0.6f), lineSprite);
        outline.raycastTarget = false;
        Stretch(outline, -2f);
        outline.transform.SetAsFirstSibling();
        return rt;
    }

    // Reused (already existing) objects are remembered so Place / Stretch keep their layout.
    private Image NewImage(string name, Transform parent, Color color, Sprite sprite)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        if (!created)
        {
            _reusedUi.Add(go);
        }
        Image img = SceneUI.GetOrAdd<Image>(go);
        img.color = color;
        if (sprite != null)
        {
            img.sprite = sprite;
        }
        return img;
    }

    private Text NewText(string name, Transform parent, string content, int size, TextAnchor anchor, Color color)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        if (!created)
        {
            _reusedUi.Add(go);
        }
        Text txt = SceneUI.GetOrAdd<Text>(go);
        txt.font = font;
        if (created)
        {
            txt.text = content;
            txt.fontSize = size;
            txt.alignment = anchor;
            txt.color = color;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
        }
        return txt;
    }

    private Text NewText(string name, Transform parent, string content, int size, TextAnchor anchor, Color color, Vector2 anchorP, Vector2 pivot, Vector2 pos, Vector2 sizeDelta)
    {
        Text txt = NewText(name, parent, content, size, anchor, color);
        Place(txt, anchorP, pivot, pos, sizeDelta);
        return txt;
    }

    private Text NewTextIn(string name, Transform parent, string content, int size, TextAnchor anchor, Color color)
    {
        Text txt = NewText(name, parent, content, size, anchor, color);
        Stretch(txt, 0f);
        txt.raycastTarget = false;
        return txt;
    }

    private void PlaceTL(Component c, float x, float y, float w, float h)
    {
        Place(c, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(w, h));
    }

    private void Place(Component c, Vector2 anchor, Vector2 pivot, Vector2 anchoredPos, Vector2 size)
    {
        if (_reusedUi.Contains(c.gameObject)) return;
        RectTransform rt = c.transform as RectTransform;
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
    }

    private void Stretch(Component c, float pad)
    {
        if (_reusedUi.Contains(c.gameObject)) return;
        RectTransform rt = c.transform as RectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
    }

    // 64x1 horizontal gradient sampled from f(0..1).
    private Sprite GradSprite(Func<float, Color> f)
    {
        Texture2D tex = new Texture2D(64, 1, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int x = 0; x < 64; x++)
        {
            Color c = f(x / 63f);
            c.a = 1f;
            tex.SetPixel(x, 0, c);
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, 64f, 1f), new Vector2(0.5f, 0.5f), 100f);
    }

    // HSV wheel: hue by angle, saturation by radius, full value; soft 1px edge.
    private static Sprite MakeWheel(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float r = size / 2f;
        for (int y = 0; y < size; y++)
        {
            float dy = (y + 0.5f - r) / r;
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - r) / r;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                Color c;
                if (d <= 1f)
                {
                    c = Color.HSVToRGB(Mathf.Repeat(Mathf.Atan2(dy, dx) / (Mathf.PI * 2f), 1f), d, 1f);
                    c.a = Mathf.Clamp01((1f - d) * size * 0.5f);
                }
                else
                {
                    c = new Color(0f, 0f, 0f, 0f);
                }
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    // White circle, alpha = clamped distance inside the edge.
    private static Sprite MakeCircle(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float r = size / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - r;
                float dy = y + 0.5f - r;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy))));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    // White ring between innerRatio * radius and the radius (soft edges).
    private static Sprite MakeRing(int size, float innerRatio)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float r = size / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - r;
                float dy = y + 0.5f - r;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Min(Mathf.Clamp01(r - d), Mathf.Clamp01(d - r * innerRatio));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    // 4x4 white sprite.
    private static Sprite MakeSolid()
    {
        Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        Color[] px = new Color[16];
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = Color.white;
        }
        tex.SetPixels(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);
    }
}
