using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Puts every UI text in the player's language (L) just before it is drawn. The scene's labels
/// and the ones scripts build keep their English; whenever a text's content changes — a label
/// set, a status message, a counter — it is looked up in L's table on the frame it changes and
/// replaced with the translation before the canvas renders, so English never shows first.
///
/// The game's font (MikadoMedium) has neither Cyrillic nor Chinese letters. A text in Russian or
/// Chinese that its font cannot draw is given the built-in dynamic font, which draws Cyrillic
/// itself and Chinese through the system's fonts; back in English it gets its own font again.
///
/// Typing in an input field is never translated. Created at start and kept for the session.
/// </summary>
public sealed class UiTranslator : MonoBehaviour
{
    private sealed class Seen
    {
        public Text Text;
        public string Source;     // what the game set, in English
        public string Written;    // what is on it now (the translation, or the source)
        public Font OwnFont;
        public bool Swapped;
    }

    private const float RescanSeconds = 0.5f;

    private static UiTranslator instance;
    private static Font wideFont;

    private readonly Dictionary<Text, Seen> seen = new Dictionary<Text, Seen>();
    private readonly List<Text> gone = new List<Text>();
    private readonly HashSet<Text> typed = new HashSet<Text>();
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (instance != null) return;
        var go = new GameObject("UiTranslator");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<UiTranslator>();
    }

    /// <summary>A text built by a script: looked after from now on (otherwise found within half a second).</summary>
    public static void Track(Text text)
    {
        if (instance == null || text == null) return;
        instance.Visit(text);
    }

    /// <summary>The font every language can be drawn in: built in, dynamic, with the system's fonts behind it.</summary>
    public static Font WideFont
    {
        get
        {
            if (wideFont == null) wideFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return wideFont;
        }
    }

    private void OnEnable()
    {
        Canvas.willRenderCanvases += BeforeCanvasesRender;
        SceneManager.sceneLoaded += OnSceneLoaded;
        L.Changed += OnLanguageChanged;
    }

    private void OnDisable()
    {
        Canvas.willRenderCanvases -= BeforeCanvasesRender;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        L.Changed -= OnLanguageChanged;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => nextScan = 0f;

    private void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + RescanSeconds;
        Scan();
    }

    // New texts: the scene's at load, and whatever scripts build later.
    private void Scan()
    {
        typed.Clear();
        foreach (InputField field in FindObjectsByType<InputField>(FindObjectsInactive.Include))
        {
            if (field.textComponent != null) typed.Add(field.textComponent);
        }

        foreach (Text text in FindObjectsByType<Text>(FindObjectsInactive.Include))
        {
            if (!seen.ContainsKey(text)) Visit(text);
        }
    }

    private void Visit(Text text)
    {
        if (typed.Contains(text)) return;

        var entry = new Seen { Text = text, OwnFont = text.font };
        seen[text] = entry;
        Apply(entry, text.text);
    }

    // Every frame, before anything is drawn: only texts whose content changed since we last wrote them.
    private void BeforeCanvasesRender()
    {
        gone.Clear();
        foreach (KeyValuePair<Text, Seen> pair in seen)
        {
            Seen entry = pair.Value;
            if (entry.Text == null) { gone.Add(pair.Key); continue; }

            string now = entry.Text.text;
            if (ReferenceEquals(now, entry.Written) || now == entry.Written) continue;

            Apply(entry, now);
        }

        foreach (Text dead in gone) seen.Remove(dead);
    }

    private void OnLanguageChanged()
    {
        foreach (Seen entry in seen.Values)
        {
            if (entry.Text != null) Apply(entry, entry.Source);
        }
    }

    private static void Apply(Seen entry, string source)
    {
        entry.Source = source;
        GameLanguage language = L.Current;
        string shown = L.Translate(source, language);

        // Also when the language is English again: the label still shows the last translation.
        if (entry.Text.text != shown) entry.Text.text = shown;
        entry.Written = entry.Text.text;

        FitFont(entry, shown, language);
    }

    // The game's own font where it can draw the text; the wide one where it cannot — in any
    // language, since a player's name may be Chinese or Russian on an English screen.
    private static void FitFont(Seen entry, string shown, GameLanguage language)
    {
        Text text = entry.Text;
        bool needWide = !string.IsNullOrEmpty(shown) && !CanDraw(entry.OwnFont, shown);

        if (needWide && !entry.Swapped)
        {
            if (text.font != WideFont) entry.OwnFont = text.font;
            text.font = WideFont;
            entry.Swapped = true;
        }
        else if (!needWide && entry.Swapped)
        {
            text.font = entry.OwnFont;
            entry.Swapped = false;
        }
    }

    private static bool CanDraw(Font font, string text)
    {
        if (font == null) return false;
        if (font == WideFont) return true;

        foreach (char c in text)
        {
            if (c < 0x80 || char.IsWhiteSpace(c)) continue;
            if (!font.HasCharacter(c)) return false;
        }

        return true;
    }
}
