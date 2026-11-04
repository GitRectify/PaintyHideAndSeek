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

// One card in the shop. Prices, ownership and coin-pack prices are the server's (ShopInventory);
// the card only says what it shows and which server item or package it stands for.
[Serializable]
public class ShopItem
{
    [Tooltip("Unique, stable id for the card.")]
    public string id;
    public ShopCategory category;
    public string displayName;
    public Sprite icon;

    [Tooltip("Coin packs: the coins shown until the server's package has loaded.")]
    public int coinAmount;

    [Header("Presentation")]
    [Tooltip("Optional ribbon text, e.g. \"Popular\" or \"Best Value\".")]
    public string badge;
    [Tooltip("Owned (and equipped if nothing else is) from the start.")]
    public bool ownedByDefault;

    [Header("Server (online shop)")]
    [Tooltip("The server catalog's item id (server.json Catalog), or for a coin pack its top-up package id (Wallet:TopUps). Empty = not sold online yet (\"coming soon\"). Every bullet pack names the one \"bullets\" item.")]
    public string serverId;
    [Tooltip("The game's own number for the item, the same as the server's GameId: avatar = CharacterSelect index, pose = picker slot + 1, weapon = WeaponSkins id.")]
    public int gameId;
    [Tooltip("Bullets bought by this card (bullet packs only).")]
    public int quantity = 1;
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
