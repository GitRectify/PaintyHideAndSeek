using System;
using System.Collections.Generic;
using UnityEngine;

public enum ShopCategory
{
    Coins = 0,
    Avatars = 1,
    Poses = 2,
    Weapons = 3,
    Bullets = 4
}

public enum ShopPriceType
{
    [Tooltip("Bought with in-game coins.")]
    Coins = 0,
    [Tooltip("Real-money pack (IAP / wallet top-up). Grants coinAmount.")]
    RealMoney = 1,
    [Tooltip("Not purchasable; unlocks at a player level.")]
    LevelLock = 2
}

[Serializable]
public class ShopItem
{
    [Tooltip("Unique, stable id. Used as the save key and (later) as the backend item id.")]
    public string id;
    public ShopCategory category;
    public string displayName;
    public Sprite icon;

    [Header("Price")]
    public ShopPriceType priceType = ShopPriceType.Coins;
    [Tooltip("Coin cost when priceType = Coins.")]
    public int coinPrice;
    [Tooltip("Label shown on the button when priceType = RealMoney, e.g. \"$0.99\".")]
    public string realPriceLabel;
    [Tooltip("Coins granted when priceType = RealMoney.")]
    public int coinAmount;
    [Tooltip("Level required when priceType = LevelLock.")]
    public int unlockLevel;

    [Header("Presentation")]
    [Tooltip("Optional ribbon text, e.g. \"Popular\" or \"Best Value\".")]
    public string badge;
    [Tooltip("Owned (and equipped if nothing else is) from the start.")]
    public bool ownedByDefault;
}

[CreateAssetMenu(fileName = "ShopCatalog", menuName = "HideAndSeek/Shop Catalog")]
public class ShopCatalog : ScriptableObject
{
    [SerializeField] private List<ShopItem> items = new List<ShopItem>();

    public IReadOnlyList<ShopItem> Items => items;

    public List<ShopItem> GetItems(ShopCategory category)
    {
        var list = new List<ShopItem>();
        foreach (ShopItem item in items)
        {
            if (item != null && item.category == category) list.Add(item);
        }
        return list;
    }
}
