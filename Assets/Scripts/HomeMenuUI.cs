using System.Collections;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;

// Main home menu: Play / Multiplayer / Shop buttons plus the coin balance. The one currency is the
// server wallet: the balance shows once signed in ("-" before), and the Shop signs in first.
// Play opens the role-selection screen (the original home canvas: PlayNow = Hider,
// SeekerMode = Seeker, both still wired to HomeUI.BeginMatch).
public class HomeMenuUI : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The main home canvas this menu lives on.")]
    [SerializeField] private GameObject homeCanvas;
    [Tooltip("Role selection canvas (Hider / Seeker) opened by Play.")]
    [SerializeField] private GameObject roleSelectCanvas;
    [SerializeField] private ShopUI shopUI;
    [SerializeField] private UnityEngine.UI.Text coinText;

    [Header("Buttons")]
    [SerializeField] private UnityEngine.UI.Button playButton;
    [SerializeField] private UnityEngine.UI.Button multiplayerButton;
    [SerializeField] private UnityEngine.UI.Button shopButton;
    [Tooltip("Back button on the role selection screen; returns to this home menu.")]
    [SerializeField] private UnityEngine.UI.Button roleSelectBackButton;

    [Header("Toast")]
    [Tooltip("The toast Multiplayer used for \"coming soon\"; now it tells how a coin top-up ended when that happens on Home. Falls back to the text object.")]
    [SerializeField] private GameObject toastRoot;
    [SerializeField] private UnityEngine.UI.Text toastText;

    [SerializeField] private float toastSeconds = 2.5f;

    private GameObject ToastObject => toastRoot != null ? toastRoot : (toastText != null ? toastText.gameObject : null);
    private Coroutine toast;

    private void Awake()
    {
        if (playButton != null) playButton.onClick.AddListener(OnPlay);
        if (multiplayerButton != null) multiplayerButton.onClick.AddListener(OnMultiplayer);
        if (shopButton != null) shopButton.onClick.AddListener(OnShop);
        if (roleSelectBackButton != null) roleSelectBackButton.onClick.AddListener(ShowHome);
    }

    // Once per launch (the menu scene also reloads on a character switch): an online round that
    // was running when the app closed can still be rejoined while the server holds the seat.
    private static bool rejoinOffered;

    private void Start()
    {
        if (rejoinOffered) return;
        rejoinOffered = true;
        OnlineLobby.OfferRejoin();
    }

    private void OnEnable()
    {
        ShopInventory.Changed += RefreshCoins;
        PendingTopUp.Settled += HandleTopUpSettled;
        RefreshCoins();
        if (ToastObject != null) ToastObject.SetActive(false);
    }

    private void OnDisable()
    {
        ShopInventory.Changed -= RefreshCoins;
        PendingTopUp.Settled -= HandleTopUpSettled;
        toast = null;
    }

    // A top-up the player paid in the browser, settled while Home is up (the shop says it itself).
    private void HandleTopUpSettled(PendingTopUp.Entry entry, PaymentState state)
    {
        if (toastText == null || ToastObject == null) return;
        if (homeCanvas != null && !homeCanvas.activeInHierarchy) return;
        if (toast != null) StopCoroutine(toast);
        toast = StartCoroutine(ShowToast(PendingTopUp.MessageFor(entry, state)));
    }

    private IEnumerator ShowToast(string message)
    {
        toastText.text = message;
        ToastObject.SetActive(true);
        yield return new WaitForSecondsRealtime(toastSeconds);
        ToastObject.SetActive(false);
        toast = null;
    }

    private void OnPlay()
    {
        AudioManager.ButtonTap();
        if (roleSelectCanvas == null) return;
        if (homeCanvas != null) homeCanvas.SetActive(false);
        roleSelectCanvas.SetActive(true);
    }

    // Role selection -> back to the main home menu.
    public void ShowHome()
    {
        AudioManager.ButtonTap();
        if (roleSelectCanvas != null) roleSelectCanvas.SetActive(false);
        if (homeCanvas != null) homeCanvas.SetActive(true);
    }

    // Multiple Play: sign in (and choose a name on the first run), then Seeker/Hider, room,
    // matchmaking and the online round (OnlineLobby, OnlineRound).
    private void OnMultiplayer()
    {
        AudioManager.ButtonTap();
        OnlineSignIn.Open(OnSignedIn, null);
    }

    private void OnSignedIn(MetaProfile profile)
    {
        RefreshCoins();
        OnlineLobby.Open(profile, null);
    }

    // The shop sells for server coins, so it signs in first (at once when already signed in).
    private void OnShop()
    {
        if (shopUI == null) return;
        OnlineSignIn.Open(_ =>
        {
            RefreshCoins();
            shopUI.Open();
        }, null);
    }

    private void RefreshCoins()
    {
        if (coinText == null) return;
        coinText.text = ShopInventory.SignedIn ? ShopInventory.CoinsText(ShopInventory.CoinCents) : "-";
    }
}
