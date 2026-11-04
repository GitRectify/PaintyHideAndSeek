using System;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;

// Shop ownership, prices, purchases and equipping, on the server's wallet (MetaClient). The UI
// only talks to this class. There is one currency, the server's: prices, ownership and the
// balance come from ListItems, and a purchase names items and quantities only — the server
// charges and grants in one step. Equipping is a local preference, limited to what is owned:
//   * avatar  = CharacterSelect's character (the server checks ownership at FindMatch);
//   * weapon  = sent with SelectWeapon at the start of each online round (0 = the standard gun);
//   * poses are picked in the round itself (PoseSelectorUI), bullets are spent by shooting.
public static class ShopInventory
{
    private const string Category = "none";
    private const string EquippedWeaponKey = "ShopEquippedWeapon";

    /// <summary>Poses every profile owns (server: 0 = no pose, 1-2 = the picker's two free slots).</summary>
    public const int FreePoseMax = 2;

    /// <summary>Raised when the listing, the balance or what is equipped changes.</summary>
    public static event Action Changed;

    private static readonly Dictionary<string, MetaItem> listing = new Dictionary<string, MetaItem>(StringComparer.Ordinal);
    private static MetaClient hookedMeta;

    /// <summary>True once a ListItems answer arrived this session.</summary>
    public static bool Loaded { get; private set; }

    public static bool SignedIn
    {
        get
        {
            MecchaNetClient net = MecchaNetClient.Instance;
            return net != null && !string.IsNullOrEmpty(net.SessionToken);
        }
    }

    /// <summary>The server balance in cents (1/100 coin), as last reported.</summary>
    public static long CoinCents => MecchaNetClient.Instance != null ? MecchaNetClient.Instance.CoinCents : 0;

    public static string CoinsText(long cents) =>
        cents % 100 == 0 ? (cents / 100).ToString("N0") : (cents / 100.0).ToString("N2");

    // ================= Listing =================

    /// <summary>Asks the server for prices, ownership and the balance. done gets an error text, or null.</summary>
    public static void Refresh(Action<string> done)
    {
        MetaClient meta = MetaClient.Instance;
        if (meta == null || !SignedIn)
        {
            done?.Invoke("Sign in to use the shop.");
            return;
        }

        Hook(meta);
        meta.ListItems(reply =>
        {
            if (reply.TimedOut || reply.Error != MetaError.None)
            {
                done?.Invoke(reply.Error == MetaError.ShopUnavailable
                    ? "The shop is turned off on this server."
                    : ErrorText(reply.Error, reply.TimedOut));
                return;
            }

            listing.Clear();
            foreach (MetaItem item in reply.Items ?? new MetaItem[0]) listing[item.Id] = item;
            Loaded = true;
            Changed?.Invoke();
            done?.Invoke(null);
        });
    }

    private static void Hook(MetaClient meta)
    {
        if (hookedMeta == meta) return;
        if (hookedMeta != null) hookedMeta.OnBalanceChanged -= HandleBalance;
        hookedMeta = meta;
        meta.OnBalanceChanged += HandleBalance;
    }

    private static void HandleBalance(long cents, int bullets) => Changed?.Invoke();

    private static bool TryListing(ShopItem item, out MetaItem listed)
    {
        listed = default;
        return item != null && !string.IsNullOrEmpty(item.serverId) && listing.TryGetValue(item.serverId, out listed);
    }

    /// <summary>Bullets this card buys (1 for everything else).</summary>
    private static uint Quantity(ShopItem item) =>
        item.category == ShopCategory.Bullets ? (uint)Mathf.Max(1, item.quantity) : 1u;

    /// <summary>The server sells it now (a bullet pack also has to fit the per-purchase cap).</summary>
    public static bool IsForSale(ShopItem item)
    {
        if (item == null || item.category == ShopCategory.Coins) return false;   // top-ups come with payments
        if (!TryListing(item, out MetaItem listed)) return false;
        return listed.MaxQuantity == 0 || Quantity(item) <= listed.MaxQuantity;
    }

    public static bool IsOwned(ShopItem item)
    {
        if (item == null) return false;
        if (item.ownedByDefault) return true;
        return TryListing(item, out MetaItem listed) && listed.Owned;
    }

    /// <summary>What the card costs on the server, in cents; 0 when it is not for sale.</summary>
    public static long PriceCents(ShopItem item) =>
        TryListing(item, out MetaItem listed) ? listed.PriceCents * Quantity(item) : 0;

    /// <summary>
    /// Whether the player may strike a pose online. Before the listing has arrived the answer is
    /// yes and the server judges (a refused pose comes back in LoadoutState).
    /// </summary>
    public static bool OwnsPose(int poseId)
    {
        if (poseId <= FreePoseMax) return true;
        if (!Loaded) return true;
        foreach (MetaItem item in listing.Values)
            if (item.Kind == ItemKind.Pose && (int)item.GameId == poseId) return item.Owned;
        return false;
    }

    private static bool OwnsWeapon(int weaponId)
    {
        if (weaponId <= 0) return true;
        if (!Loaded) return true;
        foreach (MetaItem item in listing.Values)
            if (item.Kind == ItemKind.Weapon && (int)item.GameId == weaponId) return item.Owned;
        return false;
    }

    // ================= Equipping =================

    public static bool CanEquip(ShopItem item) =>
        item != null && (item.category == ShopCategory.Avatars || item.category == ShopCategory.Weapons);

    public static bool IsEquipped(ShopItem item)
    {
        if (!CanEquip(item) || !IsOwned(item)) return false;
        if (item.category == ShopCategory.Avatars) return CurrentAvatar() == item.gameId;
        return EquippedWeaponId == item.gameId;
    }

    /// <summary>The weapon to carry online: the equipped one while it is owned, else the standard gun.</summary>
    public static int EquippedWeaponId
    {
        get
        {
            int id = UtilGame.GetDataInt(EquippedWeaponKey, 0, Category);
            return OwnsWeapon(id) ? id : 0;
        }
    }

    /// <summary>
    /// Equips an owned avatar or weapon. An avatar is CharacterSelect's character, which reloads
    /// the scene to swap the body. Equipping the weapon already carried puts the standard gun back.
    /// </summary>
    public static void Equip(ShopItem item)
    {
        if (!CanEquip(item) || !IsOwned(item)) return;

        if (item.category == ShopCategory.Avatars)
        {
            CharacterSelect characters = UnityEngine.Object.FindAnyObjectByType<CharacterSelect>();
            if (characters != null) characters.Select(item.gameId);
            return;
        }

        int id = EquippedWeaponId == item.gameId ? 0 : item.gameId;
        UtilGame.SetDataInt(EquippedWeaponKey, id, Category);
        Changed?.Invoke();
    }

    private static int CurrentAvatar()
    {
        CharacterSelect characters = UnityEngine.Object.FindAnyObjectByType<CharacterSelect>();
        return characters != null ? characters.CurrentIndex : 0;
    }

    // ================= Purchasing =================

    /// <summary>Buys the card on the server. done gets (bought, text for the player).</summary>
    public static void Purchase(ShopItem item, Action<bool, string> done)
    {
        MetaClient meta = MetaClient.Instance;
        if (meta == null || !SignedIn)
        {
            done?.Invoke(false, "Sign in to use the shop.");
            return;
        }

        if (!IsForSale(item) || !TryListing(item, out MetaItem listed))
        {
            done?.Invoke(false, "Coming soon");
            return;
        }

        var lines = new[] { new MetaPurchaseLine { ItemId = listed.Id, Quantity = Quantity(item) } };
        meta.Purchase(lines, receipt =>
        {
            if (receipt.TimedOut)
            {
                // Unknown outcome. Not sent again from here: the refreshed balance and ownership
                // show whether it went through.
                Refresh(null);
                done?.Invoke(false, "No answer from the server. Check your coins before trying again.");
                return;
            }

            if (receipt.Error != MetaError.None)
            {
                done?.Invoke(false, receipt.Error == MetaError.ShopUnavailable
                    ? "The shop is turned off on this server."
                    : ErrorText(receipt.Error, false));
                return;
            }

            bool bought = receipt.Status == PurchaseStatus.Completed || receipt.Status == PurchaseStatus.AlreadyCompleted;
            Refresh(null);   // ownership and the balance as the server now has them
            done?.Invoke(bought, PurchaseText(receipt.Status, item));
        });
    }

    private static string PurchaseText(PurchaseStatus status, ShopItem item)
    {
        switch (status)
        {
            case PurchaseStatus.Completed:
            case PurchaseStatus.AlreadyCompleted:
                return item.category == ShopCategory.Bullets ? "+" + item.quantity + " bullets" : item.displayName + " unlocked!";
            case PurchaseStatus.NotEnoughCoins: return "Not enough coins";
            case PurchaseStatus.AlreadyOwned: return "You already own that";
            case PurchaseStatus.UnknownItem: return "That item is no longer for sale";
            case PurchaseStatus.BadQuantity: return "That amount can't be bought";
            case PurchaseStatus.Unavailable: return "The store failed. You were not charged";
            default: return "The purchase was refused (" + status + ")";
        }
    }

    private static string ErrorText(MetaError error, bool timedOut)
    {
        if (timedOut) return "The server did not answer. Try again.";
        switch (error)
        {
            case MetaError.NotSignedIn:
            case MetaError.BadSession: return "Your session has expired. Sign in again.";
            case MetaError.RateLimited: return "Too many requests. Wait a moment.";
            default: return "The server could not answer (" + error + ").";
        }
    }
}
