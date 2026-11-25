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
        banner.transform.parent.gameObject.SetActive(test);

        if (walletOff) account.text = "No wallet is connected on this server.";
        else if (!haveWallet) account.text = "";
        else if (!wallet.HasAccount) account.text = "Wallet account: could not be read just now";
        else account.text = $"Wallet account: <b>{ShopInventory.MoneyText(wallet.AccountMinor, wallet.Currency)}</b>";

        // The wallet's own page, when the provider has one.
        bool page = haveWallet && !string.IsNullOrEmpty(wallet.AccountUrl);
        openWalletButton.gameObject.SetActive(page);
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

    // The coin packs as the shop shows them (design B): the coins' picture, the amount and the price.
    private void BuildDeposit()
    {
        for (int i = depositList.childCount - 1; i >= 0; i--)
        {
            depositList.GetChild(i).gameObject.SetActive(false);
            Destroy(depositList.GetChild(i).gameObject);
        }

        packsNote.text = packages.Length == 0
            ? (walletOff || packagesLoaded ? "Deposits are not available on this server." : "Loading the coin packs...")
            : "Pay on the wallet page that opens. The coins arrive here a few seconds after.";

        int shown = Mathf.Min(packages.Length, MaxPackagesShown);
        for (int i = 0; i < shown; i++)
        {
            MetaTopUpPackage package = packages[i];
            bool waiting = PendingTopUp.IsWaiting(package.Id);

            PaintboxFrame card = Paintbox.CardFrame(depositList, "Pack_" + package.Id);
            Button button = card.Root.gameObject.AddComponent<Button>();
            button.targetGraphic = card.Fill;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => StartDeposit(package));
            card.Root.gameObject.AddComponent<PaintboxPress>();

            Image well = Paintbox.Rounded(card.Body, "Picture", Paintbox.Card, 18f);
            well.rectTransform.anchorMin = new Vector2(0f, 0f);
            well.rectTransform.anchorMax = new Vector2(0.38f, 1f);
            well.rectTransform.offsetMin = new Vector2(8f, 8f);
            well.rectTransform.offsetMax = new Vector2(0f, -8f);
            Image art = Paintbox.Image(well.rectTransform, "Art", Paintbox.Art("coins"), Color.white);
            art.preserveAspect = true;
            Paintbox.Fill(art.rectTransform, 6f, 6f, 6f, 6f);

            Text amount = Paintbox.Label(card.Body, $"{CoinBalance.Format(package.CoinCents)} coins", 32f, Paintbox.Ink, Paintbox.Weight.Bold);
            Paintbox.Fit(amount, 18);
            amount.rectTransform.anchorMin = new Vector2(0.38f, 0.5f);
            amount.rectTransform.anchorMax = new Vector2(1f, 1f);
            amount.rectTransform.offsetMin = new Vector2(20f, 0f);
            amount.rectTransform.offsetMax = new Vector2(-14f, -14f);
            amount.alignment = TextAnchor.LowerLeft;
            Text price = Paintbox.Label(card.Body, waiting ? "Waiting for payment \u00b7 tap to reopen" : ShopInventory.MoneyText(package.PriceMinor, package.Currency),
                                        24f, waiting ? Paintbox.Muted : Paintbox.Ink, waiting ? Paintbox.Weight.Regular : Paintbox.Weight.Bold);
            Paintbox.Fit(price, 14);
            price.rectTransform.anchorMin = new Vector2(0.38f, 0f);
            price.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            price.rectTransform.offsetMin = new Vector2(20f, 14f);
            price.rectTransform.offsetMax = new Vector2(-14f, -4f);
            price.alignment = TextAnchor.UpperLeft;
        }
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

    private Text packsNote;

    // Design B (2026-11-24): a page with the coins and the wallet account side by side, the coin
    // packs as cards, and Wallet history and Close in the foot.
    private void BuildUI()
    {
        var root = new GameObject("OnlineWalletUI");
        root.transform.SetParent(transform, false);
        Transform canvas = OnlineUI.CreateCanvas("OnlineWalletCanvas", root.transform, 60).transform;

        PaintboxPage page = PaintboxPage.Build(canvas, "WalletScreen", "Wallet", Close, PaintboxPage.Top.None, null, foot: true);
        screen = page.Root.gameObject;
        RectTransform test = Paintbox.Tag(page.Right, "TEST WALLET - test currency only, no real money", Paintbox.Hot, Paintbox.HotInk, "CircleAlert", 22f);
        banner = test.GetComponentInChildren<Text>();

        // The coins, and the wallet's own account beside them.
        PaintboxFrame coinsCard = Paintbox.Panel(page.Content, "Coins", 30f);
        coinsCard.Root.anchorMin = new Vector2(0f, 1f);
        coinsCard.Root.anchorMax = new Vector2(0.5f, 1f);
        coinsCard.Root.pivot = new Vector2(0.5f, 1f);
        coinsCard.Root.offsetMin = new Vector2(0f, -150f);
        coinsCard.Root.offsetMax = new Vector2(-12f, 0f);
        RectTransform coinGlyph = Paintbox.CoinGlyph(coinsCard.Body, 72f);
        Paintbox.Pin(coinGlyph, new Vector2(0f, 0.5f), new Vector2(34f, 0f), new Vector2(72f, 72f));
        coinGlyph.pivot = new Vector2(0f, 0.5f);
        Text coins = Paintbox.Label(coinsCard.Body, "", 40f, Paintbox.Ink);
        Paintbox.Fill(coins.rectTransform, 130f, 30f, 0f, 0f);
        CoinBalance.Bind(coins, "Coins: <b>{0}</b>");

        PaintboxFrame accountCard = Paintbox.Panel(page.Content, "Account", 30f);
        accountCard.Root.anchorMin = new Vector2(0.5f, 1f);
        accountCard.Root.anchorMax = new Vector2(1f, 1f);
        accountCard.Root.pivot = new Vector2(0.5f, 1f);
        accountCard.Root.offsetMin = new Vector2(12f, -150f);
        accountCard.Root.offsetMax = Vector2.zero;
        Image walletIcon = Paintbox.IconImage(accountCard.Body, "Wallet", 44f, Paintbox.Muted);
        Paintbox.Pin(walletIcon.rectTransform, new Vector2(0f, 0.5f), new Vector2(34f, 0f), new Vector2(44f, 44f));
        walletIcon.rectTransform.pivot = new Vector2(0f, 0.5f);
        account = Paintbox.Label(accountCard.Body, "", 28f, Paintbox.Ink);
        Paintbox.Fit(account, 16);
        Paintbox.Fill(account.rectTransform, 100f, 330f, 0f, 0f);
        openWalletButton = Paintbox.Button(accountCard.Body, "Open wallet page", Paintbox.Tone.Secondary, "ArrowUpRight", OpenWalletPage, 26f);
        Paintbox.Pin((RectTransform)openWalletButton.transform, new Vector2(1f, 0.5f), new Vector2(-24f, 0f), new Vector2(290f, 76f));
        ((RectTransform)openWalletButton.transform).pivot = new Vector2(1f, 0.5f);

        // The coin packs.
        Text kicker = PaintboxPage.Kicker(page.Content, "COIN PACKS");
        Paintbox.TopBand(kicker.rectTransform, 176f, 34f);
        packsNote = Paintbox.Label(page.Content, "", 23f, Paintbox.Muted, Paintbox.Weight.Regular, TextAnchor.MiddleRight);
        Paintbox.TopBand(packsNote.rectTransform, 176f, 34f, 300f, 0f);
        RectTransform view = Paintbox.Fill(Paintbox.NewRect(page.Content, "PacksView"), 0f, 0f, 222f, 0f);
        ScrollRect scroll = view.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        view.gameObject.AddComponent<RectMask2D>().padding = new Vector4(-6f, 0f, -6f, 0f);
        view.gameObject.AddComponent<Image>().color = Color.clear;
        depositList = Paintbox.NewRect(view, "Packs");
        depositList.anchorMin = new Vector2(0f, 1f);
        depositList.anchorMax = new Vector2(1f, 1f);
        depositList.pivot = new Vector2(0.5f, 1f);
        depositList.offsetMin = depositList.offsetMax = Vector2.zero;
        GridLayoutGroup grid = depositList.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(600f, 200f);
        grid.spacing = new Vector2(22f, 22f);
        grid.padding = new RectOffset(4, 4, 4, 20);
        depositList.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        depositList.gameObject.AddComponent<PaintboxGridFit>().MinWidth = 560f;
        scroll.content = depositList;
        scroll.viewport = view;

        // The foot: how a deposit went, Wallet history and Close.
        status = page.FootDetail;
        page.FootTitle.text = "";
        historyButton = page.FootButton("Wallet history", Paintbox.Tone.Paper, "History", OpenHistory, 300f);
        closeButton = page.FootButton("Close", Paintbox.Tone.Primary, "Check", Close, 200f);
    }
}
