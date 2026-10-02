using UnityEngine;

// Per-character camera framing for the Seeker (Mode 1) camera, read by
// CameraController.UpdateCam through ActiveCamProfile (so the fields are public, as in dump.cs,
// TypeDefIndex 9714). Fields, order, headers and tooltips match the dump; defaults come from the
// raw .ctor. No methods besides the constructor.
public class SeekerCamProfile : MonoBehaviour
{
    [Header("Camera POSITION (relative to the character)")]
    [Tooltip("Camera height relative to the character's pivot (hips). Higher value = higher camera; lower value = lower camera (watch out for the camera clipping into the head when tpsBack ≈ 0).")]
    public float tpsHeight = 4.99f;
    [Tooltip("How far back to pull the camera. 0 = directly overhead (first-person); higher values ​​= pulled back (third-person, character visible).")]
    public float tpsBack = 0.12f;
    [Tooltip("Shifted horizontally toward the right shoulder (+ = right). When shifted to the right, the character is positioned on the left side of the frame.")]
    public float tpsSide = -0.07f;

    [Header("Where the camera LOOKS (aim point ahead of the character)")]
    [Tooltip("How far ahead of the character is the aiming point? (Keep the room in the frame to find it.)")]
    public float tpsLookAhead = 10.7f;
    [Tooltip("The height of the aiming point relative to the pivot. A higher value means the camera tilts down less, positioning the frame/crosshair lower on the screen; a lower value results in a steeper downward tilt.")]
    public float tpsLookHeight = 2.5f;
    [Tooltip("Lateral offset of the aiming point. Set using `tpsSide` so the camera looks straight ahead (without angling toward the character).")]
    public float tpsLookSide = 0.5f;

    [Header("Lie down (btnnam / pose_nam button) — Lower camera when lying down")]
    [Tooltip("When lying down, by how much (in world units) is the camera lowered compared to the standing position? A higher value means the camera is closer to the ground when lying down; 0 means it is not lowered.")]
    public float proneHeightDrop = 2.5f;
    [Tooltip("When in the prone position, lower the AIM POINT by that same amount so that the entire view shifts downward (preventing it from tilting up toward the sky). This is typically set close to `proneHeightDrop`.")]
    public float proneLookDrop = 1.8f;
    [Tooltip("When prone, the camera's backward offset uses an absolute value instead of `tpsBack` (not a delta); a negative value positions the camera in front of the character. While standing, `tpsBack` is still used. Smoothly interpolate using `proneLerpSpeed`.")]
    public float proneBack = 0.12f;
}
