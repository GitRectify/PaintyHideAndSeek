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

        // Keys never move between the shop's buttons (a key meant for the round could press a tab).
        foreach (UnityEngine.UI.Button button in (root != null ? root : gameObject).GetComponentsInChildren<UnityEngine.UI.Button>(true))
        {
            UnityEngine.UI.Navigation none = button.navigation;
            none.mode = UnityEngine.UI.Navigation.Mode.None;
            button.navigation = none;
        }

        StyleToast();

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

        // The paint panel draws above everything in a round, the shop included: it would sit over
        // the tabs and take their taps. The shop puts it away (it opens again from its button).
        ChameleonPaint paint = FindAnyObjectByType<ChameleonPaint>();
        if (paint != null && paint.DrawingActive) paint.CloseDrawingNow();

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
        if (UnityEngine.EventSystems.EventSystem.current != null) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);

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
            // The card may be older than the server's list (a refresh still on its way, a list from
            // before a sign-in): ask again before saying it is not for sale.
            _busy = true;
            ShowToast("Checking...");
            ShopInventory.Refresh(error =>
            {
                _busy = false;
                if (this == null || !isActiveAndEnabled) return;
                if (ShopInventory.IsForSale(item)) Buy(item);
                else ShowToast(error ?? (item.category == ShopCategory.Coins ? "Coin top-ups aren't available yet" : "Coming soon"));
            });
            return;
        }

        Buy(item);
    }

    private void Buy(ShopItem item)
    {
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
            RefreshCards();
            ShowToast(message, bought);
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

    private void ShowToast(string message) => ShowToast(message, false);

    // A bought item says so in green, others in the shop's dark; both opaque, in the shop's header
    // between its logo and the coin balance, clear of every card (2026-11-13: a see-through strip
    // over the buttons went unnoticed, and one in the middle of the grid covered the cards).
    private void ShowToast(string message, bool success)
    {
        if (toastText == null) return;
        if (_toast != null) StopCoroutine(_toast);
        if (toastBackground != null) toastBackground.color = success ? new Color(0.13f, 0.58f, 0.27f, 0.98f) : new Color(0.09f, 0.11f, 0.17f, 0.96f);
        _toast = StartCoroutine(ToastRoutine(message, success ? Mathf.Max(toastSeconds, 2.4f) : toastSeconds));
    }

    private IEnumerator ToastRoutine(string message, float seconds)
    {
        toastText.text = message;
        GameObject toast = ToastObject;
        toast.SetActive(true);
        toast.transform.SetAsLastSibling();

        // Slides down into the header as it fades in, and back up as it goes.
        var rt = toast.transform as RectTransform;
        for (float t = 0f; t < ToastSlideSeconds; t += Time.unscaledDeltaTime)
        {
            PlaceToast(rt, t / ToastSlideSeconds);
            yield return null;
        }
        PlaceToast(rt, 1f);
        yield return new WaitForSecondsRealtime(seconds);
        for (float t = ToastSlideSeconds; t > 0f; t -= Time.unscaledDeltaTime)
        {
            PlaceToast(rt, t / ToastSlideSeconds);
            yield return null;
        }
        toast.SetActive(false);
        _toast = null;
    }

    private const float ToastSlideSeconds = 0.25f;
    private static readonly Vector2 ToastHome = new Vector2(0f, -22f);
    private CanvasGroup toastGroup;

    private void PlaceToast(RectTransform rt, float t)
    {
        float e = 1f - (1f - Mathf.Clamp01(t)) * (1f - Mathf.Clamp01(t));
        if (rt != null) rt.anchoredPosition = ToastHome + Vector2.up * (1f - e) * 60f;
        if (toastGroup != null) toastGroup.alpha = e;
    }

    private UnityEngine.UI.Image toastBackground;

    private void StyleToast()
    {
        GameObject toast = ToastObject;
        if (toast == null) return;
        toastBackground = toast.GetComponent<UnityEngine.UI.Image>();
        if (toastBackground != null)
        {
            // Its sprite is a dark, see-through pill that no tint can lighten: a plain panel with an
            // edge instead, coloured per message.
            toastBackground.sprite = null;
            toastBackground.type = UnityEngine.UI.Image.Type.Simple;
            UnityEngine.UI.Outline edge = toast.GetComponent<UnityEngine.UI.Outline>();
            if (edge == null) edge = toast.AddComponent<UnityEngine.UI.Outline>();
            edge.effectColor = new Color(0f, 0f, 0f, 0.55f);
            edge.effectDistance = new Vector2(3f, -3f);
        }
        if (toast.transform is RectTransform rt)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);     // the header, top centre
            rt.anchoredPosition = ToastHome;
            rt.sizeDelta = new Vector2(760f, 92f);
        }
        toastGroup = toast.GetComponent<CanvasGroup>();
        if (toastGroup == null) toastGroup = toast.AddComponent<CanvasGroup>();
        if (toastText != null)
        {
            toastText.fontSize = 32;
            toastText.resizeTextForBestFit = true;
            toastText.resizeTextMinSize = 20;
            toastText.resizeTextMaxSize = 32;
        }
    }
}
