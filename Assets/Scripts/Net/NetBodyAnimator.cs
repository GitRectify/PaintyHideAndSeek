using Meccha.Client;
using Meccha.Shared.Game;
using UnityEngine;

// Drives another player's body animator from what the server replicates, with the same
// parameters the local player's scripts use offline: "Nam" (prone, Mode1FirstPersonGun),
// "Pose" = 1 while on a wall (WallClimber) and otherwise the struck pose (PoseSelectorUI), and
// "GunFire" on a shot (the gun and BotSeekController). "Speed" is NetEntityView's own, from the
// interpolated movement. A parameter the body's controller does not have is skipped.
[DisallowMultipleComponent]
public class NetBodyAnimator : MonoBehaviour
{
    private static readonly int NamHash = Animator.StringToHash("Nam");
    private static readonly int PoseHash = Animator.StringToHash("Pose");
    private static readonly int GunFireHash = Animator.StringToHash("GunFire");

    private NetEntityView view;
    private Animator anim;
    private bool hasNam, hasPose, hasGunFire;
    private bool wasClimbing, wasProne;

    public void Bind(NetEntityView entity)
    {
        view = entity;
        anim = GetComponentInChildren<Animator>();
        if (anim == null) return;

        foreach (AnimatorControllerParameter p in anim.parameters)
        {
            if (p.nameHash == NamHash && p.type == AnimatorControllerParameterType.Bool) hasNam = true;
            else if (p.nameHash == PoseHash && p.type == AnimatorControllerParameterType.Int) hasPose = true;
            else if (p.nameHash == GunFireHash && p.type == AnimatorControllerParameterType.Trigger) hasGunFire = true;
        }
    }

    /// <summary>The shot's gun animation (the sound and tracer are OnlineRound's).</summary>
    public void PlayShot()
    {
        if (anim != null && hasGunFire && !view.Eliminated) anim.SetTrigger(GunFireHash);
    }

    private void LateUpdate()
    {
        if (view == null || anim == null || view.Eliminated) return;

        EntityFlags flags = view.Flags;
        bool prone = (flags & EntityFlags.Prone) != 0;
        bool climbing = (flags & EntityFlags.Climbing) != 0;

        if (hasNam && prone != wasProne) anim.SetBool(NamHash, prone);
        wasProne = prone;

        if (hasPose && climbing != wasClimbing)
        {
            // Off the wall: back to the pose they struck (0 = none), as WallClimber does offline.
            anim.SetInteger(PoseHash, climbing ? 1 : PoseSelectorUI.AnimatorPoseFor(view.Pose));
        }
        wasClimbing = climbing;
    }

    /// <summary>True while the body is on a wall, whose "Pose" 1 a pose change must not overwrite.</summary>
    public bool Climbing => wasClimbing;
}
