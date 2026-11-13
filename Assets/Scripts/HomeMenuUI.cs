using System.Collections;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;

// Main home menu: Play / Multiplayer / Shop buttons plus the coin balance. The one currency is the
// server wallet: the label is bound to the server's balance (CoinBalance: "-" until signed in, never
// a number of its own), and tapping it signs in and opens the wallet (OnlineWallet). The Shop signs
// in first too. A player who has signed in before is signed in quietly when Home first shows
// (NetBootstrap.QuietSignIn), so the balance is simply there.
// Play opens the role-selection screen (the original home canvas: PlayNow = Hider,
// SeekerMode = Seeker, both still wired to HomeUI.BeginMatch). Multiplayer signs in and opens the
// same role screen for the online match (OnlineLobby).
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

        CoinBalance.Bind(coinText);
        OnlineWallet.MakeOpener(coinText);

        // Every Ad button is a Shop button (2026-11-13): the round's HUD, the side screen and the
        // matchmaking screen. They need the server, so they show in Multiple Play only.
        MakeShopButton(FindSceneObject("Btn_ADS"), "Btn_Shop");
        MakeShopButton(FindSceneChild("RoleSelectCanvas", "AdBtn"), "RoleShopBtn");
        MakeShopButton(FindSceneChild("MatchmakingCanvas", "AdBtn"), "LobbyShopBtn");

        // The role screen (Single Play's and Multiple Play's) has a coin pill of its own, which
        // nothing updated: it showed the number it was saved with in the scene.
        if (roleSelectCanvas != null)
        {
            foreach (UnityEngine.UI.Text label in CoinBalance.BindNamed(roleSelectCanvas.transform)) OnlineWallet.MakeOpener(label);
        }
    }

    // Once per launch: an online round that was running when the app closed can still be
    // rejoined while the server holds the seat.
    private static bool rejoinOffered;

    private void Start()
    {
        if (rejoinOffered) return;
        rejoinOffered = true;
        OnlineLobby.OfferRejoin();

        // A player who has been online before gets their balance without tapping anything. No
        // screen, and nothing at all for someone who has only ever played Single Play.
        NetBootstrap.QuietSignIn();
    }

    private void OnEnable()
    {
        PendingTopUp.Settled += HandleTopUpSettled;
        if (ToastObject != null) ToastObject.SetActive(false);
    }

    private void OnDisable()
    {
        PendingTopUp.Settled -= HandleTopUpSettled;
        toast = null;
    }

    // A top-up the player paid in the browser, settled while Home is up (the shop says it itself).
    private void HandleTopUpSettled(PendingTopUp.Entry entry, PaymentState state)
    {
        if (state == PaymentState.Credited) return;   // CoinNotice confirms a credit, on every screen
        if (homeCanvas != null && !homeCanvas.activeInHierarchy) return;
        // In the notice slot like every other notice (it was a box in the middle of Home).
        OnlineMatches.Notify(PendingTopUp.MessageFor(entry, state));
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

    // Role selection -> back to the main home menu (and out of Multiple Play, when it was choosing a side).
    public void ShowHome()
    {
        AudioManager.ButtonTap();
        if (roleSelectCanvas != null) roleSelectCanvas.SetActive(false);
        if (homeCanvas != null) homeCanvas.SetActive(true);
        OnlineLobby.RoleSelectClosed();
    }

    // Multiple Play: sign in (and choose a name on the first run), then Seeker/Hider, room,
    // matchmaking and the online round (OnlineLobby, OnlineRound).
    private void OnMultiplayer()
    {
        AudioManager.ButtonTap();
        OnlineSignIn.Open(OnSignedIn, null);
    }

    private void OnSignedIn(MetaProfile profile) => OnlineLobby.Open(profile, null);

    // The shop sells for server coins, so it signs in first (at once when already signed in).
    private void OnShop()
    {
        if (shopUI == null) return;
        OnlineSignIn.Open(_ => shopUI.Open(), null);
    }

    // An Ad button (they did nothing: ads are off) made a Shop button, so the shop opens at any
    // time, mid-round included. Restyled here, from the scene's own purple button and coin art, so
    // the scene file is left as it is. Shown in Multiple Play only (OnlineMatches keeps it so).
    private void MakeShopButton(GameObject ads, string newName)
    {
        if (ads == null || shopUI == null) return;

        ads.name = newName;
        OnlineMatches.MultiplayerOnly(ads);
        bool small = ((RectTransform)ads.transform).sizeDelta.y < 90f;   // the side and lobby screens' 76 x 78
        UnityEngine.UI.Image bg = ads.GetComponent<UnityEngine.UI.Image>();
        UnityEngine.UI.Image purple = shopButton != null ? shopButton.GetComponent<UnityEngine.UI.Image>() : null;
        UnityEngine.UI.Image coin = FindSceneObject("CoinIcon")?.GetComponent<UnityEngine.UI.Image>();
        if (bg != null && purple != null && purple.sprite != null)
        {
            bg.sprite = purple.sprite;
            bg.type = UnityEngine.UI.Image.Type.Sliced;
            bg.color = Color.white;
            bg.preserveAspect = false;
        }

        if (coin != null && coin.sprite != null)
        {
            var icon = new GameObject("Icon", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            icon.transform.SetParent(ads.transform, false);
            var irt = (RectTransform)icon.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.anchoredPosition = new Vector2(0f, small ? 9f : 12f);
            irt.sizeDelta = small ? new Vector2(40f, 40f) : new Vector2(52f, 52f);
            var image = icon.GetComponent<UnityEngine.UI.Image>();
            image.sprite = coin.sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        UnityEngine.UI.Text label = OnlineUI.NewLabel(ads.transform, "SHOP", small ? 17 : 22, new Vector2(0f, small ? -24f : -32f),
                                                      new Vector2(100f, 30f), TextAnchor.MiddleCenter, true);
        label.raycastTarget = false;

        UnityEngine.UI.Button button = ads.GetComponent<UnityEngine.UI.Button>();
        if (button == null) button = ads.AddComponent<UnityEngine.UI.Button>();
        button.targetGraphic = bg;
        button.onClick.RemoveAllListeners();
        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            button.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);   // the remove-ads popup
        button.onClick.AddListener(() => OnlineSignIn.Open(_ => shopUI.OpenFrom(null), null));
    }

    private static GameObject FindSceneChild(string canvasName, string childName)
    {
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t.name == childName && t.gameObject.scene.IsValid() && t.parent != null && t.parent.name == canvasName) return t.gameObject;
        return null;
    }

    private static GameObject FindSceneObject(string name)
    {
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t.name == name && t.gameObject.scene.IsValid()) return t.gameObject;
        return null;
    }
}
