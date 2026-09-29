using UnityEngine;
using UnityEngine.UI;

// NOTE: Small icon-swap helper - polls CameraController._freeLook each frame and updates its own
// Image sprite when the value changes. Accesses _freeLook directly (raw field read in the
// decompiled code), same unverified-accessibility caveat noted for this field in earlier
// sessions (Mode1FirstPersonGun, LoadingScreen).
//
// RE-VERIFICATION PASS: checked Apply()'s full control flow (img/cam null-and-refetch handling,
// the force||changed gating, the offset-based sprite selection, the graceful no-op when the
// resolved sprite field is null) against the raw pseudocode - matches exactly. One redundant
// defensive null-check in the raw (`if (cam == null) crash;` immediately after a path that
// already guarantees cam != null) is safely omitted here since it can never fire; that's a
// register-reuse artifact, not a behavior difference.
public class ScoutCamButtonIcon : MonoBehaviour
{
    // NOTE: confirmed to exist via raw field offsets (0x20 when freeLook == true, 0x28 when
    // false) - names inferred from purpose, unconfirmed. The mapping of which offset is
    // freeLookOnSprite vs freeLookOffSprite assumes declaration order matches offset order;
    // not independently verifiable from this dump alone.
    [SerializeField] private Sprite freeLookOnSprite;
    [SerializeField] private Sprite freeLookOffSprite;

    private Image img;
    private CameraController cam;
    private int _last = -1;

    private void Start()
    {
        img = GetComponent<Image>();
        cam = FindFirstObjectByType<CameraController>();
        Apply(true);
    }

    private void Update()
    {
        Apply(false);
    }

    private void Apply(bool force)
    {
        if (img == null)
        {
            return;
        }

        if (cam == null)
        {
            cam = FindFirstObjectByType<CameraController>();
            if (cam == null)
            {
                return;
            }
        }

        int freeLook = cam._freeLook ? 1 : 0;
        if (!force && _last == freeLook)
        {
            return;
        }

        _last = freeLook;
        Sprite sprite = (freeLook != 0) ? freeLookOnSprite : freeLookOffSprite;
        if (sprite != null)
        {
            img.sprite = sprite;
        }
    }
}