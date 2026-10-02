using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// NOTE: Resolves the previously-open "LoadingScreen.Run(...) implementation" caveat — this class
// was only known from call sites before. Confirms the third singleton pattern again (lazily
// find-or-create, unlike the plain bare-field pattern of AdMgr/SelectPlayerUI) and a 6th
// occurrence of the cross-cutting scene-lookup helper (static here, same as LevelManager's).
// Re-verified method by method against raw Ghidra output; all string literals ("LoadingScreenRunner",
// "LoadingCanvas", "Bg/Pro/Fill", "Show MREC", "Off MREC") confirmed against Dumpstringliteral.json.
// Fields, offsets, method list and accessibility all match dump.cs (TypeDefIndex 9687):
// duration 0x20, canvasGO 0x28, fill 0x30, running 0x38, resolved 0x40, static _instance 0x0.
public class LoadingScreen : MonoBehaviour
{
    [Tooltip("Seconds for the bar to fill from 0 to 1 on each loading transition.")]
    public float duration = 1.2f;

    private GameObject canvasGO;
    private Image fill;
    private Coroutine running;
    private bool resolved;

    private static LoadingScreen _instance;

    private static LoadingScreen Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<LoadingScreen>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("LoadingScreenRunner");
                    _instance = go.AddComponent<LoadingScreen>();
                }
            }
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
        }
    }

    // FIX (confirmed from raw): none of these three silently skip on a null Instance - a null falls
    // to the shared NRE-throw. (Instance never actually returns null, since it creates a runner
    // object on demand.) Run(Action) really does call get_Instance twice: once for the Show target
    // and once to read duration.
    public static void Run(Action onComplete)
    {
        LoadingScreen target = Instance;
        target.Show(Instance.duration, onComplete);
    }

    public static void Run(float seconds, Action onComplete)
    {
        Instance.Show(seconds, onComplete);
    }

    public static void ShowInstant()
    {
        Instance.ShowInstantInternal();
    }

    // CONFIRMED: the branch reads RootManager+0x58, which dump.cs names `public bool LoadingShow`
    // (a Firebase Remote Config flag). false = just hide the canvas and call onComplete
    // immediately; true = show the MREC ad and animate the fill bar via FillRoutine before calling
    // onComplete.
    //
    // FIX (confirmed): raw explicitly throws (jumps straight to the function's shared NRE-throw
    // target, bypassing the onComplete?.Invoke() epilogue entirely) when AdMgr.Instance is null
    // inside the LoadingShow branch. A prior draft instead wrapped that whole branch body in
    // `if (AdMgr.Instance != null) {...}`, so a null AdMgr.Instance would silently fall through
    // to invoking onComplete instead - a real, reachable divergence (AdMgr.Instance being unset
    // is a plausible state, e.g. ad SDK not yet initialized). Fixed to throw immediately, matching
    // raw's goto exactly.
    public void Show(float seconds, Action onComplete)
    {
        RootManager root = RootManager.Instance;
        if (root == null)
        {
            throw new NullReferenceException("RootManager instance not available");
        }

        if (!root.LoadingShow)
        {
            Resolve();
            if (running == null && canvasGO != null)
            {
                canvasGO.SetActive(false);
            }
        }
        else
        {
            // if (AdMgr.Instance == null)
            // {
            //     throw new NullReferenceException("AdMgr instance not available");
            // }
            // AdMgr.Instance.OnMrec = true;
            Debug.Log("Show MREC");
            Resolve();

            if (canvasGO != null)
            {
                if (running != null)
                {
                    StopCoroutine(running);
                }
                running = StartCoroutine(FillRoutine(seconds, onComplete));
                return;
            }
        }

        onComplete?.Invoke();
    }

    private void ShowInstantInternal()
    {
        Resolve();
        if (canvasGO == null)
        {
            return;
        }

        RootManager root = RootManager.Instance;
        if (root == null)
        {
            throw new NullReferenceException("RootManager instance not available");
        }

        // Same RootManager.LoadingShow flag (+0x58) as Show() above.
        if (!root.LoadingShow)
        {
            canvasGO.SetActive(false);
            return;
        }

        canvasGO.SetActive(true);
        canvasGO.transform.SetAsLastSibling();
        if (fill != null)
        {
            fill.fillAmount = 0.06f;
        }
    }

    private void Resolve()
    {
        if (resolved && canvasGO != null)
        {
            return;
        }

        canvasGO = FindInScene("LoadingCanvas");
        if (canvasGO == null)
        {
            resolved = true;
            return;
        }

        Transform fillTransform = canvasGO.transform.Find("Bg/Pro/Fill");
        fill = (fillTransform != null) ? fillTransform.GetComponent<Image>() : null;

        if (fill == null)
        {
            // CONFIRMED: Ghidra mislabels the field read here as "m_OverrideSprite", but the actual
            // instruction is `ldr w11,[x?, #0xe8]` (0x02369bc8) followed by `cmp w11,#0x3`, and
            // dump.cs puts Image.m_Type at 0xE8. So this is Image.type == Filled (Simple=0,
            // Sliced=1, Tiled=2, Filled=3). Ghidra's Image struct is shifted 8 bytes from the real
            // layout (the same shift explains PanelLoading's "m_PreserveAspect" read). The
            // loop's raw bounds-check compiled to a convoluted bitmask expression
            // (uVar1 & ~((int)uVar1>>31)) that's equivalent to a plain "until index==length" for
            // any realistic (non-negative, sub-2^31) array length - a plain foreach is exact.
            // FIX (confirmed from raw): a null array or null element throws rather than being
            // skipped - the natural NRE on the foreach / img.type reproduces that.
            Image[] images = canvasGO.GetComponentsInChildren<Image>(true);
            foreach (Image img in images)
            {
                if (img.type == Image.Type.Filled)
                {
                    fill = img;
                    break;
                }
            }
        }

        canvasGO.SetActive(false);
        resolved = true;
    }

    // Decompiled from LoadingScreen.<FillRoutine>d__14$$MoveNext (states 0 / 1 / 2; <t>5__2 is `t`).
    // Note it overwrites its own `seconds` parameter with the clamped value, and uses unscaled time.
    // After the bar finishes it waits one more frame, then hides the canvas, clears `running`,
    // invokes onComplete, logs "Off MREC", hides the MREC ad, and shows a native interstitial if
    // RootManager.OnInterPlayGame is set. Null canvasGO, AdMgr.Instance or RootManager.Instance all
    // throw in raw - the natural NREs below reproduce that.
    private IEnumerator FillRoutine(float seconds, Action onComplete)
    {
        canvasGO.SetActive(true);
        canvasGO.transform.SetAsLastSibling();
        if (fill != null)
        {
            fill.fillAmount = 0f;
        }

        float t = 0f;
        seconds = Mathf.Max(0.05f, seconds);

        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            if (fill != null)
            {
                fill.fillAmount = Mathf.Clamp01(t / seconds);
            }
            yield return null;
        }

        if (fill != null)
        {
            fill.fillAmount = 1f;
        }
        yield return null;

        canvasGO.SetActive(false);
        running = null;
        onComplete?.Invoke();

        Debug.Log("Off MREC");
        // AdMgr.Instance.OnMrec = false;

        RootManager root = RootManager.Instance;
        if (root.OnInterPlayGame)
        {
            root.ShowInterAds_Native();
        }
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
}