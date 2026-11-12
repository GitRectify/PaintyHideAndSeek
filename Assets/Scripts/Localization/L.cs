using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>The languages the game is translated into.</summary>
public enum GameLanguage
{
    English = 0,
    Chinese = 1,    // Simplified Chinese
    Russian = 2,
}

/// <summary>
/// The game's text in the player's language (Settings > Language): English, Chinese or Russian.
///
/// Every translation is in Resources/Localization/strings.txt, one line per text: the English,
/// the Chinese and the Russian, separated by tabs (\n for a line break inside one). The English is
/// the key, so the game's scripts and scene keep their English and nothing has to be renamed: a
/// line may hold {0}, {1} for the parts that change ("Searching in {0}..."), and those parts are
/// translated too when they are texts of their own ("Practice").
///
/// <see cref="Translate"/> is what <see cref="UiTranslator"/> runs on every UI text just before it
/// is drawn, so a label set in English anywhere shows in the chosen language. Text with rich-text
/// tags or several lines is translated piece by piece. Anything not in the table — a player's
/// name, a number — is left as it is.
/// </summary>
public static class L
{
    private const string PrefsKey = "GameLanguage";
    private const string TablePath = "Localization/strings";

    /// <summary>Raised after the language changes, once every label has been told.</summary>
    public static event Action Changed;

    private static bool loaded;
    private static GameLanguage current;

    // English text -> the other languages, exact; and the {0} lines as patterns, longest first.
    private static readonly Dictionary<string, string[]> exact = new Dictionary<string, string[]>(StringComparer.Ordinal);
    private static readonly List<(Regex Pattern, string[] Texts, int Places)> patterns = new List<(Regex, string[], int)>();

    // What a whole string came out as, per language (strings repeat every frame).
    private static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);
    private const int CacheLimit = 4000;

    private static readonly Regex Tag = new Regex(@"(<[^<>]+>)", RegexOptions.Compiled);

    public static GameLanguage Current
    {
        get
        {
            EnsureLoaded();
            return current;
        }
    }

    /// <summary>The names the language picker shows: each in its own language.</summary>
    public static string NameOf(GameLanguage language)
    {
        switch (language)
        {
            case GameLanguage.Chinese: return "中文";
            case GameLanguage.Russian: return "Русский";
            default: return "English";
        }
    }

    /// <summary>Whether the language needs letters a Latin-only font lacks.</summary>
    public static bool NeedsWideFont(GameLanguage language) => language != GameLanguage.English;

    public static void Set(GameLanguage language)
    {
        EnsureLoaded();
        if (language == current) return;

        current = language;
        cache.Clear();
        try
        {
            PlayerPrefs.SetInt(PrefsKey, (int)language);
            PlayerPrefs.Save();
        }
        catch (Exception)
        {
            // Saved next time; the change applies now anyway.
        }

        Changed?.Invoke();
    }

    /// <summary>The text in the current language: the English as given when there is no translation.</summary>
    public static string T(string english)
    {
        if (string.IsNullOrEmpty(english)) return english;
        EnsureLoaded();
        return current == GameLanguage.English ? english : Translate(english, current);
    }

    /// <summary>A {0}-style line in the current language, filled in.</summary>
    public static string F(string english, params object[] args) => string.Format(CultureInfo.InvariantCulture, T(english), args);

    /// <summary>
    /// The whole of a UI string in a language: exactly, as a {0} line, or piece by piece around
    /// rich-text tags and line breaks. Unknown text comes back unchanged.
    /// </summary>
    public static string Translate(string text, GameLanguage language)
    {
        if (language == GameLanguage.English || string.IsNullOrEmpty(text)) return text;
        EnsureLoaded();

        if (cache.TryGetValue(text, out string done)) return done;

        string result = TranslateWhole(text, language) ?? TranslatePieces(text, language);
        if (cache.Count > CacheLimit) cache.Clear();
        cache[text] = result;
        return result;
    }

    // ================= Inside =================

    private static string TranslateWhole(string text, GameLanguage language)
    {
        int index = (int)language - 1;

        if (exact.TryGetValue(text, out string[] texts)) return texts[index];

        string trimmed = text.Trim();
        if (trimmed.Length != text.Length && trimmed.Length > 0 && exact.TryGetValue(trimmed, out texts))
            return text.Replace(trimmed, texts[index]);

        foreach ((Regex pattern, string[] lines, int places) in patterns)
        {
            Match match = pattern.Match(text);
            if (!match.Success) continue;

            var args = new object[places];
            for (int i = 0; i < places; i++)
            {
                string part = match.Groups["p" + i].Value;
                args[i] = exact.TryGetValue(part, out string[] own) ? own[index] : part;   // "Practice" in "Searching in Practice..."
            }

            try
            {
                return string.Format(CultureInfo.InvariantCulture, lines[index], args);
            }
            catch (FormatException)
            {
                return lines[index];
            }
        }

        return null;
    }

    // Rich text and several lines: each run of words between tags, and each line, on its own.
    private static string TranslatePieces(string text, GameLanguage language)
    {
        if (text.IndexOf('<') < 0 && text.IndexOf('\n') < 0) return text;

        var built = new StringBuilder(text.Length + 16);
        bool changed = false;
        string[] lines = text.Split('\n');

        for (int l = 0; l < lines.Length; l++)
        {
            string line = lines[l];
            if (l > 0) built.Append('\n');

            string whole = line.Length > 0 ? TranslateWhole(line, language) : null;
            if (whole != null)
            {
                built.Append(whole);
                changed = true;
                continue;
            }

            foreach (string piece in Tag.Split(line))
            {
                if (piece.Length == 0 || piece[0] == '<') { built.Append(piece); continue; }

                string own = TranslateWhole(piece, language);
                built.Append(own ?? piece);
                if (own != null) changed = true;
            }
        }

        return changed ? built.ToString() : text;
    }

    private static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;

        int saved;
        try { saved = PlayerPrefs.GetInt(PrefsKey, -1); }
        catch (Exception) { saved = -1; }
        current = saved >= 0 && saved <= 2 ? (GameLanguage)saved : FromDevice();

        TextAsset table = Resources.Load<TextAsset>(TablePath);
        if (table == null)
        {
            Debug.LogWarning("[L] Resources/" + TablePath + ".txt is missing; the game stays in English.");
            return;
        }

        var found = new List<(Regex, string[], int)>();
        foreach (string raw in table.text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

            string[] cells = line.Split('\t');
            if (cells.Length < 3) continue;

            string english = Unescape(cells[0]);
            string[] others = { Unescape(cells[1]), Unescape(cells[2]) };
            if (english.Length == 0) continue;

            if (english.Contains("{0}")) found.Add((ToPattern(english), others, CountPlaces(english)));
            else exact[english] = others;
        }

        // The most specific line first: "Use at least {0} characters." before "{0} coins".
        found.Sort((a, b) => b.Item1.ToString().Length.CompareTo(a.Item1.ToString().Length));
        patterns.AddRange(found);
    }

    // A first run picks the device's language when it is one the game has.
    private static GameLanguage FromDevice()
    {
        switch (Application.systemLanguage)
        {
            case SystemLanguage.Chinese:
            case SystemLanguage.ChineseSimplified:
            case SystemLanguage.ChineseTraditional:
                return GameLanguage.Chinese;
            case SystemLanguage.Russian:
                return GameLanguage.Russian;
            default:
                return GameLanguage.English;
        }
    }

    private static string Unescape(string cell) => cell.Replace("\\n", "\n").Replace("\\t", "\t");

    private static int CountPlaces(string english)
    {
        int n = 0;
        while (english.Contains("{" + n + "}")) n++;
        return n;
    }

    // "Searching in {0}..." -> ^Searching in (?<p0>.+?)\.\.\.$
    private static Regex ToPattern(string english)
    {
        var regex = new StringBuilder("^");
        int at = 0;
        foreach (Match place in Regex.Matches(english, @"\{(\d+)\}"))
        {
            regex.Append(Regex.Escape(english.Substring(at, place.Index - at)));
            regex.Append("(?<p").Append(place.Groups[1].Value).Append(">.+?)");
            at = place.Index + place.Length;
        }

        regex.Append(Regex.Escape(english.Substring(at))).Append('$');
        return new Regex(regex.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }
}
