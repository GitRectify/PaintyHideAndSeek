// using System;
// using System.Collections;
// using System.Collections.Generic;
// using UnityEngine;
// using UnityEngine.EventSystems;
// using UnityEngine.Rendering.Universal;
// using UnityEngine.UI;

// public class ChameleonPaint : MonoBehaviour
// {
//     // ====================== TUNABLES (from .ctor defaults) ======================

//     public string characterRootName = "Player";
//     public string environmentRootName = "Room";

//     [Header("Panel / UI")]
//     public float panelScale = 1.4f;
//     public Sprite closeIcon;
//     public Sprite pickerIcon;
//     public Sprite eraserIcon;
//     public Sprite paintIcon;
//     public Sprite pickBgSprite;   // TODO: confirmed as fields, but never assigned at runtime in the
//     public Sprite noPickBgSprite; // dump shown — presumably Inspector-assigned "active/inactive" button backgrounds.

//     [Header("Brush size slider")]
//     public Sprite sizeTrackSprite;
//     public Sprite sizeFillSprite;
//     public Sprite sizeHandleSprite;
//     public Sprite sizeRingSprite;
//     public Sprite sizeDotSprite;
//     public Sprite brushIconSprite;
//     public float sizePreviewDotMin = 12f;
//     public float sizePreviewDotMax = 44f;

//     [Header("Cursor")]
//     public float cursorIconSize = 110f;
//     public float cursorTouchLift = 120f;

//     [Header("Goop splatter")]
//     public GameObject goopSprayPrefab;
//     public int goopEmitCount = 1;
//     public float goopInterval = 0.04f;
//     public float goopSpraySpeed = 1.5f;

//     [Header("Eyedropper")]
//     public bool eyedropTrueAlbedo = true;
//     public bool eyedropIgnoreShadow = true;
//     public float eyedropBrightness = 0.85f;

//     [Header("Loupe (magnifier)")]
//     public bool loupeEnabled = true;
//     public float loupeZoom = 5f;
//     public float loupeSize = 240f;
//     // FIXED: raw packed constant 0x42f00000c3520000 decodes to (-210, 124), not (-210, 120).
//     public Vector2 loupePos = new Vector2(-210f, 124f);

//     [Header("Material")]
//     public float metallic;
//     public float roughness = 0.5f;

//     [Header("Scene gate")]
//     public Button paintGateButton;
//     public Sprite gateOnSprite;
//     public Sprite gateOffSprite;

//     public int bodyColorSampleStride = 8;

//     // ====================== RUNTIME STATE ======================

//     private Font font;
//     private Camera cam;
//     private CameraController camCtrl;

//     private Texture2D readTex;
//     private Sprite solidSprite;
//     private Sprite circleSprite;
//     private Sprite ringSprite;
//     private Sprite wheelSprite;
//     private Sprite lineSprite;

//     private Transform characterTf;
//     private Renderer[] bodyRenderers;
//     private SkinnedMeshRenderer[] skinnedRenderers;
//     private MeshCollider[] skinnedColliders;
//     private Mesh[] bakedColliderMeshes;
//     private Animator charAnimator;

//     private ParticleSystem goopSpray;
//     private ParticleSystem goopGlobs;
//     private float _goopTimer;

//     private Texture2D paintTex;
//     private Color32[] paintBuf;
//     private bool dirty;
//     private bool camoDirty;
//     private Color _avgBody = Color.white;

//     public bool drawingOn;
//     // 0 = paint, 1 = eyedropper, 2 = erase
//     public int mode;

//     private GameObject panelGO;
//     private Transform uiRoot;
//     private Canvas canvas;
//     private readonly HashSet<GameObject> _reusedUi = new HashSet<GameObject>();

//     private Image eyeBtnImg;
//     private Image eraseBtnImg;
//     private Text modeText;

//     private RectTransform wheelMarker;
//     private float wheelRadius = 100f;
//     private Texture2D vSliderTex;
//     private Image vSliderImg;
//     private RectTransform vMarker;
//     private RectTransform rMark, gMark, bMark;
//     private Text rVal, gVal, bVal;
//     private Image mFill, roFill;
//     private Text mVal, roVal;
//     private Text hsvVal, hexVal;
//     private Image preview;

//     private Image cursorIcon;

//     private Color brush = Color.white;
//     private float H, S, V;

//     private int brushPx = 22;

//     private RectTransform _sizeHandleRt;
//     private Image _sizeFill;
//     private RectTransform _sizeDotRt;
//     private float _sizeTrackW;
//     private float _sizeHandleD;

//     private bool sceneGestureActive;
//     private bool sceneGestureOrbit;

//     private bool readPending;
//     private Vector2 readPos;

//     private Texture2D albedoPix;

//     private GameObject loupeGO;
//     private Camera loupeCam;
//     private RenderTexture loupeRT;
//     private RawImage loupeImg;
//     private Text loupeZoomText;

//     private Image paintGateImg;

//     private static int BaseColorPropId;  // Shader.PropertyToID("_BaseColor")
//     private static int ColorPropId;      // Shader.PropertyToID("_Color")
//     private static int BaseMapPropId;    // Shader.PropertyToID("_BaseMap")
//     private static int MainTexPropId;    // Shader.PropertyToID("_MainTex")
//     private static int MetallicPropId;   // Shader.PropertyToID("_Metallic")
//     private static int SmoothnessPropId; // Shader.PropertyToID("_Smoothness")

//     public bool DrawingActive => drawingOn;

//     // ====================== LIFECYCLE ======================

//     private void Start()
//     {
//         font = UIFont.Default;
//         cam = Camera.main;
//         camCtrl = Object.FindFirstObjectByType<CameraController>();

//         readTex = new Texture2D(5, 5, TextureFormat.RGBA32, false);

//         solidSprite = MakeSolid();
//         circleSprite = MakeCircle(96);
//         ringSprite = MakeRing(96, 0.7f);
//         wheelSprite = MakeWheel(256);
//         lineSprite = MakeSolid();

//         FindCharacter();
//         ResolveGoop();
//         EnsureCollider();
//         EnsureRoomColliders();
//         InitPaintCanvas();
//         EnsureEventSystem();
//         BuildUI();

//         SetBrush(Color.white);
//         RefreshMaterialUI();
//         RefreshBrushUI();
//         SetMode(0);
//         CloseDrawing();
//     }

//     private Sprite MakeSolid()
//     {
//         Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
//         Color[] colors = new Color[16];
//         for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
//         tex.SetPixels(colors);
//         tex.Apply();
//         return Sprite.Create(tex, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);
//     }

//     private Sprite MakeCircle(int size)
//     {
//         Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
//         tex.wrapMode = TextureWrapMode.Clamp;

//         float radius = size / 2f;
//         for (int y = 0; y < size; y++)
//         {
//             float dy = (y + 0.5f) - radius;
//             for (int x = 0; x < size; x++)
//             {
//                 float dx = (x + 0.5f) - radius;
//                 float edge = radius - Mathf.Sqrt(dx * dx + dy * dy);
//                 tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(edge)));
//             }
//         }
//         tex.Apply();
//         return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
//     }

//     private Sprite MakeRing(int size, float innerRatio)
//     {
//         Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
//         tex.wrapMode = TextureWrapMode.Clamp;

//         float radius = size / 2f;
//         for (int y = 0; y < size; y++)
//         {
//             float dy = (y + 0.5f) - radius;
//             for (int x = 0; x < size; x++)
//             {
//                 float dx = (x + 0.5f) - radius;
//                 float dist = Mathf.Sqrt(dx * dx + dy * dy);
//                 float outerEdge = Mathf.Clamp01(radius - dist);
//                 float innerEdge = Mathf.Clamp01(dist - radius * innerRatio);
//                 float alpha = Mathf.Min(outerEdge, innerEdge);
//                 tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
//             }
//         }
//         tex.Apply();
//         return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
//     }

//     // A full HSV color wheel: hue from angle, saturation from radius, brightness fades near the edge.
//     //
//     // NOTE ON RAW FIDELITY: the decompiled hue calculation computes the fractional part of
//     // atan2(y,x)/(2*PI) via truncate-then-subtract, then CLAMPS (not wraps) the result to [0,1].
//     // Since atan2 returns negative values for roughly half the circle, and truncation of a value in
//     // (-1,1) is always 0, that clamp forces every negative-angle pixel to hue 0 (red) instead of
//     // wrapping around to the correct hue on the far side of the wheel. Taken completely literally,
//     // the original color wheel would render as a rainbow on one half and solid red (varying only in
//     // saturation) on the other half. That looks like a genuine defect in the original rather than
//     // intentional design, so this reconstruction uses Mathf.Repeat to produce a proper full-spectrum
//     // wheel instead of reproducing the apparent bug — flagging it here rather than silently doing
//     // either one. If you want the literal (probably-broken) original behavior, replace the hue line
//     // below with: `float raw = angle / (2f*Mathf.PI); float hue = Mathf.Clamp01(raw - (int)raw);`
//     private Sprite MakeWheel(int size)
//     {
//         Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
//         tex.wrapMode = TextureWrapMode.Clamp;

//         float half = size / 2f;
//         for (int y = 0; y < size; y++)
//         {
//             float ny = ((y + 0.5f) - half) / half;
//             for (int x = 0; x < size; x++)
//             {
//                 float nx = ((x + 0.5f) - half) / half;
//                 float dist = Mathf.Sqrt(nx * nx + ny * ny);

//                 Color c;
//                 if (dist <= 1f)
//                 {
//                     float hue = Mathf.Repeat(Mathf.Atan2(ny, nx) / (2f * Mathf.PI), 1f);
//                     Color rgb = Color.HSVToRGB(hue, dist, 1f, true);
//                     float alpha = Mathf.Clamp01((1f - dist) * size * 0.5f);
//                     c = new Color(rgb.r, rgb.g, rgb.b, alpha);
//                 }
//                 else
//                 {
//                     c = new Color(0f, 0f, 0f, 0f);
//                 }
//                 tex.SetPixel(x, y, c);
//             }
//         }
//         tex.Apply();
//         return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
//     }

//     // Resolves the player's renderers/colliders/animator for painting on, skipping anything parented
//     // under a scene object named "gun" (so the held prop doesn't get treated as skin).
//     private void FindCharacter()
//     {
//         GameObject playerGO = PlayerRef.Resolve(characterRootName) as GameObject;
//         if (playerGO == null)
//         {
//             Debug.LogWarning("[ChameleonPaint] Character '" + characterRootName + "' not found.");
//             bodyRenderers = new Renderer[0];
//             return;
//         }

//         characterTf = playerGO.transform;

//         Renderer[] allRenderers = playerGO.GetComponentsInChildren<Renderer>();
//         List<Renderer> filtered = new List<Renderer>(allRenderers.Length);
//         foreach (Renderer r in allRenderers)
//         {
//             if (r == null) continue;
//             if (!IsUnderGun(r.transform))
//             {
//                 filtered.Add(r);
//             }
//         }
//         bodyRenderers = filtered.ToArray();

//         skinnedRenderers = playerGO.GetComponentsInChildren<SkinnedMeshRenderer>(true);

//         // Skinned meshes ship with any pre-baked MeshCollider stripped — BeginPaintCollider()
//         // rebuilds them on demand from a baked snapshot of the current pose.
//         foreach (SkinnedMeshRenderer smr in skinnedRenderers)
//         {
//             if (smr == null) continue;
//             MeshCollider existing = smr.GetComponent<MeshCollider>();
//             if (existing != null)
//             {
//                 Object.Destroy(existing);
//             }
//         }
//         skinnedColliders = null;
//         bakedColliderMeshes = null;

//         charAnimator = playerGO.GetComponentInChildren<Animator>(true);

//         Debug.Log("[ChameleonPaint] Found '" + characterRootName + "' with " +
//                   bodyRenderers.Length + " body renderer(s) (gun excluded); skinnedParts=" +
//                   (skinnedRenderers != null ? skinnedRenderers.Length : 0) +
//                   " animator=" + (charAnimator != null));
//     }

//     private static bool IsUnderGun(Transform t)
//     {
//         while (t != null)
//         {
//             if (t.name == "gun") return true;
//             t = t.parent;
//         }
//         return false;
//     }

//     // Resolves (or instantiates) the goop-splatter particle systems used when painting.
//     private void ResolveGoop()
//     {
//         if (goopSpray == null)
//         {
//             GameObject prefabSource = goopSprayPrefab;
//             GameObject instance;

//             if (prefabSource != null)
//             {
//                 instance = Object.Instantiate(prefabSource);
//                 instance.name = "GoopSpray";
//             }
//             else
//             {
//                 GameObject found = GameObject.Find("GoopSpray");
//                 if (found == null)
//                 {
//                     Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
//                     foreach (Transform t in all)
//                     {
//                         if (t.name == "GoopSpray" && t.gameObject.scene.IsValid())
//                         {
//                             found = t.gameObject;
//                             break;
//                         }
//                     }
//                 }
//                 instance = found;
//             }

//             if (instance != null)
//             {
//                 goopSpray = instance.GetComponent<ParticleSystem>();
//             }
//         }

//         if (goopSpray != null)
//         {
//             ParticleSystem[] children = goopSpray.GetComponentsInChildren<ParticleSystem>(true);
//             foreach (ParticleSystem ps in children)
//             {
//                 if (ps != goopSpray)
//                 {
//                     goopGlobs = ps;
//                     break;
//                 }
//             }
//         }

//         if (goopGlobs != null && !goopGlobs.isPlaying)
//         {
//             goopGlobs.Play();
//         }

//         if (goopGlobs == null)
//         {
//             Debug.LogWarning("[ChameleonPaint] GoopSpray chưa resolve được — gán 'goopSprayPrefab' trong Inspector; splatter khi tô đang TẮT.");
//         }
//     }

//     // Adds a MeshCollider (matching the body mesh) to the character if it doesn't already have one,
//     // so raycasts against the character (painting, eyedropper) have something to hit.
//     private void EnsureCollider()
//     {
//         if (characterTf == null) return;
//         if (characterTf.GetComponent<Collider>() != null) return;

//         MeshFilter mf = characterTf.GetComponent<MeshFilter>();
//         MeshCollider mc = characterTf.gameObject.AddComponent<MeshCollider>();
//         if (mf != null && mf.sharedMesh != null)
//         {
//             mc.sharedMesh = mf.sharedMesh;
//         }
//     }

//     // Adds MeshColliders to any readable-mesh room geometry that's missing one, so the albedo
//     // eyedropper can raycast against walls/floors too.
//     private void EnsureRoomColliders()
//     {
//         if (string.IsNullOrEmpty(environmentRootName)) return;

//         GameObject env = GameObject.Find(environmentRootName);
//         if (env == null)
//         {
//             Debug.LogWarning("[ChameleonPaint] Environment '" + environmentRootName +
//                               "' not found; albedo eyedropper limited.");
//             return;
//         }

//         int added = 0;
//         MeshRenderer[] renderers = env.GetComponentsInChildren<MeshRenderer>();
//         foreach (MeshRenderer mr in renderers)
//         {
//             if (mr.GetComponent<Collider>() != null) continue;

//             MeshFilter mf = mr.GetComponent<MeshFilter>();
//             if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;

//             MeshCollider mc = mr.gameObject.AddComponent<MeshCollider>();
//             mc.sharedMesh = mf.sharedMesh;
//             added++;
//         }

//         Debug.Log("[ChameleonPaint] Added " + added + " environment colliders for the albedo eyedropper.");
//     }

//     private void InitPaintCanvas()
//     {
//         paintTex = new Texture2D(512, 512, TextureFormat.RGBA32, false);
//         paintTex.wrapMode = TextureWrapMode.Clamp;
//         paintBuf = new Color32[512 * 512];

//         ClearCanvas(Color.white);
//         ApplyCanvasToBody();
//     }

//     // Creates a scene EventSystem if one doesn't already exist, preferring the new Input System's UI
//     // module when available and falling back to the legacy StandaloneInputModule otherwise.
//     private void EnsureEventSystem()
//     {
//         if (Object.FindFirstObjectByType<EventSystem>() != null) return;

//         GameObject go = new GameObject("EventSystem");
//         go.AddComponent<EventSystem>();

//         System.Type inputModuleType = System.Type.GetType(
//             "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");

//         if (inputModuleType != null)
//         {
//             go.AddComponent(inputModuleType);
//         }
//         else
//         {
//             go.AddComponent<StandaloneInputModule>();
//         }
//     }

//     // ====================== UI CONSTRUCTION ======================
//     //
//     // NOTE ON THIS REGION: every position/size/color below was decoded directly from the packed hex
//     // constants in the dump (not guessed) — including several corrections to the previous pass, noted
//     // inline below. The one thing genuinely missing is the drag-callback bodies — the color wheel,
//     // V-slider, brush-size slider, and bar sliders all wire up a UIDrag component via a
//     // compiler-generated closure, but none of the actual "on drag, do X" lambda bodies were in the
//     // pasted assembly (same situation as the palette-swatch buttons in CamouflageGame, and the
//     // hold-buttons in CameraController). What's wired below is a confident, well-motivated
//     // reconstruction based on what each control obviously controls (confirmed by the Set-methods that
//     // already exist: SetHS, SetV, SetChannel, SetBrushSize), not decompiled fact.

//     private void BuildUI()
//     {
//         _reusedUi.Clear();

//         GameObject canvasGO = SceneUI.GetOrCreateCanvas("ChameleonPaintCanvas", out bool canvasCreated);
//         canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);
//         if (canvasCreated)
//         {
//             canvas.renderMode = RenderMode.ScreenSpaceOverlay;

//             CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
//             scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
//             scaler.referenceResolution = new Vector2(1080f, 1920f);
//             scaler.matchWidthOrHeight = 1f;
//         }
//         SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);

//         uiRoot = canvas.transform;

//         // Full-screen invisible catcher: routes scene-space pointer gestures (orbit-drag / paint
//         // strokes) while the panel above handles its own UI clicks.
//         Image sceneCatcher = NewImage("Scene", uiRoot, new Color(1f, 1f, 1f, 0f), null);
//         Stretch(sceneCatcher, 0f);
//         sceneCatcher.raycastTarget = true;

//         UIDrag sceneDrag = SceneUI.GetOrAdd<UIDrag>(sceneCatcher.gameObject);
//         sceneDrag.onDown = OnScenePointer;
//         sceneDrag.onDrag = OnScenePointerMove;
//         sceneDrag.onUp = OnScenePointerUp;
//         sceneDrag.onExit = OnScenePointerExit;

//         // Panel background.
//         Image panel = NewImage("PickerPanel", uiRoot, new Color(0.1f, 0.11f, 0.13f, 0.94f), solidSprite);
//         Place(panel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -30f), new Vector2(860f, 520f));

//         if (!_reusedUi.Contains(panel.gameObject))
//         {
//             float scale = Mathf.Clamp(panelScale, 0.5f, 3f);
//             panel.rectTransform.localScale = Vector3.one * scale;
//         }
//         panelGO = panel.gameObject;
//         Transform panelTf = panel.transform;

//         // Close button.
//         Sprite closeSprite = closeIcon != null ? closeIcon : circleSprite;
//         Image closeBtnImg = NewImage("CloseBtn", panelTf, Color.white, closeSprite);
//         if (closeIcon == null)
//         {
//             closeBtnImg.color = new Color(0.5f, 0.2f, 0.2f, 0.95f);
//         }
//         closeBtnImg.preserveAspect = true;
//         Place(closeBtnImg, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(796f, -6f), new Vector2(58f, 58f));

//         Button closeBtn = SceneUI.GetOrAdd<Button>(closeBtnImg.gameObject);
//         closeBtn.targetGraphic = closeBtnImg;
//         closeBtn.onClick.AddListener(CloseDrawing);

//         Text closeLabel = NewTextIn("X", closeBtnImg.transform, "X", 32, TextAnchor.MiddleCenter, Color.white);
//         closeLabel.gameObject.SetActive(closeIcon == null);

//         // Color wheel.
//         Image wheelImg = NewImage("Wheel", panelTf, Color.white, wheelSprite);
//         Place(wheelImg, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -22f), new Vector2(250f, 250f));

//         wheelRadius = wheelImg.rectTransform.rect.width * 0.5f;

//         UIDrag wheelDrag = SceneUI.GetOrAdd<UIDrag>(wheelImg.gameObject);
//         wheelDrag.onDrag = OnWheelDrag;

//         Image wheelMarkImg = NewImage("WheelMark", wheelImg.transform, Color.white, ringSprite);
//         wheelMarker = wheelMarkImg.rectTransform;
//         wheelMarker.anchorMax = new Vector2(0.5f, 0.5f);
//         wheelMarker.anchorMin = new Vector2(0.5f, 0.5f);
//         wheelMarker.sizeDelta = new Vector2(20f, 20f);
//         wheelMarkImg.raycastTarget = false;

//         // Value (brightness) slider — vertical gradient strip.
//         vSliderTex = new Texture2D(1, 64, TextureFormat.RGBA32, false);
//         vSliderTex.wrapMode = TextureWrapMode.Clamp;
//         Sprite vSliderSprite = Sprite.Create(vSliderTex, new Rect(0f, 0f, 1f, 64f), new Vector2(0.5f, 0.5f), 100f);

//         vSliderImg = NewImage("VSlider", panelTf, Color.white, vSliderSprite);
//         Place(vSliderImg, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(286f, -22f), new Vector2(46f, 250f));

//         UIDrag vDrag = SceneUI.GetOrAdd<UIDrag>(vSliderImg.gameObject);
//         vDrag.onDrag = OnVSliderDrag;

//         vMarker = MakeMarker(vSliderImg.transform);

//         // Preview swatch.
//         Image previewBack = NewImage("PreviewBack", panelTf, new Color(0f, 0f, 0f, 0.6f), solidSprite);
//         Place(previewBack, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -288f), new Vector2(310f, 46f));

//         preview = NewImage("Preview", previewBack.transform, Color.white, solidSprite);
//         Stretch(preview, 4f);
//         preview.raycastTarget = false;

//         // Eyedropper / erase buttons.
//         // FIXED: raw checks noPickBgSprite (not pickBgSprite) as the "real asset available" test for
//         // BOTH buttons, falling back to circleSprite (the same fallback field used by the close
//         // button above) when it's null. The custom tint colors below only apply in that fallback case
//         // and were already correct.
//         Sprite eyeSprite = noPickBgSprite != null ? noPickBgSprite : circleSprite;
//         eyeBtnImg = NewImage("EyeBtn", panelTf, Color.white, eyeSprite);
//         if (noPickBgSprite == null)
//         {
//             eyeBtnImg.color = new Color(0.25f, 0.27f, 0.32f, 1f);
//         }
//         Place(eyeBtnImg, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, -340f), new Vector2(150f, 62f));

//         Button eyeBtn = SceneUI.GetOrAdd<Button>(eyeBtnImg.gameObject);
//         eyeBtn.targetGraphic = eyeBtnImg;
//         AddButtonContent(eyeBtnImg.transform, pickerIcon, "PICK");

//         Sprite eraseSprite = noPickBgSprite != null ? noPickBgSprite : circleSprite;
//         eraseBtnImg = NewImage("EraseBtn", panelTf, Color.white, eraseSprite);
//         if (noPickBgSprite == null)
//         {
//             eraseBtnImg.color = new Color(0.32f, 0.22f, 0.22f, 1f);
//         }
//         Place(eraseBtnImg, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(182f, -340f), new Vector2(150f, 62f));

//         Button eraseBtn = SceneUI.GetOrAdd<Button>(eraseBtnImg.gameObject);
//         eraseBtn.targetGraphic = eraseBtnImg;
//         AddButtonContent(eraseBtnImg.transform, eraserIcon, "ERASE");

//         eyeBtn.onClick.AddListener(ToggleEyedrop);
//         eraseBtn.onClick.AddListener(ToggleErase);

//         // R/G/B sliders.
//         Func<float, Color> redGrad = t => new Color(t, brush.g, brush.b);
//         Sprite redSprite = GradSprite(redGrad);
//         rMark = MakeBar(panelTf, "R", 384f, 70f, 288f, redSprite, v => SetChannel(0, v), ref rVal);

//         Func<float, Color> greenGrad = t => new Color(brush.r, t, brush.b);
//         Sprite greenSprite = GradSprite(greenGrad);
//         gMark = MakeBar(panelTf, "G", 384f, 112f, 288f, greenSprite, v => SetChannel(1, v), ref gVal);

//         Func<float, Color> blueGrad = t => new Color(brush.r, brush.g, t);
//         Sprite blueSprite = GradSprite(blueGrad);
//         bMark = MakeBar(panelTf, "B", 384f, 154f, 288f, blueSprite, v => SetChannel(2, v), ref bVal);

//         hsvVal = NewText(panelTf, "HSV", "", 24, TextAnchor.MiddleLeft, new Color(1f, 1f, 1f, 0.85f));
//         Place(hsvVal, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(388f, -200f), new Vector2(470f, 30f));
//         hsvVal.raycastTarget = false;

//         hexVal = NewText(panelTf, "Hex", "", 26, TextAnchor.MiddleLeft, Color.white);
//         Place(hexVal, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(388f, -234f), new Vector2(470f, 32f));
//         hexVal.raycastTarget = false;

//         MakeSizeSlider(panelTf, 384f, 276f);

//         MakeSliderBox(panelTf, "Metallic", 424f, v => { metallic = v; ApplyMaterialParams(); }, out mFill, out mVal);
//         MakeSliderBox(panelTf, "Roughness", 472f, v => { roughness = v; ApplyMaterialParams(); }, out roFill, out roVal);

//         // Cursor icon (follows the pointer while drawingOn, hidden otherwise).
//         cursorIcon = NewImage("CursorIcon", uiRoot, Color.white, pickerIcon);
//         cursorIcon.raycastTarget = false;
//         cursorIcon.preserveAspect = true;
//         cursorIcon.rectTransform.sizeDelta = new Vector2(cursorIconSize, cursorIconSize);
//         cursorIcon.rectTransform.pivot = new Vector2(0.12f, 0.16f);
//         cursorIcon.gameObject.SetActive(false);

//         BuildLoupeUI();
//         BindSceneGateButton();
//     }

//     private Image NewImage(string name, Transform parent, Color color, Sprite sprite)
//     {
//         GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
//         if (!created)
//         {
//             _reusedUi.Add(go);
//         }

//         Image img = SceneUI.GetOrAdd<Image>(go);
//         img.color = color;
//         if (sprite != null)
//         {
//             img.sprite = sprite;
//         }
//         return img;
//     }

//     private Text NewText(Transform parent, string name, string content, int size, TextAnchor anchor, Color color)
//     {
//         GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
//         if (!created)
//         {
//             _reusedUi.Add(go);
//         }

//         Text text = SceneUI.GetOrAdd<Text>(go);
//         text.font = font;
//         if (created)
//         {
//             text.text = content;
//             text.fontSize = size;
//             text.alignment = anchor;
//             text.color = color;
//             text.horizontalOverflow = HorizontalWrapMode.Overflow;
//             text.verticalOverflow = VerticalWrapMode.Overflow;
//         }
//         return text;
//     }

//     // NewText + Stretch-to-fill-parent + non-interactive, for text that decorates a button/icon.
//     private Text NewTextIn(string name, Transform parent, string content, int size, TextAnchor anchor, Color color)
//     {
//         Text t = NewText(parent, name, content, size, anchor, color);
//         Stretch(t, 0f);
//         t.raycastTarget = false;
//         return t;
//     }

//     private void Stretch(Component c, float pad)
//     {
//         if (_reusedUi.Contains(((Component)c).gameObject)) return;

//         RectTransform rt = (RectTransform)c.transform;
//         rt.anchorMin = Vector2.zero;
//         rt.anchorMax = Vector2.one;
//         rt.offsetMin = new Vector2(pad, pad);
//         rt.offsetMax = new Vector2(-pad, -pad);
//     }

//     private void Place(Component c, Vector2 anchor, Vector2 pivot, Vector2 anchoredPos, Vector2 size)
//     {
//         if (_reusedUi.Contains(((Component)c).gameObject)) return;

//         RectTransform rt = (RectTransform)c.transform;
//         rt.anchorMin = anchor;
//         rt.anchorMax = anchor;
//         rt.pivot = pivot;
//         rt.anchoredPosition = anchoredPos;
//         rt.sizeDelta = size;
//     }

//     private void PlaceTL(Component c, float x, float y, float w, float h)
//     {
//         Place(c, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(w, h));
//     }

//     // Shows either an icon (if assigned) or a fallback text label inside a button.
//     private void AddButtonContent(Transform btn, Sprite icon, string fallbackText)
//     {
//         if (icon != null)
//         {
//             Image img = NewImage("Icon", btn, Color.white, icon);
//             Stretch(img, 12f);
//             img.raycastTarget = false;
//             img.preserveAspect = true;
//             return;
//         }

//         NewTextIn("Label", btn, fallbackText, 24, TextAnchor.MiddleCenter, Color.white);
//     }

//     // Bakes a Func<float,Color> gradient (0..1) into a 64px-wide horizontal strip sprite — used for
//     // the R/G/B slider backgrounds.
//     private Sprite GradSprite(Func<float, Color> f)
//     {
//         Texture2D tex = new Texture2D(64, 1, TextureFormat.RGBA32, false);
//         tex.wrapMode = TextureWrapMode.Clamp;

//         for (int x = 0; x < 64; x++)
//         {
//             Color c = f(x / 63f);
//             c.a = 1f;
//             tex.SetPixel(x, 0, c);
//         }
//         tex.Apply();
//         return Sprite.Create(tex, new Rect(0f, 0f, 64f, 1f), new Vector2(0.5f, 0.5f), 100f);
//     }

//     // Builds one labeled color-channel bar: "R" / gradient strip with a drag marker / numeric value.
//     private RectTransform MakeBar(Transform parent, string label, float x0, float y, float barW,
//         Sprite grad, Action<float> onVal, out Text valText)
//     {
//         // FIXED: raw decodes this label's size as (82, 30) — draft previously had height 28.
//         Text lbl = NewText(parent, label + "Lbl", label, 22, TextAnchor.MiddleLeft, Color.white);
//         PlaceTL(lbl, x0, y, 82f, 30f);
//         lbl.raycastTarget = false;

//         Image barImg = NewImage(label + "Bar", parent, Color.white, grad);
//         PlaceTL(barImg, x0 + 90f, y, barW, 26f);

//         RectTransform marker = MakeMarker(barImg.transform);

//         UIDrag drag = SceneUI.GetOrAdd<UIDrag>(barImg.gameObject);
//         drag.onDrag = e => onVal(BarFraction(barImg.rectTransform, e));

//         // FIXED: raw decodes this value-text's size as (160, 30) — draft previously had (90, 28).
//         valText = NewText(parent, label + "Val", "0.00", 22, TextAnchor.MiddleLeft, Color.white);
//         PlaceTL(valText, x0 + 90f + barW + 8f, y, 160f, 30f);
//         valText.raycastTarget = false;

//         return marker;
//     }

//     // NOTE: reconstructed helper — converts a drag event's pointer position into a 0..1 fraction
//     // along a horizontal bar's RectTransform. The original per-slider drag callbacks weren't in the
//     // dump (see the region note above); this local-space projection is the obvious way such a
//     // callback would compute its fraction, consistent with how MakeBar's marker (MakeMarker) is
//     // positioned by SetMarkerX using the same 0..1 convention.
//     private static float BarFraction(RectTransform rt, PointerEventData e)
//     {
//         RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out Vector2 local);
//         float t = (local.x - rt.rect.xMin) / rt.rect.width;
//         return Mathf.Clamp01(t);
//     }

//     private static float BarFractionVertical(RectTransform rt, PointerEventData e)
//     {
//         RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, e.position, e.pressEventCamera, out Vector2 local);
//         float t = (local.y - rt.rect.yMin) / rt.rect.height;
//         return Mathf.Clamp01(t);
//     }

//     private void OnWheelDrag(PointerEventData e)
//     {
//         RectTransformUtility.ScreenPointToLocalPointInRectangle(
//             (RectTransform)wheelMarker.parent, e.position, e.pressEventCamera, out Vector2 local);

//         float dist = local.magnitude;
//         float s = Mathf.Clamp01(dist / wheelRadius);
//         float h = Mathf.Repeat(Mathf.Atan2(local.y, local.x) / (2f * Mathf.PI), 1f);
//         SetHS(h, s);
//     }

//     private void OnVSliderDrag(PointerEventData e)
//     {
//         float t = BarFractionVertical(vSliderImg.rectTransform, e);
//         SetV(t);
//     }

//     private RectTransform MakeMarker(Transform bar)
//     {
//         Image line = NewImage("Marker", bar, Color.white, lineSprite);
//         line.raycastTarget = false;
//         RectTransform rt = line.rectTransform;
//         rt.anchorMin = new Vector2(0f, 0f);
//         rt.anchorMax = new Vector2(0f, 1f);
//         rt.sizeDelta = new Vector2(6f, 0f);

//         Image glow = NewImage("MarkerGlow", line.transform, new Color(0f, 0f, 0f, 0.6f), lineSprite);
//         glow.raycastTarget = false;
//         Stretch(glow, -2f);
//         glow.transform.SetAsFirstSibling();

//         return rt;
//     }

//     // Horizontal brush-size slider: track / fill / round handle, plus a live ring+dot size preview.
//     private void MakeSizeSlider(Transform parent, float x0, float y)
//     {
//         _sizeTrackW = 320f;
//         _sizeHandleD = 38f;

//         Image track = NewImage("SizeTrack", parent, Color.white, sizeTrackSprite);
//         PlaceTL(track, x0, y, 320f, 30f);

//         Image fill = NewImage("SizeFill", track.transform, Color.white, sizeFillSprite);
//         fill.type = Image.Type.Filled;
//         fill.fillMethod = Image.FillMethod.Horizontal;
//         fill.fillOrigin = 0;
//         fill.fillAmount = 0f;
//         fill.raycastTarget = false;
//         Stretch(fill, 0f);
//         _sizeFill = fill;

//         Image handle = NewImage("SizeHandle", track.transform, Color.white, sizeHandleSprite);
//         handle.raycastTarget = false;
//         handle.preserveAspect = true;
//         RectTransform handleRt = handle.rectTransform;
//         handleRt.anchorMax = new Vector2(0.5f, 0.5f);
//         handleRt.anchorMin = new Vector2(0.5f, 0.5f);
//         handleRt.pivot = new Vector2(0.5f, 0.5f);
//         handleRt.sizeDelta = new Vector2(38f, 38f);
//         handleRt.anchoredPosition = Vector2.zero;
//         _sizeHandleRt = handleRt;

//         UIDrag trackDrag = SceneUI.GetOrAdd<UIDrag>(track.gameObject);
//         trackDrag.onDrag = OnSizeSliderDrag;

//         Image brushIconImg = NewImage("SizeIcon", parent, Color.white, brushIconSprite);
//         brushIconImg.raycastTarget = false;
//         brushIconImg.preserveAspect = true;
//         PlaceTL(brushIconImg, x0, y + 40f, 30f, 30f);

//         // FIXED: raw decodes this label's size as (160, 30) — draft previously had width 200.
//         Text sizeLbl = NewText(parent, "SizeLbl", "Size", 24, TextAnchor.MiddleLeft, Color.white);
//         PlaceTL(sizeLbl, x0 + 38f, y + 40f, 160f, 30f);
//         sizeLbl.raycastTarget = false;

//         Image ring = NewImage("SizeRing", parent, Color.white, sizeRingSprite);
//         ring.raycastTarget = false;
//         ring.preserveAspect = true;
//         PlaceTL(ring, x0 + 320f + 36f, y - 13f, 56f, 56f);

//         Image dot = NewImage("SizeDot", ring.transform, Color.white, sizeDotSprite);
//         dot.raycastTarget = false;
//         dot.preserveAspect = true;
//         RectTransform dotRt = dot.rectTransform;
//         dotRt.anchorMax = new Vector2(0.5f, 0.5f);
//         dotRt.anchorMin = new Vector2(0.5f, 0.5f);
//         dotRt.pivot = new Vector2(0.5f, 0.5f);
//         dotRt.anchoredPosition = Vector2.zero;
//         dotRt.sizeDelta = new Vector2(sizePreviewDotMin, sizePreviewDotMin);
//         _sizeDotRt = dotRt;

//         RefreshBrushUI();
//     }

//     private void OnSizeSliderDrag(PointerEventData e)
//     {
//         float t = BarFraction((RectTransform)_sizeHandleRt.parent, e);
//         SetBrushSize(t);
//     }

//     // A horizontal box slider used for Metallic/Roughness (label, dark filled track, centered value text).
//     private void MakeSliderBox(Transform parent, string label, float y, Action<float> onVal, out Image fillOut, out Text valTextOut)
//     {
//         // FIXED: raw decodes this label's size as (196, 40) — draft previously had (194, 34).
//         Text lbl = NewText(parent, label + "Lbl", label, 28, TextAnchor.MiddleLeft, Color.white);
//         PlaceTL(lbl, 24f, y, 196f, 40f);
//         lbl.raycastTarget = false;

//         // FIXED: raw decodes this box's size as (606, 40) — draft previously had (578, 34).
//         Image box = NewImage(label + "Box", parent, new Color(0.05f, 0.05f, 0.07f, 1f), solidSprite);
//         PlaceTL(box, 226f, y, 606f, 40f);

//         Image fill = NewImage(label + "Fill", box.transform, new Color(0.3f, 0.46f, 0.66f, 0.95f), solidSprite);
//         fill.type = Image.Type.Filled;
//         fill.fillMethod = Image.FillMethod.Horizontal;
//         fill.fillOrigin = 0;
//         fill.fillAmount = 0f;
//         fill.raycastTarget = false;
//         Stretch(fill, 3f);
//         fillOut = fill;

//         Text valText = NewTextIn(label + "Val", box.transform, "0.00", 26, TextAnchor.MiddleCenter, Color.white);
//         valTextOut = valText;

//         UIDrag drag = SceneUI.GetOrAdd<UIDrag>(box.gameObject);
//         drag.onDrag = e => onVal(BarFraction(box.rectTransform, e));
//     }

//     // Builds the magnifier ("loupe") overlay: a circular rim, a masked render of loupeCam's view,
//     // a crosshair, and a zoom-level pill readout.
//     private void BuildLoupeUI()
//     {
//         if (uiRoot == null) return;

//         Sprite ringOverlay = Resources.Load<Sprite>("LoupeCircle");
//         if (ringOverlay == null) ringOverlay = circleSprite;

//         loupeGO = SceneUI.GetOrCreate("EyedropLoupe", uiRoot, out bool created);
//         RectTransform loupeRt = SceneUI.GetOrAdd<RectTransform>(loupeGO);
//         if (created)
//         {
//             // FIXED: raw decodes this anchor/pivot triple as (1.0, 0.5) — a right-edge,
//             // vertically-centered anchor — not (0.5, 1.0) as previously drafted. Paired with
//             // loupePos (-210, 124), the loupe sits offset left from the right edge of the screen and
//             // above vertical center.
//             loupeRt.anchorMax = new Vector2(1f, 0.5f);
//             loupeRt.anchorMin = new Vector2(1f, 0.5f);
//             loupeRt.pivot = new Vector2(1f, 0.5f);
//             loupeRt.anchoredPosition = loupePos;
//             loupeRt.sizeDelta = new Vector2(loupeSize, loupeSize);
//         }

//         Image rim = NewImage("Rim", loupeGO.transform, Color.white, ringOverlay);
//         rim.raycastTarget = false;
//         Stretch(rim, 0f);

//         Image maskBgImg = NewImage("Mask", rim.transform, Color.white, ringOverlay);
//         maskBgImg.raycastTarget = false;
//         Stretch(maskBgImg, 8f);
//         Mask mask = SceneUI.GetOrAdd<Mask>(maskBgImg.gameObject);
//         mask.showMaskGraphic = false;

//         loupeImg = SceneUI.GetOrAdd<RawImage>(SceneUI.GetOrCreate("LoupeCircle", maskBgImg.transform, out bool _));
//         loupeImg.color = Color.white;
//         loupeImg.raycastTarget = false;
//         if (loupeRT != null)
//         {
//             loupeImg.texture = loupeRT;
//         }
//         Stretch(loupeImg, 0f);

//         MakeCrossBar(loupeGO.transform, "CrossH", new Vector2(46f, 3f));
//         MakeCrossBar(loupeGO.transform, "CrossV", new Vector2(3f, 46f));

//         Image zoomPill = NewImage("ZoomPill", loupeGO.transform, new Color(0.08f, 0.09f, 0.11f, 0.9f), null);
//         zoomPill.raycastTarget = false;
//         RectTransform zoomRt = zoomPill.rectTransform;
//         zoomRt.anchorMax = new Vector2(0.5f, 0f);
//         zoomRt.anchorMin = new Vector2(0.5f, 0f);
//         zoomRt.pivot = new Vector2(0.5f, 0.5f);
//         zoomRt.anchoredPosition = new Vector2(0f, 4f);
//         zoomRt.sizeDelta = new Vector2(150f, 44f);

//         Transform zoomTxtParent = zoomPill.transform;
//         GameObject zoomTxtGO = SceneUI.GetOrCreate("ZoomTxt", zoomTxtParent, out bool _);
//         loupeZoomText = SceneUI.GetOrAdd<Text>(zoomTxtGO);
//         loupeZoomText.font = font;
//         loupeZoomText.raycastTarget = false;
//         loupeZoomText.text = "ZOOM x" + Mathf.RoundToInt(loupeZoom);
//         loupeZoomText.alignment = TextAnchor.MiddleCenter;
//         loupeZoomText.color = Color.white;
//         loupeZoomText.fontSize = 26;
//         Stretch(loupeZoomText, 0f);

//         loupeGO.SetActive(false);
//     }

//     private void MakeCrossBar(Transform parent, string name, Vector2 size)
//     {
//         Image img = NewImage(name, parent, new Color(1f, 1f, 1f, 0.92f), null);
//         img.raycastTarget = false;
//         RectTransform rt = img.rectTransform;
//         rt.anchorMax = new Vector2(0.5f, 0.5f);
//         rt.anchorMin = new Vector2(0.5f, 0.5f);
//         rt.pivot = new Vector2(0.5f, 0.5f);
//         rt.anchoredPosition = Vector2.zero;
//         rt.sizeDelta = size;
//     }

//     // Resolves the scene's paint-gate button (the world-space button that toggles drawing mode),
//     // either from the Inspector field or by name, and caches its Image for open/close sprite swaps.
//     private void BindSceneGateButton()
//     {
//         if (paintGateButton == null)
//         {
//             GameObject found = GameObject.Find("PaintGateButton");
//             if (found != null)
//             {
//                 paintGateButton = found.GetComponent<Button>();
//             }
//         }

//         if (paintGateButton == null)
//         {
//             Debug.LogWarning("[ChameleonPaint] No scene Paint gate button is assigned (field 'paintGateButton') and none named 'PaintGateButton' was found — painting cannot be opened.");
//             return;
//         }

//         paintGateImg = paintGateButton.GetComponent<Image>();
//     }

//     // Creates (once) a secondary camera rendering into loupeRT, mirroring the main camera's settings,
//     // used to drive the magnifier view.
//     private void EnsureLoupeCamera()
//     {
//         if (loupeCam != null) return;
//         if (cam == null) cam = Camera.main;

//         if (loupeRT == null)
//         {
//             loupeRT = new RenderTexture(256, 256, 16);
//             loupeRT.Create();
//         }

//         GameObject go = new GameObject("EyedropLoupeCam");
//         loupeCam = go.AddComponent<Camera>();
//         if (cam != null)
//         {
//             loupeCam.CopyFrom(cam);
//         }
//         loupeCam.targetTexture = loupeRT;
//         loupeCam.aspect = 1f;
//         loupeCam.enabled = false;

//         UniversalAdditionalCameraData srcData = cam != null ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
//         UniversalAdditionalCameraData dstData = loupeCam.GetComponent<UniversalAdditionalCameraData>();
//         if (dstData == null)
//         {
//             dstData = go.AddComponent<UniversalAdditionalCameraData>();
//         }
//         if (srcData != null)
//         {
//             // NOTE: URP camera-data copy — the decompiled code copies a handful of raw fields between
//             // the two UniversalAdditionalCameraData instances (render-type/renderer-index/antialiasing
//             // byte and int fields at specific offsets). Reproduced here via the public API instead of
//             // matching field-for-field, since URP doesn't expose most of those as settable properties
//             // beyond renderPostProcessing; behavior should match for a simple "mirror the main camera"
//             // magnifier.
//             dstData.renderPostProcessing = srcData.renderPostProcessing;
//         }

//         if (loupeImg != null)
//         {
//             loupeImg.texture = loupeRT;
//         }
//     }

//     private void OnDestroy()
//     {
//         if (loupeRT != null)
//         {
//             loupeRT.Release();
//             Object.Destroy(loupeRT);
//             loupeRT = null;
//         }

//         if (loupeCam != null)
//         {
//             Object.Destroy(loupeCam.gameObject);
//             loupeCam = null;
//         }
//     }

//     // ====================== SKIN PAINT COLLIDERS ======================

//     // Bakes a MeshCollider for each SkinnedMeshRenderer (in its current pose) so raycasts can hit the
//     // skinned body accurately — only needed while actively drawing.
//     private void BeginPaintCollider()
//     {
//         if (skinnedRenderers == null || skinnedRenderers.Length == 0) return;

//         int n = skinnedRenderers.Length;
//         if (skinnedColliders == null || skinnedColliders.Length != n) skinnedColliders = new MeshCollider[n];
//         if (bakedColliderMeshes == null || bakedColliderMeshes.Length != n) bakedColliderMeshes = new Mesh[n];

//         for (int i = 0; i < n; i++)
//         {
//             if (skinnedRenderers[i] == null) continue;
//             if (bakedColliderMeshes[i] == null) bakedColliderMeshes[i] = new Mesh();
//             if (skinnedColliders[i] == null) skinnedColliders[i] = skinnedRenderers[i].gameObject.AddComponent<MeshCollider>();
//         }

//         RebakePaintCollider();
//     }

//     private void RebakePaintCollider()
//     {
//         if (skinnedRenderers == null || skinnedColliders == null || bakedColliderMeshes == null) return;

//         int n = Mathf.Min(skinnedColliders.Length, Mathf.Min(bakedColliderMeshes.Length, skinnedRenderers.Length));
//         for (int i = 0; i < n; i++)
//         {
//             if (skinnedRenderers[i] == null || skinnedColliders[i] == null) continue;
//             if (bakedColliderMeshes[i] == null) bakedColliderMeshes[i] = new Mesh();

//             skinnedRenderers[i].BakeMesh(bakedColliderMeshes[i], true);
//             skinnedColliders[i].sharedMesh = null;
//             skinnedColliders[i].sharedMesh = bakedColliderMeshes[i];
//         }
//     }

//     private void EndPaintCollider()
//     {
//         if (skinnedColliders == null) return;

//         for (int i = 0; i < skinnedColliders.Length; i++)
//         {
//             if (skinnedColliders[i] != null)
//             {
//                 Object.Destroy(skinnedColliders[i]);
//                 skinnedColliders[i] = null;
//             }
//         }
//     }

//     // ====================== PAINT BUFFER ======================

//     private void ClearCanvas(Color c)
//     {
//         if (paintBuf == null) return;

//         Color32 c32 = c;
//         for (int i = 0; i < paintBuf.Length; i++)
//         {
//             paintBuf[i] = c32;
//         }

//         if (paintTex != null)
//         {
//             paintTex.SetPixels32(paintBuf);
//             paintTex.Apply();
//         }
//         camoDirty = true;
//     }

//     private void ApplyCanvasToBody()
//     {
//         if (bodyRenderers == null) return;

//         foreach (Renderer r in bodyRenderers)
//         {
//             if (r == null) continue;

//             Material mat = r.material;
//             mat.SetColor(BaseColorPropId, Color.white);
//             mat.SetColor(ColorPropId, Color.white);
//             mat.SetTexture(BaseMapPropId, paintTex);
//             mat.SetTexture(MainTexPropId, paintTex);
//             mat.SetFloat(MetallicPropId, metallic);
//             mat.SetFloat(SmoothnessPropId, 1f - roughness);
//         }
//     }

//     public bool TryGetAverageBodyColor(out Color avg)
//     {
//         if (paintBuf == null)
//         {
//             avg = Color.white;
//             return false;
//         }

//         if (camoDirty)
//         {
//             RecomputeAverageBodyColor();
//         }
//         avg = _avgBody;
//         return true;
//     }

//     // Averages a strided sample of paintBuf, skipping near-white "unpainted" pixels so the camo-match
//     // check (BotSeekController.ComputeCamo01 etc.) reflects what's actually been painted rather than
//     // being diluted by the default white canvas. Falls back to a dense unfiltered average if every
//     // sampled pixel looks unpainted.
//     //
//     // FIXED: the raw bit-masked comparisons decode to checking the R, G, and B channels (each against
//     // a threshold in the ~249-250 range) — the previous draft mistakenly checked px.a (alpha) instead
//     // of blue. Since this paint buffer's alpha is always 255 (StampAtUV and ClearCanvas both always
//     // write full opacity), that made the middle condition permanently dead and silently dropped the
//     // blue channel from the "looks painted" test entirely.
//     private void RecomputeAverageBodyColor()
//     {
//         int stride = Mathf.Max(1, bodyColorSampleStride);
//         if (paintBuf == null) return;

//         long sumR = 0, sumG = 0, sumB = 0;
//         int count = 0;

//         for (int y = 0; y < 512; y += stride)
//         {
//             for (int x = 0; x < 512; x += stride)
//             {
//                 Color32 px = paintBuf[y * 512 + x];
//                 bool looksPainted = px.r < 0xfa || px.g < 0x7d || px.b < 0xfa;
//                 if (looksPainted)
//                 {
//                     sumR += px.r; sumG += px.g; sumB += px.b;
//                     count++;
//                 }
//             }
//         }

//         if (count == 0)
//         {
//             for (int y = 0; y < 512; y += stride)
//             {
//                 for (int x = 0; x < 512; x += stride)
//                 {
//                     Color32 px = paintBuf[y * 512 + x];
//                     sumR += px.r; sumG += px.g; sumB += px.b;
//                     count++;
//                 }
//             }
//         }

//         if (count < 1)
//         {
//             _avgBody = Color.white;
//         }
//         else
//         {
//             float denom = count * 255f;
//             _avgBody = new Color(sumR / denom, sumG / denom, sumB / denom, 1f);
//         }

//         camoDirty = false;
//     }

//     private void ApplyMaterialParams()
//     {
//         if (bodyRenderers == null) return;

//         foreach (Renderer r in bodyRenderers)
//         {
//             if (r == null) continue;

//             Material mat = r.material;
//             mat.SetFloat(MetallicPropId, metallic);
//             mat.SetFloat(SmoothnessPropId, 1f - roughness);
//         }
//     }

//     public void ResetPaint()
//     {
//         if (paintBuf == null) return;

//         metallic = 0f;
//         roughness = 0.5f;
//         ClearCanvas(Color.white);
//         ApplyCanvasToBody();
//         SetBrush(Color.white);
//         RefreshMaterialUI();
//     }

//     // Stamps a soft circular brush dab into paintBuf at the given UV, blending toward 'col' with a
//     // 1.7x-feathered falloff (fully opaque at the center, fading out before the true edge radius).
//     private void StampAtUV(Vector2 uv, Color col)
//     {
//         if (paintBuf == null) return;

//         int cx = Mathf.RoundToInt(Mathf.Repeat(uv.x, 1f) * 511f);
//         int cy = Mathf.RoundToInt(Mathf.Repeat(uv.y, 1f) * 511f);

//         int r = brushPx;
//         int minX = Mathf.Clamp(cx - r, 0, 511);
//         int maxX = Mathf.Clamp(cx + r, 0, 511);
//         int minY = Mathf.Clamp(cy - r, 0, 511);
//         int maxY = Mathf.Clamp(cy + r, 0, 511);

//         for (int y = minY; y <= maxY; y++)
//         {
//             for (int x = minX; x <= maxX; x++)
//             {
//                 float dx = x - cx;
//                 float dy = y - cy;
//                 float dist = Mathf.Sqrt(dx * dx + dy * dy);
//                 if (dist > r) continue;

//                 float alpha = Mathf.Clamp01((1f - dist / r) * 1.7f);

//                 int idx = y * 512 + x;
//                 Color32 existing = paintBuf[idx];
//                 byte nr = (byte)(col.r * 255f * alpha + existing.r * (1f - alpha));
//                 byte ng = (byte)(col.g * 255f * alpha + existing.g * (1f - alpha));
//                 byte nb = (byte)(col.b * 255f * alpha + existing.b * (1f - alpha));
//                 paintBuf[idx] = new Color32(nr, ng, nb, 255);
//             }
//         }

//         dirty = true;
//         camoDirty = true;
//     }

//     private void LateUpdate()
//     {
//         if (!dirty) return;
//         if (paintTex == null) return;

//         paintTex.SetPixels32(paintBuf);
//         paintTex.Apply(false);
//         dirty = false;
//     }

//     // ====================== POINTER / SCENE INTERACTION ======================

//     public void OnScenePointer(PointerEventData e)
//     {
//         if (!drawingOn)
//         {
//             if (camCtrl == null) return;
//         }
//         else
//         {
//             MoveCursorIcon(e.position);

//             if (mode == 1)
//             {
//                 UpdateLoupe(e.position);
//                 if (eyedropTrueAlbedo && TryRaycastAlbedo(e.position, out Color sampled))
//                 {
//                     SetBrush(sampled);
//                     return;
//                 }
//                 RequestRead(e.position);
//                 return;
//             }

//             if (!sceneGestureActive)
//             {
//                 sceneGestureActive = true;
//                 bool hitCharacter = TryRaycastCharacter(e.position, out RaycastHit _unused);
//                 sceneGestureOrbit = !hitCharacter;
//                 if (hitCharacter)
//                 {
//                     StampOnCharacter(e.position);
//                     return;
//                 }
//             }
//             else if (!sceneGestureOrbit)
//             {
//                 StampOnCharacter(e.position);
//                 return;
//             }

//             if (camCtrl == null) return;
//         }

//         camCtrl.yaw += camCtrl.orbitDragSpeed * e.delta.x;
//         camCtrl.pitch = Mathf.Clamp(camCtrl.pitch - camCtrl.orbitDragSpeed * e.delta.y, camCtrl.minPitch, camCtrl.maxPitch);
//     }

//     private void MoveCursorIcon(Vector2 screenPos)
//     {
//         if (cursorIcon == null || !drawingOn) return;

//         if (!cursorIcon.gameObject.activeSelf)
//         {
//             cursorIcon.gameObject.SetActive(true);
//         }

//         cursorIcon.rectTransform.position = new Vector3(screenPos.x, screenPos.y + cursorTouchLift, 0f);
//     }

//     // Aims loupeCam at whatever's under the pointer (from the main camera's position), adjusts its
//     // FOV to achieve loupeZoom magnification relative to the main camera, and renders one frame.
//     private void UpdateLoupe(Vector2 screenPos)
//     {
//         if (!loupeEnabled) return;
//         if (cam == null) cam = Camera.main;
//         if (cam == null) return;

//         EnsureLoupeCamera();
//         if (loupeCam == null) return;

//         Ray ray = cam.ScreenPointToRay(screenPos);
//         Vector3 targetPoint = Physics.Raycast(ray, out RaycastHit hit, 10000f)
//             ? hit.point
//             : ray.origin + ray.direction * 30f;

//         loupeCam.transform.position = cam.transform.position;
//         loupeCam.transform.rotation = Quaternion.LookRotation(targetPoint - cam.transform.position, cam.transform.up);

//         float baseFov = cam.fieldOfView;
//         float zoom = Mathf.Max(1f, loupeZoom);
//         float halfAngle = Mathf.Atan(Mathf.Tan(baseFov * 0.5f * Mathf.Deg2Rad) / zoom);
//         loupeCam.fieldOfView = halfAngle * 2f * Mathf.Rad2Deg;

//         loupeCam.Render();

//         if (loupeGO != null && !loupeGO.activeSelf)
//         {
//             loupeGO.SetActive(true);
//         }
//     }

//     // Samples the material's albedo (base color * base-map texel at the hit UV, respecting the
//     // material's tiling/offset) at whatever's under the pointer — used for the "true albedo"
//     // eyedropper mode (as opposed to sampling the rendered/lit screen pixel).
//     private bool TryRaycastAlbedo(Vector2 screenPos, out Color result)
//     {
//         result = Color.white;

//         if (cam == null) cam = Camera.main;
//         if (cam == null) return false;

//         Ray ray = cam.ScreenPointToRay(screenPos);
//         if (!Physics.Raycast(ray, out RaycastHit hit, 10000f)) return false;
//         if (!(hit.collider is MeshCollider)) return false;

//         Renderer renderer = hit.collider.GetComponent<Renderer>();
//         if (renderer == null) return false;

//         Material mat = renderer.sharedMaterial;
//         if (mat == null) return false;

//         Color tint = mat.HasProperty(BaseColorPropId) ? mat.GetColor(BaseColorPropId)
//                    : mat.HasProperty(ColorPropId) ? mat.GetColor(ColorPropId)
//                    : Color.white;

//         Texture tex = mat.HasProperty(BaseMapPropId) ? mat.GetTexture(BaseMapPropId) : null;
//         if (tex == null && mat.HasProperty(MainTexPropId)) tex = mat.GetTexture(MainTexPropId);

//         if (tex == null)
//         {
//             result = tint;
//             return true;
//         }

//         Vector4 st = mat.HasProperty("_BaseMap_ST") ? mat.GetVector("_BaseMap_ST") : new Vector4(1f, 1f, 0f, 0f);
//         Vector2 uv = hit.textureCoord;

//         int width = Mathf.Min(2048, tex.width);
//         int height = Mathf.Min(2048, tex.height);

//         RenderTexture prevActive = RenderTexture.active;
//         RenderTexture temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.Default);
//         Graphics.Blit(tex, temp);
//         RenderTexture.active = temp;

//         float u = Mathf.Repeat(st.z + st.x * uv.x, 1f);
//         float v = Mathf.Repeat(st.w + st.y * uv.y, 1f);
//         int px = Mathf.Clamp(Mathf.RoundToInt(u * (width - 1)), 0, width - 1);
//         int py = Mathf.Clamp(Mathf.RoundToInt(v * (height - 1)), 0, height - 1);

//         if (albedoPix == null)
//         {
//             albedoPix = new Texture2D(1, 1, TextureFormat.RGBA32, false);
//         }
//         albedoPix.ReadPixels(new Rect(px, py, 1f, 1f), 0, 0);
//         albedoPix.Apply(false);

//         RenderTexture.active = prevActive;
//         RenderTexture.ReleaseTemporary(temp);

//         Color texel = albedoPix.GetPixel(0, 0);
//         result = new Color(tint.r * texel.r, tint.g * texel.g, tint.b * texel.b, 1f);
//         return true;
//     }

//     private void RequestRead(Vector2 screenPos)
//     {
//         bool wasPending = readPending;
//         readPos = screenPos;
//         if (wasPending) return;

//         readPending = true;
//         StartCoroutine(ReadAtEndOfFrame());
//     }

//     public bool HitsCharacter(Vector2 screenPos)
//     {
//         return TryRaycastCharacter(screenPos, out RaycastHit _unused);
//     }

//     private void StampOnCharacter(Vector2 screenPos)
//     {
//         if (!TryRaycastCharacter(screenPos, out RaycastHit hit)) return;

//         Vector2 uv = hit.textureCoord;
//         Color col = mode == 2 ? Color.white : brush;

//         StampAtUV(uv, col);
//         AudioManager.PaintStroke();

//         if (mode != 2)
//         {
//             SprayGoop(hit.point, brush);
//         }
//     }

//     // Raycasts everything under the pointer, keeps only hits that are actually part of the character
//     // (the transform itself or a descendant), skips the rough CharacterController capsule in favor of
//     // the precise skinned-mesh colliders, and returns the nearest qualifying hit.
//     private bool TryRaycastCharacter(Vector2 screenPos, out RaycastHit best)
//     {
//         best = default;

//         if (cam == null) cam = Camera.main;
//         if (cam == null) return false;
//         if (characterTf == null) return false;

//         RebakePaintCollider();

//         Ray ray = cam.ScreenPointToRay(screenPos);
//         RaycastHit[] hits = Physics.RaycastAll(ray, 10000f);
//         if (hits.Length == 0) return false;

//         bool found = false;
//         float bestDist = float.MaxValue;

//         foreach (RaycastHit hit in hits)
//         {
//             bool isCharacterPart = hit.transform == characterTf || hit.transform.IsChildOf(characterTf);
//             if (!isCharacterPart) continue;
//             if (hit.collider is CharacterController) continue;

//             if (hit.distance < bestDist)
//             {
//                 bestDist = hit.distance;
//                 best = hit;
//                 found = true;
//             }
//         }

//         return found;
//     }

//     private void SprayGoop(Vector3 worldPos, Color col)
//     {
//         if (goopGlobs == null) return;
//         if (Time.time - _goopTimer < goopInterval) return;

//         _goopTimer = Time.time;

//         if (!goopGlobs.isPlaying)
//         {
//             goopGlobs.Play();
//         }

//         var emitParams = new ParticleSystem.EmitParams();
//         emitParams.startColor = col;

//         int count = Mathf.Max(1, goopEmitCount);
//         for (int i = 0; i < count; i++)
//         {
//             emitParams.position = worldPos;
//             emitParams.velocity = Random.insideUnitSphere * goopSpraySpeed;
//             goopGlobs.Emit(emitParams, 1);
//         }
//     }

//     public void OnScenePointerMove(PointerEventData e)
//     {
//         if (!drawingOn) return;
//         MoveCursorIcon(e.position);
//     }

//     public void OnScenePointerUp(PointerEventData e)
//     {
//         sceneGestureActive = false;
//         if (drawingOn && mode == 1)
//         {
//             SetMode(0);
//         }
//     }

//     public void OnScenePointerExit(PointerEventData e)
//     {
//         if (cursorIcon != null)
//         {
//             cursorIcon.gameObject.SetActive(false);
//         }
//         HideLoupe();
//     }

//     private void HideLoupe()
//     {
//         if (loupeGO != null && loupeGO.activeSelf)
//         {
//             loupeGO.SetActive(false);
//         }
//     }

//     // NOTE: this coroutine's body wasn't in the pasted dump — only its state-machine constructor was
//     // shown. Reconstructed from context: readTex is a 5x5 texture never used anywhere else, which
//     // only makes sense as a small screen-pixel grab for the "sample the rendered frame" fallback
//     // eyedropper mode (as opposed to TryRaycastAlbedo's "sample the raw material" mode). Waiting for
//     // end-of-frame before reading is the standard Unity pattern for grabbing post-render screen pixels.
//     private IEnumerator ReadAtEndOfFrame()
//     {
//         yield return new WaitForEndOfFrame();

//         int px = Mathf.Clamp(Mathf.RoundToInt(readPos.x) - 2, 0, Mathf.Max(0, Screen.width - 5));
//         int py = Mathf.Clamp(Mathf.RoundToInt(readPos.y) - 2, 0, Mathf.Max(0, Screen.height - 5));

//         readTex.ReadPixels(new Rect(px, py, 5f, 5f), 0, 0);
//         readTex.Apply(false);

//         Color[] pixels = readTex.GetPixels();
//         float r = 0f, g = 0f, b = 0f;
//         foreach (Color c in pixels) { r += c.r; g += c.g; b += c.b; }
//         int n = Mathf.Max(1, pixels.Length);
//         Color sampled = new Color(r / n, g / n, b / n, 1f);

//         SetBrush(NormalizeAlbedo(sampled));
//         readPending = false;
//     }

//     // Removes shadow-darkening from a sampled color by boosting brightness toward eyedropBrightness
//     // (based on the brightest channel), when eyedropIgnoreShadow is enabled.
//     private Color NormalizeAlbedo(Color c)
//     {
//         if (!eyedropIgnoreShadow) return new Color(c.r, c.g, c.b, 1f);

//         float maxCh = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
//         if (maxCh < 0.004f) return new Color(c.r, c.g, c.b, 1f);

//         float boost = Mathf.Clamp(eyedropBrightness / maxCh, 1f, 6f);
//         return new Color(Mathf.Clamp01(c.r * boost), Mathf.Clamp01(c.g * boost), Mathf.Clamp01(c.b * boost), 1f);
//     }

//     // ====================== BRUSH / COLOR UI ======================

//     public void SetBrush(Color c)
//     {
//         brush = new Color(c.r, c.g, c.b, 1f);
//         Color.RGBToHSV(brush, out H, out S, out V);
//         RefreshColorUI();
//     }

//     private void RefreshMaterialUI()
//     {
//         if (mFill != null) mFill.fillAmount = metallic;
//         if (roFill != null) roFill.fillAmount = roughness;
//         if (mVal != null) mVal.text = metallic.ToString("0.00");
//         if (roVal != null) roVal.text = roughness.ToString("0.00");
//     }

//     // Updates the brush-size handle position/fill and the live size-preview dot from brushPx.
//     private void RefreshBrushUI()
//     {
//         float t = Mathf.Clamp01((brushPx - 5f) / 43f);

//         if (_sizeHandleRt != null)
//         {
//             float half = _sizeHandleD * 0.5f;
//             float x = half + t * ((_sizeTrackW - half) - half);
//             _sizeHandleRt.anchoredPosition = new Vector2(x, 0f);

//             if (_sizeFill != null)
//             {
//                 _sizeFill.fillAmount = _sizeTrackW > 0f ? x / _sizeTrackW : t;
//             }
//         }

//         if (_sizeDotRt != null)
//         {
//             float size = sizePreviewDotMin + t * (sizePreviewDotMax - sizePreviewDotMin);
//             _sizeDotRt.sizeDelta = new Vector2(size, size);
//         }
//     }

//     // NOTE: the "active but no icon-sprite assigned" highlight tints below (for the eye/erase
//     // buttons) and the cursor-icon's non-paint pivot Y were read in the decompiled code from small
//     // hardcoded data-section float tables, whose actual values aren't recoverable from the pseudocode
//     // alone. Reasonable placeholder tints/values are used instead — purely cosmetic, doesn't affect
//     // any gameplay logic.
//     public void SetMode(int m)
//     {
//         mode = m;
//         if (m != 1) HideLoupe();

//         bool hasToggleSprites = pickBgSprite != null && noPickBgSprite != null;

//         if (eyeBtnImg != null)
//         {
//             if (!hasToggleSprites)
//             {
//                 eyeBtnImg.color = m == 1 ? new Color(0.45f, 0.65f, 0.95f, 0.95f) : new Color(0.25f, 0.27f, 0.32f, 1f);
//             }
//             else
//             {
//                 eyeBtnImg.sprite = m == 1 ? noPickBgSprite : pickBgSprite;
//                 eyeBtnImg.color = Color.white;
//             }
//         }

//         if (eraseBtnImg != null)
//         {
//             if (!hasToggleSprites)
//             {
//                 eraseBtnImg.color = m == 2 ? new Color(0.95f, 0.45f, 0.45f, 0.95f) : new Color(0.32f, 0.22f, 0.22f, 1f);
//             }
//             else
//             {
//                 eraseBtnImg.sprite = m == 2 ? noPickBgSprite : pickBgSprite;
//                 eraseBtnImg.color = Color.white;
//             }
//         }

//         if (modeText != null)
//         {
//             modeText.text = m == 2 ? "ERASER: drag over the character to erase"
//                            : m == 1 ? "EYEDROPPER: click a surface to sample"
//                            : "PAINT: click / drag on the character";
//         }

//         if (cursorIcon != null)
//         {
//             Sprite icon = m == 1 ? pickerIcon : m == 2 ? eraserIcon : paintIcon;
//             if (icon != null) cursorIcon.sprite = icon;

//             RectTransform rt = cursorIcon.rectTransform;
//             rt.pivot = new Vector2(0.12f, m == 0 ? 0.16f : 0f);
//             cursorIcon.gameObject.SetActive(false);
//         }
//     }

//     public void CloseDrawing()
//     {
//         drawingOn = false;
//         EndPaintCollider();
//         mode = 0;

//         if (panelGO != null) panelGO.SetActive(false);
//         if (paintGateImg != null && gateOffSprite != null) paintGateImg.sprite = gateOffSprite;
//         if (modeText != null) modeText.gameObject.SetActive(false);
//         if (cursorIcon != null) cursorIcon.gameObject.SetActive(false);

//         HideLoupe();
//     }

//     public void OpenDrawing()
//     {
//         drawingOn = true;
//         BeginPaintCollider();

//         if (panelGO != null) panelGO.SetActive(true);
//         if (paintGateImg != null && gateOnSprite != null) paintGateImg.sprite = gateOnSprite;
//         if (modeText != null) modeText.gameObject.SetActive(true);

//         SetMode(0);
//     }

//     public void ToggleDrawing()
//     {
//         if (RootManager.Instance == null) return;

//         RootManager.Instance.ShowInterAds_Native();

//         if (drawingOn) CloseDrawing(); else OpenDrawing();
//     }

//     public void ToggleEyedrop()
//     {
//         SetMode(mode != 1 ? 1 : 0);
//     }

//     public void ToggleErase()
//     {
//         SetMode(mode != 2 ? 2 : 0);
//     }

//     private static void SetMarkerY(RectTransform m, float v)
//     {
//         if (m == null) return;
//         float t = Mathf.Clamp01(v);
//         m.anchorMin = new Vector2(0f, t);
//         m.anchorMax = new Vector2(1f, t);
//         m.pivot = new Vector2(0.5f, 0.5f);
//         m.anchoredPosition = Vector2.zero;
//         m.sizeDelta = new Vector2(0f, 6f);
//     }

//     private static void SetMarkerX(RectTransform m, float v)
//     {
//         if (m == null) return;
//         float t = Mathf.Clamp01(v);
//         m.anchorMin = new Vector2(t, 0f);
//         m.anchorMax = new Vector2(t, 1f);
//         m.pivot = new Vector2(0.5f, 0.5f);
//         m.anchoredPosition = Vector2.zero;
//         m.sizeDelta = new Vector2(6f, 0f);
//     }

//     public void SetHS(float h, float s)
//     {
//         float hh = Mathf.Repeat(h, 1f);
//         float ss = Mathf.Clamp01(s);
//         float vv = Mathf.Max(0.03f, V);
//         SetBrush(Color.HSVToRGB(hh, ss, vv, true));
//     }

//     public void SetV(float v)
//     {
//         SetBrush(Color.HSVToRGB(H, S, Mathf.Clamp01(v), true));
//     }

//     public void SetChannel(int i, float val)
//     {
//         float v = Mathf.Clamp01(val);
//         Color c = brush;
//         if (i == 0) c.r = v;
//         else if (i == 1) c.g = v;
//         else c.b = v;
//         SetBrush(c);
//     }

//     public void SetBrushSize(float v)
//     {
//         float t = Mathf.Clamp01(v);
//         brushPx = Mathf.RoundToInt(t * 43f + 5f);
//         RefreshBrushUI();
//     }

//     // Updates every color-dependent UI element (preview swatch, wheel marker, V-slider gradient,
//     // R/G/B markers and value texts, HSV readout, hex readout) from the current brush/H/S/V state.
//     private void RefreshColorUI()
//     {
//         if (preview != null) preview.color = brush;

//         if (wheelMarker != null)
//         {
//             float angle = H * Mathf.PI * 2f;
//             float radius = S * wheelRadius;
//             wheelMarker.anchoredPosition = new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
//         }

//         if (vSliderTex != null)
//         {
//             int height = vSliderTex.height;
//             for (int y = 0; y < height; y++)
//             {
//                 Color c = Color.HSVToRGB(H, S, y / (float)(height - 1), true);
//                 c.a = 1f;
//                 vSliderTex.SetPixel(0, y, c);
//             }
//             vSliderTex.Apply();
//         }

//         SetMarkerY(vMarker, V);
//         SetMarkerX(rMark, brush.r);
//         SetMarkerX(gMark, brush.g);
//         SetMarkerX(bMark, brush.b);

//         if (rVal != null) rVal.text = brush.r.ToString("0.00");
//         if (gVal != null) gVal.text = brush.g.ToString("0.00");
//         if (bVal != null) bVal.text = brush.b.ToString("0.00");

//         if (hsvVal != null)
//         {
//             hsvVal.text = "H " + (H * 360f).ToString("0") + "   S " + S.ToString("0.00") + "   V " + V.ToString("0.00");
//         }

//         if (hexVal != null)
//         {
//             hexVal.text = "#" + ColorUtility.ToHtmlStringRGB(brush);
//         }
//     }

//     public void AuthorUI()
//     {
//         if (font == null) font = UIFont.Default;

//         if (solidSprite == null)
//         {
//             solidSprite = MakeSolid();
//             circleSprite = MakeCircle(96);
//             ringSprite = MakeRing(96, 0.7f);
//             wheelSprite = MakeWheel(256);
//             lineSprite = MakeSolid();
//         }

//         EnsureEventSystem();
//         BuildUI();
//     }

//     // ====================== STATIC INIT ======================

//     static ChameleonPaint()
//     {
//         BaseColorPropId = Shader.PropertyToID("_BaseColor");
//         ColorPropId = Shader.PropertyToID("_Color");
//         BaseMapPropId = Shader.PropertyToID("_BaseMap");
//         MainTexPropId = Shader.PropertyToID("_MainTex");
//         MetallicPropId = Shader.PropertyToID("_Metallic");
//         SmoothnessPropId = Shader.PropertyToID("_Smoothness");
//     }
// }