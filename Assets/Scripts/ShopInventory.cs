using System;
using UnityEngine;

// Local (PlayerPrefs via UtilGame) implementation of shop ownership, equipping and purchasing.
// This is the single seam to replace with the backend calls from the architecture diagram
// (ListAllItems / PurchaseItems / Topup + Walletcallback) - UI code only talks to this class.
public static class ShopInventory
{
    private const string Category = "none";
    private const string OwnedPrefix = "ShopOwned_";
    private const string EquippedPrefix = "ShopEquipped_";

    public enum PurchaseResult
    {
        Success,
        AlreadyOwned,
        NotEnoughCoins,
        Locked
    }

    // Raised whenever the coin balance changes (purchase, top-up).
    public static event Action<int> CoinsChanged;
    // Raised whenever ownership / equipped state changes.
    public static event Action InventoryChanged;

    public static int Coins => GameData.Coin;

    public static bool IsOwned(ShopItem item)
    {
        if (item == null) return false;
        if (item.ownedByDefault) return true;
        return UtilGame.GetDataInt(OwnedPrefix + item.id, 0, Category) == 1;
    }

    public static string GetEquippedId(ShopCategory category, ShopCatalog catalog)
    {
        // NOTE: UtilGame.GetDataString's parameter order is (key, child, default).
        string saved = UtilGame.GetDataString(EquippedPrefix + category, Category, "");
        if (!string.IsNullOrEmpty(saved)) return saved;

        // Nothing equipped yet: fall back to the first default-owned item of the category.
        if (catalog != null)
        {
            foreach (ShopItem item in catalog.GetItems(category))
            {
                if (item.ownedByDefault) return item.id;
            }
        }
        return "";
    }

    public static bool IsEquipped(ShopItem item, ShopCatalog catalog)
    {
        if (item == null) return false;
        return GetEquippedId(item.category, catalog) == item.id;
    }

    public static void Equip(ShopItem item)
    {
        if (item == null || !IsOwned(item)) return;
        UtilGame.SetDataString(EquippedPrefix + item.category, item.id, Category);
        InventoryChanged?.Invoke();
    }

    public static PurchaseResult Purchase(ShopItem item)
    {
        switch (item.priceType)
        {
            case ShopPriceType.LevelLock:
                return PurchaseResult.Locked;

            case ShopPriceType.RealMoney:
                // TODO(backend): start the Topup flow and credit coins only after the server's
                // Walletcallback confirms payment. Granted locally for now so the flow is testable.
                Debug.LogWarning("[Shop] Real-money purchase simulated locally (no IAP/wallet yet): " + item.id);
                AddCoins(item.coinAmount);
                return PurchaseResult.Success;

            default:
                if (IsOwned(item)) return PurchaseResult.AlreadyOwned;
                if (GameData.Coin < item.coinPrice) return PurchaseResult.NotEnoughCoins;

                // TODO(backend): replace with server-verified PurchaseItems.
                GameData.Coin = GameData.Coin - item.coinPrice;
                UtilGame.SetDataInt(OwnedPrefix + item.id, 1, Category);
                CoinsChanged?.Invoke(GameData.Coin);
                InventoryChanged?.Invoke();
                return PurchaseResult.Success;
        }
    }

    public static void AddCoins(int amount)
    {
        GameData.Coin = GameData.Coin + amount;
        CoinsChanged?.Invoke(GameData.Coin);
    }
}
