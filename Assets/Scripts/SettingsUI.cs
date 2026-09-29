using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// NOTE: Runtime-built settings panel (music/SFX toggles, a graphics-quality stepper, restore
// purchases, and a home button). Same rebuild-safe HashSet<GameObject> dedup pattern as
// RemoveAdsUI. New confirmed members: AudioManager.SetMusicEnabled(bool)/SetSfxEnabled(bool)
// (static), HomeUI.GoHome(), and a fully-qualified reference to a new, still-unreversed class
// Dev.Scripts.UI.HomeScene.IAPPackage.CheckPurchased() (static). The tail of this paste also
// contained UnityEngine.Rendering.DebugDisplaySettingsUI - pure URP/Unity engine internal debug-
// rendering plumbing, not game code - and is not reconstructed here.
public class SettingsUI : MonoBehaviour
{
    private class Tog
    {
        public Image track;
        public Image knob;
        public Text onLabel;
        public Text offLabel;
        public string key;
        public bool on;
    }

    [SerializeField] private Font font;
    [SerializeField] private Sprite panelSprite;
    [SerializeField] private Sprite titleSprite;
    [SerializeField] private Sprite closeIcon;
    [SerializeField] private Sprite homeBg;
    [SerializeField] private Sprite homeIcon;
    [SerializeField] private Sprite restoreBg;
    [SerializeField] private Sprite toggleTrack;
    [SerializeField] private Sprite knobOn;
    // NOTE: confirmed to exist via a raw field offset in ApplyToggle (0x48, used when the
    // toggle is off) but never referenced by name anywhere in MakeToggle - genuinely present
    // but otherwise unused in what was decompiled here.
    [SerializeField] private Sprite knobOff;
    [SerializeField] private Sprite arrowBack;
    [SerializeField] private Sprite arrowNext;

    private GameObject canvasGO;
    private Text qualityLabel;
    private int qualityIndex;
    private readonly List<Tog> toggles = new List<Tog>();
    private readonly HashSet<GameObject> _reused = new HashSet<GameObject>();

    // NOTE: these four static readonly fields are decoded directly from the .cctor's raw writes
    // into the class's static-fields block (a string[3] array plus a packed Vector2 and two
    // packed Colors immediately following it) - not a guess.
    private static readonly string[] QualityNames = { "Low", "Medium", "High" };
    private static readonly Vector2 CenterAnchor = new Vector2(0.5f, 0.5f);
    private static readonly Color OnColor = new Color(1f, 1f, 1f, 1f);
    private static readonly Color OffColor = new Color(1f, 1f, 1f, 0.4f);

    private void Start()
    {
        EnsureBuilt();
        LoadState();
        Hide();
    }

    private void EnsureBuilt()
    {
        if (canvasGO != null)
        {
            return;
        }

        if (font == null)
        {
            font = UIFont.Default;
        }

        _reused.Clear();
        toggles.Clear();

        bool canvasCreated;
        canvasGO = SceneUI.GetOrCreate("SettingsCanvas", null, out canvasCreated);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);

        if (canvasCreated)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 52;

            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }
        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);

        Transform canvasT = canvasGO.transform;

        Image dim = NewImage("Dim", canvasT, null, new Color(0f, 0f, 0f, 0.6f), true, false);
        Stretch(dim);

        Image popup = NewImage("Popup", canvasT, panelSprite, Color.white, true, false);
        Place(popup, CenterAnchor, CenterAnchor, Vector2.zero, new Vector2(760f, 644f));
        Transform parent = popup.transform;

        Image title = NewImage("Title", parent, titleSprite, Color.white, false, true);
        Place(title, CenterAnchor, CenterAnchor, new Vector2(20f, 258f), new Vector2(230f, 70f));

        Image closeImg = NewImage("CloseBtn", parent, closeIcon, Color.white, true, true);
        Place(closeImg, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-14f, -14f), new Vector2(62f, 65f));

        Button closeBtn = SceneUI.GetOrAdd<Button>(closeImg.gameObject);
        closeBtn.targetGraphic = closeImg;

        MakeLabel("LblMusic", parent, "Music/Ambient", 120f);
        MakeToggle("TogMusic", parent, "set_music", 120f);
        MakeLabel("LblSound", parent, "Sound Effects", 50f);
        MakeToggle("TogSound", parent, "set_sound", 50f);
        MakeLabel("LblGfx", parent, "Graphic Quality", -20f);
        MakeStepper(parent, -20f);

        Button restoreBtn = NewButton("RestoreBtn", parent, restoreBg, new Vector2(-135f, -195f), new Vector2(244f, 88f));
        Text restoreLabel = NewText("Label", restoreBtn.transform, "Restore\npurchase", 26, Color.white, (int)TextAnchor.MiddleCenter, (int)HorizontalWrapMode.Wrap);
        Stretch(restoreLabel);

        Button homeBtn = NewButton("HomeBtn", parent, homeBg, new Vector2(135f, -195f), new Vector2(244f, 88f));
        if (homeIcon != null)
        {
            Image homeIconImg = NewImage("Icon", homeBtn.transform, homeIcon, Color.white, false, true);
            Place(homeIconImg, CenterAnchor, CenterAnchor, new Vector2(0f, -52f), new Vector2(56f, 56f));
        }
        Text homeLabel = NewText("Label", homeBtn.transform, "Home", 30, Color.white, (int)TextAnchor.MiddleCenter, (int)HorizontalWrapMode.Overflow);
        Place(homeLabel, CenterAnchor, CenterAnchor, new Vector2(0f, 24f), new Vector2(150f, 50f));

        WireControls();
        canvasGO.SetActive(false);
    }

    private void LoadState()
    {
        foreach (Tog t in toggles)
        {
            t.on = PlayerPrefs.GetInt(t.key, 1) == 1;
            ApplyToggle(t);
            ApplyAudioSetting(t);
        }

        int saved = PlayerPrefs.GetInt("set_quality", 1);
        qualityIndex = Mathf.Clamp(saved, 0, QualityNames.Length - 1);
        RefreshQuality(Application.isPlaying);
    }

    public void Hide()
    {
        if (canvasGO != null)
        {
            canvasGO.SetActive(false);
        }
    }

    public void AuthorUI()
    {
        EnsureBuilt();
        LoadState();
        Hide();
    }

    public void Show()
    {
        EnsureBuilt();
        LoadState();
        if (canvasGO != null)
        {
            canvasGO.SetActive(true);
        }
    }

    // NOTE: confirmed to just log a TODO rather than doing anything.
    public void OnRestore()
    {
        Debug.Log("[Settings] Restore purchase \u2014 TODO: hook up IAP.");
    }

    public void OnHome()
    {
        HomeUI homeUI = FindFirstObjectByType<HomeUI>();
        if (homeUI != null)
        {
            homeUI.GoHome();
        }
        Hide();
    }

    private void ApplyToggle(Tog t)
    {
        if (t.knob != null)
        {
            t.knob.sprite = t.on ? knobOn : knobOff;

            RectTransform knobRT = t.knob.rectTransform;
            float half = (t.track != null) ? t.track.rectTransform.rect.width * 0.5f : 48f;
            float offset = half + knobRT.rect.width * -0.5f - 3f;
            if (t.on)
            {
                offset = -offset;
            }
            knobRT.anchoredPosition = new Vector2(offset, 0f);
        }

        if (t.onLabel != null)
        {
            t.onLabel.color = t.on ? OnColor : OffColor;
        }
        if (t.offLabel != null)
        {
            t.offLabel.color = t.on ? OffColor : OnColor;
        }
    }

    private void ApplyAudioSetting(Tog t)
    {
        if (!Application.isPlaying)
        {
            return;
        }

        if (t.key == "set_music")
        {
            AudioManager.SetMusicEnabled(t.on);
            return;
        }
        if (t.key == "set_sound")
        {
            AudioManager.SetSfxEnabled(t.on);
        }
    }

    private void RefreshQuality(bool apply)
    {
        if (qualityLabel != null)
        {
            int idx = Mathf.Clamp(qualityIndex, 0, QualityNames.Length - 1);
            qualityLabel.text = QualityNames[idx];
        }

        if (apply)
        {
            ApplyGraphics(qualityIndex);
        }
    }

    // Confirmed empty - decompiles to a bare `return;` with no other statements.
    private void BtnShowInter()
    {
    }

    private void FlipToggle(Tog t)
    {
        t.on = !t.on;
        PlayerPrefs.SetInt(t.key, t.on ? 1 : 0);
        ApplyToggle(t);
        ApplyAudioSetting(t);
    }

    public void StepQuality(int dir)
    {
        int count = QualityNames.Length;
        int next = ((qualityIndex + dir) % count + count) % count;
        qualityIndex = next;
        PlayerPrefs.SetInt("set_quality", next);
        RefreshQuality(true);
    }

    // NOTE: URP's UniversalRenderPipelineAsset.m_MSAA is written via a raw field access in the
    // decompiled code rather than a visible property call - could reflect direct field access
    // via some internal/reflection trick in the original, or just be how IL2CPP represents a
    // property setter call here. Written using the public msaaSampleCount property since that's
    // the ordinary, available way to do this from outside URP's own assembly.
    private void ApplyGraphics(int idx)
    {
        float lodBias;
        int shadowQuality;

        if (idx == 0)
        {
            QualitySettings.globalTextureMipmapLimit = 2;
            lodBias = 0.5f;
            shadowQuality = 1;
        }
        else
        {
            lodBias = (idx == 1) ? 1.0f : 2.0f;
            QualitySettings.globalTextureMipmapLimit = (idx == 1) ? 1 : 0;
            shadowQuality = 2;
        }

        int pixelLightCount = (idx != 1) ? 4 : 2;
        float shadowDistance = (idx != 1) ? 150f : 100f;
        int antiAliasing = pixelLightCount;

        if (idx == 0)
        {
            pixelLightCount = 1;
            shadowDistance = 50f;
            antiAliasing = 0;
        }

        QualitySettings.lodBias = lodBias;
        QualitySettings.maximumLODLevel = 0;
        QualitySettings.shadows = (UnityEngine.ShadowQuality)shadowQuality;
        QualitySettings.shadowDistance = shadowDistance;
        QualitySettings.antiAliasing = antiAliasing;
        QualitySettings.pixelLightCount = pixelLightCount;
        QualitySettings.softParticles = idx > 0;

        UniversalRenderPipelineAsset urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urpAsset != null)
        {
            if (idx == 0)
            {
                urpAsset.renderScale = 0.6f;
                urpAsset.shadowDistance = 50f;
                urpAsset.msaaSampleCount = 1;
            }
            else
            {
                urpAsset.renderScale = (idx == 1) ? 0.85f : 1.0f;
                urpAsset.shadowDistance = shadowDistance;
                urpAsset.msaaSampleCount = (idx == 1) ? 2 : 4;
            }
        }
    }

    // Confirmed empty - decompiles to a bare `return;` with no other statements.
    private void ResolveSprites()
    {
    }

    private Image NewImage(string n, Transform parent, Sprite spr, Color col, bool raycast, bool preserveAspect)
    {
        bool created;
        GameObject go = SceneUI.GetOrCreate(n, parent, out created);
        Image img = SceneUI.GetOrAdd<Image>(go);

        if (!created)
        {
            _reused.Add(go);
        }
        else
        {
            img.raycastTarget = raycast;
            img.color = col;
            img.preserveAspect = preserveAspect;
            if (spr != null)
            {
                img.sprite = spr;
            }
        }

        return img;
    }

    private void Stretch(Component c)
    {
        if (_reused.Contains(c.gameObject))
        {
            return;
        }

        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private void Place(Component c, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        if (_reused.Contains(c.gameObject))
        {
            return;
        }

        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMax = anchor;
        rt.anchorMin = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    private void MakeLabel(string n, Transform parent, string text, float y)
    {
        Text label = NewText(n, parent, text, 28, Color.white, (int)TextAnchor.MiddleLeft, (int)HorizontalWrapMode.Overflow);
        Place(label, CenterAnchor, CenterAnchor, new Vector2(-110f, y), new Vector2(330f, 46f));
    }

    private void MakeToggle(string n, Transform parent, string key, float y)
    {
        Image track = NewImage(n, parent, toggleTrack, Color.white, true, false);
        Place(track, CenterAnchor, CenterAnchor, new Vector2(192f, y), new Vector2(96f, 42f));

        Button btn = SceneUI.GetOrAdd<Button>(track.gameObject);
        btn.targetGraphic = track;

        Transform trackT = track.transform;
        Image knob = NewImage("Knob", trackT, knobOn, Color.white, false, true);
        Place(knob, CenterAnchor, CenterAnchor, Vector2.zero, new Vector2(40f, 40f));

        Text onLabel = NewText("On", trackT, "On", 22, OnColor, (int)TextAnchor.MiddleRight, (int)HorizontalWrapMode.Overflow);
        Place(onLabel, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(52f, 40f));

        Text offLabel = NewText("Off", trackT, "Off", 22, OffColor, (int)TextAnchor.MiddleLeft, (int)HorizontalWrapMode.Overflow);
        Place(offLabel, new Vector2(1f, 0.5f), new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(52f, 40f));

        Tog tog = new Tog
        {
            track = track,
            knob = knob,
            onLabel = onLabel,
            offLabel = offLabel,
            key = key
        };
        toggles.Add(tog);
    }

    private void MakeStepper(Transform parent, float y)
    {
        Button back = NewButton("GfxBack", parent, arrowBack, new Vector2(108f, y), new Vector2(40f, 52f));
        Image backImg = back.GetComponent<Image>();
        if (backImg != null)
        {
            backImg.preserveAspect = true;
        }

        qualityLabel = NewText("GfxValue", parent, "Text", 26, Color.white, (int)TextAnchor.MiddleCenter, (int)HorizontalWrapMode.Overflow);
        Place(qualityLabel, CenterAnchor, CenterAnchor, new Vector2(192f, y), new Vector2(130f, 44f));

        Button next = NewButton("GfxNext", parent, arrowNext, new Vector2(276f, y), new Vector2(40f, 52f));
        Image nextImg = next.GetComponent<Image>();
        if (nextImg != null)
        {
            nextImg.preserveAspect = true;
        }
    }

    private Button NewButton(string n, Transform parent, Sprite spr, Vector2 pos, Vector2 size)
    {
        Image img = NewImage(n, parent, spr, Color.white, true, false);
        Place(img, CenterAnchor, CenterAnchor, pos, size);

        Button btn = SceneUI.GetOrAdd<Button>(img.gameObject);
        btn.targetGraphic = img;
        return btn;
    }

    private Text NewText(string n, Transform parent, string content, int size, Color col, int anchor, int wrap)
    {
        bool created;
        GameObject go = SceneUI.GetOrCreate(n, parent, out created);
        Text text = SceneUI.GetOrAdd<Text>(go);

        if (!created)
        {
            _reused.Add(go);
        }
        else
        {
            text.font = font;
            text.raycastTarget = false;
            text.text = content;
            text.fontSize = size;
            text.color = col;
            text.alignment = (TextAnchor)anchor;
            text.horizontalOverflow = (HorizontalWrapMode)wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.lineSpacing = 0.8f;
        }

        return text;
    }

    // NOTE: click-handler lambda (`<WireControls>b__0`) itself wasn't decompiled - only that
    // it's cached in a closure capturing (this, the current Tog). Reconstructed as
    // FlipToggle(t), the clear intent given exactly those captured variables. StepQualityDown/Up
    // below, by contrast, ARE fully decompiled (as SettingsUI.<>b__40_1/b__40_2) - simple
    // wrapper methods, not guessed.
    private void WireControls()
    {
        foreach (Tog t in toggles)
        {
            if (t.track == null)
            {
                continue;
            }
            Button btn = t.track.GetComponent<Button>();
            if (btn == null)
            {
                continue;
            }

            Tog captured = t;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => FlipToggle(captured));
        }

        if (canvasGO == null)
        {
            return;
        }

        Transform back = canvasGO.transform.Find("Popup/GfxBack");
        if (back != null)
        {
            Button backBtn = back.GetComponent<Button>();
            if (backBtn != null)
            {
                backBtn.onClick.RemoveAllListeners();
                backBtn.onClick.AddListener(StepQualityDown);
            }
        }

        Transform next = canvasGO.transform.Find("Popup/GfxNext");
        if (next != null)
        {
            Button nextBtn = next.GetComponent<Button>();
            if (nextBtn != null)
            {
                nextBtn.onClick.RemoveAllListeners();
                nextBtn.onClick.AddListener(StepQualityUp);
            }
        }
    }

    // Confirmed via SettingsUI.<>b__40_1 / <>b__40_2 in the decompiled output.
    private void StepQualityDown()
    {
        StepQuality(-1);
    }

    private void StepQualityUp()
    {
        StepQuality(1);
    }

    // NOTE: fully-qualified reference to a new, still-unreversed class -
    // Dev.Scripts.UI.HomeScene.IAPPackage.CheckPurchased() (static).
    public void Restore()
    {
        // Dev.Scripts.UI.HomeScene.IAPPackage.CheckPurchased();
    }
}