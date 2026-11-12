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
// A seeker's arm points where that player aims, with the aim pitch the server relays
// (NetEntityView.AimPitch), and their gun lies along the arm with its handle in the hand (GunAim),
// standing or lying down, as on their own screen. Before, it followed the hand's animation only
// and never tilted up or down on anybody else's screen.
[DisallowMultipleComponent]
public class NetBodyAnimator : MonoBehaviour
{
    private NetEntityView view;
    private Animator anim;
    private Transform gun;
    private GunAim.Arm arm;
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
        arm = GunAim.FindArm(transform);
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

        if (aims) AimGun();
    }

    // After the animation: the arm onto their aim, then the gun along the arm.
    private void AimGun()
    {
        if (arm == null || gun == null || !gun.gameObject.activeInHierarchy) return;

        GunAim.PointArm(arm, GunAim.Direction(transform, view.AimPitch));
        GunAim.AlongArm(gun, arm);
    }
}
