using System;
using UnityEngine;
using UnityEngine.EventSystems;

// Referenced from Mode1FirstPersonGun via SceneUI.GetOrAdd<UIDrag> (see that file's caveats).
// Body now fully decompiled. It's a thin pointer-event-to-delegate forwarder: each handler
// just invokes a backing Action<PointerEventData> field if one has been assigned, otherwise
// does nothing.
//
// CORRECTION from the earlier call-surface-only guess: the callback field is named "cb" (not
// "onDrag" as previously guessed), and — confirmed by this decompile — "cb" is shared by BOTH
// OnPointerDown and OnDrag, not just OnDrag. onMove/onUp/onExit are each their own separate field.
//
// NOTE: field accessibility (public vs internal/serialized) is unconfirmed — decompiled as
// plain field access either way. Declared here as public so external code can assign the
// callbacks, consistent with how a hand-wired "drag helper" component would typically be used;
// flagging this as inferred, not decompiled.
//
// NOTE: the exact interface list implemented isn't visible from method bodies alone (only the
// method bodies were in the dump, not the class's declared interface list) — the methods below
// match IPointerDownHandler, IDragHandler, IPointerUpHandler, IPointerExitHandler, and
// IPointerMoveHandler by signature, so those are declared here on that basis.
public class UIDrag : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerMoveHandler, IPointerUpHandler, IPointerExitHandler
{
    public Action<PointerEventData> cb;
    public Action<PointerEventData> onMove;
    public Action<PointerEventData> onUp;
    public Action<PointerEventData> onExit;

    public void OnPointerDown(PointerEventData e)
    {
        cb?.Invoke(e);
    }

    public void OnDrag(PointerEventData e)
    {
        cb?.Invoke(e);
    }

    public void OnPointerMove(PointerEventData e)
    {
        onMove?.Invoke(e);
    }

    public void OnPointerUp(PointerEventData e)
    {
        onUp?.Invoke(e);
    }

    public void OnPointerExit(PointerEventData e)
    {
        onExit?.Invoke(e);
    }
}