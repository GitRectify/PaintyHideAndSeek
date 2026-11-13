using System;
using UnityEngine;
using UnityEngine.UI;

// NOTE: Map-selection popup. Map index 0 is free; selecting any other map requires watching a
// rewarded ad (via AdMgr.Instance.OnRewardView) before the selection is confirmed and LevelManager
// is updated. Confirms new AdMgr members beyond the previously-known Instance/OnRewardView:
// IsRewardReady (bool getter). Singleton pattern #3 (bare static field) again. 7th occurrence of
// the cross-cutting scene-lookup helper (static, same shape as LevelManager/LoadingScreen's).
public class MapSelectPopup : MonoBehaviour
{
    [SerializeField] private string canvasName = "MapCanvas";
    [SerializeField] private string[] mapNames = { "Map1", "Map2", "Map3", "Map4", "Map5" };
    [SerializeField] private string selectChildName = "Select";
    [SerializeField] private string backButtonName = "BtnExit";

    private GameObject _canvas;
    private Transform[] _maps;
    private GameObject[] _frames;
    private int _selected;
    private Action _onConfirm;
    private Action _onBack;
    private LevelManager _lm;
    private Text _roomName;

    // NOTE: both get_Instance and set_Instance were separately decompiled (not just a bare
    // field), so this is a real property with a public setter — same as LevelManager.Instance
    // and LoadingScreen.Instance in earlier sessions, which I described as a "bare static field"
    // pattern; worth revisiting those as an accessible-property pattern instead for consistency.
    public static MapSelectPopup Instance { get; set; }

    public bool Ready => _canvas != null;

    public int SelectedMap => _selected;

    // NOTE: unlike LevelManager.Awake/LoadingScreen.Awake, this unconditionally overwrites
    // Instance with `this` — no "if (Instance == null)" guard first.
    private void Awake()
    {
        Instance = this;
        _lm = FindAnyObjectByType<LevelManager>();
        Resolve();
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
    }

    public void Show(Action onConfirm, Action onBack) => Show(onConfirm, onBack, null);

    /// <summary>
    /// Online (2026-11-13): <paramref name="lockedPrice"/> gives, per map index, the price in cents
    /// of a map this account has not bought yet, 0 for one it may play. Single Play passes none:
    /// every map is free there.
    /// </summary>
    public void Show(Action onConfirm, Action onBack, Func<int, long> lockedPrice)
    {
        _onConfirm = onConfirm;
        _onBack = onBack;
        _lockedPrice = lockedPrice;
        RefreshLocks();

        if (_canvas == null)
        {
            // NOTE: popup wasn't resolved (not present in this scene) - skip straight to
            // confirming, matching the decompiled fallback exactly.
            onConfirm?.Invoke();
            return;
        }

        _canvas.SetActive(true);
        SelectMap(0);
    }

    public void SelectMap(int i)
    {
        if (_maps == null || _maps.Length == 0)
        {
            return;
        }

        int selected = Mathf.Clamp(i, 0, _maps.Length - 1);
        _selected = selected;

        // NOTE: decompiled has no null-guard on _frames here — if it were null this would
        // crash via NullReferenceException on _frames.Length, matching the abort-on-null
        // pattern used throughout. In practice _frames is always allocated in Resolve()
        // alongside _maps.
        for (int idx = 0; idx < _frames.Length; idx++)
        {
            if (_frames[idx] != null)
            {
                _frames[idx].SetActive(idx == selected);
            }
        }

        _lm?.SetLevel(selected);
        ShowRoomName();
    }

    // The room about to be played, over the Play button: the name the level list gives it.
    private void ShowRoomName()
    {
        if (_roomName == null || _lm == null) return;
        _roomName.text = _lm.CurrentDisplayName;
    }

    // ---- Locks ----
    // Each map card has a badge ("Image", bottom right) made as the rewarded-ad sign for maps 2-5.
    // Single Play's maps are all free, so it is hidden there; online it is a lock on a map not
    // bought yet, with the price under the picture.

    private Func<int, long> _lockedPrice;

    /// <summary>Puts the locks and prices right, for instance once the shop's listing has arrived.</summary>
    public void RefreshLocks()
    {
        if (_maps == null) return;

        for (int i = 0; i < _maps.Length; i++)
        {
            Transform map = _maps[i];
            if (map == null) continue;

            long price = _lockedPrice != null ? _lockedPrice(i) : 0;
            bool locked = price > 0;

            Transform badge = map.Find("Image");
            if (badge != null)
            {
                Image badgeImage = badge.GetComponent<Image>();
                Sprite lockSprite = OnlineUI.LockSprite();
                if (locked && badgeImage != null && lockSprite != null)
                {
                    badgeImage.sprite = lockSprite;
                    badgeImage.preserveAspect = true;
                }
                badge.gameObject.SetActive(locked && lockSprite != null);
            }

            Image picture = map.Find("Map")?.GetComponent<Image>();
            if (picture != null) picture.color = locked ? new Color(0.62f, 0.62f, 0.62f, 1f) : Color.white;

            Text label = PriceLabel(map);
            label.gameObject.SetActive(locked);
            if (locked) label.text = CoinBalance.Format(price) + (price == 100 ? " coin" : " coins");
        }
    }

    /// <summary>The picture of the map at this index, as its card in the picker shows it.</summary>
    public Sprite ThumbnailAt(int index)
    {
        if (_maps == null || index < 0 || index >= _maps.Length || _maps[index] == null) return null;
        Image picture = _maps[index].Find("Map")?.GetComponent<Image>();
        return picture != null ? picture.sprite : null;
    }

    /// <summary>Whether the map at this index is locked for this account (online only).</summary>
    public bool IsLocked(int index) => _lockedPrice != null && _lockedPrice(index) > 0;

    private Text PriceLabel(Transform map)
    {
        Transform existing = map.Find("LockPrice");
        if (existing != null) return existing.GetComponent<Text>();

        var go = new GameObject("LockPrice", typeof(RectTransform));
        go.transform.SetParent(map, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(-24f, -150f);
        rt.sizeDelta = new Vector2(280f, 54f);

        Text text = go.AddComponent<Text>();
        text.font = UIFont.Default;
        text.fontSize = 36;
        text.fontStyle = FontStyle.Bold;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(1f, 0.86f, 0.3f, 1f);
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;

        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new Vector2(2f, -2f);
        return text;
    }

    // A label built just above the scene's Play button, in its panel, so it moves with it.
    private void BuildRoomName()
    {
        Transform play = FindUnder(_canvas.transform, "ButtonPlay");
        if (play == null || play.parent == null) return;

        var go = new GameObject("SelectedRoomName", typeof(RectTransform));
        go.transform.SetParent(play.parent, false);
        var rt = (RectTransform)go.transform;
        var playRt = (RectTransform)play;
        rt.anchorMin = playRt.anchorMin;
        rt.anchorMax = playRt.anchorMax;
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(Mathf.Max(420f, playRt.rect.width + 160f), 56f);
        rt.anchoredPosition = playRt.anchoredPosition + new Vector2(0f, playRt.rect.height * (1f - playRt.pivot.y) + 10f);

        _roomName = go.AddComponent<Text>();
        _roomName.font = UIFont.Default;
        _roomName.fontSize = 40;
        _roomName.fontStyle = FontStyle.Bold;
        _roomName.alignment = TextAnchor.LowerCenter;
        _roomName.color = Color.white;
        _roomName.raycastTarget = false;
        _roomName.horizontalOverflow = HorizontalWrapMode.Overflow;
        _roomName.verticalOverflow = VerticalWrapMode.Overflow;

        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.6f);
        outline.effectDistance = new Vector2(2f, -2f);
    }

    public void Back()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }

        Action onBack = _onBack;
        _onBack = null;
        _onConfirm = null;
        onBack?.Invoke();
    }

    // NOTE: map index 0 needs no ad; indices 1-4 each require AdMgr.Instance.IsRewardReady
    // before proceeding, then route through AdMgr.Instance.OnRewardView with a per-map success
    // callback (each just logs a distinct "chosen" message and calls Proceed) and a shared
    // RewardFail callback that is a genuine no-op (ad skipped/failed = stay on the popup).
    public void Confirm()
    {
        int selected = _selected;
        // Action onRewardSuccess;

        switch (selected)
        {
            case 1:
                ConfirmMap2Chosen();
                // onRewardSuccess = ConfirmMap2Chosen;
                break;
            case 2:
                ConfirmMap3Chosen();
                // onRewardSuccess = ConfirmMap3Chosen;
                break;
            case 3:
                ConfirmMap4Chosen();
                // onRewardSuccess = ConfirmMap4Chosen;
                break;
            case 4:
                ConfirmMap5Chosen();
                // onRewardSuccess = ConfirmMap5Chosen;
                break;
            default:
                // selected == 0, or any out-of-range value - no ad gate, proceed immediately.
                if (_canvas != null)
                {
                    _canvas.SetActive(false);
                }
                Proceed();
                return;
        }

        // if (AdMgr.Instance == null)
        // {
        //     throw new NullReferenceException("AdMgr instance not available");
        // }
        // if (!AdMgr.Instance.IsRewardReady)
        // {
        //     return;
        // }

        // AdMgr.Instance.OnRewardView(onRewardSuccess, RewardFail);
    }

    private void Proceed()
    {
        Action onConfirm = _onConfirm;
        _onConfirm = null;
        onConfirm?.Invoke();
    }

    private void RewardFail()
    {
        // Intentional no-op (confirmed empty body) - if the rewarded ad fails or is skipped,
        // the popup simply stays open with the selection unconfirmed.
    }

    // NOTE: log text is "Map 2/3/4/5" (1-indexed, matching _selected+1) but the room name in
    // parentheses is "(Room3)" for BOTH Map 2 and Map 3 (selected 1 and 2) - transcribed exactly
    // as decompiled; not an artifact, this is what the string literals actually say.
    private void ConfirmMap2Chosen()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
        Debug.Log("[MapSelect] Map 2 (Room3) chosen.");
        Proceed();
    }

    private void ConfirmMap3Chosen()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
        Debug.Log("[MapSelect] Map 3 (Room3) chosen.");
        Proceed();
    }

    private void ConfirmMap4Chosen()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
        Debug.Log("[MapSelect] Map 4 (Room4) chosen.");
        Proceed();
    }

    private void ConfirmMap5Chosen()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
        Debug.Log("[MapSelect] Map 5 (Room5) chosen.");
        Proceed();
    }

    private void Resolve()
    {
        _canvas = FindInScene(canvasName);
        if (_canvas == null)
        {
            Debug.LogWarning($"[MapSelect] '{canvasName}' not found.");
            return;
        }

        // NOTE: decompiled has no null-guard on mapNames here — if it were null this crashes
        // via the shared abort-on-null pattern (unreachable in practice given the ctor default).
        int count = mapNames.Length;
        _maps = new Transform[count];
        _frames = new GameObject[count];

        for (int i = 0; i < count; i++)
        {
            Transform mapTransform = FindUnder(_canvas.transform, mapNames[i]);
            if (mapTransform == null)
            {
                Debug.LogWarning($"[MapSelect] '{mapNames[i]}' not found under {canvasName}.");
            }
            else
            {
                _maps[i] = mapTransform;
                Transform selectChild = mapTransform.Find(selectChildName);
                if (selectChild != null)
                {
                    _frames[i] = selectChild.gameObject;
                }
            }
        }

        BuildRoomName();

        Transform backTransform = FindUnder(_canvas.transform, backButtonName);
        if (backTransform == null)
        {
            return;
        }

        Button backButton = backTransform.GetComponent<Button>();
        if (backButton == null)
        {
            return;
        }

        backButton.onClick.RemoveAllListeners();
        backButton.onClick.AddListener(Back);
    }

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

    // NOTE: unlike FindInScene (Find + fallback scan-all-Transforms-in-loaded-scenes), this
    // searches only within a given root's own descendants via GetComponentsInChildren.
    private static Transform FindUnder(Transform root, string name)
    {
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        foreach (Transform t in all)
        {
            if (t.name == name)
            {
                return t;
            }
        }
        return null;
    }
}