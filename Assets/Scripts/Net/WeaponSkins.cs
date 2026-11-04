using UnityEngine;

// What a weapon looks like online. Weapons are cosmetic on the server (every one shoots the
// same) and the game ships a single gun model, so a weapon id is a colour skin on that gun:
// 0 is the gun as modelled, 1-6 the shop's weapons (ShopCatalog gameId, server.json GameId).
// When real per-weapon models exist, this is the one place that changes. Adapted from the SDK
// sample's WeaponSkins.
public static class WeaponSkins
{
    private static readonly string[] Names =
    {
        "Standard", "Paint Brush", "Roller", "Spray Gun", "Paint Blaster", "Splatter Cannon", "Precision Brush",
    };

    private static readonly Color[] Colors =
    {
        Color.white,
        new Color(1.00f, 0.45f, 0.75f),
        new Color(0.20f, 0.55f, 1.00f),
        new Color(0.25f, 0.75f, 0.30f),
        new Color(0.85f, 0.15f, 0.18f),
        new Color(0.60f, 0.30f, 0.90f),
        new Color(1.00f, 0.80f, 0.25f),
    };

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static MaterialPropertyBlock _block;

    public static string NameOf(int weaponId) =>
        weaponId >= 0 && weaponId < Names.Length ? Names[weaponId] : "Weapon " + weaponId;

    public static Color ColorOf(int weaponId) =>
        weaponId > 0 && weaponId < Colors.Length ? Colors[weaponId] : Color.white;

    /// <summary>Tints every renderer under the gun; weapon 0 takes the tint off again.</summary>
    public static void Apply(Transform gun, int weaponId)
    {
        if (gun == null) return;
        _block ??= new MaterialPropertyBlock();

        foreach (Renderer renderer in gun.GetComponentsInChildren<Renderer>(true))
        {
            if (weaponId <= 0)
            {
                renderer.SetPropertyBlock(null);
                continue;
            }

            Color tint = ColorOf(weaponId);
            renderer.GetPropertyBlock(_block);
            _block.SetColor(BaseColorId, tint);
            _block.SetColor(ColorId, tint);
            renderer.SetPropertyBlock(_block);
        }
    }
}
