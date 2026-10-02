using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Field order, readonly-ness and method accessibility all match dump.cs (TypeDefIndex 9681):
// homeCanvas 0x20, menuDim 0x28, gmm 0x30, matchmaking 0x38, gameplayUI 0x40, hideOnlyUI 0x48.
// The method list here matches dump.cs one-for-one (20 methods incl. .ctor). The compiler-
// generated closures are DisplayClass7_0 (BeginMatch), 8_0 (AfterSelectPlayer), 9_0
// (BackToSelectPlayer) and 10_0 (RunMatch) - every lambda on them is decompiled. All string
// literals in Start() and the four On* logs are verified against Dumpstringliteral.json.
public class HomeUI : MonoBehaviour
{
    private GameObject homeCanvas;
    private GameObject menuDim;
    private GameModeManager gmm;
    private MatchmakingUI matchmaking;
    private readonly List<GameObject> gameplayUI = new List<GameObject>();
    private readonly List<GameObject> hideOnlyUI = new List<GameObject>();

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
    // canvas), builds the hideOnlyUI list from a fixed set of named objects, finds "ModesButton" and
    // "MenuDim", wires ModesButton to GoHome, and finally resumes a pending match if one was saved
    // to PlayerPrefs before a scene reload.
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
                if (IsExcludedFromGameplayUI(child.name)) continue;

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

        // FIX (confirmed from raw HomeUI$$Start): a null array, a null element, or an element with a
        // null gameObject all jump to the shared NRE-throw - they are not silently skipped. The last
        // "ModesButton" match wins (the loop never breaks early).
        GameObject modesButtonGO = null;

        Transform[] allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
        if (allTransforms == null)
        {
            throw new NullReferenceException("Resources.FindObjectsOfTypeAll<Transform>() returned null");
        }

        foreach (Transform t in allTransforms)
        {
            if (t == null)
            {
                throw new NullReferenceException("HomeUI.Start: null Transform in scan");
            }

            GameObject go = t.gameObject;
            if (go == null)
            {
                throw new NullReferenceException("HomeUI.Start: Transform with null gameObject");
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

        // CONFIRMED: the UnityAction targets HomeUI.GoHome. Start's metadata-init block registers
        // exactly one method handle, Method$HomeUI.GoHome() - a direct call like the two GoHome()
        // calls below never needs one, so it exists only to build this delegate (the same pattern
        // BeginMatch/BackToSelectPlayer use for their GoHome onBack). A null onClick throws.
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
    private static GameObject FindInSceneIncludingInactive(string n)
    {
        GameObject found = GameObject.Find(n);
        if (found != null) return found;

        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        if (all == null) return null;

        foreach (Transform t in all)
        {
            if (t == null) continue;
            if (t.name != n) continue;

            GameObject go = t.gameObject;
            if (go == null) continue;

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

    // All three lambdas on HomeUI.<>c__DisplayClass8_0 (captures this + mode) are decompiled:
    //   b__0 (LoadingScreen onComplete): show MapSelectPopup if it exists and is Ready, else RunMatch.
    //   b__1 (popup onConfirm): RunMatch(mode).
    //   b__2 (popup onBack): BackToSelectPlayer(mode).
    // b__1/b__2 are cached in the closure's <>9__1 / <>9__2 fields, which is exactly what nested C#
    // lambdas compile to. The raw re-reads MapSelectPopup.Instance after the Ready check and throws
    // if it's null by then - the natural NRE on popup.Show reproduces that.
    private void AfterSelectPlayer(int mode)
    {
        SetHome(false);

        LoadingScreen.Run(() =>
        {
            MapSelectPopup popup = MapSelectPopup.Instance;
            if (popup != null && popup.Ready)
            {
                popup.Show(() => RunMatch(mode), () => BackToSelectPlayer(mode));
                return;
            }

            RunMatch(mode);
        });
    }

    // onConfirm = <BeginMatch>b__0 (DisplayClass7_0): AfterSelectPlayer(mode).
    // onBack = GoHome, bound directly as a method group (no closure).
    // The raw hides the Action constructors' target arguments, but the method's metadata-init block
    // registers exactly two delegate targets - Method$HomeUI.GoHome() and <BeginMatch>b__0 - so the
    // mode-capturing closure must be onConfirm and GoHome must be onBack.
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

        SelectPlayerUI.Instance.Show(mode, () => AfterSelectPlayer(mode), GoHome);
    }

    private void SetHome(bool v)
    {
        if (homeCanvas != null)
        {
            homeCanvas.SetActive(v);
        }
    }

    // Same shape as BeginMatch, but falls back to GoHome() instead of AfterSelectPlayer(mode) when
    // SelectPlayerUI isn't available/ready — this is the "go back" path, reached from the map
    // popup's onBack (<AfterSelectPlayer>b__2).
    // onConfirm = <BackToSelectPlayer>b__0 (DisplayClass9_0, confirmed): AfterSelectPlayer(mode).
    // onBack = GoHome (method group) - same two-target evidence as BeginMatch.
    private void BackToSelectPlayer(int mode)
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

        SelectPlayerUI.Instance.Show(mode, () => AfterSelectPlayer(mode), GoHome);
    }

    // Lambdas on HomeUI.<>c__DisplayClass10_0 (captures this + mode):
    //   b__0 (LoadingScreen onComplete): with no MatchmakingUI, run another loading pass whose
    //        callback is b__2; otherwise start the matchmaking lobby with hunter = (mode == 1) and
    //        ready-callback b__1. Mode 1 is the seeker role, matching StartMode(1) ->
    //        gmm.BeginRound(true).
    //   b__1 (matchmaking ready): LoadingScreen.Run(b__3).
    //   b__2 (no-matchmaking callback): StartMode(mode).
    //   b__3 (after the post-lobby loading screen): StartMode(mode) - this is where the match
    //        actually starts on the matchmaking path.
    private void RunMatch(int mode)
    {
        SetHome(false);

        LoadingScreen.Run(() =>
        {
            if (matchmaking == null)
            {
                LoadingScreen.Run(() => StartMode(mode));
                return;
            }

            matchmaking.Begin(mode == 1, () =>
            {
                LoadingScreen.Run(() => StartMode(mode));
            });
        });
    }

    // Confirmed from raw: each is a single Debug.Log of one string literal and nothing else. Text
    // confirmed against Dumpstringliteral.json (StringLiteral_11481 / 11483 / 11482 / 11480) - these
    // really are placeholder logs in the shipped game.
    public void OnLeaderboard() => Debug.Log("[Home] Leaderboard — TODO");
    public void OnSettings() => Debug.Log("[Home] Settings — TODO");
    public void OnAd() => Debug.Log("[Home] Rewarded ad — TODO");
    public void OnAddCoins() => Debug.Log("[Home] Add coins — TODO");

    public void StartMode(int mode)
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

    private void SetGameplayUI(bool v)
    {
        if (gameplayUI == null) return;

        foreach (GameObject go in gameplayUI)
        {
            if (go != null) go.SetActive(v);
        }
    }

    private void SetHideOnlyUI(bool v)
    {
        if (hideOnlyUI == null) return;

        foreach (GameObject go in hideOnlyUI)
        {
            if (go != null) go.SetActive(v);
        }
    }

    // CONFIRMED unused: Ghidra finds no code references to HomeUI$$WireChild - only its exception-
    // unwind table entry (fde_table_entry) and two DATA entries (the IL2CPP method-pointer tables).
    // Dead code left in the original build.
    private void WireChild(string childName, UnityAction action)
    {
        if (homeCanvas == null) return;

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
