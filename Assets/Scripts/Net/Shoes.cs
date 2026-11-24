using System;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;

/// <summary>
/// The player's shoes (2026-11-24, user's request): each pair runs at its own speed
/// (<see cref="ShoeSpecs"/>, the same table the server judges by). Pair 0 is the standard one
/// everybody has; the others are bought in the Shop's Shoes tab, kept for good, and worn by choice.
///
/// The pair worn is the account's (MetaClient.EquipShoes, saved on the server), so it is the same
/// after a restart and on another phone. This keeps the last pair the server confirmed, also on the
/// device, for Single Play, which runs with no server. In an online match the server's word is the
/// match's (MecchaNetClient.ShoeId, from LoadoutState): it lets this player run exactly that fast,
/// and a pair put on in the Shop during the match reaches it at once.
///
/// JoystickMover multiplies its speed by <see cref="SpeedScale"/>; climbing, jumping and falling are
/// the same in any shoes. A new pair is a new entry in ShoeSpecs, a name and colour here, and a
/// catalog item on the server.
/// </summary>
public static class Shoes
{
    private const string EquippedKey = "Shoes.Equipped";

    private static readonly string[] Names = { "Standard Shoes", "Sneakers", "Running Shoes", "Speed Boots" };

    // The upper's colour for each pair (the sole is white, the stripe white or dark).
    private static readonly Color[] Colors =
    {
        new Color(0.55f, 0.60f, 0.68f),
        new Color(0.20f, 0.55f, 1.00f),
        new Color(1.00f, 0.40f, 0.20f),
        new Color(1.00f, 0.76f, 0.16f),
    };

    private static readonly Dictionary<int, Sprite> icons = new Dictionary<int, Sprite>();

    /// <summary>Raised when the pair worn changes (the Shop's cards follow it).</summary>
    public static event Action Changed;

    /// <summary>The pair this account wears, as the server last said (and kept on the device for Single Play).</summary>
    public static int Equipped
    {
        get
        {
            int id = PlayerPrefs.GetInt(EquippedKey, ShoeSpecs.Standard);
            return ShoeSpecs.IsKnown(id) ? id : ShoeSpecs.Standard;
        }
        private set
        {
            if (Equipped == value) return;
            PlayerPrefs.SetInt(EquippedKey, value);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// How much faster than standard the player runs now: in an online match the pair the server
    /// lets them run in, anywhere else the account's.
    /// </summary>
    public static float SpeedScale
    {
        get
        {
            MecchaNetClient net = MecchaNetClient.Instance;
            int shoes = OnlineRound.Active && net != null ? net.ShoeId : Equipped;
            return ShoeSpecs.SpeedScale(shoes);
        }
    }

    public static string NameOf(int shoeId) => shoeId >= 0 && shoeId < Names.Length ? Names[shoeId] : "Shoes " + shoeId;

    /// <summary>"+20% speed", or "" for the standard pair.</summary>
    public static string SpeedText(int shoeId)
    {
        int percent = ShoeSpecs.Of(shoeId).SpeedPercent;
        return percent > 0 ? $"+{percent}% speed" : "";
    }

    /// <summary>The server's profile, at every sign-in and after any change: the pair the account wears.</summary>
    public static void Note(MetaProfile profile) => Equipped = ShoeSpecs.IsKnown(profile.EquippedShoes) ? profile.EquippedShoes : ShoeSpecs.Standard;

    /// <summary>
    /// Wears this pair from now on, on the server: <paramref name="done"/> gets (worn, text for the
    /// player). Only a pair the account owns (or the standard one) is taken.
    /// </summary>
    public static void Equip(int shoeId, Action<bool, string> done)
    {
        MetaClient meta = MetaClient.Instance;
        if (meta == null || !ShopInventory.SignedIn)
        {
            done?.Invoke(false, "Sign in to use the shop.");
            return;
        }

        meta.EquipShoes(shoeId, reply =>
        {
            if (!reply.TimedOut && reply.Error == MetaError.None)
            {
                Note(reply.Profile);
                ShopInventory.NotifyChanged();
                done?.Invoke(true, NameOf(Equipped) + " equipped");
                return;
            }

            done?.Invoke(false, reply.TimedOut ? "The server did not answer. Try again."
                : reply.Error == MetaError.ShoesNotOwned ? "Buy these shoes first."
                : reply.Error == MetaError.UnknownRequest ? "This server has no shoes yet."
                : "The server could not answer (" + reply.Error + ").");
        });
    }

    // ================= The picture =================

    /// <summary>A side view of the pair, drawn once per pair: the upper in its colour, a white sole and stripe.</summary>
    public static Sprite Icon(int shoeId)
    {
        if (icons.TryGetValue(shoeId, out Sprite cached) && cached != null) return cached;

        const int size = 192;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Shoes" + shoeId };
        Color upper = shoeId >= 0 && shoeId < Colors.Length ? Colors[shoeId] : Colors[0];
        Color sole = new Color(0.96f, 0.96f, 0.98f);
        Color stripe = shoeId == ShoeSpecs.Standard ? new Color(0.30f, 0.33f, 0.40f) : Color.white;
        Color edge = new Color(0.08f, 0.09f, 0.13f);
        bool boot = shoeId == 3;     // the speed boots come up higher at the ankle

        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // 3 x 3 samples a pixel, for smooth edges.
                Color sum = Color.clear;
                for (int sy = 0; sy < 3; sy++)
                {
                    for (int sx = 0; sx < 3; sx++)
                    {
                        float u = (x + (sx + 0.5f) / 3f) / size;
                        float v = (y + (sy + 0.5f) / 3f) / size;
                        sum += Sample(u, v, upper, sole, stripe, edge, boot);
                    }
                }
                pixels[y * size + x] = sum / 9f;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        Sprite sprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.name = tex.name;
        icons[shoeId] = sprite;
        return sprite;
    }

    // One point of the picture (u right, v up, 0..1): a sneaker facing right.
    private static Color Sample(float u, float v, Color upper, Color sole, Color stripe, Color edge, bool boot)
    {
        const float outline = 0.022f;
        float soleTop = 0.34f, soleBottom = 0.22f;

        float soleDist = RoundedBox(u, v, 0.08f, soleBottom, 0.92f, soleTop, 0.06f);
        float upperDist = UpperDistance(u, v, soleTop, boot);
        float shoe = Mathf.Min(soleDist, upperDist);
        if (shoe > outline) return Color.clear;
        if (shoe > 0f) return edge;

        if (soleDist <= 0f && v < soleTop - 0.005f)
        {
            // A darker tread line along the bottom of the sole.
            return v < soleBottom + 0.035f ? Color.Lerp(sole, edge, 0.35f) : sole;
        }

        // Two speed stripes across the side, and the laces' holes along the top of the toe.
        float diagonal = u * 0.75f + v;
        bool onStripe = u > 0.30f && u < 0.80f && v > soleTop + 0.03f &&
                        (Mathf.Abs(diagonal - 0.95f) < 0.035f || Mathf.Abs(diagonal - 1.06f) < 0.035f);
        if (onStripe) return stripe;

        float lacesV = Mathf.Lerp(0.62f, 0.44f, Mathf.InverseLerp(0.42f, 0.78f, u));
        bool lace = u > 0.44f && u < 0.76f && Mathf.Abs(v - (lacesV - 0.04f)) < 0.012f && Mathf.Repeat(u * 22f, 1f) < 0.45f;
        if (lace) return Color.Lerp(upper, edge, 0.6f);

        // A little shade towards the sole.
        return Color.Lerp(upper * 0.82f, upper, Mathf.InverseLerp(soleTop, 0.7f, v));
    }

    // The upper: from the heel up to the collar, then sloping down to a rounded toe.
    private static float UpperDistance(float u, float v, float soleTop, bool boot)
    {
        float collar = boot ? 0.80f : 0.66f;
        float heel = 0.12f, toe = 0.90f;

        // Height of the upper's top edge at u: level over the heel, sloping to the toe.
        float top = u < 0.40f ? collar : Mathf.Lerp(collar - 0.04f, soleTop + 0.10f, Mathf.InverseLerp(0.40f, toe, u));
        float inside = Mathf.Max(Mathf.Max(heel - u, u - toe), Mathf.Max(soleTop - 0.02f - v, v - top));

        // The toe rounded off.
        float toeRound = Vector2.Distance(new Vector2(u, v), new Vector2(toe - 0.07f, soleTop + 0.06f)) - 0.10f;
        if (u > toe - 0.07f && v > soleTop + 0.06f) inside = Mathf.Max(inside, toeRound);
        return inside;
    }

    private static float RoundedBox(float u, float v, float x0, float y0, float x1, float y1, float r)
    {
        float cx = Mathf.Clamp(u, x0 + r, x1 - r), cy = Mathf.Clamp(v, y0 + r, y1 - r);
        float d = Vector2.Distance(new Vector2(u, v), new Vector2(cx, cy)) - r;
        return d;
    }
}
