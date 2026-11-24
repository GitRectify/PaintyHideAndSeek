using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// The Shoot button held down (2026-11-24, user's request): while a finger, the mouse or the Fire
/// key (KeyboardPlay presses the button as a finger does) holds it, the gun fires again each time
/// its cooldown ends online, and at the seeker bots' rate in Single Play (which has no cooldown);
/// letting go stops it at once. The gun does the firing and keeps the timing
/// (Mode1FirstPersonGun.PressFire / ReleaseFire); this only says when the button is down. With
/// several fingers on the screen, only the ones that came down on this button count.
/// </summary>
[DisallowMultipleComponent]
public sealed class FireHold : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public Mode1FirstPersonGun gun;

    private readonly HashSet<int> pointers = new HashSet<int>();

    public void OnPointerDown(PointerEventData e)
    {
        if (e.button != PointerEventData.InputButton.Left) return;
        pointers.Add(e.pointerId);
        if (gun != null) gun.PressFire();
    }

    // Comes to this button whichever way the finger went after it came down here.
    public void OnPointerUp(PointerEventData e)
    {
        if (!pointers.Remove(e.pointerId)) return;
        if (pointers.Count == 0) LetGo();
    }

    // Hidden with the round, or the game put in the background: nothing is held any more.
    private void OnDisable() => LetGo();

    private void OnApplicationFocus(bool focused)
    {
        if (!focused) LetGo();
    }

    private void LetGo()
    {
        pointers.Clear();
        if (gun != null) gun.ReleaseFire();
    }
}
