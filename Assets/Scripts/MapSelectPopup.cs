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
        _lm = FindFirstObjectByType<LevelManager>();
        Resolve();
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
    }

    public void Show(Action onConfirm, Action onBack)
    {
        _onConfirm = onConfirm;
        _onBack = onBack;

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
        Action onRewardSuccess;

        switch (selected)
        {
            case 1:
                onRewardSuccess = ConfirmMap2Chosen;
                break;
            case 2:
                onRewardSuccess = ConfirmMap3Chosen;
                break;
            case 3:
                onRewardSuccess = ConfirmMap4Chosen;
                break;
            case 4:
                onRewardSuccess = ConfirmMap5Chosen;
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

        if (AdMgr.Instance == null)
        {
            throw new NullReferenceException("AdMgr instance not available");
        }
        if (!AdMgr.Instance.IsRewardReady)
        {
            return;
        }

        AdMgr.Instance.OnRewardView(onRewardSuccess, RewardFail);
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