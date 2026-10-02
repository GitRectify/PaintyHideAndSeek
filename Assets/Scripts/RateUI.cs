using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output. Fields (names, order, attributes,
// offsets) and method accessibility match dump.cs (TypeDefIndex 9709). All string literals are
// confirmed against Dumpstringliteral.json.
//
// Decoded from libil2cpp.so with capstone:
//  - Awake (RVA 0x227B250) is a single "b Resolve" and Late (0x227C5A4) a single "b Hide"; Ghidra
//    printed Resolve's / Hide's whole body under their names.
//  - <>c__DisplayClass22_0 (stars 0x10, this 0x18).<Resolve>b__0 = SetRating(stars).
//  - The Image virtual call on each "on" overlay is vtable slot 25 = raycastTarget = false.
//  - HideRateButton's "x[5].monitor" is GameController + 0x80 = btnRate; the Button field Ghidra
//    printed as "[10].fields.m_CachedPtr" / "[1].klass" is Button + 0x100 = onClick.
//
// Convention: where raw jumps to the NullReferenceException stub, the C# just dereferences
// naturally; explicit null checks are kept only where raw really skips.
public class RateUI : MonoBehaviour
{
    [Header("Scene names")]
    [Tooltip("Root popup canvas — the object closed on Late / low-rating Rate Now. Auto-detected; blank = this GameObject.")]
    public string canvasName = "RateCanvas";
    [Tooltip("The 5 clickable star objects, low → high.")]
    public string[] starNames = { "s1", "s2", "s3", "s4", "s5" };
    [Tooltip("The 5 'filled' overlay objects (a child of each star), low → high.")]
    public string[] onNames = { "on1", "on2", "on3", "on4", "on5" };
    public string rateNowButtonName = "ButtonRateNow";
    public string lateButtonName = "ButtonLate";
    [Tooltip("Popup shown after Rate Now at minStarsToRate or above. Blank = skip.")]
    public string thanksCanvasName = "ThanksCanvas";
    [Tooltip("Feedback popup shown after Rate Now below minStarsToRate. Blank = skip (just close).")]
    public string helpCanvasName = "HelpCanvas";

    [Header("Rating")]
    [Tooltip("Minimum lit stars for Rate Now to open the store. Below this, Rate Now only closes the popup.")]
    public int minStarsToRate = 4;
    [Tooltip("Reset the stars to 0 each time the popup becomes visible.")]
    public bool resetOnShow = true;

    [Header("Store link")]
    [Tooltip("Android package id for the store URL. Blank = Application.identifier (this app).")]
    public string androidPackageOverride = "";
    [Tooltip("iOS App Store numeric id (e.g. 1234567890). Blank = web Play Store fallback.")]
    public string iosAppId = "";
    [Tooltip("In the Editor, log the store URL instead of actually launching a browser.")]
    public bool logStoreInEditor = true;
    private GameObject _canvas;
    private GameObject[] _on;
    private int _rating;
    private bool _resolved;
    private GameObject _thanks;
    private GameObject _help;

    public int Rating => _rating;

    private void Awake()
    {
        Resolve();
    }

    private void OnEnable()
    {
        if (_resolved && resetOnShow)
        {
            SetRating(0);
        }
    }

    // Finds the popup root (this object if it holds the stars, else the canvasName object), makes
    // each star clickable (setting that many stars) with its "on" overlay (named, else the first
    // child), and wires Rate Now / Late.
    private void Resolve()
    {
        if (_resolved) return;
        Transform root = transform;
        if (starNames.Length == 0 || FindUnder(root, starNames[0]) == null)
        {
            GameObject canvasGO = FindInScene(canvasName);
            if (canvasGO != null)
            {
                root = canvasGO.transform;
            }
        }
        _canvas = root.gameObject;
        _on = new GameObject[starNames.Length];
        for (int i = 0; i < starNames.Length; i++)
        {
            Transform star = FindUnder(root, starNames[i]);
            if (star == null)
            {
                Debug.LogWarning("[RateUI] star '" + starNames[i] + "' not found.");
                continue;
            }
            Transform on = i < onNames.Length ? FindUnder(star, onNames[i]) : null;
            if (on == null && star.childCount > 0)
            {
                on = star.GetChild(0);
            }
            if (on != null)
            {
                _on[i] = on.gameObject;
                Image img = on.GetComponent<Image>();
                if (img != null)
                {
                    img.raycastTarget = false;
                }
            }
            Button btn = star.GetComponent<Button>();
            if (btn == null)
            {
                btn = star.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                Graphic g = star.GetComponent<Graphic>();
                if (g != null)
                {
                    btn.targetGraphic = g;
                }
            }
            int stars = i + 1;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => SetRating(stars));
        }
        WireButton(root, rateNowButtonName, RateNow);
        WireButton(root, lateButtonName, Late);
        _resolved = true;
        SetRating(0);
    }

    // Adds the listener only when the button has no persistent (Inspector) listeners.
    private void WireButton(Transform root, string objName, UnityAction action)
    {
        Transform t = FindUnder(root, objName);
        if (t == null)
        {
            Debug.LogWarning("[RateUI] button '" + objName + "' not found.");
            return;
        }
        Button b = t.GetComponent<Button>();
        if (b == null)
        {
            Debug.LogWarning("[RateUI] '" + objName + "' has no Button.");
            return;
        }
        if (b.onClick.GetPersistentEventCount() > 0) return;
        b.onClick.RemoveAllListeners();
        b.onClick.AddListener(action);
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

    // minStarsToRate or more: store + thanks. Fewer (but some): counts as rated, feedback popup.
    // None: just close.
    public void RateNow()
    {
        if (_rating >= minStarsToRate)
        {
            HideRateButton();
            OpenStore();
            ShowThanks();
            return;
        }
        if (_rating > 0)
        {
            HideRateButton();
            GameData.isRate = 1;
            ShowHelp();
            return;
        }
        Hide();
    }

    public void ShowThanks()
    {
        Hide();
        GameObject thanks = ResolveCanvas(ref _thanks, thanksCanvasName);
        if (thanks != null)
        {
            thanks.SetActive(true);
        }
    }

    // The feedback popup shows the same stars that were picked.
    public void ShowHelp()
    {
        GameObject help = ResolveCanvas(ref _help, helpCanvasName);
        if (help != null)
        {
            MirrorStars(help.transform);
        }
        Hide();
        if (help != null)
        {
            help.SetActive(true);
        }
    }

    private void MirrorStars(Transform root)
    {
        for (int i = 0; i < starNames.Length; i++)
        {
            Transform star = FindUnder(root, starNames[i]);
            Transform on = null;
            if (star != null)
            {
                on = i < onNames.Length ? FindUnder(star, onNames[i]) : null;
            }
            if (on == null && star != null && star.childCount > 0)
            {
                on = star.GetChild(0);
            }
            if (on != null)
            {
                on.gameObject.SetActive(i < _rating);
            }
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

    // Once rated, the home screen's rate button goes away.
    private static void HideRateButton()
    {
        GameController gc = SingletonMonoBehavior<GameController>.Instance;
        if (gc == null) return;
        if (gc.btnRate == null) return;
        gc.btnRate.SetActive(false);
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

    public void Hide()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
    }

    // Play Store (market:// on Android, the web page elsewhere) or the App Store on iOS when an id
    // is set; marks the game as rated.
    private void OpenStore()
    {
        string pkg = string.IsNullOrEmpty(androidPackageOverride) ? Application.identifier : androidPackageOverride;
        string url = "https://play.google.com/store/apps/details?id=" + pkg;
        if (Application.isEditor && logStoreInEditor)
        {
            Debug.Log("[RateUI] (editor) would open store — market://details?id=" + pkg + "  |  " + url);
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
            url = "market://details?id=" + pkg;
        }
        Application.OpenURL(url);
        GameData.isRate = 1;
    }

    private static Transform FindUnder(Transform root, string name)
    {
        if (root == null) return null;
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

    // GameObject.Find, else any scene object of that name (also inactive ones).
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
}
