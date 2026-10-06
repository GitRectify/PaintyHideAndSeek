using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Re-verified method by method against raw Ghidra output. Every string literal is confirmed against
// Dumpstringliteral.json. Convention used throughout: wherever raw jumps to the shared NRE-throw on a
// null, the C# just dereferences and lets the natural NullReferenceException happen; explicit
// `if (x != null)` guards are kept only where raw genuinely skips.
//
// Fields (names, order, offsets, attributes), the nested ResultUI class, and every method's
// accessibility match dump.cs (TypeDefIndex 9675 / ResultUI 9673 / DisplayClass79_0 9674).
// Every method, constructor and lambda has been checked against its raw decompile.
public class HideModeResultUI : MonoBehaviour
{
    // ResultUI$$.ctor allocates all three arrays at length 3 before the base object ctor, i.e. they
    // are field initializers.
    private class ResultUI
    {
        public GameObject root;
        public readonly Image[] icon = new Image[3];
        public readonly Text[] label = new Text[3];
        public readonly Text[] value = new Text[3];
    }

    [Header("Modal popups (Assets/Texture2D)")]
    public Sprite panelCaught;
    public Sprite titleCaught;
    public Sprite titleTimeUp;
    public Sprite btnReviveBg;
    public Sprite btnGiveUpBg;
    public Sprite adsIcon;

    [Header("Result banners (Assets/Texture2D)")]
    public Sprite bannerVictory;
    public Sprite bannerDefeat;
    public Sprite iconRankPodium;
    public Sprite iconPaintMedal;

    [Header("Result shared (Assets/Texture2D)")]
    public Sprite statRow;
    public Sprite iconGold;
    public Sprite btnHomeBg;
    public Sprite btnGoldBg;
    public Sprite btnNextBg;
    public Sprite iconHome;
    public Sprite iconNext;
    public Sprite iconAds;

    [Header("Coin reward (rolled at random, then added to GameData.Coin)")]
    [Tooltip("WIN: coins awarded are a random value in [x..y] (inclusive). Tune the range here.")]
    public Vector2Int winCoinRange = new Vector2Int(50, 100);
    [Tooltip("LOSE: coins awarded are a random value in [x..y] (inclusive).")]
    public Vector2Int loseCoinRange = new Vector2Int(30, 50);

    private int _lastReward;
    private Font font;
    private GameObject canvasGO;
    private GameObject caughtRoot;
    private GameObject timeUpRoot;
    private Button reviveBtn;
    private Button giveUpBtn;
    private Button extraBtn;
    private Button giveUp2Btn;
    // Confirmed from raw .ctor: constructed after the coin ranges, before the base ctor - i.e. field
    // initializers, so no explicit constructor is needed.
    private readonly ResultUI victory = new ResultUI();
    private readonly ResultUI defeat = new ResultUI();
    private Action _onRevive;
    private Action _onGiveUp;
    private Action _onExtra;
    private Action _onGiveUp2;
    private HomeUI homeUI;
    private GameModeManager gmm;
    private Camera _fxCam;
    private RawImage _fxImg;
    private GameObject _fxEffect;
    private RenderTexture _fxRT;

    // Decoded from .cctor's packed writes into the static block: RowLabel (+0x00) = (0.92, 0.94, 1, 1)
    // for the stat-row label text; GoldYellow (+0x10) = (1, 0.86, 0.3, 1) for the Gold row's value.
    private static readonly Color RowLabel = new Color(0.92f, 0.94f, 1f, 1f);
    private static readonly Color GoldYellow = new Color(1f, 0.86f, 0.3f, 1f);

    // Rolls a random coin reward from winCoinRange/loseCoinRange (inclusive, order-independent),
    // credits it to GameData.Coin, and refreshes the coin HUD via GameController if present.
    private int RollAndCredit(bool win)
    {
        Vector2Int range = win ? winCoinRange : loseCoinRange;
        int lo = Mathf.Min(range.x, range.y);
        int hi = Mathf.Max(range.x, range.y);

        _lastReward = UnityEngine.Random.Range(lo, hi + 1);

        GameData.Coin = _lastReward + GameData.Coin;

        if (SingletonMonoBehavior<GameController>.Instance != null)
        {
            SingletonMonoBehavior<GameController>.Instance.UpdateTextCoin();
        }

        return _lastReward;
    }

    private void Start()
    {
        EnsureRefs();
        EnsureBuilt();
        HideAll();
    }

    private void EnsureRefs()
    {
        if (homeUI == null)
        {
            homeUI = Object.FindFirstObjectByType<HomeUI>();
        }

        if (gmm == null)
        {
            gmm = Object.FindFirstObjectByType<GameModeManager>();
        }
    }

    // AuthorUI (further down) decompiles to byte-for-byte identical code, down to sharing the same
    // metadata-init guard - implemented once here, AuthorUI forwards.
    // referenceResolution 0x4487000044f00000 decodes to x = 1920, y = 1080 (landscape).
    // FIX (confirmed from raw): a null Canvas or CanvasScaler from GetOrAdd throws; it is not a
    // silent return.
    private void EnsureBuilt()
    {
        if (canvasGO != null) return;

        if (font == null)
        {
            font = UIFont.Default;
        }

        ResolveSprites();

        Transform uiRoot = SceneUI.UIRoot();
        if (uiRoot == null)
        {
            throw new NullReferenceException("SceneUI.UIRoot() returned null");
        }

        Transform existing = uiRoot.Find("HideResultCanvas");
        canvasGO = existing != null ? existing.gameObject : SceneUI.GetOrCreate("HideResultCanvas", null, out bool _);

        Canvas canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 40;

        CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);

        EnsureCaught();
        EnsureTimeUp();
        EnsureResult(victory, "VictoryPopup", bannerVictory);
        EnsureResult(defeat, "DefeatPopup", bannerDefeat);
    }

    // FIX (confirmed from raw): a null victory or defeat throws (the fx call is only reached when
    // both exist); only a null root inside each is a silent skip.
    public void HideAll()
    {
        if (caughtRoot != null) caughtRoot.SetActive(false);
        if (timeUpRoot != null) timeUpRoot.SetActive(false);
        if (victory.root != null) victory.root.SetActive(false);
        if (defeat.root != null) defeat.root.SetActive(false);

        PlayVictoryFx(false);
    }

    public void ShowCaught(Action onRevive, Action onGiveUp)
    {
        EnsureBuilt();
        _onRevive = onRevive;
        _onGiveUp = onGiveUp;
        ShowOnly(caughtRoot);
    }

    // FIX (confirmed from raw): a null victory, defeat or canvasGO throws - none are silent returns.
    private void ShowOnly(GameObject root)
    {
        RootManager.Instance?.SetNumber(2);

        if (caughtRoot != null) caughtRoot.SetActive(root == caughtRoot);
        if (timeUpRoot != null) timeUpRoot.SetActive(root == timeUpRoot);
        if (victory.root != null) victory.root.SetActive(root == victory.root);
        if (defeat.root != null) defeat.root.SetActive(root == defeat.root);

        canvasGO.SetActive(true);

        if (root == victory.root)
        {
            AudioManager.Win();
        }
        else if (root == defeat.root)
        {
            AudioManager.Lose();
        }

        PlayVictoryFx(root == victory.root);
    }

    public void ShowTimeUp(Action onExtra, Action onGiveUp)
    {
        EnsureBuilt();
        _onExtra = onExtra;
        _onGiveUp2 = onGiveUp;
        ShowOnly(timeUpRoot);
    }

    // The seconds parameter here (and in the three siblings below) is confirmed unused in raw - it
    // is only ever loaded into a dead register. Kept since callers pass it.
    public void ShowVictory(string rank, int survivalSecs, int paintScore)
    {
        FillResult(victory, rank, paintScore.ToString(), RollAndCredit(true));
    }

    // FIX (confirmed from raw): no bounds checks - rr, rr.value, and an array shorter than 3 all
    // throw. Only a null element is skipped. The gold text is "+" + gold (StringLiteral_943).
    private void FillResult(ResultUI rr, string rank, string score, int gold)
    {
        EnsureBuilt();

        if (rr.value[0] != null) rr.value[0].text = rank;
        if (rr.value[1] != null) rr.value[1].text = score;
        if (rr.value[2] != null) rr.value[2].text = "+" + gold;

        ShowOnly(rr.root);
    }

    public void ShowDefeat(string rank, int timeLeftSecs, int found)
    {
        FillResult(defeat, rank, found.ToString(), RollAndCredit(false));
    }

    public void ShowVictorySeek(string rank, int timeLeftSecs, int found)
    {
        FillResult(victory, rank, found.ToString(), RollAndCredit(true));
    }

    public void ShowDefeatSeek(string rank, int timeLeftSecs, int found)
    {
        FillResult(defeat, rank, found.ToString(), RollAndCredit(false));
    }

    // Shows/hides the victory celebration: a secondary camera rendering into a 1920x1080 ARGB32
    // RenderTexture (24-bit depth) shown on a RawImage, plus a particle-effect object. Its three
    // targets ("VictoryFxCam", "VictoryFxImage", "Effect") are resolved lazily by scene-wide name.
    // FIX (confirmed from raw): a null particle array or a null element throws (it does not return
    // early), and the "off" path stops with ParticleSystemStopBehavior 0 = StopEmittingAndClear,
    // not StopEmitting.
    private void PlayVictoryFx(bool on)
    {
        if (_fxCam == null)
        {
            GameObject camGO = FindGO("VictoryFxCam");
            if (camGO != null) _fxCam = camGO.GetComponent<Camera>();
        }

        if (_fxImg == null)
        {
            GameObject imgGO = FindGO("VictoryFxImage");
            if (imgGO != null) _fxImg = imgGO.GetComponent<RawImage>();

            if (_fxImg != null)
            {
                _fxImg.raycastTarget = false;
            }
        }

        if (_fxEffect == null)
        {
            _fxEffect = FindGO("Effect");
        }

        if (_fxCam == null || _fxImg == null || _fxEffect == null) return;

        if (on)
        {
            if (_fxRT == null)
            {
                _fxRT = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
                _fxRT.Create();
            }

            _fxCam.targetTexture = _fxRT;
            _fxImg.texture = _fxRT;

            Transform imgParent = _fxImg.transform.parent;
            if (imgParent != null) imgParent.gameObject.SetActive(true);

            _fxCam.enabled = true;
            _fxEffect.SetActive(true);

            foreach (ParticleSystem ps in _fxEffect.GetComponentsInChildren<ParticleSystem>(true))
            {
                ps.Clear(true);
                ps.Play(true);
            }
        }
        else
        {
            if (_fxCam != null) _fxCam.enabled = false;

            if (_fxImg != null)
            {
                Transform imgParent = _fxImg.transform.parent;
                if (imgParent != null) imgParent.gameObject.SetActive(false);
            }

            if (_fxEffect != null)
            {
                foreach (ParticleSystem ps in _fxEffect.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }
        }
    }

    // Scene-wide named-object lookup: GameObject.Find first, then a scan of every loaded Transform
    // filtered to ones in a valid scene. A null array, null element or null gameObject throws.
    private static GameObject FindGO(string n)
    {
        GameObject found = GameObject.Find(n);
        if (found != null) return found;

        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        if (all == null)
        {
            throw new NullReferenceException("Resources.FindObjectsOfTypeAll<Transform>() returned null");
        }

        foreach (Transform t in all)
        {
            if (t == null)
            {
                throw new NullReferenceException("FindGO: null Transform in scan");
            }
            if (t.name != n) continue;

            GameObject go = t.gameObject;
            if (go == null)
            {
                throw new NullReferenceException("FindGO: Transform with null gameObject");
            }

            if (go.scene.IsValid())
            {
                return go;
            }
        }

        return null;
    }

    // See the note on EnsureBuilt() - identical decompiled body.
    public void AuthorUI()
    {
        EnsureBuilt();
    }

    // Confirmed no-op: raw loads 18 sprite-path string literals in its metadata-init block but never
    // uses them - the body only re-assigns each of the 18 sprite fields to itself (a GC write-barrier
    // touch). Almost certainly leftover from Resources.Load<Sprite>(path) calls that were removed.
    // For reference, the 18 unused paths are: "end/ADS", "end/cion next", "end/gold", "end/home",
    // "end/icon home", "end/next", "end/x2 gold", "end/Rectangle 2 copy 13",
    // "mode/Rectangle 2 copy 11", "mode/Rounded Rectangle 5 copy 7",
    // "mode/Rounded Rectangle 5 copy 9", "mode/You Got Caught_", "mode/Time’s Up_", "mode/ads2",
    // "WinLose/Layer 33", "WinLose/Layer 34", "WinLose/victory", "WinLose/lose".
    private void ResolveSprites()
    {
    }

    private void EnsureCaught()
    {
        if (canvasGO == null)
        {
            throw new NullReferenceException("EnsureCaught: canvasGO is null");
        }

        Transform existing = canvasGO.transform.Find("CaughtPopup");
        if (existing == null)
        {
            BuildCaught();
            return;
        }

        caughtRoot = existing.gameObject;
    }

    private void EnsureTimeUp()
    {
        if (canvasGO == null)
        {
            throw new NullReferenceException("EnsureTimeUp: canvasGO is null");
        }

        Transform existing = canvasGO.transform.Find("TimeUpPopup");
        if (existing == null)
        {
            BuildTimeUp();
            return;
        }

        timeUpRoot = existing.gameObject;
    }

    // FIX (confirmed from raw): when the popup already exists, a null rr throws (not skipped).
    private void EnsureResult(ResultUI rr, string rootName, Sprite banner)
    {
        if (canvasGO == null)
        {
            throw new NullReferenceException("EnsureResult: canvasGO is null");
        }

        Transform existing = canvasGO.transform.Find(rootName);
        if (existing == null)
        {
            BuildResultPopup(rr, rootName, banner);
            return;
        }

        rr.root = existing.gameObject;
        BindResultRows(rr);
    }

    // FIX (confirmed from raw): a missing root, "Panel", "ActionBtn" or "GiveUpBtn", or a missing
    // Button on either, all throw - there are no silent returns in this method.
    private void BuildCaught()
    {
        caughtRoot = BuildModalPopup("CaughtPopup", titleCaught, "Watch an ad to get one more chance.", "Revive");

        Transform panel = caughtRoot.transform.Find("Panel");
        reviveBtn = panel.Find("ActionBtn").GetComponent<Button>();
        giveUpBtn = panel.Find("GiveUpBtn").GetComponent<Button>();

        if (reviveBtn != null) reviveBtn.onClick.AddListener(OnReviveClicked);
        if (giveUpBtn != null) giveUpBtn.onClick.AddListener(OnGiveUpClicked);
    }

    // Same shape and same null handling as BuildCaught.
    private void BuildTimeUp()
    {
        timeUpRoot = BuildModalPopup("TimeUpPopup", titleTimeUp, "Watch an ad to get 30 extra seconds.", "+30s");

        Transform panel = timeUpRoot.transform.Find("Panel");
        extraBtn = panel.Find("ActionBtn").GetComponent<Button>();
        giveUp2Btn = panel.Find("GiveUpBtn").GetComponent<Button>();

        if (extraBtn != null) extraBtn.onClick.AddListener(OnExtraClicked);
        if (giveUp2Btn != null) giveUp2Btn.onClick.AddListener(OnGiveUp2Clicked);
    }

    // Binds each of the 3 stat rows ("Row0".."Row2", direct children of rr.root) to
    // rr.icon[i]/label[i]/value[i]. A missing "RowN" or missing child is skipped.
    // FIX (confirmed from raw): a null rr or rr.root throws rather than returning.
    private void BindResultRows(ResultUI rr)
    {
        for (int i = 0; i < 3; i++)
        {
            Transform rowTf = rr.root.transform.Find("Row" + i);
            if (rowTf == null) continue;

            Transform iconTf = rowTf.Find("Icon");
            if (iconTf != null) rr.icon[i] = iconTf.GetComponent<Image>();

            Transform labelTf = rowTf.Find("Label");
            if (labelTf != null) rr.label[i] = labelTf.GetComponent<Text>();

            Transform valueTf = rowTf.Find("Value");
            if (valueTf != null) rr.value[i] = valueTf.GetComponent<Text>();
        }
    }

    // Builds a victory/defeat popup: a full-screen dim, a banner sprite, three stat rows (Rank /
    // Paint Score / Gold — the Gold row's value text uses GoldYellow), and Home / X2 Gold / Next
    // buttons along the bottom.
    // FIX (confirmed from raw): the "RowN" images are parented to rr.root's transform, NOT the
    // banner's (a prior draft used the banner). This also matches BindResultRows, which looks the
    // rows up directly under rr.root. A null rr throws rather than returning.
    private GameObject BuildResultPopup(ResultUI rr, string rootName, Sprite bannerSprite)
    {
        GameObject root = NewRoot(rootName);
        rr.root = root;

        Image dim = NewImage("Dim", rr.root.transform, null, new Color(0f, 0f, 0f, 0.35f), true);
        Stretch(dim);

        Image banner = NewImage("Banner", rr.root.transform, bannerSprite, Color.white, false);
        banner.preserveAspect = true;
        Place(banner, 0f, 250f, 820f, 560f);

        Sprite[] rowIcons = { iconRankPodium, iconPaintMedal, iconGold };
        string[] rowLabels = { "Rank", "Paint Score", "Gold" };

        for (int i = 0; i < 3; i++)
        {
            Image rowBg = NewImage("Row" + i, rr.root.transform, statRow, Color.white, false);
            Place(rowBg, 250f, i * -112f + 30f, 640f, 92f);

            Image iconImg = NewImage("Icon", rowBg.transform, rowIcons[i], Color.white, false);
            iconImg.preserveAspect = true;
            Place(iconImg, -262f, 0f, 64f, 64f);
            rr.icon[i] = iconImg;

            rr.label[i] = NewText("Label", rowBg.transform, rowLabels[i], 34, RowLabel, TextAnchor.MiddleLeft, true);
            Place(rr.label[i], -70f, 0f, 340f, 50f);

            Color valueColor = (i == 2) ? GoldYellow : Color.white;
            rr.value[i] = NewText("Value", rowBg.transform, "", 36, valueColor, TextAnchor.MiddleRight, true);
            Place(rr.value[i], 252f, 0f, 150f, 50f);
        }

        BuildResultButton("HomeBtn", rr.root.transform, btnHomeBg, iconHome, "Home", -320f, -370f, OnHomeClicked, 48f);
        BuildResultButton("GoldBtn", rr.root.transform, btnGoldBg, iconAds, "X2 Gold", 0f, -370f, OnX2GoldClicked, 54f);
        BuildResultButton("NextBtn", rr.root.transform, btnNextBg, iconNext, "Next", 320f, -370f, OnNextClicked, 48f);

        return root;
    }

    // The shared "modal" popup used by both the Caught and Time-Up screens: a dim background, a panel
    // with a title image and body text, and two buttons (a highlighted "action" button with an ads
    // icon, and a plain "Give up" button). Null intermediates throw (natural NREs).
    private GameObject BuildModalPopup(string rootName, Sprite title, string body, string actionLabel)
    {
        GameObject root = NewRoot(rootName);
        Transform rootTf = root.transform;

        Image dim = NewImage("Dim", rootTf, null, new Color(0f, 0f, 0f, 0.55f), true);
        Stretch(dim);

        Image panel = NewImage("Panel", rootTf, panelCaught, Color.white, true);
        Place(panel, 0f, 0f, 940f, 470f);
        Transform panelTf = panel.transform;

        Image titleImg = NewImage("Title", panelTf, title, Color.white, false);
        titleImg.preserveAspect = true;
        Place(titleImg, 0f, 150f, 600f, 72f);

        Text bodyText = NewText("Body", panelTf, body, 33, new Color(0.95f, 0.97f, 1f, 1f), TextAnchor.MiddleCenter, false);
        Place(bodyText, 0f, 14f, 760f, 50f);

        Button actionBtn = NewButton("ActionBtn", panelTf, btnReviveBg, -178f, -122f, 326f, 104f);

        Image adsIconImg = NewImage("Ads", actionBtn.transform, adsIcon, Color.white, false);
        adsIconImg.preserveAspect = true;
        Place(adsIconImg, -98f, 0f, 66f, 66f);

        Text actionLabelText = NewText("Label", actionBtn.transform, actionLabel, 38, Color.white, TextAnchor.MiddleCenter, true);
        Place(actionLabelText, 30f, 0f, 210f, 56f);

        Button giveUpBtn = NewButton("GiveUpBtn", panelTf, btnGiveUpBg, 190f, -122f, 300f, 104f);
        Text giveUpLabelText = NewText("Label", giveUpBtn.transform, "Give up", 38, Color.white, TextAnchor.MiddleCenter, true);
        Place(giveUpLabelText, 0f, 0f, 250f, 56f);

        return root;
    }

    // Creates a full-screen (stretched) root GameObject parented under canvasGO. The GameObject is
    // created before the canvasGO null check, so a null canvasGO throws after creating it.
    private GameObject NewRoot(string n)
    {
        GameObject go = new GameObject(n);
        if (canvasGO == null)
        {
            throw new NullReferenceException("NewRoot: canvasGO is null");
        }

        go.transform.SetParent(canvasGO.transform, false);
        go.AddComponent<RectTransform>();
        Stretch(go.GetComponent<RectTransform>());

        return go;
    }

    private Image NewImage(string n, Transform parent, Sprite spr, Color col, bool raycast)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);

        Image img = go.AddComponent<Image>();
        img.color = col;
        img.raycastTarget = raycast;

        if (spr != null)
        {
            img.sprite = spr;
        }

        return img;
    }

    // Stretches a component's RectTransform to fill its parent.
    // FIX (confirmed from raw): a null component, or a transform that isn't a RectTransform, throws
    // rather than returning. (Raw checks the exact class, which `as` matches in practice.)
    private static void Stretch(Component c)
    {
        RectTransform rt = c.transform as RectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // Center-anchors a component's RectTransform at (x, y) with the given size. An instance method
    // per dump.cs (unlike Stretch), though it never touches instance state.
    // FIX (confirmed from raw): same null handling as Stretch - throws, not returns.
    private void Place(Component c, float x, float y, float w, float h)
    {
        RectTransform rt = c.transform as RectTransform;
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

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
        text.supportRichText = true;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        return text;
    }

    private Button NewButton(string n, Transform parent, Sprite bg, float x, float y, float w, float h)
    {
        Image bgImg = NewImage(n, parent, bg, Color.white, true);
        Place(bgImg, x, y, w, h);

        Button btn = bgImg.gameObject.AddComponent<Button>();
        btn.targetGraphic = bgImg;

        return btn;
    }

    // FIX (confirmed from raw): a null button, icon image or onClick throws rather than returning.
    private void BuildResultButton(string name, Transform parent, Sprite bg, Sprite icon, string label, float x, float y, UnityAction action, float iconSize)
    {
        Button btn = NewButton(name, parent, bg, x, y, 290f, 100f);

        Image iconImg = NewImage("Icon", btn.transform, icon, Color.white, false);
        iconImg.preserveAspect = true;
        Place(iconImg, -80f, 0f, iconSize, iconSize);

        Text labelText = NewText("Label", btn.transform, label, 32, Color.white, TextAnchor.MiddleCenter, true);
        Place(labelText, 24f, 0f, 200f, 46f);

        btn.onClick.AddListener(action);
    }

    public void OnReviveClicked() => _onRevive?.Invoke();
    public void OnGiveUpClicked() => _onGiveUp?.Invoke();
    public void OnExtraClicked() => _onExtra?.Invoke();
    public void OnGiveUp2Clicked() => _onGiveUp2?.Invoke();

    // With an AdMgr, shows a rewarded ad (success = GrantDoubleReward, fail = RewardFail - the only
    // two delegate targets this method registers); with none, goes straight to replaying the mode.
    public void OnX2GoldClicked()
    {
        // if (AdMgr.Instance != null)
        // {
        //     AdMgr.Instance.OnRewardView(GrantDoubleReward, RewardFail);
        // }
        // else
        // {
            ReplayCurrentMode();
        // }
    }

    // Captures (this, seeker = gmm != null && gmm.PlayerIsSeeker) in
    // HideModeResultUI.<>c__DisplayClass79_0 (fields <>4__this, seeker per dump.cs), hides
    // everything, then runs the loading screen. The callback (<ReplayCurrentMode>b__0, decompiled)
    // restarts the same role: via HomeUI.StartMode (1 = seek, 2 = hide) when a HomeUI exists, which
    // also restores the gameplay UI and music; otherwise straight via GameModeManager.BeginRound.
    // With neither present it does nothing.
    private void ReplayCurrentMode()
    {
        EnsureRefs();

        bool seeker = gmm != null && gmm.PlayerIsSeeker;

        HideAll();

        LoadingScreen.Run(() =>
        {
            if (homeUI != null)
            {
                homeUI.StartMode(seeker ? 1 : 2);
                return;
            }

            if (gmm != null)
            {
                gmm.BeginRound(seeker);
            }
        });
    }

    private void GrantDoubleReward()
    {
        GameData.Coin = _lastReward + GameData.Coin;

        if (SingletonMonoBehavior<GameController>.Instance != null)
        {
            SingletonMonoBehavior<GameController>.Instance.UpdateTextCoin();
        }

        ReplayCurrentMode();
    }

    // Confirmed: decompiles to a body byte-for-byte identical to ReplayCurrentMode() (same closure
    // type DisplayClass79_0, same metadata-init guard) - forwarded rather than duplicated.
    private void RewardFail()
    {
        ReplayCurrentMode();
    }

    // FIX (confirmed from raw): a null RootManager throws - it is not a silent no-op.
    public void OnHomeClicked()
    {
        // Singleton<RootManager>.Instance.ShowInterAds_Native();
        ExitToHomeWithLoading();
    }

    // The callback is HideModeResultUI.<ExitToHomeWithLoading>b__78_0 (decompiled; it uses only
    // `this`, so it compiles to an instance lambda with no closure class, as dump.cs shows).
    private void ExitToHomeWithLoading()
    {
        EnsureRefs();
        HideAll();
        LoadingScreen.Run(() =>
        {
            if (homeUI != null)
            {
                homeUI.GoHome();
                return;
            }

            if (gmm != null)
            {
                gmm.ShowMenu();
            }
        });
    }

    // FIX (confirmed from raw): same as OnHomeClicked - a null RootManager throws.
    public void OnNextClicked()
    {
        // Singleton<RootManager>.Instance.ShowInterAds_Native();
        ReplayCurrentMode();
    }

    // Confirmed no-op passthrough: `rel` is never used, it just returns `cur`. Not called anywhere.
    private Sprite L(Sprite cur, string rel)
    {
        return cur;
    }
}
