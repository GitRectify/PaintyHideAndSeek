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
    [Tooltip("Đường kính chấm preview (px UI) khi cọ NHỎ nhất (brushPx=5) và LỚN nhất (brushPx=48). Chấm to/nhỏ tuyến tính theo cỡ cọ.")]
    public float sizePreviewDotMin = 12f;
    public float sizePreviewDotMax = 44f;

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

    [Header("Tool-button skins (Assets/Texture2D)")]
    [Tooltip("Background sprite for a tool button while its mode is ACTIVE / pressed (pick.png).")]
    public Sprite pickBgSprite;
    [Tooltip("Background sprite for a tool button while its mode is INACTIVE / unpressed (no pick.png).")]
    public Sprite noPickBgSprite;
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
    private float roughness = 0.5f;
    private float H;
    private float S;
    private float V;
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
    private Image eyeBtnImg;
    private Image eraseBtnImg;
    private Image cursorIcon;
    private Image paintGateImg;
    private Image mFill;
    private Image roFill;
    private GameObject panelGO;
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
    private RectTransform _sizeDotRt;
    private float _sizeTrackW;
    private float _sizeHandleD;
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
        camCtrl = FindFirstObjectByType<CameraController>();
        readTex = new Texture2D(5, 5, TextureFormat.RGBA32, false);
        solidSprite = MakeSolid();
        circleSprite = MakeCircle(96);
        ringSprite = MakeRing(96, 0.7f);
        wheelSprite = MakeWheel(256);
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
        CloseDrawing();
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
            levelMgr = FindFirstObjectByType<LevelManager>(FindObjectsInactive.Include);
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
        roughness = 0.5f;
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
        RefreshColorUI();
    }

    // Wheel pick. A near-black brush jumps to full brightness so the hue is visible.
    private void SetHS(float h, float s)
    {
        SetBrush(Color.HSVToRGB(Mathf.Repeat(h, 1f), Mathf.Clamp01(s), V >= 0.03f ? V : 1f));
    }

    private void SetV(float v)
    {
        SetBrush(Color.HSVToRGB(H, S, Mathf.Clamp01(v)));
    }

    private void SetChannel(int i, float val)
    {
        Color c = brush;
        val = Mathf.Clamp01(val);
        if (i == 0)
        {
            c.r = val;
        }
        else if (i == 1)
        {
            c.g = val;
        }
        else
        {
            c.b = val;
        }
        SetBrush(c);
    }

    // Slider 0..1 -> brush radius 5..48 texels.
    private void SetBrushSize(float v)
    {
        brushPx = Mathf.RoundToInt(Mathf.Lerp(5f, 48f, Mathf.Clamp01(v)));
        RefreshBrushUI();
    }

    // Tool button skins / colours, the hint text and the cursor icon for the new mode.
    private void SetMode(Mode m)
    {
        mode = m;
        if (m != Mode.Eyedrop)
        {
            HideLoupe();
        }
        bool skinned = pickBgSprite != null && noPickBgSprite != null;
        if (eyeBtnImg != null)
        {
            if (skinned)
            {
                eyeBtnImg.sprite = m == Mode.Eyedrop ? pickBgSprite : noPickBgSprite;
                eyeBtnImg.color = Color.white;
            }
            else
            {
                eyeBtnImg.color = m == Mode.Eyedrop ? new Color(0.95f, 0.7f, 0.2f, 1f) : new Color(0.25f, 0.27f, 0.32f, 1f);
            }
        }
        if (eraseBtnImg != null)
        {
            if (skinned)
            {
                eraseBtnImg.sprite = m == Mode.Erase ? pickBgSprite : noPickBgSprite;
                eraseBtnImg.color = Color.white;
            }
            else
            {
                eraseBtnImg.color = m == Mode.Erase ? new Color(0.9f, 0.45f, 0.4f, 1f) : new Color(0.32f, 0.22f, 0.22f, 1f);
            }
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
        if (panelGO != null)
        {
            panelGO.SetActive(true);
        }
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
        drawingOn = false;
        EndPaintCollider();
        mode = Mode.Paint;
        if (panelGO != null)
        {
            panelGO.SetActive(false);
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

    public void ToggleEyedrop()
    {
        SetMode(mode == Mode.Eyedrop ? Mode.Paint : Mode.Eyedrop);
    }

    public void ToggleErase()
    {
        SetMode(mode == Mode.Erase ? Mode.Paint : Mode.Erase);
    }

    private void RefreshColorUI()
    {
        if (preview != null)
        {
            preview.color = brush;
        }
        if (wheelMarker != null)
        {
            float a = H * Mathf.PI * 2f;
            float r = S * wheelRadius;
            wheelMarker.anchoredPosition = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }
        if (vSliderTex != null)
        {
            // The V slider shows the current hue / saturation from black to full value.
            for (int y = 0; y < vSliderTex.height; y++)
            {
                Color c = Color.HSVToRGB(H, S, y / (float)(vSliderTex.height - 1));
                c.a = 1f;
                vSliderTex.SetPixel(0, y, c);
            }
            vSliderTex.Apply(false);
        }
        SetMarkerY(vMarker, V);
        SetMarkerX(rMark, brush.r);
        SetMarkerX(gMark, brush.g);
        SetMarkerX(bMark, brush.b);
        if (rVal != null)
        {
            rVal.text = brush.r.ToString("0.00");
        }
        if (gVal != null)
        {
            gVal.text = brush.g.ToString("0.00");
        }
        if (bVal != null)
        {
            bVal.text = brush.b.ToString("0.00");
        }
        if (hsvVal != null)
        {
            hsvVal.text = "H " + (H * 360f).ToString("0") + "   S " + S.ToString("0.00") + "   V " + V.ToString("0.00");
        }
        if (hexVal != null)
        {
            hexVal.text = "#" + ColorUtility.ToHtmlStringRGB(brush);
        }
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
        if (mVal != null)
        {
            mVal.text = metallic.ToString("0.00");
        }
        if (roVal != null)
        {
            roVal.text = roughness.ToString("0.00");
        }
    }

    // Size slider handle / fill and the preview dot follow brushPx (5..48).
    private void RefreshBrushUI()
    {
        float t = Mathf.Clamp01((brushPx - 5f) / 43f);
        if (_sizeHandleRt != null || _sizeFill != null)
        {
            float half = _sizeHandleD * 0.5f;
            float x = Mathf.Lerp(half, _sizeTrackW - half, t);
            if (_sizeHandleRt != null)
            {
                _sizeHandleRt.anchoredPosition = new Vector2(x, 0f);
            }
            if (_sizeFill != null)
            {
                _sizeFill.fillAmount = _sizeTrackW > 0f ? x / _sizeTrackW : t;
            }
        }
        if (_sizeDotRt != null)
        {
            float d = Mathf.Lerp(sizePreviewDotMin, sizePreviewDotMax, t);
            _sizeDotRt.sizeDelta = new Vector2(d, d);
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
            wheelSprite = MakeWheel(256);
            lineSprite = MakeSolid();
        }
        EnsureEventSystem();
        BuildUI();
    }

    // "ChameleonPaintCanvas" (1080x1920): a full-screen invisible "Scene" catcher for painting /
    // orbiting, and the "PickerPanel" (860x520 top-left, scaled by panelScale) with the close
    // button, HSV wheel, V slider, preview, PICK / ERASE buttons, R/G/B bars, HSV / hex readouts,
    // brush size slider and Metallic / Roughness boxes. Objects that already existed keep their
    // authored layout (_reusedUi).
    private void BuildUI()
    {
        RectTransform wrt;
        RectTransform vrt;
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

        Image panel = NewImage("PickerPanel", uiRoot, new Color(0.1f, 0.11f, 0.13f, 0.94f), solidSprite);
        Place(panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -30f), new Vector2(860f, 520f));
        if (!_reusedUi.Contains(panel.gameObject))
        {
            panel.rectTransform.localScale = Vector3.one * Mathf.Clamp(panelScale, 0.5f, 3f);
        }
        Transform p = panel.transform;
        panelGO = panel.gameObject;

        bool hasClose = closeIcon != null;
        Image close = NewImage("CloseBtn", p, Color.white, hasClose ? closeIcon : solidSprite);
        if (!hasClose)
        {
            close.color = new Color(0.5f, 0.2f, 0.2f, 0.95f);
        }
        close.preserveAspect = true;
        Place(close, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(796f, -6f), new Vector2(58f, 58f));
        SceneUI.GetOrAdd<Button>(close.gameObject).targetGraphic = close;
        NewTextIn("X", close.transform, "X", 32, TextAnchor.MiddleCenter, Color.white).gameObject.SetActive(!hasClose);

        Image wheel = NewImage("Wheel", p, Color.white, wheelSprite);
        Place(wheel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -22f), new Vector2(250f, 250f));
        wrt = wheel.rectTransform;
        wheelRadius = wrt.rect.width * 0.5f;
        SceneUI.GetOrAdd<UIDrag>(wheel.gameObject).cb = e =>
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(wrt, e.position, null, out Vector2 lp);
            Vector2 d = lp - wrt.rect.center;
            float x = d.x / (wrt.rect.width * 0.5f);
            float y = d.y / (wrt.rect.height * 0.5f);
            SetHS(Mathf.Atan2(y, x) / (Mathf.PI * 2f), Mathf.Min(Mathf.Sqrt(x * x + y * y), 1f));
        };
        wheelMarker = NewImage("WheelMark", wheel.transform, Color.white, ringSprite).rectTransform;
        wheelMarker.anchorMax = new Vector2(0.5f, 0.5f);
        wheelMarker.anchorMin = new Vector2(0.5f, 0.5f);
        wheelMarker.sizeDelta = new Vector2(20f, 20f);
        wheelMarker.GetComponent<Image>().raycastTarget = false;

        vSliderTex = new Texture2D(1, 64, TextureFormat.RGBA32, false);
        vSliderTex.wrapMode = TextureWrapMode.Clamp;
        Sprite vSprite = Sprite.Create(vSliderTex, new Rect(0f, 0f, 1f, 64f), new Vector2(0.5f, 0.5f), 100f);
        vSliderImg = NewImage("VSlider", p, Color.white, vSprite);
        Place(vSliderImg, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(286f, -22f), new Vector2(46f, 250f));
        vrt = vSliderImg.rectTransform;
        SceneUI.GetOrAdd<UIDrag>(vSliderImg.gameObject).cb = e =>
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(vrt, e.position, null, out Vector2 lp);
            SetV(Mathf.Clamp01((lp.y - vrt.rect.yMin) / vrt.rect.height));
        };
        vMarker = MakeMarker(vSliderImg.transform);

        Image previewBack = NewImage("PreviewBack", p, new Color(0f, 0f, 0f, 0.6f), solidSprite);
        Place(previewBack, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -288f), new Vector2(310f, 46f));
        preview = NewImage("Preview", previewBack.transform, Color.white, solidSprite);
        Stretch(preview, 4f);
        preview.raycastTarget = false;

        Image eye = NewImage("EyeBtn", p, Color.white, noPickBgSprite != null ? noPickBgSprite : solidSprite);
        if (noPickBgSprite == null)
        {
            eye.color = new Color(0.25f, 0.27f, 0.32f, 1f);
        }
        Place(eye, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -340f), new Vector2(150f, 62f));
        eyeBtnImg = eye;
        SceneUI.GetOrAdd<Button>(eye.gameObject).targetGraphic = eye;
        AddButtonContent(eye.transform, pickerIcon, "PICK");

        Image erase = NewImage("EraseBtn", p, Color.white, noPickBgSprite != null ? noPickBgSprite : solidSprite);
        if (noPickBgSprite == null)
        {
            erase.color = new Color(0.32f, 0.22f, 0.22f, 1f);
        }
        Place(erase, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(182f, -340f), new Vector2(150f, 62f));
        eraseBtnImg = erase;
        SceneUI.GetOrAdd<Button>(erase.gameObject).targetGraphic = erase;
        AddButtonContent(erase.transform, eraserIcon, "ERASE");

        rMark = MakeBar(p, "R", 384f, 70f, 288f, GradSprite(c => new Color(c, 0f, 0f, 1f)), v => SetChannel(0, v), out rVal);
        gMark = MakeBar(p, "G", 384f, 112f, 288f, GradSprite(c => new Color(0f, c, 0f, 1f)), v => SetChannel(1, v), out gVal);
        bMark = MakeBar(p, "B", 384f, 154f, 288f, GradSprite(c => new Color(0f, 0f, c, 1f)), v => SetChannel(2, v), out bVal);

        hsvVal = NewText("HSV", p, "", 24, TextAnchor.MiddleLeft, new Color(1f, 1f, 1f, 0.85f));
        Place(hsvVal, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(388f, -200f), new Vector2(470f, 30f));
        hsvVal.raycastTarget = false;
        hexVal = NewText("Hex", p, "", 26, TextAnchor.MiddleLeft, Color.white);
        Place(hexVal, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(388f, -234f), new Vector2(470f, 32f));
        hexVal.raycastTarget = false;

        MakeSizeSlider(p, 384f, 276f);
        MakeSliderBox(p, "Metallic", 424f, v =>
        {
            metallic = Mathf.Clamp01(v);
            ApplyMaterialParams();
            RefreshMaterialUI();
        }, out mFill, out mVal);
        MakeSliderBox(p, "Roughness", 472f, v =>
        {
            roughness = Mathf.Clamp01(v);
            ApplyMaterialParams();
            RefreshMaterialUI();
        }, out roFill, out roVal);

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

    // Icon (inset 12) when there is one, else a fallback text label.
    private void AddButtonContent(Transform btn, Sprite icon, string fallbackText)
    {
        if (icon != null)
        {
            Image img = NewImage("Icon", btn, Color.white, icon);
            Stretch(img, 12f);
            img.raycastTarget = false;
            img.preserveAspect = true;
            return;
        }
        NewTextIn("Label", btn, fallbackText, 24, TextAnchor.MiddleCenter, Color.white);
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

    // 320x30 brush-size track (fill + 38px handle, draggable), brush icon + "Size" label below,
    // and a preview ring with a dot sized like the brush.
    private void MakeSizeSlider(Transform parent, float x0, float y)
    {
        RectTransform trackRt;
        _sizeTrackW = 320f;
        _sizeHandleD = 38f;

        Image track = NewImage("SizeTrack", parent, Color.white, sizeTrackSprite);
        Place(track, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0, -y), new Vector2(320f, 30f));
        trackRt = track.rectTransform;

        Image fill = NewImage("SizeFill", track.transform, Color.white, sizeFillSprite);
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = 0;
        fill.fillAmount = 0f;
        fill.raycastTarget = false;
        Stretch(fill, 0f);
        _sizeFill = fill;

        Image handle = NewImage("SizeHandle", track.transform, Color.white, sizeHandleSprite);
        handle.raycastTarget = false;
        handle.preserveAspect = true;
        RectTransform hrt = handle.rectTransform;
        hrt.anchorMax = new Vector2(0f, 0.5f);
        hrt.anchorMin = new Vector2(0f, 0.5f);
        hrt.pivot = new Vector2(0.5f, 0.5f);
        hrt.sizeDelta = new Vector2(38f, 38f);
        hrt.anchoredPosition = Vector2.zero;
        _sizeHandleRt = hrt;

        SceneUI.GetOrAdd<UIDrag>(track.gameObject).cb = e =>
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(trackRt, e.position, null, out Vector2 lp);
            SetBrushSize(Mathf.Clamp01((lp.x - trackRt.rect.xMin) / trackRt.rect.width));
        };

        Image icon = NewImage("SizeIcon", parent, Color.white, brushIconSprite);
        icon.raycastTarget = false;
        icon.preserveAspect = true;
        float rowY = -(y + 30f + 10f);
        Place(icon, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0, rowY), new Vector2(30f, 30f));

        Text lbl = NewText("SizeLbl", parent, "Size", 24, TextAnchor.MiddleLeft, Color.white);
        Place(lbl, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0 + 30f + 8f, rowY), new Vector2(160f, 30f));
        lbl.raycastTarget = false;

        Image ring = NewImage("SizeRing", parent, Color.white, sizeRingSprite);
        ring.raycastTarget = false;
        ring.preserveAspect = true;
        Place(ring, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0 + 320f + 36f, -(y - 13f)), new Vector2(56f, 56f));

        Image dot = NewImage("SizeDot", ring.transform, Color.white, sizeDotSprite);
        dot.raycastTarget = false;
        dot.preserveAspect = true;
        RectTransform drt = dot.rectTransform;
        drt.anchorMax = new Vector2(0.5f, 0.5f);
        drt.anchorMin = new Vector2(0.5f, 0.5f);
        drt.pivot = new Vector2(0.5f, 0.5f);
        drt.anchoredPosition = Vector2.zero;
        drt.sizeDelta = new Vector2(sizePreviewDotMin, sizePreviewDotMin);
        _sizeDotRt = drt;

        RefreshBrushUI();
    }

    // "<label>Lbl" + a dark draggable "<label>Box" with a horizontal "<label>Fill" and a centred
    // "<label>Val" text.
    private void MakeSliderBox(Transform parent, string label, float y, Action<float> onVal, out Image fill, out Text valText)
    {
        RectTransform brt;
        Text lbl = NewText(label + "Lbl", parent, label, 28, TextAnchor.MiddleLeft, Color.white);
        Place(lbl, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -y), new Vector2(196f, 40f));
        lbl.raycastTarget = false;

        Image box = NewImage(label + "Box", parent, new Color(0.05f, 0.05f, 0.07f, 1f), solidSprite);
        Place(box, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(226f, -y), new Vector2(606f, 40f));

        fill = NewImage(label + "Fill", box.transform, new Color(0.3f, 0.46f, 0.66f, 0.95f), solidSprite);
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Horizontal;
        fill.fillOrigin = 0;
        fill.fillAmount = 0f;
        fill.raycastTarget = false;
        Stretch(fill, 3f);

        valText = NewText(label + "Val", box.transform, "0.00", 26, TextAnchor.MiddleCenter, Color.white);
        Stretch(valText, 0f);
        valText.raycastTarget = false;

        brt = box.rectTransform;
        SceneUI.GetOrAdd<UIDrag>(box.gameObject).cb = e =>
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(brt, e.position, null, out Vector2 lp);
            onVal(Mathf.Clamp01((lp.x - brt.rect.xMin) / brt.rect.width));
        };
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
