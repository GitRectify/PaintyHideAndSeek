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
    private const string HandBoneName = "mixamorig:RightHand";
    private static readonly int PoseHash = Animator.StringToHash("Pose");
    private int requestedPose = -1;

    // ================= Entry point: poses =================

    /// <summary>A pose picked in PoseSelectorUI (server id: slot + 1, 0 = none).</summary>
    public static void SendPose(int poseId)
    {
        if (!Active || Net == null) return;
        _instance.requestedPose = poseId;
        Net.SelectPose(poseId);
    }

    // ================= Remote players' loadout =================

    // Snapshots carry each player's pose and weapon; a change shows on their body here.
    private static void ApplyRemoteLoadout(NetEntityView view)
    {
        if (view == null) return;

        // On a wall "Pose" is the climb (NetBodyAnimator); the struck pose comes back after it.
        NetBodyAnimator body = view.GetComponent<NetBodyAnimator>();
        Animator animator = view.GetComponentInChildren<Animator>();
        if (animator != null && HasParameter(animator, PoseHash) && (body == null || !body.Climbing))
            animator.SetInteger(PoseHash, PoseSelectorUI.AnimatorPoseFor(view.Pose));

        WeaponSkins.Apply(FindChild(view.transform, RemoteGunName), view.WeaponId);
    }

    // Other seekers carry the same gun as the local player, as Single Play's bot seekers do
    // (BotSeekController.AttachGunsToSeekers): a copy of the scene's gun in the body's hand bone,
    // with the same local placement, and the animator's "Gun" layer on. The copy is only a model:
    // colliders and scripts are stripped.
    private void AttachRemoteGun(GameObject body)
    {
        Transform template = LocalGun();
        if (template == null) return;

        Transform hand = FindChild(body.transform, HandBoneName);
        if (hand == null || hand.Find(RemoteGunName) != null) return;

        GameObject copy = Instantiate(template.gameObject, hand, false);
        copy.name = RemoteGunName;
        copy.transform.localPosition = template.localPosition;
        copy.transform.localRotation = template.localRotation;
        copy.transform.localScale = template.localScale;
        foreach (Collider c in copy.GetComponentsInChildren<Collider>(true)) Destroy(c);
        foreach (MonoBehaviour m in copy.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(m);
        copy.SetActive(true);

        Animator animator = body.GetComponentInChildren<Animator>();
        if (animator != null)
        {
            for (int i = 0; i < animator.layerCount; i++)
            {
                if (animator.GetLayerName(i) != "Gun") continue;
                animator.SetLayerWeight(i, 1f);
                break;
            }
        }
    }

    private static Transform FindChild(Transform root, string name)
    {
        if (root == null) return null;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    private static bool HasParameter(Animator animator, int hash)
    {
        foreach (AnimatorControllerParameter p in animator.parameters)
            if (p.nameHash == hash) return true;
        return false;
    }

    // ================= The local player's loadout =================

    // LoadoutState: the server's answer to every pose/weapon request, and its bullet count when
    // the match is found and after each accepted shot. A refused change comes back unchanged.
    private void HandleLoadoutChanged()
    {
        RefreshBullets();
        ApplyLocalWeapon();

        if (requestedPose < 0) return;
        if (Net.Pose != requestedPose)
        {
            if (poses != null) poses.ShowServerPose(Net.Pose);
            ShowBanner(ShopInventory.OwnsPose(requestedPose)
                ? "Changing poses too fast. Try again in a moment."
                : "That pose isn't unlocked online. Get it in the Shop.", 2.5f);
        }
        requestedPose = -1;
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
