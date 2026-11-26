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
//   * weapon  = sent with SelectWeapon at the start of each online round (0 = the standard gun),
//     and switched in the round with the weapon dock;
//   * poses are picked in the round itself (PoseSelectorUI);
//   * features (Extras): unlocked for good, nothing to equip; Next Player (GameFeatures) is what
//     lets a player knocked out of an online round switch between the players still in;
//   * shoes: each pair runs faster (ShoeSpecs); the pair worn is the account's, saved on the
//     server (Shoes.Equip), and sets the player's speed in both modes (Shoes.SpeedScale);
//   * weapons and their ammo (2026-11-24, replacing bullet types): a weapon decides how its
//     holder shoots (cooldown, bullet size) and has rounds of its own: 10 with the weapon, more in
//     ammo packs (kind Ammo, GameId = the weapon), up to what it holds. The standard weapon is
//     everyone's and never runs out. The rounds are the server's (MetaItem.OwnedAmmo).
// The shop the player sees is the server's too (SDK 0.9.8): its categories in its order, each
// item's name, description, picture and numbers (Items, Categories), so an operator changes the
// shop from the admin panel without a new game.
// Coin packs are the server's top-up packages (ListTopUps): coins and price come from the server,
// paid at the wallet provider's checkout and credited by the provider's callback to the server.
// PendingTopUp watches each one until the server says how it ended. The balance itself is
// CoinBalance's: this class only passes it on, and never keeps a number of its own.
public static class ShopInventory
{
    private const string Category = "none";
    private const string EquippedWeaponKey = "ShopEquippedWeapon";

    /// <summary>Poses every profile owns (server: 0 = no pose, 1-2 = the picker's two free slots).</summary>
    public const int FreePoseMax = 2;

    /// <summary>Raised when the listing, the balance or what is equipped changes.</summary>
    public static event Action Changed;

    private static readonly Dictionary<string, MetaItem> listing = new Dictionary<string, MetaItem>(StringComparer.Ordinal);
    private static readonly List<MetaItem> ordered = new List<MetaItem>();          // as the server listed them
    private static readonly List<MetaShopCategory> categories = new List<MetaShopCategory>();
    private static readonly List<MetaTopUpPackage> orderedPackages = new List<MetaTopUpPackage>();
    private static readonly Dictionary<string, MetaTopUpPackage> packages = new Dictionary<string, MetaTopUpPackage>(StringComparer.Ordinal);
    private static MetaClient hookedMeta;

    /// <summary>True once a ListItems answer arrived this session.</summary>
    public static bool Loaded { get; private set; }

    /// <summary>True once ListTopUps answered this session (with packages, or without a wallet).</summary>
    public static bool TopUpsLoaded { get; private set; }

    public static bool SignedIn
    {
        get
        {
            MecchaNetClient net = MecchaNetClient.Instance;
            return net != null && !string.IsNullOrEmpty(net.SessionToken);
        }
    }

    // A card that says what can be afforded, or shows a price, follows the balance too.
    static ShopInventory()
    {
        CoinBalance.Changed += () => Changed?.Invoke();
    }

    /// <summary>The server balance in cents (1/100 coin), as last reported (CoinBalance).</summary>
    public static long CoinCents => CoinBalance.Cents;

    /// <summary>An amount of coins as every screen writes it (CoinBalance.Format).</summary>
    public static string CoinsText(long cents) => CoinBalance.Format(cents);

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
        RefreshTopUps(meta);
        meta.ListItems(reply =>
        {
            if (reply.TimedOut || reply.Error != MetaError.None)
            {
                done?.Invoke(reply.Error == MetaError.ShopUnavailable
                    ? "The shop is turned off on this server."
                    : ErrorText(reply.Error, reply.TimedOut));
                return;
            }

            string before = Loaded ? Fingerprint() : null;
            listing.Clear();
            ordered.Clear();
            foreach (MetaItem item in reply.Items ?? new MetaItem[0])
            {
                listing[item.Id] = item;
                ordered.Add(item);
            }
            categories.Clear();
            if (reply.Categories != null) categories.AddRange(reply.Categories);
            ImagePort = reply.ImagePort;
            Loaded = true;
            // Told only when something is different: the open shop asks again every little while
            // (items an operator adds show up), and an answer with nothing new redraws nothing.
            if (before == null || before != Fingerprint()) Changed?.Invoke();
            done?.Invoke(null);
        });
    }

    // Everything the shop shows of the listing, in one string, to tell whether an answer changed it.
    private static string Fingerprint()
    {
        var sb = new System.Text.StringBuilder();
        foreach (MetaShopCategory c in categories) sb.Append(c.Id).Append(':').Append(c.Name).Append(':').Append(c.Icon).Append(':').Append(c.Special).Append('|');
        foreach (MetaItem i in ordered)
        {
            sb.Append(i.Id).Append(':').Append(i.Kind).Append(':').Append(i.GameId).Append(':').Append(i.PriceCents).Append(':').Append(i.Owned)
              .Append(':').Append(i.Category).Append(':').Append(i.Name).Append(':').Append(i.Description).Append(':').Append(i.Image)
              .Append(':').Append(i.OwnedAmmo).Append(':').Append(i.Ammo).Append(':').Append(i.AmmoCapacity)
              .Append(':').Append(i.OriginalPriceCents).Append(':').Append(i.SaleEndsUnix).Append(':').Append(i.SaleName).Append('|');
        }
        return sb.ToString();
    }

    // The coin packs on sale. Without a wallet provider (WalletUnavailable) or on a failed answer
    // there are none, and the Coins cards say "Coming soon" until the next refresh.
    private static void RefreshTopUps(MetaClient meta)
    {
        meta.ListTopUps(reply =>
        {
            string before = TopUpsLoaded ? PackagesFingerprint() : null;
            packages.Clear();
            orderedPackages.Clear();
            if (!reply.TimedOut && reply.Error == MetaError.None)
            {
                foreach (MetaTopUpPackage package in reply.Packages ?? new MetaTopUpPackage[0])
                {
                    packages[package.Id] = package;
                    orderedPackages.Add(package);
                }
            }
            else if (reply.Error != MetaError.WalletUnavailable)
            {
                Debug.LogWarning($"[Shop] Coin packs could not be listed: {(reply.TimedOut ? "timed out" : reply.Error.ToString())}");
            }

            TopUpsLoaded = true;
            // Redrawn only when the packs are different (one added, changed or turned off in the
            // admin Shop page), not at every refresh of the open shop.
            if (before == null || before != PackagesFingerprint()) Changed?.Invoke();
        });
    }

    private static string PackagesFingerprint()
    {
        var sb = new System.Text.StringBuilder();
        foreach (MetaTopUpPackage p in orderedPackages) sb.Append(p.Id).Append(':').Append(p.CoinCents).Append(':').Append(p.PriceMinor).Append(':').Append(p.Currency).Append('|');
        return sb.ToString();
    }

    private static void Hook(MetaClient meta)
    {
        if (hookedMeta == meta) return;
        if (hookedMeta != null) hookedMeta.OnBalanceChanged -= HandleBalance;
        hookedMeta = meta;
        meta.OnBalanceChanged += HandleBalance;
    }

    private static void HandleBalance(long cents, BulletStock bullets) => Changed?.Invoke();

    /// <summary>Tells the shop's views that something they show changed (a top-up's state).</summary>
    public static void NotifyChanged() => Changed?.Invoke();

    private static bool TryListing(ShopItem item, out MetaItem listed)
    {
        listed = default;
        if (item == null) return false;
        // No server id: not for sale anywhere, but for shoes, which the server names by pair.
        if (string.IsNullOrEmpty(item.serverId) && item.category != ShopCategory.Shoes) return false;
        if (!string.IsNullOrEmpty(item.serverId) && listing.TryGetValue(item.serverId, out listed)) return true;

        // A server whose catalog names the item otherwise still sells the same thing: its kind and
        // the game's own number say which (2026-11-13: such cards said "Coming soon").
        if (!KindOf(item.category, out ItemKind kind)) return false;
        foreach (MetaItem candidate in listing.Values)
        {
            if (candidate.Kind != kind || (int)candidate.GameId != item.gameId) continue;
            listed = candidate;
            return true;
        }
        return false;
    }

    private static bool KindOf(ShopCategory category, out ItemKind kind)
    {
        switch (category)
        {
            case ShopCategory.Avatars: kind = ItemKind.Avatar; return true;
            case ShopCategory.Poses: kind = ItemKind.Pose; return true;
            case ShopCategory.Weapons: kind = ItemKind.Weapon; return true;
            case ShopCategory.Shoes: kind = ItemKind.Shoes; return true;
            default: kind = default; return false;
        }
    }

    private static uint Quantity(ShopItem item) => 1u;

    /// <summary>The server sells it now.</summary>
    public static bool IsForSale(ShopItem item)
    {
        if (item == null) return false;
        if (item.category == ShopCategory.Coins) return TryPackage(item, out _);
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

    // ================= Coin packs =================

    private static bool TryPackage(ShopItem item, out MetaTopUpPackage package)
    {
        package = default;
        return item != null && item.category == ShopCategory.Coins && !string.IsNullOrEmpty(item.serverId) &&
               packages.TryGetValue(item.serverId, out package);
    }

    /// <summary>
    /// The coins a pack card gives: the server's amount, and nothing until the server has listed
    /// it — a number from the card itself could differ from what the server sells.
    /// </summary>
    public static string PackCoinsText(ShopItem item) =>
        TryPackage(item, out MetaTopUpPackage package) ? CoinsText(package.CoinCents) : "";

    /// <summary>A pack's real-money price as the server has it, e.g. "$4.99"; empty when it is not on sale.</summary>
    public static string PackPriceText(ShopItem item) =>
        TryPackage(item, out MetaTopUpPackage package) ? MoneyText(package.PriceMinor, package.Currency) : "";

    // Currencies whose smallest unit is the whole unit (ISO 4217 exponent 0).
    private static readonly HashSet<string> WholeUnitCurrencies = new HashSet<string> { "JPY", "KRW", "VND", "CLP", "ISK", "UGX", "XAF", "XOF" };

    public static string MoneyText(long minor, string currency)
    {
        // The same digits on every phone, whatever its language.
        currency = (currency ?? "").ToUpperInvariant();
        string amount = WholeUnitCurrencies.Contains(currency)
            ? minor.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            : (minor / 100m).ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

        switch (currency)
        {
            case "USD": return "$" + amount;
            case "EUR": return "€" + amount;
            case "GBP": return "£" + amount;
            case "JPY": return "¥" + amount;
            // XTS is the code kept for testing: the development wallet's currency, no real money.
            default: return currency.Length > 0 ? amount + " " + currency : amount;
        }
    }

    /// <summary>
    /// Opens a payment for a coin pack and sends the player to the provider's checkout. The coins
    /// arrive when the provider tells the server; PendingTopUp watches for that and says how it
    /// ended. done gets (opened, text for the player).
    /// </summary>
    public static void StartTopUp(ShopItem item, Action<bool, string> done)
    {
        MetaClient meta = MetaClient.Instance;
        if (meta == null || !SignedIn)
        {
            done?.Invoke(false, "Sign in to use the shop.");
            return;
        }

        if (!TryPackage(item, out MetaTopUpPackage package))
        {
            done?.Invoke(false, "Coin top-ups aren't available yet");
            return;
        }

        StartTopUp(package, done);
    }

    /// <summary>The same for a package the server listed: what the wallet screen's DEPOSIT uses.</summary>
    public static void StartTopUp(MetaTopUpPackage package, Action<bool, string> done)
    {
        MetaClient meta = MetaClient.Instance;
        if (meta == null || !SignedIn)
        {
            done?.Invoke(false, "Sign in to use the shop.");
            return;
        }

        // A payment for this pack is still open: back to its checkout rather than a second one.
        if (PendingTopUp.TryFor(package.Id, out PendingTopUp.Entry open))
        {
            PendingTopUp.OpenCheckout(open);
            done?.Invoke(true, "Finish paying on the payment page.");
            return;
        }

        meta.CreateTopUp(package.Id, reply =>
        {
            if (reply.TimedOut || reply.Error != MetaError.None)
            {
                // Nothing to pay yet; an unanswered payment that was opened anyway simply expires.
                done?.Invoke(false, reply.TimedOut
                    ? "Couldn't open the payment. Try again."
                    : reply.Error == MetaError.WalletUnavailable
                        ? "Payments aren't available right now."
                        : ErrorText(reply.Error, false));
                return;
            }

            PendingTopUp.Entry entry = PendingTopUp.Add(reply.TopUp, package);
            PendingTopUp.OpenCheckout(entry);
            done?.Invoke(true, "Finish paying on the payment page.");
        });
    }

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
        return true;    // the shop doesn't sell it, so there is nothing to buy
    }

    /// <summary>
    /// Whether the server's listing says this account owns the avatar (CharacterSelect index).
    /// False before the listing has arrived and for an avatar the shop does not list: the
    /// profile's own list answers then (OnlineLobby).
    /// </summary>
    public static bool OwnsAvatar(int avatarId)
    {
        if (!Loaded) return false;
        foreach (MetaItem item in listing.Values)
            if (item.Kind == ItemKind.Avatar && (int)item.GameId == avatarId) return item.Owned;
        return false;
    }

    // ================= Features =================

    /// <summary>
    /// The server's listing of a feature (GameFeatures id), as a card the death screen can buy it
    /// with; null before the listing has arrived and when the server does not sell it.
    /// </summary>
    public static ShopItem FeatureItem(int featureId)
    {
        foreach (MetaItem item in listing.Values)
        {
            if (item.Kind != ItemKind.Feature || (int)item.GameId != featureId) continue;
            return new ShopItem
            {
                id = "feature_" + item.Id,
                category = ShopCategory.Extras,
                displayName = featureId == GameFeatures.NextPlayer ? "Next Player" : item.Id,
                serverId = item.Id,
                gameId = featureId,
                quantity = 1,
            };
        }
        return null;
    }

    /// <summary>Whether this account has unlocked a feature, as the server last listed it.</summary>
    public static bool OwnsFeature(int featureId)
    {
        foreach (MetaItem item in listing.Values)
            if (item.Kind == ItemKind.Feature && (int)item.GameId == featureId) return item.Owned;
        return false;
    }

    // ================= Shoes =================

    /// <summary>
    /// The Shoes tab's cards: the standard pair (everyone's) and every pair the game has, faster
    /// each time. Prices and ownership come from the server's listing by kind and pair, like any
    /// card; a pair the server does not sell says "Coming soon".
    /// </summary>
    public static List<ShopItem> ShoeItems()
    {
        var list = new List<ShopItem>();
        for (int id = 0; id < ShoeSpecs.Count; id++)
        {
            list.Add(new ShopItem
            {
                id = "shoes_" + id,
                category = ShopCategory.Shoes,
                displayName = Shoes.NameOf(id),
                icon = Shoes.Icon(id),
                badge = Shoes.SpeedText(id),
                ownedByDefault = id == ShoeSpecs.Standard,
                serverId = "",
                gameId = id,
                quantity = 1,
            });
        }
        return list;
    }

    // ================= Maps =================

    /// <summary>
    /// The maps the server sells (kind Map; GameId = the game's map number, Room2 = 2), as shop cards
    /// in map order; none before the listing has arrived. A map the server does not sell is free,
    /// and so is the first map always.
    /// </summary>
    public static List<ShopItem> MapItems()
    {
        var list = new List<ShopItem>();
        LevelManager levels = LevelManager.Instance;
        MapSelectPopup picker = MapSelectPopup.Instance;
        foreach (MetaItem item in listing.Values)
        {
            if (item.Kind != ItemKind.Map) continue;
            int number = (int)item.GameId;
            list.Add(new ShopItem
            {
                id = "map_" + item.Id,
                category = ShopCategory.Extras,
                displayName = levels != null && number >= 1 && number <= levels.Count ? levels.DisplayNameAt(number - 1) : "Room " + number,
                icon = picker != null ? picker.ThumbnailAt(number - 1) : null,
                serverId = item.Id,
                gameId = number,
                quantity = 1,
            });
        }
        list.Sort((a, b) => a.gameId.CompareTo(b.gameId));
        return list;
    }

    /// <summary>The card that buys this map (the game's map number); null when the server does not sell it.</summary>
    public static ShopItem MapItem(int mapNumber) => MapItems().Find(i => i.gameId == mapNumber);

    /// <summary>
    /// Online: whether this map has to be bought first and this account has not, as the server last
    /// listed it. False for a map the server does not sell, and before the listing has arrived (the
    /// server then says so itself: FindMatchError.MapLocked).
    /// </summary>
    public static bool MapLocked(int mapNumber)
    {
        foreach (MetaItem item in listing.Values)
            if (item.Kind == ItemKind.Map && (int)item.GameId == mapNumber) return !item.Owned;
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

    // ================= Weapons =================

    /// <summary>A weapon's name as the server's shop lists it (the game's own before the listing arrives).</summary>
    public static string WeaponName(int weaponId)
    {
        foreach (MetaItem item in ordered)
            if (item.Kind == ItemKind.Weapon && (int)item.GameId == weaponId && !string.IsNullOrEmpty(item.Name)) return item.Name;
        return weaponId == WeaponSpecs.Standard ? "Standard Gun" : WeaponSkins.NameOf(weaponId);
    }

    /// <summary>Rounds of a weapon this player has, as the server last listed them (-1: unlimited).</summary>
    public static int AmmoOf(int weaponId)
    {
        if (weaponId == WeaponSpecs.Standard) return -1;
        foreach (MetaItem item in ordered)
            if (item.Kind == ItemKind.Weapon && (int)item.GameId == weaponId) return item.Owned ? Mathf.Max(0, item.OwnedAmmo) : 0;
        return 0;
    }

    /// <summary>Keeps a weapon as the one carried into online rounds (the in-round weapon dock's pick too).</summary>
    public static void SetEquippedWeapon(int weaponId)
    {
        if (weaponId != WeaponSpecs.Standard && !OwnsWeapon(weaponId)) return;
        if (UtilGame.GetDataInt(EquippedWeaponKey, 0, Category) == weaponId) return;
        UtilGame.SetDataInt(EquippedWeaponKey, weaponId, Category);
        Changed?.Invoke();
    }

    // ================= The server's shop (SDK 0.9.8) =================

    /// <summary>The HTTP port the game server serves item pictures on (0: none).</summary>
    public static int ImagePort { get; private set; }

    // The shop's usual categories, for a server that names none (older than SDK 0.9.8).
    private static readonly MetaShopCategory[] DefaultCategories =
    {
        new MetaShopCategory { Id = "coins", Name = "Coins", Icon = "coins", Special = "coins" },
        new MetaShopCategory { Id = "avatars", Name = "Characters", Icon = "user" },
        new MetaShopCategory { Id = "poses", Name = "Poses", Icon = "person" },
        new MetaShopCategory { Id = "weapons", Name = "Weapons", Icon = "crosshair" },
        new MetaShopCategory { Id = "shoes", Name = "Shoes", Icon = "footprints" },
        new MetaShopCategory { Id = "extras", Name = "More", Icon = "sparkles" },
    };

    /// <summary>The shop's categories in the server's order (the usual ones from an older server).</summary>
    public static IReadOnlyList<MetaShopCategory> Categories => categories.Count > 0 ? (IReadOnlyList<MetaShopCategory>)categories : DefaultCategories;

    /// <summary>The category an item is shown in: the server's, or its kind's usual one.</summary>
    public static string CategoryOf(MetaItem item)
    {
        if (!string.IsNullOrEmpty(item.Category)) return item.Category;
        switch (item.Kind)
        {
            case ItemKind.Avatar: return "avatars";
            case ItemKind.Pose: return "poses";
            case ItemKind.Weapon:
            case ItemKind.Ammo: return "weapons";
            case ItemKind.Shoes: return "shoes";
            default: return "extras";
        }
    }

    /// <summary>When the first sale on show ends (Unix seconds, UTC); 0 when nothing is on sale (SDK 0.9.9).</summary>
    public static long NextSaleEndUnix
    {
        get
        {
            long first = 0;
            foreach (MetaItem item in ordered)
                if (item.OnSale && item.SaleEndsUnix > 0 && (first == 0 || item.SaleEndsUnix < first)) first = item.SaleEndsUnix;
            return first;
        }
    }

    /// <summary>
    /// The items of a category in the server's order. Bullet types are no longer sold to players
    /// (their numbers moved to the weapons), so an old catalog's bullets are left out.
    /// </summary>
    public static List<MetaItem> ItemsIn(string categoryId)
    {
        var list = new List<MetaItem>();
        foreach (MetaItem item in ordered)
            if (item.Kind != ItemKind.Bullets && CategoryOf(item) == categoryId) list.Add(item);

        // The standard shoes are everyone's and never sold, but they are worn from the shop too
        // (to take a faster pair off): their card goes first wherever the shoes are.
        if (list.Exists(i => i.Kind == ItemKind.Shoes) && !list.Exists(i => i.Kind == ItemKind.Shoes && i.GameId == ShoeSpecs.Standard))
            list.Insert(list.FindIndex(i => i.Kind == ItemKind.Shoes), StandardShoes);
        return list;
    }

    private static readonly MetaItem StandardShoes = new MetaItem
    {
        Id = "shoes-standard", Kind = ItemKind.Shoes, GameId = ShoeSpecs.Standard, Owned = true, MaxQuantity = 1,
        Category = "shoes", Name = "Standard Shoes", Description = "Everyone's pair: the usual speed.", Image = "builtin:shoes-0",
    };

    /// <summary>The coin packs on sale, in the server's order (none without a wallet provider).</summary>
    public static IReadOnlyList<MetaTopUpPackage> Packages => orderedPackages;

    public static bool TryItem(string id, out MetaItem item)
    {
        if (id == StandardShoes.Id) { item = StandardShoes; return true; }
        return listing.TryGetValue(id ?? "", out item);
    }

    /// <summary>The weapon item for a weapon id, or false.</summary>
    public static bool TryWeapon(int weaponId, out MetaItem weapon)
    {
        foreach (MetaItem item in ordered)
            if (item.Kind == ItemKind.Weapon && (int)item.GameId == weaponId) { weapon = item; return true; }
        weapon = default;
        return false;
    }

    /// <summary>A weapon's numbers as the server sells it (the game's own for one it does not list).</summary>
    public static WeaponSpec SpecOf(MetaItem weapon) =>
        weapon.CooldownMillis > 0 ? WeaponSpec.FromWire(weapon.CooldownMillis, weapon.RadiusPercent) : WeaponSpecs.DefaultOf((int)weapon.GameId);

    /// <summary>Whether an item is owned: bought, the standard weapon, a free one, or the standard shoes.</summary>
    public static bool IsOwned(MetaItem item) =>
        item.Owned || (item.Kind == ItemKind.Weapon && item.GameId == WeaponSpecs.Standard) ||
        (item.Kind == ItemKind.Shoes && item.GameId == ShoeSpecs.Standard);

    /// <summary>Things one keeps equipped (characters, weapons, shoes) as against things simply owned.</summary>
    public static bool CanEquip(MetaItem item) =>
        item.Kind == ItemKind.Avatar || item.Kind == ItemKind.Weapon || item.Kind == ItemKind.Shoes;

    public static bool IsEquipped(MetaItem item)
    {
        if (!CanEquip(item) || !IsOwned(item)) return false;
        switch (item.Kind)
        {
            case ItemKind.Avatar: return CurrentAvatar() == (int)item.GameId;
            case ItemKind.Shoes: return Shoes.Equipped == (int)item.GameId;
            default: return EquippedWeaponId == (int)item.GameId;
        }
    }

    /// <summary>
    /// Equips an owned character, weapon or pair of shoes. done gets (worked, text for the
    /// player); shoes are the server's to confirm (Shoes.Equip).
    /// </summary>
    public static void Equip(MetaItem item, Action<bool, string> done)
    {
        if (!CanEquip(item) || !IsOwned(item))
        {
            done?.Invoke(false, "Buy it first.");
            return;
        }

        string name = string.IsNullOrEmpty(item.Name) ? item.Id : item.Name;
        switch (item.Kind)
        {
            case ItemKind.Avatar:
                if (OnlineRound.Active)
                {
                    done?.Invoke(false, "Change your character after this match.");
                    return;
                }
                CharacterSelect characters = UnityEngine.Object.FindAnyObjectByType<CharacterSelect>();
                if (characters != null) characters.Select((int)item.GameId);
                Changed?.Invoke();
                done?.Invoke(true, L.F("{0} equipped", name));
                return;

            case ItemKind.Shoes:
                Shoes.Equip((int)item.GameId, (worn, message) => done?.Invoke(worn, message));
                return;

            default:
                UtilGame.SetDataInt(EquippedWeaponKey, (int)item.GameId, Category);
                Changed?.Invoke();
                done?.Invoke(true, L.F("{0} equipped", name));
                return;
        }
    }

    /// <summary>
    /// Buys an item on the server: <paramref name="quantity"/> packs of ammo, one of anything
    /// else. done gets (bought, the server's status, text for the player).
    /// </summary>
    public static void Purchase(MetaItem item, uint quantity, Action<bool, PurchaseStatus, string> done)
    {
        MetaClient meta = MetaClient.Instance;
        if (meta == null || !SignedIn)
        {
            done?.Invoke(false, PurchaseStatus.Unavailable, "Sign in to use the shop.");
            return;
        }

        quantity = item.Kind == ItemKind.Ammo ? Math.Max(1u, quantity) : 1u;
        var lines = new[] { new MetaPurchaseLine { ItemId = item.Id, Quantity = quantity } };
        meta.Purchase(lines, receipt =>
        {
            if (receipt.TimedOut)
            {
                Refresh(null);
                done?.Invoke(false, PurchaseStatus.Unavailable, "No answer from the server. Check your coins before trying again.");
                return;
            }

            if (receipt.Error != MetaError.None)
            {
                done?.Invoke(false, PurchaseStatus.Unavailable, receipt.Error == MetaError.ShopUnavailable
                    ? "The shop is turned off on this server."
                    : ErrorText(receipt.Error, false));
                return;
            }

            bool bought = receipt.Status == PurchaseStatus.Completed || receipt.Status == PurchaseStatus.AlreadyCompleted;

            // Owned from this moment on every screen: the refresh below confirms it a round trip later.
            if ((bought || receipt.Status == PurchaseStatus.AlreadyOwned) && item.Kind != ItemKind.Ammo &&
                listing.TryGetValue(item.Id, out MetaItem owned))
            {
                owned.Owned = true;
                listing[item.Id] = owned;
                int at = ordered.FindIndex(i => i.Id == item.Id);
                if (at >= 0) ordered[at] = owned;
                Changed?.Invoke();
            }

            // A weapon just bought is the one carried into the next shot.
            if (bought && item.Kind == ItemKind.Weapon) SetEquippedWeapon((int)item.GameId);

            Refresh(null);   // ownership, rounds and the balance as the server now has them
            done?.Invoke(bought, receipt.Status, PurchaseText(receipt.Status, item, quantity));
        });
    }

    private static string PurchaseText(PurchaseStatus status, MetaItem item, uint quantity)
    {
        string name = string.IsNullOrEmpty(item.Name) ? item.Id : item.Name;
        switch (status)
        {
            case PurchaseStatus.Completed:
            case PurchaseStatus.AlreadyCompleted:
                return item.Kind == ItemKind.Ammo
                    ? L.F("+{0} rounds for {1}", Math.Max(1, item.Ammo) * (int)quantity, WeaponName((int)item.GameId))
                    : L.F("{0} unlocked!", name);
            case PurchaseStatus.NeedsWeapon: return L.F("Buy the {0} first: ammo goes with its weapon.", WeaponName((int)item.GameId));
            case PurchaseStatus.AmmoFull: return "That is more rounds than the weapon holds.";
            default: return PurchaseText(status, new ShopItem { displayName = name });
        }
    }

    // ================= Equipping =================

    public static bool CanEquip(ShopItem item) =>
        item != null && (item.category == ShopCategory.Avatars || item.category == ShopCategory.Weapons || item.category == ShopCategory.Shoes);

    public static bool IsEquipped(ShopItem item)
    {
        if (!CanEquip(item) || !IsOwned(item)) return false;
        if (item.category == ShopCategory.Avatars) return CurrentAvatar() == item.gameId;
        if (item.category == ShopCategory.Shoes) return Shoes.Equipped == item.gameId;
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
    /// Equips an owned avatar or weapon. An avatar is CharacterSelect's character, whose body is
    /// swapped in place. Equipping the weapon already carried puts the standard gun back.
    /// </summary>
    public static void Equip(ShopItem item)
    {
        if (!CanEquip(item) || !IsOwned(item)) return;

        if (item.category == ShopCategory.Avatars)
        {
            CharacterSelect characters = UnityEngine.Object.FindAnyObjectByType<CharacterSelect>();
            if (characters != null) characters.Select(item.gameId);
            Changed?.Invoke();   // the shop shows the new one as equipped
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

            // Owned from this moment on every card: the refresh below only confirms it, a round trip later.
            if ((bought || receipt.Status == PurchaseStatus.AlreadyOwned) &&
                listing.TryGetValue(listed.Id, out MetaItem owned))
            {
                owned.Owned = true;
                listing[listed.Id] = owned;
                int at = ordered.FindIndex(i => i.Id == listed.Id);
                if (at >= 0) ordered[at] = owned;
                Changed?.Invoke();
            }

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
                return item.displayName + " unlocked!";
            case PurchaseStatus.NotEnoughCoins: return "Not enough coins";
            case PurchaseStatus.AlreadyOwned: return "You already own that";
            case PurchaseStatus.UnknownItem: return "That item is no longer for sale";
            case PurchaseStatus.BadQuantity: return "That amount can't be bought";
            case PurchaseStatus.Unavailable: return "The store failed. You were not charged";
            case PurchaseStatus.NeedsWeapon: return "Buy its weapon first";
            case PurchaseStatus.AmmoFull: return "That is more rounds than the weapon holds.";
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
