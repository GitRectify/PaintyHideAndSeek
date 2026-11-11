using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Part of OnlineRound (OnlineRound.cs has the overview). Loadout: poses and weapons, the local player's and those shown on other players' bodies.
public partial class OnlineRound
{
    // ================= Fields: poses and weapons =================

    private const string RemoteGunName = "NetGun";
    private const string CharacterGunName = "gun";      // the Players prefabs' own, in the hand
    private const string HandBoneName = "mixamorig:RightHand";
    private static readonly int PoseHash = Animator.StringToHash("Pose");
    private const float PoseAnswerSeconds = 1.5f;
    private int requestedPose = -1;
    private float poseAnswerDueAt;

    // ================= Entry point: poses =================

    /// <summary>A pose picked in PoseSelectorUI (server id: slot + 1, 0 = none).</summary>
    public static void SendPose(int poseId)
    {
        if (!Active || Net == null) return;
        _instance.requestedPose = poseId;
        _instance.poseAnswerDueAt = Time.unscaledTime + PoseAnswerSeconds;
        Net.SelectPose(poseId);
    }

    // ================= Remote players' loadout =================

    // Snapshots carry each player's pose and weapon; a change shows on their body here.
    private static void ApplyRemoteLoadout(NetEntityView view)
    {
        if (view == null) return;

        BodyAnimation.SetPose(view.GetComponentInChildren<Animator>(), PoseSelectorUI.AnimatorPoseFor(view.Pose));
        WeaponSkins.Apply(FindChild(view.transform, RemoteGunName) ?? FindChild(view.transform, CharacterGunName), view.WeaponId);
    }

    // Other seekers carry a gun with the animator's "Gun" layer on: the one their character was
    // built with, or on a bot's body the gun prefab, as Single Play's bot seekers do (SeekerGun).
    // Returns the gun, with the materials its prefab gives it.
    private Transform AttachRemoteGun(GameObject body)
    {
        Animator anim = body.GetComponentInChildren<Animator>();

        Transform gun = FindChild(body.transform, CharacterGunName);
        if (gun != null) gun.gameObject.SetActive(true);
        else
        {
            Transform hand = FindChild(body.transform, HandBoneName);
            if (hand == null) return null;

            gun = hand.Find(RemoteGunName);
            if (gun == null) gun = SeekerGun.Attach(hand, RemoteGunName);
            if (gun == null) return null;
        }

        BodyAnimation.SetCarriesGun(anim, true);
        return gun;
    }

    private static Transform FindChild(Transform root, string name)
    {
        if (root == null) return null;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    // ================= The local player's loadout =================

    // LoadoutState: the server's answer to every pose/weapon request, and its bullet count when
    // the match is found and after each accepted shot. A refused change comes back unchanged.
    private void HandleLoadoutChanged()
    {
        RefreshBullets();
        ApplyLocalWeapon();
    }

    // Judged once the taps have stopped: the answer to one pick arriving after the next was made
    // is not a refusal of the next.
    private void CheckPoseAnswer()
    {
        if (requestedPose < 0 || Time.unscaledTime < poseAnswerDueAt) return;

        int asked = requestedPose;
        requestedPose = -1;
        if (Net.Pose == asked) return;

        if (poses != null) poses.ShowServerPose(Net.Pose);
        if (!Net.IsConnected) return;
        ShowBanner(ShopInventory.OwnsPose(asked)
            ? "Changing poses too fast. Try again in a moment."
            : "That pose isn't unlocked online. Get it in the Shop.", 2.5f);
    }

    // ================= Weapons =================

    private Transform LocalGun() => gunHolder != null ? gunHolder.Gun : null;

    private void SelectEquippedWeapon()
    {
        if (!hunter || Net == null) return;
        int wanted = ShopInventory.EquippedWeaponId;
        if (wanted != Net.WeaponId) Net.SelectWeapon(wanted);   // answered with LoadoutState
    }

    private void ApplyLocalWeapon()
    {
        if (!active) return;
        WeaponSkins.Apply(LocalGun(), hunter ? Net.WeaponId : 0);
    }
}
