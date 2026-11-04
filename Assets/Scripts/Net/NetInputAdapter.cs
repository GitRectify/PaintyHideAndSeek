// Copied unchanged from the Meccha Client SDK sample "Online integration" (v1.0.0). Don't import
// the sample's own copy of this file as well, or NetInputAdapter is defined twice.

using UnityEngine;

namespace Meccha.Client
{
    /// <summary>
    /// Bridges the existing gameplay scripts to the netcode.
    ///
    /// Everything network-aware lives here so the reconstructed scripts stay untouched: the
    /// joystick still reports an axis, the camera still owns the yaw, the fire button still
    /// fires. That separation matters while those scripts are still being rebuilt from the
    /// decompile — they will change, and none of those changes should break the protocol.
    ///
    /// Field names match the reversed client: Joystick exposes Horizontal/Vertical (Joystick
    /// Pack), CameraController exposes yaw/pitch/firstPerson.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetInputAdapter : MonoBehaviour, INetInputSource
    {
        [Header("Scene references (leave empty to find at runtime)")]
        [SerializeField] private Joystick joystick;
        [SerializeField] private CameraController cameraController;

        [Header("Button state, driven by the existing UI")]
        [SerializeField] private bool jumpHeld;
        [SerializeField] private bool fireHeld;
        [SerializeField] private bool proneHeld;
        [SerializeField] private bool climbUpHeld;
        [SerializeField] private bool climbDownHeld;
        [SerializeField] private bool paintPanelOpen;

        private void Awake()
        {
            // FindAnyObjectByType: Unity 6.5 deprecated FindFirstObjectByType. The scene has one of each.
            if (joystick == null) joystick = FindAnyObjectByType<Joystick>();
            if (cameraController == null) cameraController = FindAnyObjectByType<CameraController>();
        }

        private void OnEnable()
        {
            if (MecchaNetClient.Instance != null) MecchaNetClient.Instance.InputSource = this;
        }

        private void OnDisable()
        {
            if (MecchaNetClient.Instance != null && ReferenceEquals(MecchaNetClient.Instance.InputSource, this))
                MecchaNetClient.Instance.InputSource = null;
        }

        // ---- INetInputSource ----------------------------------------------------------------

        public Vector2 MoveAxis => joystick != null
            ? new Vector2(joystick.Horizontal, joystick.Vertical)
            : Vector2.zero;

        /// <summary>
        /// The camera's yaw is the player's facing: JoystickMover moves relative to the camera,
        /// so this is the value the server has to integrate against or predicted and authoritative
        /// movement will point in different directions.
        /// </summary>
        public float YawDegrees => cameraController != null ? cameraController.yaw : transform.eulerAngles.y;

        public float PitchDegrees => cameraController != null ? cameraController.pitch : 0f;

        public bool Jump => jumpHeld;
        public bool Fire => fireHeld;
        public bool Prone => proneHeld;
        public bool ClimbUp => climbUpHeld;
        public bool ClimbDown => climbDownHeld;
        public bool PaintPanelOpen => paintPanelOpen;

        // ---- Called by the existing UI ------------------------------------------------------

        public void SetJump(bool held) => jumpHeld = held;
        public void SetProne(bool held) => proneHeld = held;
        public void SetPaintPanelOpen(bool open) => paintPanelOpen = open;

        /// <summary>
        /// Wire these to WallClimber's two on-screen buttons. It drives climbing from buttons
        /// rather than the joystick — the joystick moves you sideways along the wall — so the
        /// server needs both states separately to reproduce the same motion.
        /// </summary>
        public void SetClimbUp(bool held) => climbUpHeld = held;
        public void SetClimbDown(bool held) => climbDownHeld = held;

        /// <summary>
        /// Hook this to the fire button. The direction is what the server rewinds against, so it
        /// must be the direction actually drawn on screen this frame — take it from the camera or
        /// the barrel, never from a stored aim that may be a frame stale.
        /// </summary>
        public void FirePressed(Vector3 worldDirection)
        {
            fireHeld = true;

            if (MecchaNetClient.Instance != null) MecchaNetClient.Instance.Fire(worldDirection);
        }

        public void FireReleased() => fireHeld = false;
    }
}
