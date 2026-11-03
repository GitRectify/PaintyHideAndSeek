using System.Collections;
using Meccha.Shared.Protocol;
using UnityEngine;

// Main home menu: Play / Multiplayer / Shop buttons plus the coin balance.
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

    [Header("Multiplayer (sign-in only; matchmaking not connected yet)")]
    [Tooltip("Object shown/hidden for the toast (e.g. its background pill). Falls back to the text object.")]
    [SerializeField] private GameObject toastRoot;
    [SerializeField] private UnityEngine.UI.Text toastText;
    [SerializeField] private string multiplayerMessage= "Online multiplayer coming soon!";
    [SerializeField] private float toastSeconds = 1.6f;

    private Coroutine _toast;

    private GameObject ToastObject => toastRoot != null ? toastRoot : (toastText != null ? toastText.gameObject : null);

    private void Awake()
    {
        if (playButton != null) playButton.onClick.AddListener(OnPlay);
        if (multiplayerButton != null) multiplayerButton.onClick.AddListener(OnMultiplayer);
        if (shopButton != null) shopButton.onClick.AddListener(OnShop);
        if (roleSelectBackButton != null) roleSelectBackButton.onClick.AddListener(ShowHome);
    }

    private void OnEnable()
    {
        ShopInventory.CoinsChanged += HandleCoinsChanged;
        RefreshCoins();
        if (ToastObject != null) ToastObject.SetActive(false);
    }

    private void OnDisable()
    {
        ShopInventory.CoinsChanged -= HandleCoinsChanged;
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

    // Multiple Play: sign in (and choose a name on the first run). TODO(backend): role and room
    // selection -> FindMatch; until then a signed-in player gets the toast.
    private void OnMultiplayer()
    {
        AudioManager.ButtonTap();
        OnlineSignIn.Open(OnSignedIn, null);
    }

    private void OnSignedIn(MetaProfile profile)
    {
        ShowToast($"Signed in as {profile.UserId} ({OnlineUI.CoinsText(profile.CoinCents)}). {multiplayerMessage}");
    }

    private void ShowToast(string message)
    {
        if (toastText == null) return;
        if (_toast != null) StopCoroutine(_toast);
        _toast = StartCoroutine(ToastRoutine(message));
    }

    private void OnShop()
    {
        if (shopUI != null) shopUI.Open();
    }

    private void HandleCoinsChanged(int coins) => RefreshCoins();

    private void RefreshCoins()
    {
        if (coinText != null) coinText.text = GameData.Coin.ToString();
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
