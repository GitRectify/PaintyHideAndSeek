using UnityEngine;

// The gun in a seeker bot's hand, and in the hands of other seekers' bodies online:
// Resources/GameObjects/gun. Where it sits in the hand comes from the Player prefab, whose skeleton
// the Bot prefab shares, so it does not depend on which character the local player picked.
public static class SeekerGun
{
    private const string GunPrefabPath = "GameObjects/gun";
    private const string PlacementPrefabPath = "Players/Player";
    private const string PlacementObjectName = "gun";

    private static GameObject prefab;
    private static Transform placement;
    private static bool loaded;

    /// <summary>A new gun in this hand bone, or null when the prefab is missing.</summary>
    public static Transform Attach(Transform hand, string objectName)
    {
        if (hand == null) return null;
        Load();

        if (prefab == null)
        {
            Debug.LogWarning($"[SeekerGun] Resources/{GunPrefabPath} not found; seeker bots have no gun.");
            return null;
        }

        GameObject gun = Object.Instantiate(prefab, hand, false);
        gun.name = objectName;
        if (placement != null) gun.transform.SetLocalPositionAndRotation(placement.localPosition, placement.localRotation);
        gun.SetActive(true);
        return gun.transform;
    }

    private static void Load()
    {
        if (loaded) return;
        loaded = true;

        prefab = Resources.Load<GameObject>(GunPrefabPath);

        GameObject player = Resources.Load<GameObject>(PlacementPrefabPath);
        if (player == null) return;

        foreach (Transform t in player.GetComponentsInChildren<Transform>(true))
        {
            if (t.name != PlacementObjectName) continue;
            placement = t;
            return;
        }
    }
}
