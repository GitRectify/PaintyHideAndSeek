using Meccha.Client;
using Meccha.Shared.Game;
using UnityEngine;

// Plays another player's body from what the server replicates: lying down and climbing from the
// replicated flags, moving from how the body is being drawn, and a shot when one is drawn. Their
// struck pose comes from OnlineRound.ApplyRemoteLoadout. BodyAnimation does the switching, exactly
// as it does for the player's own body and the bots. A bot this game hosts is left to its controller.
//
// Which way the body faces is the server's word, on a wall as anywhere else: the way its player
// has turned it (a seeker turns with their aim, a hider with the turn buttons) or, for a bot, the
// way its host has. It used to be turned to the wall here instead, so a seeker looking round from
// a wall never turned on anybody else's screen.
//
// A seeker's gun points where that player aims: their arm tilts with the aim pitch the server
// relays (NetEntityView.AimPitch) as the player's own does, and the gun is turned onto the line
// their shots fly along (GunAim), standing or lying down. Before, it followed the hand's
// animation only and never tilted up or down on anybody else's screen.
[DisallowMultipleComponent]
public class NetBodyAnimator : MonoBehaviour
{
    private const string ArmBoneName = "mixamorig:RightArm";    // Mode1FirstPersonGun.armBoneName

    private NetEntityView view;
    private Animator anim;
    private Transform gun, rightArm;
    private bool aims;

    /// <param name="carried">The seeker's gun, or null.</param>
    /// <param name="aimsWithPitch">A real player, whose aim pitch the server relays (a bot sends none).</param>
    public void Bind(NetEntityView entity, Transform carried = null, bool aimsWithPitch = false)
    {
        view = entity;
        view.DrivesAnimatorSpeed = false;
        anim = GetComponentInChildren<Animator>();
        gun = carried;
        aims = aimsWithPitch && carried != null;

        rightArm = null;
        foreach (Transform t in GetComponentsInChildren<Transform>(true))
            if (t.name == ArmBoneName) { rightArm = t; break; }
    }

    /// <summary>The gun this body carries, for the play test.</summary>
    public Transform Gun => gun;

    /// <summary>The shot's gun animation (the sound and tracer are OnlineRound's).</summary>
    public void PlayShot()
    {
        if (view != null && !view.Eliminated) BodyAnimation.PlayShot(anim);
    }

    private void LateUpdate()
    {
        if (view == null || anim == null || view.Eliminated || view.IsHosted) return;

        EntityFlags flags = view.Flags;
        bool prone = (flags & EntityFlags.Prone) != 0;
        BodyAnimation.SetProne(anim, prone);
        BodyAnimation.SetClimbing(anim, (flags & EntityFlags.Climbing) != 0);
        BodyAnimation.SetSpeed(anim, view.AnimationSpeed / GameTuning.MoveSpeed);

        if (aims) AimGun(prone);
    }

    // After the animation: the arm tilted by the aim (not lying down, as for the player's own), then
    // the gun onto the aim's line.
    private void AimGun(bool prone)
    {
        if (gun == null || !gun.gameObject.activeInHierarchy) return;

        float pitch = view.AimPitch;
        if (!prone && rightArm != null && Mathf.Abs(pitch) > 0.01f)
            rightArm.Rotate(transform.right, -pitch, Space.World);

        GunAim.PointAlong(gun, GunAim.Direction(transform, pitch));
    }
}
