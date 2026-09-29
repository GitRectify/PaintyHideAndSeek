using System;
using UnityEngine;
using UnityEngine.EventSystems;

// NOTE: OnPointerDown and OnDrag both invoke the SAME backing field (`onPointer`) — this isn't a
// decompiler artifact, both methods independently read `(__this->fields).onPointer`. OnPointerUp
// uses a separate field (`onUp`). Both fields are directly-settable public Actions (no
// AddListener/RemoveListener wrapping visible in this dump), consistent with a small drag-relay
// component that just forwards pointer events to whoever owns/wires it (e.g. a joystick).
public class JoyDrag : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    public Action<PointerEventData> onPointer;
    public Action<PointerEventData> onUp;

    public void OnPointerDown(PointerEventData e)
    {
        onPointer?.Invoke(e);
    }

    public void OnDrag(PointerEventData e)
    {
        onPointer?.Invoke(e);
    }

    public void OnPointerUp(PointerEventData e)
    {
        onUp?.Invoke(e);
    }
}