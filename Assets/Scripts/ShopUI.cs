using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Shop screen: left category tabs, 3x2 item grid, coin balance and close button.
public class ShopUI : MonoBehaviour
{
    [Header("Data")]
    [SerializeField] private ShopCatalog catalog;

    [Header("Root")]
    [Tooltip("The object toggled by Open/Close (usually this ShopCanvas).")]
    [SerializeField] private GameObject root;
    [SerializeField] private UnityEngine.UI.Button closeButton;
    [SerializeField] private UnityEngine.UI.Text coinText;

    [Header("Tabs (order = ShopCategory: Coins, Avatars, Poses, Weapons, Bullets)")]
    [SerializeField] private UnityEngine.UI.Button[] tabButtons;
    [SerializeField] private UnityEngine.UI.Image[] tabBackgrounds;
    [SerializeField] private Sprite tabActiveSprite;
    [SerializeField] private Sprite tabInactiveSprite;

    [Header("Grid")]
    [SerializeField] private ShopItemCard[] cards;

    [Header("Feedback")]
    [Tooltip("Object shown/hidden for the toast (e.g. its background pill). Falls back to the text object.")]
    [SerializeField] private GameObject toastRoot;
    [SerializeField] private UnityEngine.UI.Text toastText;
    [SerializeField] private float toastSeconds = 1.6f;

    private GameObject ToastObject => toastRoot != null ? toastRoot : (toastText != null ? toastText.gameObject : null);

    [Header("Home")]
    [Tooltip("Hidden while the shop is open, restored on close.")]
    [SerializeField] private GameObject homeCanvas;

    private ShopCategory _current = ShopCategory.Coins;
    private Coroutine _toast;

    private void Awake()
    {
        if (closeButton != null) closeButton.onClick.AddListener(Close);
        for (int i = 0; i < tabButtons.Length; i++)
        {
            int index = i;
            if (tabButtons[i] != null) tabButtons[i].onClick.AddListener(() => SelectTab(index));
        }
    }

    private void OnEnable()
    {
        ShopInventory.CoinsChanged += HandleCoinsChanged;
        ShopInventory.InventoryChanged += RefreshCards;
        RefreshCoins();
        SelectTab((int)_current);
        if (ToastObject != null) ToastObject.SetActive(false);
    }

    private void OnDisable()
    {
        ShopInventory.CoinsChanged -= HandleCoinsChanged;
        ShopInventory.InventoryChanged -= RefreshCards;
    }

    public void Open()
    {
        AudioManager.ButtonTap();
        if (homeCanvas != null) homeCanvas.SetActive(false);
        (root != null ? root : gameObject).SetActive(true);
    }

    public void Close()
    {
        AudioManager.ButtonTap();
        (root != null ? root : gameObject).SetActive(false);
        if (homeCanvas != null) homeCanvas.SetActive(true);
    }

    public void SelectTab(int index)
    {
        _current = (ShopCategory)index;

        for (int i = 0; i < tabBackgrounds.Length; i++)
        {
            if (tabBackgrounds[i] != null)
                tabBackgrounds[i].sprite = i == index ? tabActiveSprite : tabInactiveSprite;
        }

        List<ShopItem> items = catalog != null ? catalog.GetItems(_current) : new List<ShopItem>();
        for (int i = 0; i < cards.Length; i++)
        {
            if (cards[i] == null) continue;
            cards[i].Bind(i < items.Count ? items[i] : null, catalog, HandleCardClicked);
        }
    }

    private void HandleCardClicked(ShopItem item)
    {
        AudioManager.ButtonTap();

        if (item.priceType == ShopPriceType.Coins && ShopInventory.IsOwned(item))
        {
            ShopInventory.Equip(item);
            ShowToast(item.displayName + " equipped");
            return;
        }

        switch (ShopInventory.Purchase(item))
        {
            case ShopInventory.PurchaseResult.Success:
                if (item.priceType == ShopPriceType.RealMoney)
                {
                    ShowToast("+" + item.coinAmount.ToString("N0") + " coins");
                }
                else
                {
                    ShopInventory.Equip(item);
                    ShowToast(item.displayName + " unlocked!");
                }
                break;
            case ShopInventory.PurchaseResult.NotEnoughCoins:
                ShowToast("Not enough coins");
                break;
            case ShopInventory.PurchaseResult.Locked:
                ShowToast("Unlocks at Level " + item.unlockLevel);
                break;
        }
    }

    private void RefreshCards()
    {
        foreach (ShopItemCard card in cards)
        {
            if (card != null && card.gameObject.activeSelf) card.RefreshState(catalog);
        }
    }

    private void HandleCoinsChanged(int coins) => RefreshCoins();

    private void RefreshCoins()
    {
        if (coinText != null) coinText.text = ShopInventory.Coins.ToString();
    }

    private void ShowToast(string message)
    {
        if (toastText == null) return;
        if (_toast != null) StopCoroutine(_toast);
        _toast = StartCoroutine(ToastRoutine(message));
    }

    private IEnumerator ToastRoutine(string message)
    {
        toastText.text = message;
        ToastObject.SetActive(true);
        yield return new WaitForSecondsRealtime(toastSeconds);
        ToastObject.SetActive(false);
        _toast = null;
    }
}
