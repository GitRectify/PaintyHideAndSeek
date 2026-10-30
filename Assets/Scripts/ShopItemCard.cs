using System;
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
                          || item.category == ShopCategory.Bullets;
        string label = item.category == ShopCategory.Coins ? item.coinAmount.ToString("N0") : item.displayName;
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

        bool coin = false, check = false, locked = false, interactable = true;
        Sprite bg = buySprite;
        string text;

        if (_item.priceType == ShopPriceType.RealMoney)
        {
            text = _item.realPriceLabel;
        }
        else if (_item.priceType == ShopPriceType.LevelLock)
        {
            text = "Unlocks at Level " + _item.unlockLevel;
            locked = true;
            interactable = false;
            bg = disabledSprite;
        }
        else if (ShopInventory.IsEquipped(_item, catalog))
        {
            text = "Equipped";
            check = true;
            interactable = false;
            bg = disabledSprite;
        }
        else if (ShopInventory.IsOwned(_item))
        {
            text = "Equip";
            bg = equipSprite;
        }
        else
        {
            text = _item.coinPrice.ToString("N0");
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
