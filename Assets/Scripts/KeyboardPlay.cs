using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Plays the game from a keyboard with the player's bindings (KeyBindings). Movement goes into the
/// joystick's direction (JoystickMover, NetInputAdapter); turning and aiming call what the turn
/// buttons and the aim drag call; every other action presses the on-screen button for it, the way
/// a finger does. So a key works exactly when and how its button does: Fire only for a seeker in
/// a round, Climb only beside a wall, Start only while hiding, the same online and offline.
/// Created at start and kept for the session; it does nothing without a keyboard.
/// </summary>
public sealed class KeyboardPlay : MonoBehaviour
{
    // Degrees a second the aim keys tilt the gun, and the turn keys turn the body.
    private const float AimKeySpeed = 60f;

    private static KeyboardPlay instance;

    // Tapped buttons: the on-screen ones each action presses, by the names the scene and the
    // scripts give them.
    private static readonly Dictionary<GameAction, string> TapButtons = new Dictionary<GameAction, string>
    {
        { GameAction.Jump, "Jump" },
        { GameAction.Fire, "FireBtn" },
        { GameAction.LieDown, "btnnam" },
        { GameAction.Paint, "PaintGateButton" },
        { GameAction.Poses, "Btn_DoiDang" },
        { GameAction.FreeCamera, "ButtonCamera" },
        { GameAction.StartRound, "ButtonStart" },
    };

    // Held buttons: down while the key is, up when it is let go.
    private static readonly Dictionary<GameAction, string> HoldButtons = new Dictionary<GameAction, string>
    {
        { GameAction.ClimbUp, "ClimbUp" },
        { GameAction.ClimbDown, "ClimbDown" },
    };

    private readonly Dictionary<GameAction, GameObject> holding = new Dictionary<GameAction, GameObject>();
    private int keyTurn;     // the turn the keys started: 1 to the right, -1 to the left, 0 none

    private GameModeManager gmm;
    private JoystickMover mover;
    private Mode1FirstPersonGun gun;
    private SettingsUI settings;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (instance != null) return;
        var go = new GameObject("KeyboardPlay");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<KeyboardPlay>();
    }

    private void Update()
    {
        if (Keyboard.current == null || KeyBindings.Blocked)
        {
            LetGo();
            return;
        }

        if (gmm == null) gmm = FindAnyObjectByType<GameModeManager>();
        if (mover == null) mover = FindAnyObjectByType<JoystickMover>(FindObjectsInactive.Include);
        if (gun == null) gun = FindAnyObjectByType<Mode1FirstPersonGun>(FindObjectsInactive.Include);
        if (settings == null) settings = FindAnyObjectByType<SettingsUI>(FindObjectsInactive.Include);

        if (KeyBindings.Pressed(GameAction.Settings) && settings != null)
        {
            if (KeyBindingsUI.IsOpen) KeyBindingsUI.Close();
            else if (settings.IsShown) settings.Hide();
            else settings.Show();
        }

        bool playing = gmm != null && gmm.RoundActive && !gmm.PlayerFrozen && !gmm.SeekerHideCountdown;
        Turn(playing);
        Aim(playing);

        foreach (KeyValuePair<GameAction, string> tap in TapButtons)
            if (KeyBindings.Pressed(tap.Key)) Tap(tap.Value);

        foreach (KeyValuePair<GameAction, string> hold in HoldButtons)
        {
            if (KeyBindings.Pressed(hold.Key)) Press(hold.Key, hold.Value);
            else if (!KeyBindings.Held(hold.Key)) Release(hold.Key);
        }
    }

    private void OnDisable() => LetGo();

    // The turn keys do what holding a turn button does (JoystickMover), on the floor or a wall.
    private void Turn(bool playing)
    {
        int want = !playing ? 0 : (KeyBindings.Held(GameAction.TurnRight) ? 1 : 0) - (KeyBindings.Held(GameAction.TurnLeft) ? 1 : 0);
        if (want == keyTurn || mover == null) return;

        mover.HoldTurn(want);
        keyTurn = want;
    }

    // The aim keys tilt a seeker's gun up and down, as dragging on the screen does.
    private void Aim(bool playing)
    {
        if (!playing || gun == null || !gun.GunModeOn) return;
        float tilt = (KeyBindings.Held(GameAction.AimUp) ? 1f : 0f) - (KeyBindings.Held(GameAction.AimDown) ? 1f : 0f);
        if (tilt != 0f) gun.AimBy(0f, tilt * AimKeySpeed * Time.unscaledDeltaTime);
    }

    // ---- Pressing on-screen buttons as a finger does ----

    private static void Tap(string buttonName)
    {
        GameObject button = Find(buttonName);
        if (button == null) return;
        PointerEventData data = Pointer(button);
        ExecuteEvents.ExecuteHierarchy(button, data, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(button, data, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.ExecuteHierarchy(button, data, ExecuteEvents.pointerClickHandler);
    }

    private void Press(GameAction action, string buttonName)
    {
        GameObject button = Find(buttonName);
        if (button == null) return;
        ExecuteEvents.ExecuteHierarchy(button, Pointer(button), ExecuteEvents.pointerDownHandler);
        holding[action] = button;
    }

    private void Release(GameAction action)
    {
        if (!holding.TryGetValue(action, out GameObject button)) return;
        holding.Remove(action);
        if (button != null) ExecuteEvents.ExecuteHierarchy(button, Pointer(button), ExecuteEvents.pointerUpHandler);
    }

    // Everything the keys hold, let go (typing began, the Controls screen opened, no keyboard).
    private void LetGo()
    {
        foreach (GameAction action in new List<GameAction>(holding.Keys)) Release(action);
        if (keyTurn != 0 && mover != null) mover.HoldTurn(0);
        keyTurn = 0;
    }

    private static PointerEventData Pointer(GameObject button)
    {
        var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
        if (button.transform is RectTransform rt) data.position = RectTransformUtility.WorldToScreenPoint(null, rt.position);
        data.pointerPress = button;
        return data;
    }

    // The button of that name on screen now, if it can be pressed.
    private static GameObject Find(string buttonName)
    {
        foreach (RectTransform rt in FindObjectsByType<RectTransform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (rt.name != buttonName || !rt.gameObject.activeInHierarchy) continue;
            UnityEngine.UI.Selectable selectable = rt.GetComponent<UnityEngine.UI.Selectable>();
            if (selectable != null && !selectable.IsInteractable()) continue;
            return rt.gameObject;
        }
        return null;
    }
}
