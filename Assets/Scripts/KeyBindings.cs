using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>What a key can do in play (Settings &gt; Controls).</summary>
public enum GameAction
{
    MoveForward,
    MoveBack,
    MoveLeft,
    MoveRight,
    TurnLeft,
    TurnRight,
    AimUp,
    AimDown,
    Jump,
    Fire,
    LieDown,
    ClimbUp,
    ClimbDown,
    Paint,
    Poses,
    FreeCamera,
    StartRound,
    Settings,
    SwitchBullet,
}

/// <summary>
/// The keyboard's bindings: one key per action, the player's own choice (Settings &gt; Controls),
/// kept in PlayerPrefs, with the defaults below. Read through Held/Pressed/Released and Move.
/// Nothing reads the keyboard while a text field is being typed in or the Controls screen is
/// waiting for a new key.
/// </summary>
public static class KeyBindings
{
    private const string PrefsPrefix = "Keys.";

    public static readonly GameAction[] All = (GameAction[])Enum.GetValues(typeof(GameAction));

    private static readonly Dictionary<GameAction, Key> Defaults = new Dictionary<GameAction, Key>
    {
        { GameAction.MoveForward, Key.W },
        { GameAction.MoveBack, Key.S },
        { GameAction.MoveLeft, Key.A },
        { GameAction.MoveRight, Key.D },
        { GameAction.TurnLeft, Key.Q },
        { GameAction.TurnRight, Key.E },
        { GameAction.AimUp, Key.UpArrow },
        { GameAction.AimDown, Key.DownArrow },
        { GameAction.Jump, Key.Space },
        { GameAction.Fire, Key.F },
        { GameAction.LieDown, Key.C },
        { GameAction.ClimbUp, Key.R },
        { GameAction.ClimbDown, Key.X },
        { GameAction.Paint, Key.P },
        { GameAction.Poses, Key.G },
        { GameAction.FreeCamera, Key.V },
        { GameAction.StartRound, Key.Enter },
        { GameAction.Settings, Key.Escape },
        { GameAction.SwitchBullet, Key.B },
    };

    // English names: the key of the translation table (L), so they show in the player's language.
    private static readonly Dictionary<GameAction, string> Names = new Dictionary<GameAction, string>
    {
        { GameAction.MoveForward, "Move forward" },
        { GameAction.MoveBack, "Move back" },
        { GameAction.MoveLeft, "Move left" },
        { GameAction.MoveRight, "Move right" },
        { GameAction.TurnLeft, "Turn left" },
        { GameAction.TurnRight, "Turn right" },
        { GameAction.AimUp, "Aim up" },
        { GameAction.AimDown, "Aim down" },
        { GameAction.Jump, "Jump" },
        { GameAction.Fire, "Fire" },
        { GameAction.LieDown, "Lie down / stand up" },
        { GameAction.ClimbUp, "Climb up" },
        { GameAction.ClimbDown, "Climb down" },
        { GameAction.Paint, "Paint" },
        { GameAction.Poses, "Poses" },
        { GameAction.FreeCamera, "Free camera" },
        { GameAction.StartRound, "Start (end hiding)" },
        { GameAction.Settings, "Settings" },
        { GameAction.SwitchBullet, "Switch bullet" },
    };

    private static Dictionary<GameAction, Key> current;

    /// <summary>Raised after any binding changes (one key, or all reset).</summary>
    public static event Action Changed;

    /// <summary>True while the Controls screen waits for a key: play ignores the keyboard meanwhile.</summary>
    public static bool Listening { get; set; }

    /// <summary>Play ignores the keyboard: the Controls screen is listening, or a text field is being typed in.</summary>
    public static bool Blocked => Listening || Typing();

    public static Key DefaultOf(GameAction action) => Defaults[action];

    public static string NameOf(GameAction action) => Names[action];

    public static Key Get(GameAction action)
    {
        Load();
        return current[action];
    }

    /// <summary>
    /// Binds <paramref name="key"/> to <paramref name="action"/>. An action that had that key gets
    /// this action's old one, so no key ever does two things.
    /// </summary>
    public static void Set(GameAction action, Key key)
    {
        Load();
        Key old = current[action];
        foreach (GameAction other in All)
        {
            if (other == action || current[other] != key) continue;
            current[other] = old;
            Save(other);
        }

        current[action] = key;
        Save(action);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }

    /// <summary>Every action back to its default key.</summary>
    public static void ResetAll()
    {
        foreach (GameAction action in All) PlayerPrefs.DeleteKey(PrefsPrefix + action);
        PlayerPrefs.Save();
        current = null;
        Load();
        Changed?.Invoke();
    }

    /// <summary>What a key is called on this keyboard (its layout's name for it).</summary>
    public static string KeyName(Key key)
    {
        if (key == Key.None) return "-";
        try
        {
            Keyboard keyboard = Keyboard.current;
            string shown = keyboard != null ? keyboard[key].displayName : null;
            if (!string.IsNullOrWhiteSpace(shown) && shown.Length > 1) return shown;
            if (!string.IsNullOrWhiteSpace(shown)) return shown.ToUpperInvariant();
        }
        catch (Exception)
        {
            // A key this keyboard does not have: its name is still known.
        }

        switch (key)
        {
            case Key.UpArrow: return "Up";
            case Key.DownArrow: return "Down";
            case Key.LeftArrow: return "Left";
            case Key.RightArrow: return "Right";
            case Key.Escape: return "Esc";
            default: return key.ToString();
        }
    }

    public static bool Held(GameAction action) => Read(action, k => k.isPressed);
    public static bool Pressed(GameAction action) => Read(action, k => k.wasPressedThisFrame);
    public static bool Released(GameAction action) => Read(action, k => k.wasReleasedThisFrame);

    /// <summary>The movement keys as a stick: x to the right, y forward, never longer than 1.</summary>
    public static Vector2 Move
    {
        get
        {
            if (Keyboard.current == null || Blocked) return Vector2.zero;
            var move = new Vector2(
                (Held(GameAction.MoveRight) ? 1f : 0f) - (Held(GameAction.MoveLeft) ? 1f : 0f),
                (Held(GameAction.MoveForward) ? 1f : 0f) - (Held(GameAction.MoveBack) ? 1f : 0f));
            return Vector2.ClampMagnitude(move, 1f);
        }
    }

    /// <summary>A stick and the movement keys together, no longer than 1.</summary>
    public static Vector2 WithKeys(Vector2 stick) => Vector2.ClampMagnitude(stick + Move, 1f);

    private static bool Read(GameAction action, Func<UnityEngine.InputSystem.Controls.KeyControl, bool> test)
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || Blocked) return false;
        Key key = Get(action);
        if (key == Key.None) return false;
        try
        {
            return test(keyboard[key]);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Typing()
    {
        EventSystem events = EventSystem.current;
        GameObject selected = events != null ? events.currentSelectedGameObject : null;
        if (selected == null) return false;
        InputField field = selected.GetComponent<InputField>();
        return field != null && field.isFocused;
    }

    private static void Load()
    {
        if (current != null) return;
        current = new Dictionary<GameAction, Key>();
        foreach (GameAction action in All)
        {
            int saved = PlayerPrefs.GetInt(PrefsPrefix + action, -1);
            current[action] = saved > 0 && Enum.IsDefined(typeof(Key), saved) ? (Key)saved : Defaults[action];
        }
    }

    private static void Save(GameAction action) => PlayerPrefs.SetInt(PrefsPrefix + action, (int)current[action]);
}
