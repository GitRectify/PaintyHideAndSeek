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
[DisallowMultipleComponent]
public class NetBodyAnimator : MonoBehaviour
{
    private NetEntityView view;
    private Animator anim;

    public void Bind(NetEntityView entity)
    {
        view = entity;
        view.DrivesAnimatorSpeed = false;
        anim = GetComponentInChildren<Animator>();
    }

    /// <summary>The shot's gun animation (the sound and tracer are OnlineRound's).</summary>
    public void PlayShot()
    {
        if (view != null && !view.Eliminated) BodyAnimation.PlayShot(anim);
    }

    private void LateUpdate()
    {
        if (view == null || anim == null || view.Eliminated || view.IsHosted) return;

        EntityFlags flags = view.Flags;
        BodyAnimation.SetProne(anim, (flags & EntityFlags.Prone) != 0);
        BodyAnimation.SetClimbing(anim, (flags & EntityFlags.Climbing) != 0);
        BodyAnimation.SetSpeed(anim, view.AnimationSpeed / GameTuning.MoveSpeed);
    }
}
