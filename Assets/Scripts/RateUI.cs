using System.Globalization;
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
//
// 2026-11-19: ratings are saved (RatingSync -> the server, read in the admin panel). Rate Now
// saves the stars; the feedback popup's Submit (which had no handler) saves the words with them;
// the thanks popup's Continue closes it. The popup opens on the player's own rating, and a line
// under the stars shows it and everyone's average.
//
// 2026-11-20: rating stays in the game. Every rating goes on to the feedback popup (it used to
// open the app store page in a browser at 4 stars or more, with nowhere to write anything), and
// the comment box there is the game's own: the scene's TextMesh Pro field has no text, viewport
// or placeholder wired, so nothing typed into it ever showed or was sent.
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
    [Tooltip("Popup shown once the rating and its comment are saved. Blank = skip.")]
    public string thanksCanvasName = "ThanksCanvas";
    [Tooltip("Feedback popup shown after Rate Now: the comment box and Submit. Blank = skip (just close).")]
    public string helpCanvasName = "HelpCanvas";

    [Header("Rating")]
    [Tooltip("Reset the stars to 0 each time the popup becomes visible.")]
    public bool resetOnShow = true;

    private GameObject _canvas;
    private InputField _comment;
    private GameObject[] _on;
    private int _rating;
    private bool _resolved;
    private GameObject _thanks;
    private GameObject _help;
    private bool _helpWired, _thanksWired;
    private Text _summary;

    public int Rating => _rating;

    private void Awake()
    {
        Resolve();
    }

    private void OnEnable()
    {
        if (_resolved && resetOnShow)
        {
            SetRating(RatingSync.MyStars);   // the player's own rating, 0 when they have none
        }
        RatingSync.Changed += ShowSummary;
        ShowSummary();
        RatingSync.Refresh();                // the latest average, when signed in
    }

    private void OnDisable()
    {
        RatingSync.Changed -= ShowSummary;
    }

    // Design B (2026-11-24): rating the game is RateScreen's page. Whatever opens this popup (the
    // side screen's star, a round's end) gets the page instead, before the popup is ever drawn.
    private void LateUpdate()
    {
        if (_canvas == null || !_canvas.activeInHierarchy) return;
        Hide();
        RateScreen.Open(OnlineLobby.InMultiplayer && PlaySetupUI.IsOpen);
    }

    // Under the stars: the player's rating and everyone's average, as far as they are known.
    private void ShowSummary()
    {
        if (_summary == null) return;

        string text = "";
        if (RatingSync.MyStars > 0)
        {
            text = string.Format("Your rating: {0}/5", RatingSync.MyStars);
            if (!RatingSync.Saved) text += "\n" + "Saved online at your next sign-in.";
        }

        if (RatingSync.Count > 0)
        {
            string average = RatingSync.Average.ToString("0.0", CultureInfo.InvariantCulture);
            string line = string.Format("Players' average: {0}/5 ({1} ratings)", average, RatingSync.Count);
            text = text.Length == 0 ? line : text + "\n" + line;
        }

        _summary.text = text;
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

        // The line between the stars (y 38) and Rate Now (top at y -86).
        Transform popup = FindUnder(root, "Popup");
        if (popup != null)
        {
            _summary = OnlineUI.NewLabel(popup, "", 24, new Vector2(0f, -46f), new Vector2(680f, 76f));
            _summary.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.6f);
        }

        _resolved = true;
        SetRating(RatingSync.MyStars);
        ShowSummary();
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

    // Some stars: saved at once, then the feedback popup for a comment (its Submit saves the words
    // with the stars, and thanks). None: just close. Nothing leaves the game.
    public void RateNow()
    {
        if (_rating > 0)
        {
            RatingSync.Submit(_rating, RatingSync.MyComment);
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
            if (!_thanksWired)
            {
                _thanksWired = true;
                WireButton(thanks.transform, "ButtonContinue", () => thanks.SetActive(false));
            }
            thanks.SetActive(true);
        }
    }

    // The feedback popup's Submit: the words go with the stars, then thanks.
    private void SubmitHelp()
    {
        string words = _comment != null ? _comment.text : "";
        GameObject help = ResolveCanvas(ref _help, helpCanvasName);
        if (help != null) help.SetActive(false);

        int stars = _rating > 0 ? _rating : RatingSync.MyStars;
        if (stars > 0) RatingSync.Submit(stars, words);
        AudioManager.ButtonTap();
        ShowThanks();
    }

    // The feedback popup shows the same stars that were picked.
    public void ShowHelp()
    {
        GameObject help = ResolveCanvas(ref _help, helpCanvasName);
        if (help != null)
        {
            MirrorStars(help.transform);
            if (!_helpWired)
            {
                _helpWired = true;
                WireButton(help.transform, "ButtonSubmit", SubmitHelp);
            }

            // The words the player wrote last time, to change rather than retype.
            if (_comment == null) _comment = BuildCommentBox(help.transform);
            if (_comment != null) _comment.text = RatingSync.MyComment;
        }
        Hide();
        if (help != null)
        {
            help.SetActive(true);
        }
    }

    // The comment box, where the scene's broken one was (same place and size), in the game's own
    // font so Chinese and Russian show: several lines, up to the server's 500 characters.
    private static InputField BuildCommentBox(Transform help)
    {
        Transform old = FindUnder(help, "InputField");
        Transform parent = old != null ? old.parent : FindUnder(help, "Popup");
        if (parent == null) return null;

        Vector2 pos = new Vector2(0f, -65f), size = new Vector2(524f, 143f);
        if (old is RectTransform oldRect)
        {
            pos = oldRect.anchoredPosition;
            size = oldRect.sizeDelta;
            old.gameObject.SetActive(false);
        }

        InputField field = OnlineUI.NewInput(parent, "Tell us what you think (optional)", pos, size);
        field.lineType = InputField.LineType.MultiLineNewline;
        field.characterLimit = Meccha.Shared.Protocol.MetaCodec.MaxRatingCommentLength;
        foreach (Text t in new[] { field.textComponent, field.placeholder as Text })
        {
            if (t == null) continue;
            t.fontSize = 26;
            t.alignment = TextAnchor.UpperLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.rectTransform.offsetMin = new Vector2(16f, 10f);
            t.rectTransform.offsetMax = new Vector2(-16f, -10f);
        }
        field.name = "CommentInput";
        return field;
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

    // The home screen's rate button used to go away once rated. It stays (2026-11-19): it is where
    // the player sees their rating and everyone's average, and changes their rating. Rating still
    // stops the popup from opening by itself (GameData.isRate).
    private static void HideRateButton()
    {
    }

    public void Late()
    {
        Hide();
    }

    public void Show()
    {
        if (resetOnShow)
        {
            SetRating(RatingSync.MyStars);
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
