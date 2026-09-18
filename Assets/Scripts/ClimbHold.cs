using System;
using UnityEngine;
using UnityEngine.EventSystems;

// NOTE: confirms and corrects the earlier WallClimber.cs caveat about MakeButton's press/release
// wiring — the real field names here are onDown/onUp, not the onPress/onRelease guessed there.
public class ClimbHold : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public Action onDown;
    public Action onUp;

    // No custom ctor needed — the decompiled ctor only calls the implicit MonoBehaviour base
    // constructor.

    public void OnPointerDown(PointerEventData e)
    {
        onDown?.Invoke();
    }

    public void OnPointerUp(PointerEventData e)
    {
        onUp?.Invoke();
    }
}