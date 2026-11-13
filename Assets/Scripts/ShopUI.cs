using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Shop screen: left category tabs, 3x2 item grid, coin balance and close button. Everything it
// shows and sells is the server's, through ShopInventory; the Home menu signs in before opening it.
// The balance label is bound to the server's balance (CoinBalance) and opens the wallet when tapped.
public class ShopUI : MonoBehaviour
{
    [Header("Data")]
    [SerializeField] private ShopCatalog catalog;

    [Header("Root")]
    [Tooltip("The object toggled by Open/Close (usually this ShopCanvas).")]
    [SerializeField] private GameObject root;
    [SerializeField] private UnityEngine.UI.Button closeButton;
    [SerializeField] private UnityEngine.UI.Text coinText;

    [Header("Tabs (order = ShopCategory: Coins, Avatars, Poses, Weapons, Bullets; Extras is added at runtime)")]
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
        EnsureExtrasTab();
        if (closeButton != null) closeButton.onClick.AddListener(Close);
        for (int i = 0; i < tabButtons.Length; i++)
        {
            int index = i;
            if (tabButtons[i] != null) tabButtons[i].onClick.AddListener(() => SelectTab(index));
        }

        CoinBalance.Bind(coinText);
        OnlineWallet.MakeOpener(coinText);

        // The cards' lock images carry no picture in the scene (they showed as white squares).
        foreach (UnityEngine.UI.Image image in (root != null ? root : gameObject).GetComponentsInChildren<UnityEngine.UI.Image>(true))
        {
            if (image.name != "LockIcon" || image.sprite != null) continue;
            image.sprite = OnlineUI.LockSprite();
            image.preserveAspect = true;
        }
    }

    private void OnEnable()
    {
        ShopInventory.Changed += HandleChanged;
        PendingTopUp.Settled += HandleTopUpSettled;
        SelectTab((int)_current);
        if (ToastObject != null) ToastObject.SetActive(false);
    }

    private void OnDisable()
    {
        ShopInventory.Changed -= HandleChanged;
        PendingTopUp.Settled -= HandleTopUpSettled;
    }

    // A credit is confirmed by CoinNotice, over every screen; the shop says only how a failed one ended.
    private void HandleTopUpSettled(PendingTopUp.Entry entry, Meccha.Shared.Game.PaymentState state)
    {
        if (state != Meccha.Shared.Game.PaymentState.Credited) ShowToast(PendingTopUp.MessageFor(entry, state));
    }

    // The Extras tab (ShopCategory.Extras: Next Player) is a copy of the last of the scene's five,
    // placed under it; the tab column lays the six out. Its icon is the copied tab's, so it is hidden.
    private void EnsureExtrasTab()
    {
        int extras = (int)ShopCategory.Extras;
        if (tabButtons == null || tabButtons.Length != extras || tabButtons[extras - 1] == null) return;
        if (tabBackgrounds == null || tabBackgrounds.Length != extras) return;

        GameObject last = tabButtons[extras - 1].gameObject;
        GameObject copy = Instantiate(last, last.transform.parent);
        copy.name = "Tab_Extras";
        Transform icon = copy.transform.Find("Icon");
        if (icon != null) icon.gameObject.SetActive(false);
        OnlineUI.SetAnyText(copy, "Extras");

        System.Array.Resize(ref tabButtons, extras + 1);
        tabButtons[extras] = copy.GetComponent<UnityEngine.UI.Button>();
        System.Array.Resize(ref tabBackgrounds, extras + 1);
        UnityEngine.UI.Image lastBackground = tabBackgrounds[extras - 1];
        tabBackgrounds[extras] = lastBackground != null && lastBackground.gameObject == last
            ? copy.GetComponent<UnityEngine.UI.Image>()
            : null;
    }

    private bool _homeWasShown;
    private Action _onClosed;
    private Canvas _canvas;
    private bool _canvasOverride;
    private int _canvasOrder;

    public void Open() => OpenFrom(null);

    /// <summary>
    /// Opens the shop over whatever is on screen (Home, the rooms, a round's HUD), above it all but
    /// the paint panel; Close puts back exactly what was there and calls <paramref name="onClosed"/>.
    /// </summary>
    public void OpenFrom(Action onClosed)
    {
        AudioManager.ButtonTap();
        _onClosed = onClosed;
        _homeWasShown = homeCanvas != null && homeCanvas.activeSelf;
        if (homeCanvas != null) homeCanvas.SetActive(false);
        GameObject shown = root != null ? root : gameObject;
        shown.SetActive(true);

        _canvas = shown.GetComponent<Canvas>();
        if (_canvas != null)
        {
            _canvasOverride = _canvas.overrideSorting;
            _canvasOrder = _canvas.sortingOrder;
            if (!_canvas.isRootCanvas) _canvas.overrideSorting = true;
            _canvas.sortingOrder = 58;
        }

        // Prices, ownership and the balance as the server has them now.
        ShopInventory.Refresh(error =>
        {
            if (error != null && this != null && isActiveAndEnabled) ShowToast(error);
        });
    }

    /// <summary>Closes the shop if it is open: a match is starting under it.</summary>
    public static void CloseOpen()
    {
        ShopUI shop = FindAnyObjectByType<ShopUI>(FindObjectsInactive.Include);
        if (shop != null && (shop.root != null ? shop.root : shop.gameObject).activeInHierarchy) shop.Close();
    }

    public void Close()
    {
        AudioManager.ButtonTap();
        (root != null ? root : gameObject).SetActive(false);
        if (_canvas != null)
        {
            _canvas.sortingOrder = _canvasOrder;
            if (!_canvas.isRootCanvas) _canvas.overrideSorting = _canvasOverride;
        }

        // Back to what was there: Home only if it was showing (not over a round, not the rooms).
        if (homeCanvas != null && _homeWasShown) homeCanvas.SetActive(true);
        Action closed = _onClosed;
        _onClosed = null;
        closed?.Invoke();
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
        if (_current == ShopCategory.Extras) items.AddRange(ShopInventory.MapItems());   // maps for online play (2026-11-13)
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
            bool unequip = item.category == ShopCategory.Weapons && ShopInventory.IsEquipped(item);
            ShopInventory.Equip(item);   // an avatar swaps the character's body in place
            ShowToast(unequip ? "Standard gun equipped" : item.displayName + " equipped");
            return;
        }

        if (!ShopInventory.IsForSale(item))
        {
            ShowToast(item.category == ShopCategory.Coins ? "Coin top-ups aren't available yet" : "Coming soon");
            return;
        }

        if (item.category == ShopCategory.Coins)
        {
            // Real money: the provider's checkout opens, and PendingTopUp reports the outcome.
            _busy = true;
            ShowToast("Opening the payment...");
            ShopInventory.StartTopUp(item, (opened, message) =>
            {
                _busy = false;
                if (this != null && isActiveAndEnabled) ShowToast(message);
            });
            return;
        }

        _busy = true;
        ShowToast("Buying...");
        ShopInventory.Purchase(item, (bought, message) =>
        {
            _busy = false;
            if (this == null || !isActiveAndEnabled) return;

            // A bought weapon is carried at once, as the local shop did; an avatar waits for
            // Equip (a tap on its card), which swaps the body.
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

    private void HandleChanged() => RefreshCards();

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
