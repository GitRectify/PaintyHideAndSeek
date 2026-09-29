using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// NOTE: Resolves the previously-open "LoadingScreen.Run(...) implementation" caveat — this class
// was only known from call sites before. Confirms the third singleton pattern again (lazily
// find-or-create, unlike the plain bare-field pattern of AdMgr/SelectPlayerUI) and a 6th
// occurrence of the cross-cutting scene-lookup helper (static here, same as LevelManager's).
public class LoadingScreen : MonoBehaviour
{
    [SerializeField] private float duration = 1.2f;
    [SerializeField] private GameObject canvasGO;
    [SerializeField] private Image fill;

    private Coroutine running;
    private bool resolved;

    private static LoadingScreen _instance;

    public static LoadingScreen Instance
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

    public static void Run(Action onComplete)
    {
        LoadingScreen instance = Instance;
        if (instance != null)
        {
            instance.Show(instance.duration, onComplete);
        }
    }

    public static void Run(float seconds, Action onComplete)
    {
        LoadingScreen instance = Instance;
        if (instance != null)
        {
            instance.Show(seconds, onComplete);
        }
    }

    public static void ShowInstant()
    {
        LoadingScreen instance = Instance;
        if (instance != null)
        {
            instance.ShowInstantInternal();
        }
    }

    // NOTE: the branch condition here reads a raw field at a fixed offset (0x58) on
    // Singleton<RootManager>.Instance. RootManager itself remains unreversed beyond its
    // confirmed call surface (SetNumber, ShowInterAds_Native), so this field's real name/type
    // is a guess based purely on how it's used here — named IsShowingAd since it gates between
    // "just hide the canvas and call onComplete immediately" (false) and "log an MREC message,
    // set AdMgr.OnMrec, and animate the fill bar via a coroutine before calling onComplete"
    // (true). This also newly confirms AdMgr has a settable OnMrec member (add to AdMgr's
    // still-unreversed call-surface list).
    //
    // FIX (confirmed): raw explicitly throws (jumps straight to the function's shared NRE-throw
    // target, bypassing the onComplete?.Invoke() epilogue entirely) when AdMgr.Instance is null
    // inside the IsShowingAd branch. A prior draft instead wrapped that whole branch body in
    // `if (AdMgr.Instance != null) {...}`, so a null AdMgr.Instance would silently fall through
    // to invoking onComplete instead - a real, reachable divergence (AdMgr.Instance being unset
    // is a plausible state, e.g. ad SDK not yet initialized). Fixed to throw immediately, matching
    // raw's goto exactly.
    public void Show(float seconds, Action onComplete)
    {
        Resolve();

        if (canvasGO == null)
        {
            onComplete?.Invoke();
            return;
        }

        if (running != null)
        {
            StopCoroutine(running);
        }

        running = StartCoroutine(FillRoutine(seconds, onComplete));
    }

    private void ShowInstantInternal()
    {
        Resolve();

        if (canvasGO == null)
            return;

        canvasGO.SetActive(true);
        canvasGO.transform.SetAsLastSibling();

        if (fill != null)
        {
            fill.fillAmount = 0f;
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
            // NOTE: decompiled read here was field-mislabeled "m_OverrideSprite" (a Sprite
            // reference) compared against the int literal 3 — almost certainly a decompiler
            // offset error for Image.type (Simple=0, Sliced=1, Tiled=2, Filled=3). Reconstructed
            // as the sensible check: find any child Image configured as a Filled-type bar. The
            // loop's raw bounds-check compiled to a convoluted bitmask expression
            // (uVar1 & ~((int)uVar1>>31)) that's equivalent to a plain "until index==length" for
            // any realistic (non-negative, sub-2^31) array length - a plain foreach is exact.
            Image[] images = canvasGO.GetComponentsInChildren<Image>(true);
            if (images != null)
            {
                foreach (Image img in images)
                {
                    if (img.type == Image.Type.Filled)
                    {
                        fill = img;
                        break;
                    }
                }
            }
        }

        canvasGO.SetActive(false);
        resolved = true;
    }

    // NOTE: only the compiler-generated iterator state machine's constructor was decompiled
    // (fields: <>1__state, <>4__this, seconds, onComplete) — the actual MoveNext body (almost
    // certainly: animate fill.fillAmount up to 1 over `seconds`, then hide canvasGO, clear
    // `running`, and invoke onComplete) was not in this paste. Left as an explicit TODO rather
    // than guessed.
    private IEnumerator FillRoutine(float seconds, Action onComplete)
    {
        Resolve();

        if (canvasGO == null)
        {
            running = null;
            onComplete?.Invoke();
            yield break;
        }

        canvasGO.SetActive(true);
        canvasGO.transform.SetAsLastSibling();

        if (fill != null)
        {
            fill.fillAmount = 0f;
        }

        float durationSafe = Mathf.Max(0.01f, seconds);
        float elapsed = 0f;

        while (elapsed < durationSafe)
        {
            elapsed += Time.unscaledDeltaTime;

            if (fill != null)
            {
                fill.fillAmount =
                    Mathf.Clamp01(elapsed / durationSafe);
            }

            yield return null;
        }

        if (fill != null)
        {
            fill.fillAmount = 1f;
        }

        canvasGO.SetActive(false);

        running = null;

        onComplete?.Invoke();
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