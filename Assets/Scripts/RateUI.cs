using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// NOTE: Rate-prompt UI with a 5-star picker. Awake/Resolve are confirmed byte-identical (same
// exact-duplicate-method pattern seen elsewhere) - implemented once as Resolve(), with Awake()
// forwarding. Late/Hide are also byte-identical - implemented once as Hide(), with Late()
// forwarding. Reuses GameController.rateButtonGO, already known as a placeholder field name from
// an earlier session's CheckRateGame notes. Confirms GameData.isRate is an int property
// (consistent with GameData.ShowAds also being int rather than bool).
//
// RE-VERIFICATION PASS: found one real, repeated bug — see the two FIXED notes below (in
// Resolve() and MirrorStars()). Everything else, including the tangled star[0]-under-own-
// transform-then-canvas-fallback logic, the closure index-capture math, SetRating's bit-trick
// clamp, and every branch of OpenStore/ShowThanks/ShowHelp/Hide/ResolveCanvas, matched the raw
// pseudocode exactly.
public class RateUI : MonoBehaviour
{
    [SerializeField] private string canvasName = "RateCanvas";
    [SerializeField] private string[] starNames = { "s1", "s2", "s3", "s4", "s5" };
    [SerializeField] private string[] onNames = { "on1", "on2", "on3", "on4", "on5" };
    [SerializeField] private string rateNowButtonName = "ButtonRateNow";
    [SerializeField] private string lateButtonName = "ButtonLate";
    [SerializeField] private string thanksCanvasName = "ThanksCanvas";
    [SerializeField] private string helpCanvasName = "HelpCanvas";
    [SerializeField] private int minStarsToRate = 4;
    [SerializeField] private bool resetOnShow = true;
    [SerializeField] private string androidPackageOverride = "";
    [SerializeField] private string iosAppId = "";
    [SerializeField] private bool logStoreInEditor = true;

    private bool _resolved;
    private GameObject _canvas;
    private GameObject[] _on;
    private int _rating;
    private GameObject _thanks;
    private GameObject _help;

    public int Rating => _rating;

    private void Awake()
    {
        Resolve();
    }

    // Binds each star button (SetRating(index) on click) and the "rate now"/"maybe later"
    // buttons under either this GameObject's own transform (if a star is found directly under
    // it) or, failing that, a scene-wide canvas lookup by name. For each star, also finds an
    // "on" indicator child (by onNames[i], or the star's first child as a fallback) and starts
    // it hidden.
    private void Resolve()
    {
        if (_resolved)
        {
            return;
        }

        Transform root = transform;

        // NOTE: decompiled has no null-guard on starNames - crashes via NullReferenceException
        // if unassigned, matching the abort-on-null pattern used throughout this codebase.
        if (starNames.Length == 0 || FindUnder(root, starNames[0]) == null)
        {
            GameObject canvasGo = FindInScene(canvasName);
            if (canvasGo != null)
            {
                root = canvasGo.transform;
            }
        }

        if (root == null)
        {
            return;
        }

        _canvas = root.gameObject;
        _on = new GameObject[starNames.Length];

        for (int i = 0; i < starNames.Length; i++)
        {
            Transform star = FindUnder(root, starNames[i]);
            if (star == null)
            {
                Debug.LogWarning($"[RateUI] star '{starNames[i]}' not found.");
            }
            else
            {
                Transform onT = null;
                // FIXED: raw does NOT guard against onNames being null here - it's a `break`
                // that falls straight through to the enclosing method's final crash label,
                // aborting the entire Resolve() call (so _resolved never gets set, and the rate-
                // now/late buttons never get wired). Unlike some of the other defensive-looking
                // null checks nearby (e.g. `root != null`, which is self-guaranteed non-null by
                // this method's own control flow), onNames is a separate [SerializeField] that
                // genuinely can end up null independently of starNames - e.g. cleared in the
                // Inspector - even though the .ctor gives it a default array. Letting
                // onNames.Length throw naturally here matches that crash-on-null behavior.
                if (i < onNames.Length)
                {
                    onT = FindUnder(star, onNames[i]);
                }
                if (onT == null && star.childCount > 0)
                {
                    onT = star.GetChild(0);
                }

                if (onT != null)
                {
                    _on[i] = onT.gameObject;
                    // Image onImg = onT.GetComponent<Image>();
                    // if (onImg != null)
                    // {
                    //     // NOTE: decompiled calls this via a vtable-dispatched slot Ghidra
                    //     // couldn't name (generic "_1_Finalize"/"_2_GetHashCode" placeholders),
                    //     // passing a single bool-like 0 argument - a virtual property setter is
                    //     // the natural fit, and Behaviour.enabled is virtual where
                    //     // GameObject.SetActive is not, so read as disabling the indicator
                    //     // Image component by default rather than deactivating its GameObject.
                    //     onImg.enabled = false;
                    // }
                    _on[i].SetActive(false);
                }

                Button starBtn = star.GetComponent<Button>();
                if (starBtn == null)
                {
                    starBtn = star.gameObject.AddComponent<Button>();
                    starBtn.transition = Selectable.Transition.None;
                    Graphic starGraphic = star.GetComponent<Graphic>();
                    if (starGraphic != null)
                    {
                        starBtn.targetGraphic = starGraphic;
                    }
                }

                // NOTE: the click-handler lambda (`<Resolve>b__0`) itself wasn't decompiled -
                // only that it's cached per-instance in a closure capturing (this, star index).
                // Reconstructed as calling SetRating(index) - the clear intent given exactly
                // those captured variables and this being a star button.
                int starIndex = i + 1;
                starBtn.onClick.RemoveAllListeners();
                starBtn.onClick.AddListener(() => SetRating(starIndex));
            }
        }

        WireButton(root, rateNowButtonName, RateNow);
        WireButton(root, lateButtonName, Late);
        _resolved = true;
        SetRating(0);
    }

    private void OnEnable()
    {
        if (_resolved && resetOnShow)
        {
            SetRating(0);
        }
    }

    public void SetRating(int count)
    {
        if (_on == null)
        {
            _rating = Mathf.Clamp(count, 0, 5);
            return;
        }

        _rating = Mathf.Clamp(count, 0, _on.Length);

        for (int i = 0; i < _on.Length; i++)
        {
            if (_on[i] != null)
            {
                _on[i].SetActive(i < _rating);
            }
        }
    }

    // NOTE: 11th occurrence of a cross-cutting name-lookup pattern in this codebase, but a
    // simpler variant restricted to `root`'s own descendants (no GameObject.Find pre-check).
    private static Transform FindUnder(Transform root, string name)
    {
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].name == name)
            {
                return all[i];
            }
        }
        return null;
    }

    // NOTE: 12th occurrence of the full cross-cutting scene-lookup helper (static).
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

    // Only wires the listener if the button has no Inspector-assigned (persistent) listeners
    // already, so it never overrides manually-configured buttons.
    private void WireButton(Transform root, string objName, UnityAction action)
    {
        Transform t = FindUnder(root, objName);
        if (t == null)
        {
            Debug.LogWarning($"[RateUI] button '{objName}' not found.");
            return;
        }

        Button btn = t.GetComponent<Button>();
        if (btn == null)
        {
            Debug.LogWarning($"[RateUI] '{objName}' has no Button.");
            return;
        }

        if (btn.onClick.GetPersistentEventCount() > 0)
        {
            return;
        }

        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(action);
    }

    public void RateNow()
    {
        int rating = _rating;

        if (rating >= minStarsToRate)
        {
            HideRateButton();
            OpenStore();
            ShowThanks();
            return;
        }

        if (rating > 0)
        {
            HideRateButton();
            GameData.isRate = 1;
            ShowHelp();
            return;
        }

        Hide();
    }

    private static void HideRateButton()
    {
        GameController controller = GameController.Instance;

        if (controller == null)
            return;

        if (controller.btnRate == null)
            return;

        controller.btnRate.SetActive(false);
    }

    private void OpenStore()
    {
        string package = string.IsNullOrEmpty(androidPackageOverride) ? Application.identifier : androidPackageOverride;
        string url = "https://play.google.com/store/apps/details?id=" + package;

        if (Application.isEditor && logStoreInEditor)
        {
            Debug.Log("[RateUI] (editor) would open store \u2014 market://details?id=" + package + "  |  " + url);
            return;
        }

        RuntimePlatform platform = Application.platform;
        if (platform == RuntimePlatform.IPhonePlayer)
        {
            if (!string.IsNullOrEmpty(iosAppId))
            {
                url = "itms-apps://itunes.apple.com/app/id" + iosAppId;
            }
        }
        else if (platform == RuntimePlatform.Android)
        {
            url = "market://details?id=" + package;
        }

        Application.OpenURL(url);
        GameData.isRate = 1;
    }

    private void ShowThanks()
    {
        Hide();
        GameObject canvas = ResolveCanvas(ref _thanks, thanksCanvasName);
        if (canvas != null)
        {
            canvas.SetActive(true);
        }
    }

    private void ShowHelp()
    {
        GameObject canvas = ResolveCanvas(ref _help, helpCanvasName);
        if (canvas != null)
        {
            MirrorStars(canvas.transform);
        }
        Hide();
        if (canvas != null)
        {
            canvas.SetActive(true);
        }
    }

    private void Hide()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
    }

    private GameObject ResolveCanvas(ref GameObject cache, string n)
    {
        if (cache == null && !string.IsNullOrEmpty(n))
        {
            cache = FindInScene(n);
        }
        return cache;
    }

    // Mirrors the current rating onto a second copy of the star row (e.g. in the "help" panel).
    private void MirrorStars(Transform root)
    {
        for (int i = 0; i < starNames.Length; i++)
        {
            Transform star = FindUnder(root, starNames[i]);
            Transform onT = null;
            if (star != null)
            {
                // FIXED: same bug as in Resolve() above - raw has no null-guard on onNames here
                // either; it's a `break` that falls through to this method's own crash label.
                if (i < onNames.Length)
                {
                    onT = FindUnder(star, onNames[i]);
                }
                if (onT == null && star.childCount > 0)
                {
                    onT = star.GetChild(0);
                }
            }
            if (onT != null)
            {
                onT.gameObject.SetActive(i < _rating);
            }
        }
    }

    public void Late()
    {
        Hide();
    }

    public void Show()
    {
        if (resetOnShow)
        {
            SetRating(0);
        }
        if (_canvas != null)
        {
            _canvas.SetActive(true);
        }
    }
}