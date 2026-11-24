using System;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// The wallet screen: the player's coins, their account at the wallet provider, and the coin packs
// to deposit: a pack is paid at the provider's checkout page (the same top-ups the shop sells).
// The coin ledger is the History screen's WALLET HISTORY tab (OnlineHistory), one tap away; the
// withdrawals this screen used to offer were taken out of the game (2026-11-24, user's request).
//
// Nothing here decides anything about money. A deposit is credited when the provider tells the
// server; the balance shown is the one the server reports (CoinBalance). On a development server
// the provider is the mock wallet: test currency only, and the screen says so. Opened by tapping
// the coin balance on any screen (Home, the play setup, the shop). Built at runtime in the same
// modal style as the rest of the online screens (OnlineUI).
public class OnlineWallet : MonoBehaviour
{
    private const int MaxPackagesShown = 6;
    private const float RateLimitRetrySeconds = 2f;
    private const int RateLimitRetries = 3;

    private const string Good = "#7CFC9A";

    private static MetaClient Meta => MetaClient.Instance;

    private Action onClose;
    private int refreshVersion;

    private MetaWalletSummary wallet;
    private bool haveWallet;        // the server answered WalletSummary
    private bool walletOff;         // the server has no wallet provider
    private MetaTopUpPackage[] packages = new MetaTopUpPackage[0];
    private bool depositBusy;
    private bool packagesLoaded;
    private int rateLimited;
    private float retryRefreshAt;

    // ---- UI ---------------------------------------------------------------------------------
    private GameObject screen;
    private Text banner, account, status;
    private RectTransform depositList;
    private Button openWalletButton, historyButton, closeButton;

    // ================= Opening =================

    /// <summary>Opens the wallet over whatever is showing; <paramref name="closed"/> after Close.</summary>
    public static void Show(Action closed)
    {
        OnlineWallet walletScreen = FindAnyObjectByType<OnlineWallet>();
        if (walletScreen == null) walletScreen = new GameObject("OnlineWallet").AddComponent<OnlineWallet>();
        walletScreen.Open(closed);
    }

    /// <summary>Signs in first when needed (the wallet is the server's), then opens.</summary>
    public static void OpenSignedIn(Action closed)
    {
        AudioManager.ButtonTap();
        OnlineSignIn.Open(_ => Show(closed), closed);
    }

    /// <summary>Makes a coin label open the wallet when tapped.</summary>
    public static void MakeOpener(Text label)
    {
        if (label == null || label.GetComponent<Button>() != null) return;

        label.raycastTarget = true;
        Button button = label.gameObject.AddComponent<Button>();
        button.targetGraphic = label;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(() => OpenSignedIn(null));
    }

    private void Awake()
    {
        BuildUI();
        screen.SetActive(false);
    }

    private void OnEnable() => PendingTopUp.Settled += HandleTopUpSettled;
    private void OnDisable() => PendingTopUp.Settled -= HandleTopUpSettled;

    public void Open(Action closed)
    {
        if (Meta == null) { closed?.Invoke(); return; }

        onClose = closed;
        screen.SetActive(true);
        haveWallet = false;
        walletOff = false;
        packages = new MetaTopUpPackage[0];
        packagesLoaded = false;
        depositBusy = false;
        rateLimited = 0;
        retryRefreshAt = 0f;
        status.text = "Loading...";

        ShowHeader();
        BuildDeposit();
        Refresh();
    }

    private void Close()
    {
        AudioManager.ButtonTap();
        screen.SetActive(false);
        refreshVersion++;
        Action closed = onClose;
        onClose = null;
        closed?.Invoke();
    }

    // The ledger, on the History screen (it draws over this one).
    private void OpenHistory()
    {
        AudioManager.ButtonTap();
        OnlineHistory.Show(transform, OnlineHistory.Tab.Wallet, null);
    }

    // ================= What the server says =================

    // The summary is asked for again after anything that moves coins. The coin packs do not change
    // while the screen is open, so they are asked for once: the server allows each connection only
    // so many requests a second.
    private void Refresh()
    {
        if (Meta == null) return;
        int version = ++refreshVersion;
        retryRefreshAt = 0f;

        Meta.WalletSummary(reply =>
        {
            if (!screen.activeSelf || version != refreshVersion) return;

            if (reply.TimedOut)
            {
                status.text = "The server did not answer. Close and open the wallet to try again.";
                return;
            }

            // Too many requests in a row (a burst of taps): asked again shortly, a few times.
            if (reply.Error == MetaError.RateLimited && rateLimited < RateLimitRetries)
            {
                rateLimited++;
                retryRefreshAt = Time.unscaledTime + RateLimitRetrySeconds;
                return;
            }

            if (reply.Error == MetaError.None) rateLimited = 0;

            // An older server does not know the request at all: the same as having no wallet.
            walletOff = reply.Error == MetaError.WalletUnavailable || reply.Error == MetaError.UnknownRequest;
            haveWallet = reply.Error == MetaError.None;
            if (haveWallet) wallet = reply.Wallet;

            if (status.text == "Loading...")
                status.text = haveWallet || walletOff ? "" : $"The server could not answer ({reply.Error}).";

            ShowHeader();
            BuildDeposit();
        });

        if (!packagesLoaded)
        {
            Meta.ListTopUps(reply =>
            {
                if (!screen.activeSelf) return;
                if (reply.TimedOut || reply.Error == MetaError.RateLimited) return;   // asked again with the next refresh

                packagesLoaded = true;
                packages = reply.Error == MetaError.None && reply.Packages != null ? reply.Packages : new MetaTopUpPackage[0];
                BuildDeposit();
            });
        }
    }

    private void ShowHeader()
    {
        bool test = haveWallet && wallet.IsTest;
        banner.gameObject.SetActive(test);

        if (walletOff) account.text = "No wallet is connected on this server.";
        else if (!haveWallet) account.text = "";
        else if (!wallet.HasAccount) account.text = "Wallet account: could not be read just now";
        else account.text = $"Wallet account: <b>{ShopInventory.MoneyText(wallet.AccountMinor, wallet.Currency)}</b>";

        // The buttons along the bottom, centred whether the wallet page has a link or not.
        bool page = haveWallet && !string.IsNullOrEmpty(wallet.AccountUrl);
        openWalletButton.gameObject.SetActive(page);
        OnlineUI.Place((RectTransform)historyButton.transform, new Vector2(page ? 60f : -160f, -365f), new Vector2(280f, 74f));
        OnlineUI.Place((RectTransform)closeButton.transform, new Vector2(page ? 370f : 160f, -365f), new Vector2(260f, 74f));
    }

    // Back from the wallet's page in the browser: what was paid there shows here.
    private void OnApplicationFocus(bool focused)
    {
        if (focused && screen != null && screen.activeSelf) Refresh();
    }

    private void HandleTopUpSettled(PendingTopUp.Entry entry, PaymentState state)
    {
        if (screen == null || !screen.activeSelf) return;

        string message = PendingTopUp.MessageFor(entry, state);
        status.text = state == PaymentState.Credited ? $"<color={Good}>{message}</color>" : message;
        Refresh();
    }

    private void Update()
    {
        if (screen == null || !screen.activeSelf || Meta == null) return;
        if (retryRefreshAt > 0f && Time.unscaledTime >= retryRefreshAt) Refresh();
    }

    // ================= Deposit =================

    private void BuildDeposit()
    {
        for (int i = depositList.childCount - 1; i >= 0; i--) Destroy(depositList.GetChild(i).gameObject);

        if (packages.Length == 0)
        {
            Text none = OnlineUI.NewLabel(depositList, walletOff || packagesLoaded ? "Deposits are not available on this server." : "Loading the coin packs...",
                                          32, Vector2.zero, new Vector2(960f, 80f));
            none.color = OnlineUI.Muted;
            return;
        }

        int shown = Mathf.Min(packages.Length, MaxPackagesShown);
        for (int i = 0; i < shown; i++)
        {
            MetaTopUpPackage package = packages[i];
            bool waiting = PendingTopUp.IsWaiting(package.Id);
            string second = waiting ? "waiting for payment - tap to reopen" : ShopInventory.MoneyText(package.PriceMinor, package.Currency);

            var pos = new Vector2(i % 2 == 0 ? -255f : 255f, 120f - (i / 2) * 125f);
            OnlineUI.NewButton(depositList, $"{CoinBalance.Format(package.CoinCents)} coins\n<size=26>{second}</size>",
                               pos, new Vector2(490f, 110f), waiting ? OnlineUI.Gray : OnlineUI.Green, () => StartDeposit(package));
        }

        Text hint = OnlineUI.NewLabel(depositList, "Pay on the wallet page that opens. The coins arrive here a few seconds after.",
                                      26, new Vector2(0f, -235f), new Vector2(980f, 40f));
        hint.color = OnlineUI.Muted;
    }

    private void StartDeposit(MetaTopUpPackage package)
    {
        AudioManager.ButtonTap();
        if (depositBusy) return;

        depositBusy = true;
        status.text = "Opening the payment...";
        ShopInventory.StartTopUp(package, (opened, message) =>
        {
            depositBusy = false;
            if (!screen.activeSelf) return;
            status.text = message;
            BuildDeposit();
        });
    }

    // ================= UI =================

    private void OpenWalletPage()
    {
        AudioManager.ButtonTap();
        if (haveWallet && !string.IsNullOrEmpty(wallet.AccountUrl)) PendingTopUp.OpenUrl(wallet.AccountUrl);
    }

    private void BuildUI()
    {
        var root = new GameObject("OnlineWalletUI");
        root.transform.SetParent(transform, false);
        Transform canvas = OnlineUI.CreateCanvas("OnlineWalletCanvas", root.transform, 60).transform;

        RectTransform p = OnlineUI.Modal(canvas, "WalletScreen", new Vector2(1100f, 880f));
        screen = p.parent.gameObject;

        OnlineUI.NewLabel(p, "WALLET", 56, new Vector2(0f, 385f), new Vector2(1000f, 80f), TextAnchor.MiddleCenter, true);

        banner = OnlineUI.NewLabel(p, "TEST WALLET - test currency only, no real money", 28, new Vector2(0f, 333f), new Vector2(1000f, 40f), TextAnchor.MiddleCenter, true);
        banner.color = OnlineUI.Warning;

        Text coins = OnlineUI.NewLabel(p, "", 40, new Vector2(-260f, 278f), new Vector2(500f, 60f), TextAnchor.MiddleLeft);
        CoinBalance.Bind(coins, "Coins: <b>{0}</b>");
        account = OnlineUI.NewLabel(p, "", 32, new Vector2(250f, 278f), new Vector2(520f, 60f), TextAnchor.MiddleRight);

        // The coin packs.
        depositList = (RectTransform)new GameObject("Packs", typeof(RectTransform)).transform;
        depositList.SetParent(p, false);
        OnlineUI.Place(depositList, new Vector2(0f, 30f), new Vector2(1040f, 520f));

        status = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, -285f), new Vector2(1040f, 60f));
        status.color = OnlineUI.Warning;

        openWalletButton = OnlineUI.NewButton(p, "OPEN WALLET PAGE", new Vector2(-300f, -365f), new Vector2(400f, 74f), OnlineUI.Blue, OpenWalletPage);
        historyButton = OnlineUI.NewButton(p, "HISTORY", new Vector2(60f, -365f), new Vector2(280f, 74f), OnlineUI.Gray, OpenHistory);
        closeButton = OnlineUI.NewButton(p, "CLOSE", new Vector2(370f, -365f), new Vector2(260f, 74f), OnlineUI.Gray, Close);
    }
}
