using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
// UnityEngine.Object and System.Object are both in scope (using System + using UnityEngine), so a bare
// `Object.` is ambiguous (CS0104). This alias makes every `Object.X` mean UnityEngine.Object.
using Object = UnityEngine.Object;

public class HideModeResultUI : MonoBehaviour
{
    // Confirmed via the static constructor (decoded the packed Color hex constants). RowLabelTint is
    // used for the stat-row label text color; GoldColor is used for the "Gold" row's value text.
    private static readonly Color RowLabelTint;
    private static readonly Color GoldColor;

    static HideModeResultUI()
    {
        RowLabelTint = new Color(0.92f, 0.94f, 1f, 1f);
        GoldColor = new Color(1f, 0.86f, 0.3f, 1f);
    }

    [Serializable]
    public class ResultUI
    {
        public GameObject root;
        public Image[] icon;
        public Text[] label;
        public Text[] value;
    }

    public Vector2Int winCoinRange = new Vector2Int(50, 100);
    public Vector2Int loseCoinRange = new Vector2Int(30, 50);

    public ResultUI victory;
    public ResultUI defeat;

    public Sprite panelCaught;
    public Sprite titleCaught;
    public Sprite titleTimeUp;
    public Sprite btnReviveBg;
    public Sprite btnGiveUpBg;
    public Sprite adsIcon;
    public Sprite bannerVictory;
    public Sprite bannerDefeat;
    public Sprite iconRankPodium;
    public Sprite iconPaintMedal;
    public Sprite statRow;
    public Sprite iconGold;
    public Sprite btnHomeBg;
    public Sprite btnGoldBg;
    public Sprite btnNextBg;
    public Sprite iconHome;
    public Sprite iconNext;
    public Sprite iconAds;

    private int _lastReward;
    private HomeUI homeUI;
    private GameModeManager gmm;
    private GameObject canvasGO;
    private Font font;
    private GameObject caughtRoot;
    private GameObject timeUpRoot;
    private Button reviveBtn;
    private Button giveUpBtn;
    private Button extraBtn;
    private Button giveUp2Btn;
    private Action _onRevive;
    private Action _onGiveUp;
    private Action _onExtra;
    private Action _onGiveUp2;

    private Camera _fxCam;
    private RawImage _fxImg;
    private GameObject _fxEffect;
    private RenderTexture _fxRT;

    public HideModeResultUI()
    {
        victory = new ResultUI();
        defeat = new ResultUI();
    }

    // Rolls a random coin reward from winCoinRange/loseCoinRange, credits it to GameData.Coin, and
    // refreshes the coin HUD via GameController if present.
    private int RollAndCredit(bool win)
    {
        Vector2Int range = win ? winCoinRange : loseCoinRange;
        int lo = Mathf.Min(range.x, range.y);
        int hi = Mathf.Max(range.x, range.y);

        _lastReward = UnityEngine.Random.Range(lo, hi + 1);

        GameData.Coin = _lastReward + GameData.Coin;

        GameController controller = SingletonMonoBehavior<GameController>.Instance;
        if (controller != null)
        {
            controller.UpdateTextCoin();
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

    // NOTE: "AuthorUI" (further down) decompiles to byte-for-byte identical code to this method,
    // right down to sharing the same static-init guard variable — strong evidence they're really the
    // same source method under two names (or AuthorUI is a thin alias). Implemented once here;
    // AuthorUI() just forwards to it.
    //
    // FIX (confirmed via hex decode): the packed CanvasScaler.referenceResolution constant decodes to
    // x=1920, y=1080 (per the established low32=x/high32=y convention) - a prior draft had these
    // swapped as (1080, 1920).
    //
    // FIX (confirmed): raw throws if SceneUI.UIRoot() returns null - a prior draft's
    // `if (uiRoot == null) return;` silently skipped that case instead.
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
        canvasGO = existing != null ? existing.gameObject : SceneUI.GetOrCreate("HideResultCanvas", null, out bool _created);

        Canvas canvas = SceneUI.GetOrAdd<Canvas>(canvasGO);
        if (canvas == null) return;

        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 40;

        CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
        if (scaler == null) return;

        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);

        EnsureCaught();
        EnsureTimeUp();
        EnsureResult(victory, "VictoryPopup", bannerVictory);
        EnsureResult(defeat, "DefeatPopup", bannerDefeat);
    }

    public void HideAll()
    {
        if (caughtRoot != null) caughtRoot.SetActive(false);
        if (timeUpRoot != null) timeUpRoot.SetActive(false);
        if (victory != null && victory.root != null) victory.root.SetActive(false);
        if (defeat != null && defeat.root != null) defeat.root.SetActive(false);

        PlayVictoryFx(false);
    }

    public void ShowCaught(Action onRevive, Action onGiveUp)
    {
        EnsureBuilt();
        _onRevive = onRevive;
        _onGiveUp = onGiveUp;
        ShowOnly(caughtRoot);
    }

    private void ShowOnly(GameObject root)
    {
        RootManager.Instance?.SetNumber(2);

        if (caughtRoot != null) caughtRoot.SetActive(root == caughtRoot);
        if (timeUpRoot != null) timeUpRoot.SetActive(root == timeUpRoot);
        if (victory != null && victory.root != null) victory.root.SetActive(root == victory.root);
        if (defeat != null && defeat.root != null) defeat.root.SetActive(root == defeat.root);

        if (canvasGO == null) return;

        canvasGO.SetActive(true);

        if (victory == null) return;

        if (root == victory.root)
        {
            AudioManager.Win();
        }
        else if (defeat != null && root == defeat.root)
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

    // NOTE: survivalSecs/timeLeftSecs (here and in the three sibling ShowXxx methods below) are
    // decompiled as being loaded into the same register slot RollAndCredit's unused trailing
    // "MethodInfo" parameter occupies, and RollAndCredit never reads that slot. That means, as
    // decompiled, this parameter has no observable effect on behavior — kept in the signature since
    // external callers pass it, but it isn't wired to anything in this dump.
    public void ShowVictory(string rank, int survivalSecs, int paintScore)
    {
        string score = paintScore.ToString();
        int gold = RollAndCredit(true);
        FillResult(victory, rank, score, gold);
    }

    private void FillResult(ResultUI rr, string rank, string score, int gold)
    {
        EnsureBuilt();

        if (rr == null || rr.value == null) return;

        if (rr.value.Length > 0 && rr.value[0] != null)
        {
            rr.value[0].text = rank;
        }

        if (rr.value.Length > 1 && rr.value[1] != null)
        {
            rr.value[1].text = score;
        }

        if (rr.value.Length > 2 && rr.value[2] != null)
        {
            rr.value[2].text = "+" + gold;
        }

        ShowOnly(rr.root);
    }

    public void ShowDefeat(string rank, int timeLeftSecs, int found)
    {
        string score = found.ToString();
        int gold = RollAndCredit(false);
        FillResult(defeat, rank, score, gold);
    }

    public void ShowVictorySeek(string rank, int timeLeftSecs, int found)
    {
        string score = found.ToString();
        int gold = RollAndCredit(true);
        FillResult(victory, rank, score, gold);
    }

    public void ShowDefeatSeek(string rank, int timeLeftSecs, int found)
    {
        string score = found.ToString();
        int gold = RollAndCredit(false);
        FillResult(defeat, rank, score, gold);
    }

    // Shows/hides a secondary camera that renders into a RenderTexture displayed on a RawImage, plus
    // a particle-effect GameObject — used for the victory celebration effect. Resolves its three
    // targets (by scene-wide name search) lazily, on first use.
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

            ParticleSystem[] systems = _fxEffect.GetComponentsInChildren<ParticleSystem>(true);
            if (systems != null)
            {
                foreach (ParticleSystem ps in systems)
                {
                    if (ps == null) return;
                    ps.Clear(true);
                    ps.Play(true);
                }
            }
        }
        else
        {
            _fxCam.enabled = false;

            Transform imgParent = _fxImg.transform.parent;
            if (imgParent != null) imgParent.gameObject.SetActive(false);

            ParticleSystem[] systems = _fxEffect.GetComponentsInChildren<ParticleSystem>(true);
            if (systems != null)
            {
                foreach (ParticleSystem ps in systems)
                {
                    if (ps == null) return;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }
    }

    // Scene-wide named-object lookup (same "GameObject.Find, then fall back to scanning every loaded
    // Transform and filtering to ones actually in a valid scene" pattern used elsewhere in this
    // project, e.g. GameModeManager.FindInSceneByName).
    //
    // FIX (confirmed): raw explicitly throws in three cases here that a prior draft instead handled
    // silently - FindObjectsOfTypeAll<Transform>() returning null, a loop element being null, and
    // t.gameObject being null. Same fix as GameModeManager.FindInSceneByName earlier this session.
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

    // See the NOTE on EnsureBuilt() above — identical decompiled body, forwarded here.
    private void AuthorUI()
    {
        EnsureBuilt();
    }

    // NOTE: as decompiled, this method loads 18 sprite-path-looking string literals in its static-init
    // block but never actually reads any of them in the body — every statement below just re-assigns
    // each sprite field to its own current value (a GC write-barrier touch, functionally a no-op).
    // This strongly looks like dead code left over from what was probably originally a
    // Resources.Load<Sprite>(path) call per field, optimized away because these are Inspector-assigned
    // serialized fields already. Implemented faithfully as a no-op; the 18 literal path strings aren't
    // reproduced here since nothing in this dump uses them.
    private void ResolveSprites()
    {
        // Intentionally empty — see note above.
    }

    // FIX (confirmed): raw throws if canvasGO == null here (falls through the
    // `canvasGO != null && get_transform(canvasGO) != null` guard to the function's shared throw) -
    // a prior draft's `if (canvasGO == null) return;` silently skipped that case instead.
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

    // FIX (confirmed): same as EnsureCaught above - raw throws on canvasGO == null.
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

    // FIX (confirmed): same as EnsureCaught above - raw throws on canvasGO == null.
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

        if (rr != null)
        {
            rr.root = existing.gameObject;
            BindResultRows(rr);
        }
    }

    private void BuildCaught()
    {
        caughtRoot = BuildModalPopup("CaughtPopup", titleCaught, "Watch an ad to get one more chance.", "Revive");

        Transform panel = caughtRoot != null ? caughtRoot.transform.Find("Panel") : null;
        Transform actionBtnTf = panel != null ? panel.Find("ActionBtn") : null;
        if (actionBtnTf == null) return;

        reviveBtn = actionBtnTf.GetComponent<Button>();

        Transform giveUpBtnTf = panel.Find("GiveUpBtn");
        if (giveUpBtnTf == null) return;

        giveUpBtn = giveUpBtnTf.GetComponent<Button>();

        if (reviveBtn != null) reviveBtn.onClick.AddListener(OnReviveClicked);
        if (giveUpBtn != null) giveUpBtn.onClick.AddListener(OnGiveUpClicked);
    }

    private void BuildTimeUp()
    {
        timeUpRoot = BuildModalPopup("TimeUpPopup", titleTimeUp, "Watch an ad to get 30 extra seconds.", "+30s");

        Transform panel = timeUpRoot != null ? timeUpRoot.transform.Find("Panel") : null;
        Transform actionBtnTf = panel != null ? panel.Find("ActionBtn") : null;
        if (actionBtnTf == null) return;

        extraBtn = actionBtnTf.GetComponent<Button>();

        Transform giveUpBtnTf = panel.Find("GiveUpBtn");
        if (giveUpBtnTf == null) return;

        giveUp2Btn = giveUpBtnTf.GetComponent<Button>();

        if (extraBtn != null) extraBtn.onClick.AddListener(OnExtraClicked);
        if (giveUp2Btn != null) giveUp2Btn.onClick.AddListener(OnGiveUp2Clicked);
    }

    // Binds each of the 3 stat rows ("Row0".."Row2") under rr.root to rr.icon[i]/label[i]/value[i].
    // A missing "RowN" is skipped (that index is simply left unbound) rather than aborting the loop.
    private void BindResultRows(ResultUI rr)
    {
        if (rr == null) return;

        if (rr.icon == null || rr.icon.Length < 3)
            rr.icon = new Image[3];

        if (rr.label == null || rr.label.Length < 3)
            rr.label = new Text[3];

        if (rr.value == null || rr.value.Length < 3)
            rr.value = new Text[3];

        for (int i = 0; i < 3; i++)
        {
            if (rr.root == null) return;

            Transform rowTf = rr.root.transform.Find("Row" + i);
            if (rowTf == null) continue;

            Transform iconTf = rowTf.Find("Icon");
            if (iconTf != null)
                rr.icon[i] = iconTf.GetComponent<Image>();

            Transform labelTf = rowTf.Find("Label");
            if (labelTf != null)
                rr.label[i] = labelTf.GetComponent<Text>();

            Transform valueTf = rowTf.Find("Value");
            if (valueTf != null)
                rr.value[i] = valueTf.GetComponent<Text>();
        }
    }

    // Builds a victory/defeat popup: a full-screen dim, a banner sprite, three stat rows (Rank /
    // Paint Score / Gold — the Gold row's value text uses GoldColor instead of white), and Home /
    // X2 Gold / Next buttons along the bottom.
    private GameObject BuildResultPopup(ResultUI rr, string rootName, Sprite bannerSprite)
    {
        GameObject root = NewRoot(rootName);
        if (rr == null) return root;

        rr.root = root;
        if (rr.root == null) return root;

        Transform rootTf = rr.root.transform;

        Image dim = NewImage("Dim", rootTf, null, new Color(0f, 0f, 0f, 0.35f), true);
        Stretch(dim);

        Image banner = NewImage("Banner", rootTf, bannerSprite, Color.white, false);
        banner.preserveAspect = true;
        Place(banner, 0f, 250f, 820f, 560f);

        Sprite[] rowIcons = { iconRankPodium, iconPaintMedal, iconGold };
        string[] rowLabels = { "Rank", "Paint Score", "Gold" };

        Transform bannerTf = banner.transform;

        for (int i = 0; i < 3; i++)
        {
            Image rowBg = NewImage("Row" + i, bannerTf, statRow, Color.white, false);
            Place(rowBg, 250f, i * -112f + 30f, 640f, 92f);

            Transform rowTf = rowBg.transform;

            Image iconImg = NewImage("Icon", rowTf, rowIcons[i], Color.white, false);
            iconImg.preserveAspect = true;
            Place(iconImg, -262f, 0f, 64f, 64f);
            rr.icon[i] = iconImg;

            Text labelText = NewText("Label", rowTf, rowLabels[i], 34, RowLabelTint, (int)TextAnchor.MiddleLeft, true);
            Place(labelText, -70f, 0f, 340f, 50f);
            rr.label[i] = labelText;

            Color valueColor = (i == 2) ? GoldColor : Color.white;
            Text valueText = NewText("Value", rowTf, "", 36, valueColor, (int)TextAnchor.MiddleRight, true);
            Place(valueText, 252f, 0f, 150f, 50f);
            rr.value[i] = valueText;
        }

        BuildResultButton("HomeBtn", rootTf, btnHomeBg, iconHome, "Home", -320f, -370f, OnHomeClicked, 48f);
        BuildResultButton("GoldBtn", rootTf, btnGoldBg, iconAds, "X2 Gold", 0f, -370f, OnX2GoldClicked, 54f);
        BuildResultButton("NextBtn", rootTf, btnNextBg, iconNext, "Next", 320f, -370f, OnNextClicked, 48f);

        return root;
    }

    // Builds the shared "modal" popup shape used by both the Caught and Time-Up screens: a dim
    // background, a panel with a title image and body text, and two buttons (a highlighted "action"
    // button with an ads icon, and a plain "give up" button).
    private GameObject BuildModalPopup(string rootName, Sprite title, string body, string actionLabel)
    {
        GameObject root = NewRoot(rootName);
        if (root == null) return null;

        Transform rootTf = root.transform;

        Image dim = NewImage("Dim", rootTf, null, new Color(0f, 0f, 0f, 0.55f), true);
        Stretch(dim);

        Image panel = NewImage("Panel", rootTf, panelCaught, Color.white, true);
        Place(panel, 0f, 0f, 940f, 470f);

        Transform panelTf = panel.transform;

        Image titleImg = NewImage("Title", panelTf, title, Color.white, false);
        titleImg.preserveAspect = true;
        Place(titleImg, 0f, 150f, 600f, 72f);

        Text bodyText = NewText("Body", panelTf, body, 33, new Color(0.95f, 0.97f, 1f, 1f), (int)TextAnchor.MiddleCenter, false);
        Place(bodyText, 0f, 14f, 760f, 50f);

        Button actionBtn = NewButton("ActionBtn", panelTf, btnReviveBg, -178f, -122f, 326f, 104f);
        Transform actionBtnTf = actionBtn.transform;

        Image adsIconImg = NewImage("Ads", actionBtnTf, adsIcon, Color.white, false);
        adsIconImg.preserveAspect = true;
        Place(adsIconImg, -98f, 0f, 66f, 66f);

        Text actionLabelText = NewText("Label", actionBtnTf, actionLabel, 38, Color.white, (int)TextAnchor.MiddleCenter, true);
        Place(actionLabelText, 30f, 0f, 210f, 56f);

        Button giveUpBtn = NewButton("GiveUpBtn", panelTf, btnGiveUpBg, 190f, -122f, 300f, 104f);
        Text giveUpLabelText = NewText("Label", giveUpBtn.transform, "Give up", 38, Color.white, (int)TextAnchor.MiddleCenter, true);
        Place(giveUpLabelText, 0f, 0f, 250f, 56f);

        return root;
    }

    // Creates a full-screen (stretched) root GameObject parented under canvasGO.
    //
    // FIX (confirmed): raw throws if canvasGO == null (there is no path that returns an unparented,
    // RectTransform-less GameObject) - a prior draft's `if (canvasGO == null) return go;` returned
    // exactly that malformed object instead, which would then fail confusingly wherever the caller
    // (BuildModalPopup, BuildResultPopup) next treats it as having a RectTransform.
    private GameObject NewRoot(string n)
    {
        GameObject go = new GameObject(n);
        if (canvasGO == null)
        {
            throw new NullReferenceException("NewRoot: canvasGO is null");
        }

        go.transform.SetParent(canvasGO.transform, false);
        go.AddComponent<RectTransform>();

        RectTransform rt = go.GetComponent<RectTransform>();
        Stretch(rt);

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

    // Stretches a component's RectTransform to fill its parent (anchorMin 0,0 / anchorMax 1,1 /
    // offsets zeroed).
    private static void Stretch(Component c)
    {
        if (c == null) return;

        RectTransform rt = c.transform as RectTransform;
        if (rt == null) return;

        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // Center-anchors a component's RectTransform at (x, y) with the given size.
    private static void Place(Component c, float x, float y, float w, float h)
    {
        if (c == null) return;

        RectTransform rt = c.transform as RectTransform;
        if (rt == null) return;

        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private Text NewText(string n, Transform parent, string content, int size, Color col, int anchor, bool bold)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);

        Text text = go.AddComponent<Text>();
        text.font = font;
        text.text = content;
        text.fontSize = size;
        text.color = col;
        text.alignment = (TextAnchor)anchor;
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

    private void BuildResultButton(string name, Transform parent, Sprite bg, Sprite icon, string label, float x, float y, UnityAction action, float iconSize)
    {
        Button btn = NewButton(name, parent, bg, x, y, 290f, 100f);
        if (btn == null) return;

        Transform btnTf = btn.transform;

        Image iconImg = NewImage("Icon", btnTf, icon, Color.white, false);
        if (iconImg == null) return;

        iconImg.preserveAspect = true;
        Place(iconImg, -80f, 0f, iconSize, iconSize);

        Text labelText = NewText("Label", btnTf, label, 32, Color.white, (int)TextAnchor.MiddleCenter, true);
        Place(labelText, 24f, 0f, 200f, 46f);

        if (btn.onClick != null)
        {
            btn.onClick.AddListener(action);
        }
    }

    private void OnReviveClicked() => _onRevive?.Invoke();
    private void OnGiveUpClicked() => _onGiveUp?.Invoke();
    private void OnExtraClicked() => _onExtra?.Invoke();
    private void OnGiveUp2Clicked() => _onGiveUp2?.Invoke();

    private void OnX2GoldClicked()
    {
        // NOTE: AdMgr is accessed here via a bare static-field read (**(AdMgr_TypeInfo+0xb8)), not
        // through the SingletonMonoBehavior<T> base class GameController/RootManager elsewhere in this
        // project use — AdMgr appears to have its own, simpler static Instance field.
        if (AdMgr.Instance != null)
        {
            AdMgr.Instance.OnRewardView(GrantDoubleReward, RewardFail);
        }
        else
        {
            ReplayCurrentMode();
        }
    }

    // NOTE: the closure captured here (HideModeResultUI instance + a "was seeker" bool taken from
    // gmm.PlayerIsSeeker) is passed to LoadingScreen.Run as the onComplete callback, but that
    // callback's own body (<ReplayCurrentMode>b__0) is referenced by name in the static-init table and
    // NOT included in this dump — so what actually happens once loading finishes (almost certainly
    // restarting the round, possibly via gmm.BeginRound(wasSeeker) or similar) isn't decompiled here.
    // Paste that lambda's body to complete this.
    private void ReplayCurrentMode()
    {
        EnsureRefs();

        bool wasSeeker = gmm != null && gmm.PlayerIsSeeker;

        HideAll();

        LoadingScreen.Run(() =>
        {
            // TODO: not decompiled — see NOTE above. Captured state available here: wasSeeker.
        });
    }

    private void GrantDoubleReward()
    {
        GameData.Coin = _lastReward + GameData.Coin;

        GameController controller = SingletonMonoBehavior<GameController>.Instance;
        if (controller != null)
        {
            controller.UpdateTextCoin();
        }

        ReplayCurrentMode();
    }

    // NOTE: decompiles to a body byte-for-byte identical to ReplayCurrentMode() (same closure type,
    // same static-init guard) — implemented as a forward rather than duplicating it.
    private void RewardFail()
    {
        ReplayCurrentMode();
    }

    private void OnHomeClicked()
    {
        // NOTE: RootManager is resolved via a distinct "Singleton<T>" base class here, not the
        // "SingletonMonoBehavior<T>" base GameController uses elsewhere in this project — kept as a
        // separate type to reflect that distinction rather than assuming they're the same pattern.
        if (RootManager.Instance != null)
        {
            RootManager.Instance.ShowInterAds_Native();
            ExitToHomeWithLoading();
        }
    }

    private void ExitToHomeWithLoading()
    {
        EnsureRefs();
        HideAll();
        LoadingScreen.Run(ExitToHomeCallback);
    }

    // This is the <>b__78_0 lambda referenced by ExitToHomeWithLoading's static-init table — its body
    // is included in this dump (unlike ReplayCurrentMode's onComplete lambda above).
    private void ExitToHomeCallback()
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
    }

    private void OnNextClicked()
    {
        if (RootManager.Instance != null)
        {
            RootManager.Instance.ShowInterAds_Native();
            ReplayCurrentMode();
        }
    }

    // NOTE: a no-op passthrough as decompiled — `rel` is never used, it just returns `cur` unchanged.
    // Not called anywhere else in this dump; likely a stub for a sprite-path-relative lookup that was
    // never finished, or was simplified away.
    private Sprite L(Sprite cur, string rel)
    {
        return cur;
    }
}
