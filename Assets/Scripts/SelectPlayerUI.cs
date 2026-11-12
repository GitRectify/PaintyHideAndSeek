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

    // Multiple Play: characters the account does not own online, and where a reload resumes.
    private Func<int, bool> _locked;
    private string _resumeKey;
    private Text[] _lockLabels;
    private Text _message;

    /// <summary>PlayerPrefs flag: the online side to resume at the room screen after a character-switch reload (-1 = none).</summary>
    public const string PendingOnlineModeKey = "PendingOnlineMatchMode";

    public static SelectPlayerUI Instance { get; private set; }

    public bool Ready => _canvas != null;

    private void Awake()
    {
        Instance = this;
        _cs = FindAnyObjectByType<CharacterSelect>();
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
    public void Show(int mode, Action onConfirm, Action onBack) => Show(mode, onConfirm, onBack, null, null);

    /// <summary>
    /// Multiple Play's use of the same screen: <paramref name="locked"/> marks the characters this
    /// account cannot play online (Play refuses them, and says where to get them), and a reload
    /// for a new character comes back to <paramref name="resumeKey"/>'s flow, not Single Play's.
    /// It opens on the character now chosen.
    /// </summary>
    public void Show(int mode, Action onConfirm, Action onBack, Func<int, bool> locked, string resumeKey)
    {
        _mode = mode;
        _onConfirm = onConfirm;
        _onBack = onBack;
        _locked = locked;
        _resumeKey = resumeKey;
        _pending = locked != null && _cs != null ? _cs.CurrentIndex : 0;
        if (_canvas == null)
        {
            onConfirm?.Invoke();
            return;
        }
        _canvas.SetActive(true);
        ShowMessage("");
        RefreshLocks();
        RefreshHighlight();
    }

    private bool IsLocked(int i) => _locked != null && _locked(i);

    // A "LOCKED" ribbon on each character this account cannot play online.
    private void RefreshLocks()
    {
        if (_frames == null) return;
        if (_lockLabels == null) _lockLabels = new Text[_frames.Length];

        for (int i = 0; i < _frames.Length; i++)
        {
            bool locked = IsLocked(i);
            if (_lockLabels[i] == null && locked)
            {
                Transform button = FindUnder(_canvas.transform, buttonNames[i]);
                if (button == null) continue;
                _lockLabels[i] = NewLabel(button, "LockLabel", "LOCKED", 44, new Vector2(0f, -150f), new Vector2(360f, 70f));
                _lockLabels[i].color = new Color(1f, 0.78f, 0.35f, 1f);
            }

            if (_lockLabels[i] != null) _lockLabels[i].gameObject.SetActive(locked);
        }
    }

    private void ShowMessage(string text)
    {
        if (_message == null)
        {
            if (string.IsNullOrEmpty(text) || _canvas == null) return;
            Transform play = FindUnder(_canvas.transform, playButtonName);
            if (play == null || play.parent == null) return;
            _message = NewLabel(play.parent, "LockMessage", "", 34, ((RectTransform)play).anchoredPosition + new Vector2(0f, 95f), new Vector2(1100f, 60f));
            _message.color = new Color(1f, 0.78f, 0.35f, 1f);
        }

        _message.text = text;
        _message.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    private static Text NewLabel(Transform parent, string name, string text, int size, Vector2 pos, Vector2 box)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = box;

        Text label = go.AddComponent<Text>();
        label.font = UIFont.Default;
        label.text = text;
        label.fontSize = size;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        Outline outline = go.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.7f);
        outline.effectDistance = new Vector2(2f, -2f);
        UiTranslator.Track(label);
        return label;
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
        ShowMessage(IsLocked(_pending) ? "Not unlocked online. Get it in the Shop." : "");
        RefreshHighlight();
    }

    // Same character: continue. A different one: save it plus the pending match mode and reload
    // the scene (HomeUI resumes the mode after the reload).
    public void Confirm()
    {
        // Online, a character the account does not own cannot be played: say so, stay here.
        if (IsLocked(_pending))
        {
            ShowMessage("Not unlocked online. Get it in the Shop.");
            return;
        }

        if (_canvas != null)
        {
            _canvas.SetActive(false);
        }
        int current = _cs != null ? _cs.CurrentIndex : _pending;
        if (_cs != null && _pending != current)
        {
            LoadingScreen.ShowInstant();
            PlayerPrefs.SetInt(_cs.prefsKey, _pending);
            PlayerPrefs.SetInt(string.IsNullOrEmpty(_resumeKey) ? pendingModeKey : _resumeKey, _mode);
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
