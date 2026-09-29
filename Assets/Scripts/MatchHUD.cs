using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// NOTE: Entirely runtime-built HUD (no pre-placed scene objects needed) via SceneUI helpers -
// a circular "ring" timer (track + radial fill + centered countdown text) plus two rows of
// on/off icons for remaining fugitives/hunters. EnsureBuilt/AuthorUI are confirmed byte-identical
// (same exact-duplicate-method pattern as HideModeResultUI.EnsureBuilt/AuthorUI from an earlier
// session) - implemented once here, with AuthorUI forwarding.
public class MatchHUD : MonoBehaviour
{
    [SerializeField] private Font font;
    [SerializeField] private Sprite ringTrack;
    [SerializeField] private Sprite ringFill;
    [SerializeField] private Sprite fugitiveIcon;
    [SerializeField] private Sprite hunterIcon;

    [SerializeField] private int maxPerSide = 8;
    [SerializeField] private float ringSize = 84f;
    [SerializeField] private int pulseUnderSeconds = 10;
    [SerializeField] private float iconSize = 48f;
    [SerializeField] private float iconGap = 52f;
    [SerializeField] private float pulseSpeed = 8f;
    [SerializeField] private float pulseAmount = 0.12f;

    private GameObject canvasGO;
    private RectTransform ringRootRT;
    private Image ringFillImg;
    private Text timerText;
    private readonly List<Image> fugIcons = new List<Image>();
    private readonly List<Image> hunIcons = new List<Image>();
    private GameModeManager gmm;

    private void Start()
    {
        gmm = FindFirstObjectByType<GameModeManager>();
        EnsureBuilt();
        SetVisible(false);
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

        Transform hudRoot = SceneUI.HUDRoot();
        if (hudRoot == null)
        {
            throw new NullReferenceException("SceneUI.HUDRoot() returned null");
        }

        Transform existing = hudRoot.Find("MatchHudCanvas");
        if (existing != null)
        {
            Transform ring = existing.Find("RingTrack");
            if (ring != null)
            {
                BindUI(existing.gameObject);
                return;
            }
        }

        BuildUI();
    }

    public void AuthorUI()
    {
        EnsureBuilt();
    }

    // FIX (confirmed): raw's public method structure is `if (v) {...} if (!v) { if (ringRootRT
    // != null) { ...reset scale... } }` - the null check GATES the whole scale-reset block; if
    // ringRootRT is null, raw falls through to a normal return, it does not throw. A prior
    // draft's comment claimed the opposite ("decompiled crashes here if ringRootRT is null")
    // and relied on a natural NRE from accessing ringRootRT.localScale directly - that comment
    // was wrong. Fixed to gate on ringRootRT != null like raw actually does. Epsilon corrected
    // to the exact literal from raw (9.9999994e-11f and 1e-10f round to adjacent float32 values,
    // 1 ULP apart - meaningless for this comparison, but matched exactly since it costs nothing).
    public void SetVisible(bool v)
    {
        if (canvasGO != null && canvasGO.activeSelf != v)
        {
            canvasGO.SetActive(v);
        }

        if (!v && ringRootRT != null)
        {
            if ((ringRootRT.localScale - Vector3.one).sqrMagnitude < 9.9999994e-11f)
            {
                return;
            }
            ringRootRT.localScale = Vector3.one;
        }
    }

    // FIX (confirmed): raw's structure is `if (gmm != null) { ...RoundActive check, reads,
    // Refresh()... } LAB: SetVisible(false); return;` - the shared label is reached both when
    // !RoundActive (via an explicit goto) AND, more importantly, whenever gmm is null in the
    // very first place (the whole outer if, including its internal throw, is skipped entirely).
    // A prior draft threw immediately on gmm == null instead of gracefully hiding the HUD like
    // raw does - that would crash in any scene where Update() runs before a GameModeManager
    // exists (or if one is never found). Fixed to match raw's graceful fallback exactly.
    private void Update()
    {
        if (gmm == null || !gmm.RoundActive)
        {
            SetVisible(false);
            return;
        }

        int fug = gmm.FugitivesLeft;
        int hun = gmm.HuntersLeft;
        int secs = gmm.TimerSeconds;
        float frac = gmm.TimerFraction;
        Refresh(fug, hun, secs, frac);
    }

    private void Refresh(int fug, int hun, int secs, float frac)
    {
        SetVisible(true);

        for (int i = 0; i < fugIcons.Count; i++)
        {
            fugIcons[i].gameObject.SetActive(i < fug);
        }

        for (int i = 0; i < hunIcons.Count; i++)
        {
            hunIcons[i].gameObject.SetActive(i < hun);
        }

        if (ringFillImg != null)
        {
            ringFillImg.fillAmount = Mathf.Clamp01(frac);
        }

        if (timerText != null)
        {
            timerText.text = secs.ToString();
        }

        ApplyPulse(secs);
    }

    private void ApplyPulse(int secs)
    {
        if (ringRootRT == null)
        {
            return;
        }

        float scale = 1f;
        if (secs > 0 && secs <= pulseUnderSeconds)
        {
            float t = Mathf.Sin(Time.unscaledTime * pulseSpeed);
            scale = pulseAmount * (t * 0.5f + 0.5f) + 1f;
        }

        ringRootRT.localScale = new Vector3(scale, scale, 1f);
    }

    // NOTE: binds to an already-existing "MatchHudCanvas" hierarchy (found under SceneUI.HUDRoot()).
    // The "FugN"/"HunN" name-counters looked at first glance like they might share one packed
    // 64-bit stack slot with state leaking between loops, but each is independently zeroed right
    // before its own loop starts - reconstructed as two ordinary, independent loops. Cross-
    // confirmed against BuildUI: the same string literal IDs used to search for "FugN"/"HunN"
    // here are the exact ones used to NAME the fugitive/hunter icons when BuildUI creates them.
    private void BindUI(GameObject canvas)
    {
        canvasGO = canvas;
        Transform canvasT = canvas.transform;

        Transform ringTrackT = canvasT.Find("RingTrack");
        if (ringTrackT != null)
        {
            ringRootRT = ringTrackT as RectTransform;

            Transform ringFillT = ringTrackT.Find("RingFill");
            if (ringFillT != null)
            {
                ringFillImg = ringFillT.GetComponent<Image>();
            }

            Transform timerT = ringTrackT.Find("TimerText");
            if (timerT != null)
            {
                timerText = timerT.GetComponent<Text>();
            }
        }

        fugIcons.Clear();
        hunIcons.Clear();

        int i = 0;
        Transform fugT = canvasT.Find("Fug" + i);
        while (fugT != null)
        {
            fugIcons.Add(fugT.GetComponent<Image>());
            i++;
            fugT = canvasT.Find("Fug" + i);
        }

        int j = 0;
        Transform hunT = canvasT.Find("Hun" + j);
        while (hunT != null)
        {
            hunIcons.Add(hunT.GetComponent<Image>());
            j++;
            hunT = canvasT.Find("Hun" + j);
        }

        canvasGO.SetActive(false);
    }

    private void BuildUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }

        bool canvasCreated;
        canvasGO = SceneUI.GetOrCreateCanvas("MatchHudCanvas", out canvasCreated);

        Canvas canvasComp = SceneUI.GetOrAdd<Canvas>(canvasGO);
        if (canvasCreated)
        {
            canvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasComp.sortingOrder = 10;

            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
        }

        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);

        Transform canvasT = canvasGO.transform;

        Image ring = NewImage("RingTrack", canvasT, ringTrack, Color.white);
        ring.preserveAspect = true;
        ring.raycastTarget = false;
        PlaceTC(ring, 0f, -54f, ringSize, ringSize);
        ringRootRT = ring.rectTransform;

        Image fill = NewImage("RingFill", ring.transform, ringFill, Color.white);
        fill.raycastTarget = false;
        fill.preserveAspect = true;
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Radial360;
        fill.fillOrigin = (int)Image.Origin360.Top;
        fill.fillClockwise = true;
        fill.fillAmount = 1f;
        Stretch(fill);
        ringFillImg = fill;

        timerText = NewText("TimerText", ring.transform, "0", 32, new Color(0.27f, 0.78f, 0.22f, 1f), TextAnchor.MiddleCenter, true);
        Stretch(timerText);

        for (int i = 0; i < maxPerSide; i++)
        {
            float baseOffset = ringSize * 0.5f + 24f;

            Image fugIcon = NewImage("Fug" + i, canvasT, fugitiveIcon, Color.white);
            fugIcon.preserveAspect = true;
            fugIcon.raycastTarget = false;
            PlaceTC(fugIcon, -(baseOffset + iconGap * i), -54f, iconSize, iconSize);
            fugIcon.gameObject.SetActive(false);
            fugIcons.Add(fugIcon);

            Image hunIcon = NewImage("Hun" + i, canvasT, hunterIcon, Color.white);
            hunIcon.preserveAspect = true;
            hunIcon.raycastTarget = false;
            PlaceTC(hunIcon, baseOffset + iconGap * i, -54f, iconSize, iconSize);
            hunIcon.gameObject.SetActive(false);
            hunIcons.Add(hunIcon);
        }

        canvasGO.SetActive(false);
    }

    private static Image NewImage(string n, Transform parent, Sprite spr, Color col)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = col;
        if (spr != null)
        {
            img.sprite = spr;
        }
        return img;
    }

    // NOTE: anchorMax/anchorMin = (0.5, 1) (top-center), pivot = (0.5, 0.5) - matches the
    // packed Vector2 constants in the decompiled RectTransform setters exactly.
    private static void PlaceTC(Component c, float x, float y, float w, float h)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void Stretch(Component c)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // NOTE: unlike MatchmakingUI's NewText, this one does NOT call set_supportRichText in raw -
    // confirmed absent from the decompiled body (verified by direct comparison against
    // MatchmakingUI$$NewText, which does have that call). Not carried over here.
    private Text NewText(string n, Transform parent, string content, int size, Color col, TextAnchor anchor, bool bold)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Text text = go.AddComponent<Text>();
        text.font = font;
        text.text = content;
        text.fontSize = size;
        text.color = col;
        text.alignment = anchor;
        text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

}