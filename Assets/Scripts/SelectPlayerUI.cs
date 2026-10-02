using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output. Fields (names, order, attributes,
// offsets) and method accessibility match dump.cs (TypeDefIndex 9716). All string literals are
// confirmed against Dumpstringliteral.json.
//
// Decoded from libil2cpp.so with capstone: <>c__DisplayClass20_0 (idx 0x10, this 0x18).
// <Resolve>b__0 = PickCharacter(idx). Button+0x100 (Ghidra "[10].fields.m_CachedPtr") = onClick.
// backButtonName is declared but no method reads it (the back button is wired in the scene).
//
// Convention: where raw jumps to the NullReferenceException stub, the C# just dereferences
// naturally; explicit null checks are kept only where raw really skips.
public class SelectPlayerUI : MonoBehaviour
{
    [Tooltip("Scene name of the popup canvas.")]
    public string canvasName = "SelectPlayerCanvas";
    [Tooltip("Character button objects under the canvas, in CharacterSelect index order (BtnPlayer=Player=0, BtnPlayer2=Player2=1).")]
    public string[] buttonNames = { "BtnPlayer", "BtnPlayer2" };
    [Tooltip("Name of the PLAY (confirm) button under the canvas.")]
    public string playButtonName = "ButtonPlay";
    [Tooltip("Blue selection-frame child of each character button (shown only on the chosen one).")]
    public string selectChildName = "Select";
    [Tooltip("Back/exit button under the canvas — closes this popup and returns to the previous screen (Home).")]
    public string backButtonName = "BtnExit";
    [Tooltip("PlayerPrefs flag: match mode to resume after a character-switch reload (-1 = none). Read by HomeUI.")]
    public string pendingModeKey = "PendingMatchMode";
    private GameObject _canvas;
    private GameObject[] _frames;
    private CharacterSelect _cs;
    private Action _onConfirm;
    private Action _onBack;
    private int _mode;
    private int _pending;

    public static SelectPlayerUI Instance { get; private set; }

    public bool Ready => _canvas != null;

    private void Awake()
    {
        Instance = this;
        _cs = FindFirstObjectByType<CharacterSelect>();
        Resolve();
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
    }

    // Finds the popup, each character button's "Select" frame, and wires the character buttons
    // (PickCharacter) and PLAY (Confirm).
    private void Resolve()
    {
        _canvas = FindInScene(canvasName);
        if (_canvas == null)
        {
            Debug.LogWarning("[SelectPlayer] '" + canvasName + "' not found.");
            return;
        }
        int count = buttonNames.Length;
        _frames = new GameObject[count];
        for (int i = 0; i < count; i++)
        {
            Transform btnTf = FindUnder(_canvas.transform, buttonNames[i]);
            if (btnTf == null)
            {
                Debug.LogWarning("[SelectPlayer] '" + buttonNames[i] + "' not found under " + canvasName + ".");
                continue;
            }
            Transform frame = btnTf.Find(selectChildName);
            if (frame != null)
            {
                _frames[i] = frame.gameObject;
            }
            Button btn = btnTf.GetComponent<Button>();
            if (btn != null)
            {
                int idx = i;
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => PickCharacter(idx));
            }
        }

        Transform play = FindUnder(_canvas.transform, playButtonName);
        if (play == null)
        {
            Debug.LogWarning("[SelectPlayer] '" + playButtonName + "' not found under " + canvasName + ".");
            return;
        }
        Button playBtn = play.GetComponent<Button>();
        if (playBtn == null) return;
        playBtn.onClick.RemoveAllListeners();
        playBtn.onClick.AddListener(Confirm);
    }

    // Opens the picker for a match mode; without the popup, continues straight away.
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

    public void Back()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
        Action cb = _onBack;
        _onBack = null;
        _onConfirm = null;
        cb?.Invoke();
    }

    public void PickCharacter(int i)
    {
        _pending = Mathf.Clamp(i, 0, buttonNames.Length - 1);
        RefreshHighlight();
    }

    // Same character: continue. A different one: save it plus the pending match mode and reload
    // the scene (HomeUI resumes the mode after the reload).
    public void Confirm()
    {
        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
        int current = _cs != null ? _cs.CurrentIndex : _pending;
        if (_cs != null && _pending != current)
        {
            LoadingScreen.ShowInstant();
            PlayerPrefs.SetInt(_cs.prefsKey, _pending);
            PlayerPrefs.SetInt(pendingModeKey, _mode);
            PlayerPrefs.Save();
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            return;
        }
        Action cb = _onConfirm;
        _onConfirm = null;
        cb?.Invoke();
    }

    private void RefreshHighlight()
    {
        if (_frames == null) return;
        for (int i = 0; i < _frames.Length; i++)
        {
            if (_frames[i] != null)
            {
                _frames[i].SetActive(i == _pending);
            }
        }
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
