using System;
using Meccha.Shared.Game;
using UnityEngine;

// One card in the shop grid. Pure view: ShopUI binds an item and receives button clicks.
public class ShopItemCard : MonoBehaviour
{
    [Header("Content")]
    [SerializeField] private UnityEngine.UI.Image icon;
    [Tooltip("Name shown above the icon (Poses / Weapons / Bullets layout).")]
    [SerializeField] private UnityEngine.UI.Text topLabel;
    [Tooltip("Name/amount shown below the icon (Coins / Avatars layout).")]
    [SerializeField] private UnityEngine.UI.Text bottomLabel;
    [SerializeField] private GameObject badgeRoot;
    [SerializeField] private UnityEngine.UI.Text badgeLabel;

    [Header("Icon area (offsets inside the card: x = top inset, y = bottom inset)")]
    [SerializeField] private Vector2 iconInsetsLabelOnTop = new Vector2(62f, 108f);
    [SerializeField] private Vector2 iconInsetsLabelBelow = new Vector2(14f, 160f);

    [Header("Action button")]
    [SerializeField] private UnityEngine.UI.Button actionButton;
    [SerializeField] private UnityEngine.UI.Image actionBackground;
    [SerializeField] private UnityEngine.UI.Text actionLabel;
    [SerializeField] private GameObject actionCoinIcon;
    [SerializeField] private GameObject actionCheckIcon;
    [SerializeField] private GameObject actionLockIcon;

    [Header("Button styles")]
    [SerializeField] private Sprite buySprite;
    [SerializeField] private Sprite disabledSprite;
    [SerializeField] private Sprite equipSprite;

    private ShopItem _item;
    private Action<ShopItem> _onClick;

    public ShopItem Item => _item;

    private void Awake()
    {
        if (actionButton != null) actionButton.onClick.AddListener(HandleClick);
    }

    private void OnDestroy()
    {
        if (actionButton != null) actionButton.onClick.RemoveListener(HandleClick);
    }

    public void Bind(ShopItem item, ShopCatalog catalog, Action<ShopItem> onClick)
    {
        _item = item;
        _onClick = onClick;
        gameObject.SetActive(item != null);
        if (item == null) return;

        if (icon != null)
        {
            icon.sprite = item.icon;
            icon.enabled = item.icon != null;
            icon.preserveAspect = true;
        }

        // Coins/Avatars put the label under the art; the other tabs put it on top (per mockups).
        bool labelOnTop = item.category == ShopCategory.Poses
                          || item.category == ShopCategory.Weapons
                          || item.category == ShopCategory.Bullets
                          || item.category == ShopCategory.Extras;
        // A coin pack's amount is the server's (RefreshState), so nothing is written before it is known.
        string label = item.category == ShopCategory.Coins ? "" : item.displayName;
        if (topLabel != null)
        {
            topLabel.gameObject.SetActive(labelOnTop);
            topLabel.text = label;
        }
        if (bottomLabel != null)
        {
            bottomLabel.gameObject.SetActive(!labelOnTop);
            bottomLabel.text = label;
        }
        if (icon != null)
        {
            Vector2 insets = labelOnTop ? iconInsetsLabelOnTop : iconInsetsLabelBelow;
            RectTransform rt = icon.rectTransform;
            rt.offsetMax = new Vector2(rt.offsetMax.x, -insets.x);
            rt.offsetMin = new Vector2(rt.offsetMin.x, insets.y);
        }

        bool hasBadge = !string.IsNullOrEmpty(item.badge);
        if (badgeRoot != null) badgeRoot.SetActive(hasBadge);
        if (badgeLabel != null) badgeLabel.text = item.badge;

        RefreshState(catalog);
    }

    public void RefreshState(ShopCatalog catalog)
    {
        if (_item == null) return;

        // A special bullet pack's ribbon says how many of that type the player has (the server's
        // count, ShopInventory.SpecialBullets); with none it shows the card's own badge, if any.
        if (_item.category == ShopCategory.Bullets && _item.gameId >= 1 && _item.gameId <= BulletSpecs.SpecialCount)
        {
            int owned = ShopInventory.SpecialBullets((BulletType)_item.gameId);
            if (badgeRoot != null) badgeRoot.SetActive(owned > 0 || !string.IsNullOrEmpty(_item.badge));
            if (badgeLabel != null) badgeLabel.text = owned > 0 ? $"Own {owned}" : _item.badge;
        }

        // Prices and ownership are the server's (ShopInventory); the card only shows them.
        bool coin = false, check = false, locked = false, interactable = true;
        Sprite bg = buySprite;
        string text;

        if (_item.category == ShopCategory.Coins)
        {
            // A coin pack: the server's coins and real-money price. A pack with a payment still
            // open says so; tapping it goes back to that payment's checkout.
            string coins = ShopInventory.PackCoinsText(_item);
            if (bottomLabel != null) bottomLabel.text = coins;
            if (topLabel != null) topLabel.text = coins;

            if (!ShopInventory.TopUpsLoaded)
            {
                text = "...";
                interactable = false;
                bg = disabledSprite;
            }
            else if (!ShopInventory.IsForSale(_item))
            {
                text = "Coming soon";
                locked = true;
                interactable = false;
                bg = disabledSprite;
            }
            else if (PendingTopUp.IsWaiting(_item.serverId))
            {
                text = "Waiting...";
                bg = equipSprite;
            }
            else
            {
                text = ShopInventory.PackPriceText(_item);
            }
        }
        else if (!ShopInventory.Loaded)
        {
            text = "...";
            interactable = false;
            bg = disabledSprite;
        }
        else if (ShopInventory.IsEquipped(_item))
        {
            text = "Equipped";
            check = true;
            // A weapon other than the standard gun is tapped again to put the standard gun back.
            interactable = _item.category == ShopCategory.Weapons && _item.gameId != 0;
            bg = disabledSprite;
        }
        else if (ShopInventory.IsOwned(_item) && _item.category != ShopCategory.Bullets)
        {
            if (ShopInventory.CanEquip(_item))
            {
                text = "Equip";
                bg = equipSprite;
            }
            else
            {
                text = "Owned";
                check = true;
                interactable = false;
                bg = disabledSprite;
            }
        }
        else if (!ShopInventory.IsForSale(_item))
        {
            text = "Coming soon";
            locked = true;
            interactable = false;
            bg = disabledSprite;
        }
        else
        {
            text = ShopInventory.CoinsText(ShopInventory.PriceCents(_item));
            coin = true;
        }

        if (actionLabel != null) actionLabel.text = text;
        if (actionBackground != null && bg != null) actionBackground.sprite = bg;
        if (actionButton != null) actionButton.interactable = interactable;
        if (actionCoinIcon != null) actionCoinIcon.SetActive(coin);
        if (actionCheckIcon != null) actionCheckIcon.SetActive(check);
        if (actionLockIcon != null) actionLockIcon.SetActive(locked);
    }

    private void HandleClick()
    {
        if (_item != null) _onClick?.Invoke(_item);
    }
}
