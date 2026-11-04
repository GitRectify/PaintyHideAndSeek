using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Shop screen: left category tabs, 3x2 item grid, coin balance and close button. Everything it
// shows and sells is the server's, through ShopInventory; the Home menu signs in before opening it.
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
    private bool _busy;

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
        ShopInventory.Changed += HandleChanged;
        RefreshCoins();
        SelectTab((int)_current);
        if (ToastObject != null) ToastObject.SetActive(false);
    }

    private void OnDisable()
    {
        ShopInventory.Changed -= HandleChanged;
    }

    public void Open()
    {
        AudioManager.ButtonTap();
        if (homeCanvas != null) homeCanvas.SetActive(false);
        (root != null ? root : gameObject).SetActive(true);

        // Prices, ownership and the balance as the server has them now.
        ShopInventory.Refresh(error =>
        {
            if (error != null && this != null && isActiveAndEnabled) ShowToast(error);
        });
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
        if (_busy) return;

        if (ShopInventory.CanEquip(item) && ShopInventory.IsOwned(item))
        {
            bool unequip = ShopInventory.IsEquipped(item);
            ShopInventory.Equip(item);   // an avatar reloads the scene to swap the character
            ShowToast(unequip ? "Standard gun equipped" : item.displayName + " equipped");
            return;
        }

        if (!ShopInventory.IsForSale(item))
        {
            ShowToast(item.category == ShopCategory.Coins ? "Coin top-ups are coming soon" : "Coming soon");
            return;
        }

        _busy = true;
        ShowToast("Buying...");
        ShopInventory.Purchase(item, (bought, message) =>
        {
            _busy = false;
            if (this == null || !isActiveAndEnabled) return;

            // A bought weapon is carried at once, as the local shop did; an avatar waits for
            // Equip, which reloads the scene.
            if (bought && item.category == ShopCategory.Weapons) ShopInventory.Equip(item);
            ShowToast(message);
        });
    }

    private void RefreshCards()
    {
        foreach (ShopItemCard card in cards)
        {
            if (card != null && card.gameObject.activeSelf) card.RefreshState(catalog);
        }
    }

    private void HandleChanged()
    {
        RefreshCoins();
        RefreshCards();
    }

    private void RefreshCoins()
    {
        if (coinText != null) coinText.text = ShopInventory.CoinsText(ShopInventory.CoinCents);
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
