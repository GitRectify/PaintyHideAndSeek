using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class HomeUI : MonoBehaviour
{
    private GameModeManager gmm;
    private MatchmakingUI matchmaking;
    private GameObject homeCanvas;
    private GameObject menuDim;

    private List<GameObject> gameplayUI = new List<GameObject>();
    private List<GameObject> hideOnlyUI = new List<GameObject>();

    public void GoHome()
    {
        if (gmm != null)
        {
            gmm.ShowMenu();
        }

        if (menuDim != null)
        {
            menuDim.SetActive(false);
        }

        SetGameplayUI(false);
        SetHome(true);

        AudioManager.MusicMenu();
    }

    public void PlayAgainSeek()
    {
        StartMode(1);
    }

    public void PlayAgainHide()
    {
        StartMode(2);
    }

    // Resolves references, builds the gameplayUI list (every direct child of the scene's "UI" root
    // except homeCanvas itself and anything IsExcludedFromGameplayUI flags as a separate overlay
    // canvas), builds the hideOnlyUI list from a fixed set of named objects, finds "MenuDim", wires
    // the "ModesButton" to GoHome (see NOTE below), and finally resumes a pending match if one was
    // saved to PlayerPrefs before a scene reload.
    private void Start()
    {
        gmm = Object.FindFirstObjectByType<GameModeManager>();
        matchmaking = Object.FindFirstObjectByType<MatchmakingUI>();

        homeCanvas = FindInSceneIncludingInactive("HomeCanvas");

        GameObject uiRoot = GameObject.Find("UI");
        if (uiRoot != null)
        {
            foreach (Transform child in uiRoot.transform)
            {
                GameObject childGO = child.gameObject;
                if (childGO == homeCanvas) continue;
                if (IsExcludedFromGameplayUI(childGO.name)) continue;

                gameplayUI.Add(childGO);
            }
        }

        string[] hideOnlyNames = { "PaintGateButton", "Btn_DoiDang", "Btn_XoayNhanVat", "ButtonCamera" };
        foreach (string name in hideOnlyNames)
        {
            GameObject found = FindInSceneIncludingInactive(name);
            if (found != null)
            {
                hideOnlyUI.Add(found);
            }
        }

        GameObject modesButtonGO = null;

        // FIX: raw THROWS here on a null array, a null element, or a null gameObject. The old version
        // silently skipped all three cases. (All three are practically unreachable in Unity, but this
        // now matches raw.)
        Transform[] allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
        if (allTransforms == null)
        {
            throw new System.NullReferenceException("Resources.FindObjectsOfTypeAll<Transform>() returned null");
        }

        foreach (Transform t in allTransforms)
        {
            if (t == null)
            {
                throw new System.NullReferenceException("HomeUI.Start: null Transform in scene scan");
            }

            GameObject go = t.gameObject;
            if (go == null)
            {
                throw new System.NullReferenceException("HomeUI.Start: Transform with null gameObject");
            }
            if (!go.scene.IsValid()) continue;

            if (t.name == "ModesButton")
            {
                modesButtonGO = go;
            }
            else if (t.name == "MenuDim")
            {
                menuDim = go;
            }
        }

        // FIX (inferred from the raw's method-metadata table, high confidence): the UnityAction that
        // Ghidra shows with no arguments is `new UnityAction(GoHome)`. Start()'s init table registers
        // exactly one method pointer, `Method$HomeUI.GoHome()`, and no lambda or other handler, and
        // Start() creates exactly one UnityAction. (Same pattern as HideModeResultUI.BuildCaught, where
        // the init table names OnReviveClicked/OnGiveUpClicked for its two hidden UnityActions.)
        // The old version wired a made-up empty "OnModesButtonClicked", so this button did nothing.
        // Quick way to double-check in Ghidra: in the listing of HomeUI$$Start, look at the call to
        // UnityAction$$.ctor and see which Method$... pointer is loaded into x2.
        if (modesButtonGO != null)
        {
            Button modesButton = modesButtonGO.GetComponent<Button>();
            if (modesButton != null)
            {
                modesButton.onClick.RemoveAllListeners();
                modesButton.onClick.AddListener(GoHome);
            }
        }

        int mode = PlayerPrefs.GetInt("PendingMatchMode", -1);
        if (mode < 0)
        {
            GoHome();
        }
        else
        {
            PlayerPrefs.SetInt("PendingMatchMode", -1);
            PlayerPrefs.Save();
            GoHome();
            AfterSelectPlayer(mode);
        }
    }

    // Scene-wide named-object lookup (GameObject.Find first, then a fallback scan of every loaded
    // Transform filtered to ones in a valid scene) — the same pattern used by
    // GameModeManager.FindInSceneByName and HideModeResultUI.FindGO elsewhere in this project.
    //
    // FIX: raw THROWS on a null array, a null element, or a null gameObject. The old version returned
    // null / skipped instead.
    private static GameObject FindInSceneIncludingInactive(string n)
    {
        GameObject found = GameObject.Find(n);
        if (found != null) return found;

        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        if (all == null)
        {
            throw new System.NullReferenceException("Resources.FindObjectsOfTypeAll<Transform>() returned null");
        }

        foreach (Transform t in all)
        {
            if (t == null)
            {
                throw new System.NullReferenceException("FindInSceneIncludingInactive: null Transform in scan");
            }
            if (t.name != n) continue;

            GameObject go = t.gameObject;
            if (go == null)
            {
                throw new System.NullReferenceException("FindInSceneIncludingInactive: Transform with null gameObject");
            }

            if (go.scene.IsValid())
            {
                return go;
            }
        }

        return null;
    }

    private static bool IsExcludedFromGameplayUI(string n)
    {
        string[] excluded =
        {
            "ModeSelectCanvas", "MatchmakingCanvas", "MapCanvas", "SettingsCanvas",
            "RemoveAdsCanvas", "LoadingCanvas", "ThanksCanvas", "RateCanvas",
            "SelectPlayerCanvas", "HelpCanvas"
        };

        foreach (string name in excluded)
        {
            if (n == name) return true;
        }

        return false;
    }

    // STILL MISSING (needs one more paste): the onComplete lambda passed to LoadingScreen.Run. In
    // Ghidra, search for the function  HomeUI.<>c__DisplayClass8_0$$<AfterSelectPlayer>b__0
    // It captures `this` and `mode`. Hints from the rest of the project (not proof): HomeUI's
    // `matchmaking` field is assigned in Start() but never read by any decompiled method, and
    // MatchmakingUI.Begin(bool hunter, Action ready) / MapSelectPopup.Show(onConfirm, onBack) are
    // never called from anywhere decompiled yet, so this lambda (or RunMatch's) is probably where
    // the map-select / matchmaking steps get driven. Left empty on purpose rather than guessed:
    // until it is filled in, confirming the player-select screen will not actually start a match.
    private void AfterSelectPlayer(int mode)
    {
        SetHome(false);

        LoadingScreen.Run(() =>
        {
            // MapSelectPopup.Instance.Show(null,()=>BackToSelectPlayer(mode));
            // TODO: paste HomeUI.<>c__DisplayClass8_0$$<AfterSelectPlayer>b__0. Captured: this, mode.
            // (Temporary stand-in ONLY for a quick end-to-end test, skips the real steps: StartMode(mode);)
        });
    }

    // NOTE: SelectPlayerUI is referenced here via a bare static Instance field (same pattern as
    // AdMgr.Instance in HideModeResultUI).
    //
    // FIX (inferred, see reasoning below): the two callbacks passed to Show are NOT both unknown.
    //  - This method's init table registers `Method$HomeUI.GoHome()` even though BeginMatch never
    //    calls GoHome directly, and it builds exactly two Actions. So one Action is
    //    `new Action(GoHome)` and the other is the closure lambda <BeginMatch>b__0 (which captures
    //    `this` + `mode`). GoHome can only sensibly be the "back" callback (high confidence).
    //  - The remaining lambda is therefore "confirm". It captures `this` + `mode`, and the two
    //    fallback paths above (no SelectPlayerUI / not Ready) both call AfterSelectPlayer(mode),
    //    i.e. "no select-player screen" behaves as "confirmed immediately", so confirm ->
    //    AfterSelectPlayer(mode) (fairly high confidence).
    // To confirm 100% in Ghidra: open HomeUI.<>c__DisplayClass7_0$$<BeginMatch>b__0 - its body should
    // be a single call to AfterSelectPlayer(mode).
    public void BeginMatch(int mode)
    {
        if (SelectPlayerUI.Instance == null)
        {
            AfterSelectPlayer(mode);
            return;
        }

        if (!SelectPlayerUI.Instance.Ready)
        {
            AfterSelectPlayer(mode);
            return;
        }

        SetHome(false);

        SelectPlayerUI.Instance.Show(mode,
            () => AfterSelectPlayer(mode),   // onConfirm  (inferred, see comment above)
            GoHome);                          // onBack     (inferred from the init table)
    }

    public void SetHome(bool v)
    {
        if (homeCanvas != null)
        {
            homeCanvas.SetActive(v);
        }
    }

    // Same shape as BeginMatch, but falls back to GoHome() instead of AfterSelectPlayer(mode) when
    // SelectPlayerUI isn't available/ready — this is the "go back" path from further along in the
    // flow rather than the initial entry point.
    //
    // FIX (inferred): same init-table evidence as BeginMatch - `Method$HomeUI.GoHome()` is
    // registered and two Actions are built, one of them being the closure lambda
    // <BackToSelectPlayer>b__0 (captures `this` + `mode`). onBack = GoHome (high confidence).
    // onConfirm = AfterSelectPlayer(mode) is the natural "continue the same flow again" choice, but
    // it is a LOWER-confidence guess than in BeginMatch: RunMatch(mode) would also fit the captured
    // variables. To confirm in Ghidra open
    // HomeUI.<>c__DisplayClass9_0$$<BackToSelectPlayer>b__0 and see which method it calls.
    public void BackToSelectPlayer(int mode)
    {
        if (SelectPlayerUI.Instance == null)
        {
            GoHome();
            return;
        }

        if (!SelectPlayerUI.Instance.Ready)
        {
            GoHome();
            return;
        }

        SetHome(false);

        SelectPlayerUI.Instance.Show(mode,
            () => AfterSelectPlayer(mode),   // onConfirm  (inferred, lower confidence - see comment above)
            GoHome);                          // onBack     (inferred from the init table)
    }

    // STILL MISSING (needs one more paste): in Ghidra search for the function
    // HomeUI.<>c__DisplayClass10_0$$<RunMatch>b__0   (captures `this` and `mode`).
    // Most likely it is what finally starts the round (StartMode(mode) or gmm.BeginRound), but that
    // is a guess - left empty rather than invented.
    public void RunMatch(int mode)
    {
        SetHome(false);

        LoadingScreen.Run(() =>
        {
            // TODO: paste HomeUI.<>c__DisplayClass10_0$$<RunMatch>b__0. Captured: this, mode.
            // (Temporary stand-in ONLY for a quick end-to-end test: StartMode(mode);)
        });
    }

    public void OnLeaderboard() => Debug.Log("[Home] Leaderboard — TODO");
    public void OnSettings() => Debug.Log("[Home] Settings — TODO");
    public void OnAd() => Debug.Log("[Home] Rewarded ad — TODO");
    public void OnAddCoins() => Debug.Log("[Home] Add coins — TODO");

    private void StartMode(int mode)
    {
        SetHome(false);
        SetGameplayUI(true);
        SetHideOnlyUI(mode == 2);

        if (gmm != null)
        {
            gmm.BeginRound(mode == 1);
        }

        AudioManager.MusicGame();
    }

    // FIX: raw THROWS if the list is null; the old `if (list == null) return;` silently skipped that.
    // (foreach over a null list throws its own NullReferenceException, matching raw.)
    private void SetGameplayUI(bool v)
    {
        foreach (GameObject go in gameplayUI)
        {
            if (go != null) go.SetActive(v);
        }
    }

    // FIX: same as SetGameplayUI - raw throws on a null list instead of silently returning.
    private void SetHideOnlyUI(bool v)
    {
        foreach (GameObject go in hideOnlyUI)
        {
            if (go != null) go.SetActive(v);
        }
    }

    // NOTE: not called anywhere in this dump — presumably used elsewhere (e.g. wiring up the
    // Leaderboard/Settings/Ad/AddCoins buttons to their handlers above) in a method not included here.
    //
    // FIX: raw THROWS if homeCanvas is null here (the old `if (homeCanvas == null) return;` silently
    // skipped it). The natural NullReferenceException on the next line matches raw.
    private void WireChild(string childName, UnityAction action)
    {
        Transform childTf = homeCanvas.transform.Find(childName);
        if (childTf == null)
        {
            Debug.LogWarning("[HomeUI] '" + childName + "' not found under HomeCanvas.");
            return;
        }

        Button btn = childTf.GetComponent<Button>();
        if (btn == null)
        {
            btn = childTf.gameObject.AddComponent<Button>();
        }

        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(action);
    }
}