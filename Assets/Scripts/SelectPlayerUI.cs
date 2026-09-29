using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// NOTE: Resolves SelectPlayerUI, previously known only from call sites (Instance, Ready, Show
// were in the original handoff's never-reversed list). Structurally similar to MapSelectPopup
// (canvas resolution, per-option "Select" highlight frame, Back/Confirm) but simpler - a fixed
// 2-character picker with no ad-gating, that persists the choice via PlayerPrefs and reloads the
// current scene to apply it. Adds two new confirmed members to the already-delivered
// CharacterSelect.cs from an earlier session: CurrentIndex (int getter) and prefsKey (string
// field) - worth patching that file if you want it complete. Also confirms canvasName
// ("SelectPlayerCanvas") matches the separately-delivered SelectPlayerCanvas.cs from an earlier
// session, likely companion components on the same canvas.
//
// RE-VERIFICATION PASS: checked every control-flow branch, every warning-message construction
// point, and the Confirm()/Back()/PickCharacter()/RefreshHighlight() logic against the raw
// pseudocode line-for-line. No confirmable bugs found — this file already matched exactly.
// Specifically confirmed (not just plausible-sounding) on this pass:
//   - the crash-on-null-buttonNames behavior covers both the per-button loop AND the play-button
//     resolution below it (both sit inside the same implicit raw guard, not separately guarded);
//   - the play button's click handler is bound directly to Confirm (no closure — it captures
//     nothing), while each per-character button's handler needs (and gets) a closure over `this`
//     and its own index;
//   - Confirm()'s reload-vs-callback-only branch condition and which value feeds `currentIndex`
//     in each case (__cs.CurrentIndex vs _pending) match the raw condition exactly.
// There are no packed Vector/Color/Rect hex constants in this file to decode — it's pure
// control-flow/logic, so there was no room for the kind of bit-decoding slip found in some of the
// earlier files' UI-layout code.
public class SelectPlayerUI : MonoBehaviour
{
    [SerializeField] private string canvasName = "SelectPlayerCanvas";
    [SerializeField] private string[] buttonNames = { "BtnPlayer", "BtnPlayer2" };
    [SerializeField] private string playButtonName = "ButtonPlay";
    [SerializeField] private string selectChildName = "Select";
    // NOTE: confirmed field (assigned in .ctor) but never referenced anywhere in Resolve() or
    // any other method in this paste - genuinely unused in what was decompiled here, not an
    // omission on my part.
    [SerializeField] private string backButtonName = "BtnExit";
    [SerializeField] private string pendingModeKey = "PendingMatchMode";

    private CharacterSelect __cs;
    private GameObject _canvas;
    private GameObject[] _frames;
    private int _pending;
    private int _mode;
    private Action _onConfirm;
    private Action _onBack;

    public static SelectPlayerUI Instance { get; set; }

    public bool Ready => _canvas != null;

    private void Awake()
    {
        Instance = this;
        __cs = FindFirstObjectByType<CharacterSelect>();
        Resolve();
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
    }

    private void Resolve()
    {
        _canvas = FindInScene(canvasName);
        if (_canvas == null)
        {
            Debug.LogWarning($"[SelectPlayer] '{canvasName}' not found.");
            return;
        }

        // NOTE: decompiled has no null-guard on buttonNames here - crashes via
        // NullReferenceException if unassigned, matching the abort-on-null pattern used
        // throughout this codebase. Confirmed this applies to the play-button resolution below
        // too, not just this loop - both sit inside the same implicit raw guard.
        _frames = new GameObject[buttonNames.Length];

        for (int i = 0; i < buttonNames.Length; i++)
        {
            Transform btnT = FindUnder(_canvas.transform, buttonNames[i]);
            if (btnT == null)
            {
                Debug.LogWarning($"[SelectPlayer] '{buttonNames[i]}' not found under '{canvasName}'.");
                continue;
            }

            Transform selectT = btnT.Find(selectChildName);
            if (selectT != null)
            {
                _frames[i] = selectT.gameObject;
            }

            Button btn = btnT.GetComponent<Button>();
            if (btn != null)
            {
                // NOTE: click-handler lambda (`<Resolve>b__0`) itself wasn't decompiled - only
                // that it's cached in a closure capturing (this, index). Reconstructed as
                // PickCharacter(index), the clear intent given exactly those captured variables.
                int index = i;
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => PickCharacter(index));
            }
        }

        Transform playBtnT = FindUnder(_canvas.transform, playButtonName);
        if (playBtnT == null)
        {
            Debug.LogWarning($"[SelectPlayer] '{playButtonName}' not found under '{canvasName}'.");
            return;
        }

        Button playBtn = playBtnT.GetComponent<Button>();
        if (playBtn != null)
        {
            // CONFIRMED: bound directly to Confirm (no closure - unlike the per-button handlers
            // above, this UnityAction captures nothing, matching a plain instance-method target).
            playBtn.onClick.RemoveAllListeners();
            playBtn.onClick.AddListener(Confirm);
        }
    }

    public void Show(int mode, Action onConfirm, Action onBack)
    {
        _mode = mode;
        _onConfirm = onConfirm;
        _onBack = onBack;
        _pending = 0;

        if (_canvas == null)
        {
            onConfirm?.Invoke();
            return;
        }

        _canvas.SetActive(true);
        RefreshHighlight();
    }

    private void RefreshHighlight()
    {
        if (_frames == null)
        {
            return;
        }

        for (int i = 0; i < _frames.Length; i++)
        {
            if (_frames[i] != null)
            {
                _frames[i].SetActive(i == _pending);
            }
        }
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

    public void PickCharacter(int i)
    {
        _pending = Mathf.Clamp(i, 0, buttonNames.Length - 1);
        RefreshHighlight();
    }

    // If the picked character actually changed, shows an instant loading screen, persists the
    // choice + pending match mode to PlayerPrefs, and reloads the current scene to apply it.
    // Otherwise just fires the onConfirm callback without reloading anything.
    public void Confirm()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }

        int currentIndex = (__cs != null) ? __cs.CurrentIndex : _pending;

        if (__cs != null && _pending != currentIndex)
        {
            LoadingScreen.ShowInstant();

            // NOTE: CharacterSelect.prefsKey is accessed directly here (raw field read in the
            // decompiled code) - assumes it's public/internal on CharacterSelect; not
            // re-verified against that already-delivered file in this session.
            PlayerPrefs.SetInt(__cs.prefsKey, _pending);
            PlayerPrefs.SetInt(pendingModeKey, _mode);
            PlayerPrefs.Save();

            int buildIndex = SceneManager.GetActiveScene().buildIndex;
            SceneManager.LoadScene(buildIndex);
        }
        else
        {
            Action onConfirm = _onConfirm;
            _onConfirm = null;
            onConfirm?.Invoke();
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
}