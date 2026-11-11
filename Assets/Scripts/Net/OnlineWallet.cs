using System;
using System.Globalization;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// The wallet screen: the player's coins, their account at the wallet provider, and the three
// things that move coins in and out of the game:
//   DEPOSIT   a coin pack paid at the provider's checkout page (the same top-ups the shop sells);
//   WITHDRAW  coins paid out to the wallet account, at the server's rate;
//   HISTORY   the coin ledger: every deposit, withdrawal, purchase, entry fee and payout.
//
// Nothing here decides anything about money. A deposit is credited when the provider tells the
// server; a withdrawal sends only how many coins, and the server checks the balance, applies its
// own rate and takes the coins; the balance shown is the one the server reports (CoinBalance).
// On a development server the provider is the mock wallet: test currency only, and the screen
// says so. Opened from the coin balance on Home and in the shop, and from the room screen.
// Built at runtime in the same modal style as the rest of the online screens (OnlineUI).
public class OnlineWallet : MonoBehaviour
{
    private enum Tab { Deposit, Withdraw, History }

    private const int RowsPerPage = 5;
    private const int MaxPackagesShown = 6;
    private const float WithdrawalPollSeconds = 3f;
    private const int WithdrawalSendTries = 3;
    private const float RateLimitRetrySeconds = 2f;
    private const int RateLimitRetries = 3;
    private const string PendingWithdrawalKey = "PendingWithdrawalId";

    private const string Good = "#7CFC9A";
    private const string Bad = "#FF8A80";

    private static MetaClient Meta => MetaClient.Instance;

    private Action onClose;
    private Tab tab = Tab.Deposit;
    private int refreshVersion;

    private MetaWalletSummary wallet;
    private bool haveWallet;        // the server answered WalletSummary
    private bool walletOff;         // the server has no wallet provider
    private MetaTopUpPackage[] packages = new MetaTopUpPackage[0];
    private MetaTransaction[] lines = new MetaTransaction[0];
    private bool linesLoaded;
    private int page;
    private bool depositBusy;
    private bool packagesLoaded;
    private int rateLimited;
    private float retryRefreshAt;

    // A withdrawal on its way.
    private bool withdrawing;
    private string withdrawRequestId;
    private long withdrawRequestCents;
    private int withdrawTries;
    private string pendingWithdrawalId;
    private bool askingWithdrawal;
    private float nextWithdrawalPoll;

    // ---- UI ---------------------------------------------------------------------------------
    private GameObject screen, withdrawControls;
    private Text banner, account, status, withdrawOff, rateLabel, quoteLabel, pageLabel, historyNote;
    private RectTransform depositPanel, withdrawPanel, historyPanel, depositList, historyList;
    private Button openWalletButton, withdrawButton, prevPage, nextPage;
    private readonly Button[] tabButtons = new Button[3];
    private InputField amountInput;

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
        linesLoaded = false;
        lines = new MetaTransaction[0];
        packages = new MetaTopUpPackage[0];
        packagesLoaded = false;
        page = 0;
        depositBusy = false;
        rateLimited = 0;
        retryRefreshAt = 0f;
        status.text = "Loading...";
        pendingWithdrawalId = PlayerPrefs.GetString(PendingWithdrawalKey, "");
        if (pendingWithdrawalId.Length == 0) pendingWithdrawalId = null;
        nextWithdrawalPoll = 0f;

        ShowHeader();
        SelectTab(tab);
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

    // ================= What the server says =================

    // The summary and the ledger are asked for again after anything that moves coins. The coin
    // packs do not change while the screen is open, so they are asked for once: the server allows
    // each connection only so many requests a second.
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
            BuildTab();
        });

        if (!packagesLoaded)
        {
            Meta.ListTopUps(reply =>
            {
                if (!screen.activeSelf) return;
                if (reply.TimedOut || reply.Error == MetaError.RateLimited) return;   // asked again with the next refresh

                packagesLoaded = true;
                packages = reply.Error == MetaError.None && reply.Packages != null ? reply.Packages : new MetaTopUpPackage[0];
                if (tab == Tab.Deposit) BuildDeposit();
            });
        }

        Meta.ListTransactions(NetTuning.MaxTransactions, reply =>
        {
            if (!screen.activeSelf || version != refreshVersion) return;
            if (reply.TimedOut || reply.Error != MetaError.None) return;   // what is shown stays until an answer comes

            linesLoaded = true;
            lines = reply.Lines ?? new MetaTransaction[0];
            if (tab == Tab.History) BuildHistory();
        });
    }

    private void ShowHeader()
    {
        bool test = haveWallet && wallet.IsTest;
        banner.gameObject.SetActive(test);

        if (walletOff) account.text = "No wallet is connected on this server.";
        else if (!haveWallet) account.text = "";
        else if (!wallet.HasAccount) account.text = "Wallet account: could not be read just now";
        else account.text = $"Wallet account: <b>{Money(wallet.AccountMinor)}</b>";

        openWalletButton.gameObject.SetActive(haveWallet && !string.IsNullOrEmpty(wallet.AccountUrl));
    }

    private string Money(long minor) => ShopInventory.MoneyText(minor, haveWallet ? wallet.Currency : "");

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

    // ================= Tabs =================

    private void SelectTab(Tab selected)
    {
        tab = selected;
        for (int i = 0; i < tabButtons.Length; i++)
            tabButtons[i].targetGraphic.color = i == (int)selected ? OnlineUI.Blue : OnlineUI.Gray;

        depositPanel.gameObject.SetActive(selected == Tab.Deposit);
        withdrawPanel.gameObject.SetActive(selected == Tab.Withdraw);
        historyPanel.gameObject.SetActive(selected == Tab.History);
        BuildTab();
    }

    private void BuildTab()
    {
        switch (tab)
        {
            case Tab.Deposit: BuildDeposit(); break;
            case Tab.Withdraw: BuildWithdraw(); break;
            default: BuildHistory(); break;
        }
    }

    private static void Clear(RectTransform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--) Destroy(parent.GetChild(i).gameObject);
    }

    // ================= Deposit =================

    private void BuildDeposit()
    {
        Clear(depositList);

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

    // ================= Withdraw =================

    private bool CanWithdraw => haveWallet && wallet.WithdrawalsEnabled && wallet.CoinCentsPerMinor > 0;

    private void BuildWithdraw()
    {
        bool can = CanWithdraw;
        withdrawControls.SetActive(can);
        withdrawOff.gameObject.SetActive(!can);

        if (!can)
        {
            withdrawOff.text = !haveWallet && !walletOff ? "Loading..." : "Withdrawals are not available on this server.";
            return;
        }

        rateLabel.text = $"{CoinBalance.Format(wallet.CoinCentsPerMinor * 100)} coins pay out {Money(100)}.   " +
                         $"Smallest {CoinBalance.Format(wallet.MinWithdrawalCents)} coins, largest {CoinBalance.Format(wallet.MaxWithdrawalCents)}.";
        UpdateQuote();
    }

    /// <summary>
    /// What the typed amount would do, by the rate the server gave. For the player to read: the
    /// server works it out again from the coins alone, and its answer is what happened.
    /// </summary>
    private bool TryQuote(out long takenCents, out long amountMinor, out string problem)
    {
        takenCents = 0;
        amountMinor = 0;
        problem = "";

        string typed = (amountInput.text ?? "").Trim().Replace(',', '.');
        if (typed.Length == 0) { problem = "Enter how many coins to withdraw."; return false; }

        if (!decimal.TryParse(typed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal coins) || coins <= 0m || coins > 100000000m)
        {
            problem = "That is not an amount of coins.";
            return false;
        }

        long cents = (long)decimal.Round(coins * 100m);
        long rate = wallet.CoinCentsPerMinor;
        amountMinor = cents / rate;
        takenCents = amountMinor * rate;

        if (amountMinor <= 0 || takenCents < wallet.MinWithdrawalCents)
        {
            problem = $"The smallest withdrawal is {CoinBalance.Format(wallet.MinWithdrawalCents)} coins.";
            return false;
        }

        if (cents > wallet.MaxWithdrawalCents)
        {
            problem = $"The largest withdrawal is {CoinBalance.Format(wallet.MaxWithdrawalCents)} coins.";
            return false;
        }

        if (takenCents > CoinBalance.Cents)
        {
            problem = $"You have {CoinBalance.Text} coins.";
            return false;
        }

        return true;
    }

    private void UpdateQuote()
    {
        if (!CanWithdraw) return;

        bool ok = TryQuote(out long taken, out long amountMinor, out string problem);
        quoteLabel.text = ok
            ? $"Takes <b>{CoinBalance.Format(taken)}</b> coins, pays <b>{Money(amountMinor)}</b> into your wallet account."
            : problem;
        quoteLabel.color = ok ? Color.white : OnlineUI.Muted;
        withdrawButton.interactable = ok && !withdrawing && pendingWithdrawalId == null;
    }

    private void SetAmount(long cents)
    {
        AudioManager.ButtonTap();
        amountInput.text = (Math.Max(0, cents) / 100m).ToString("0.##", CultureInfo.InvariantCulture);
        UpdateQuote();
    }

    // The smallest amount the server will pay out: its minimum, rounded up to whole units of currency.
    private long SmallestCents()
    {
        long rate = Math.Max(1, wallet.CoinCentsPerMinor);
        return (wallet.MinWithdrawalCents + rate - 1) / rate * rate;
    }

    private void Withdraw()
    {
        AudioManager.ButtonTap();
        if (withdrawing || pendingWithdrawalId != null || !CanWithdraw) return;
        if (!TryQuote(out long taken, out _, out string problem)) { status.text = problem; return; }

        withdrawing = true;
        withdrawRequestCents = taken;
        withdrawRequestId = null;
        withdrawTries = 0;
        status.text = "Withdrawing...";
        UpdateQuote();
        SendWithdrawal();
    }

    // Only the coins travel. Sent again with the same request id when no answer comes: the server
    // takes the coins once however often it arrives.
    private void SendWithdrawal()
    {
        withdrawTries++;
        withdrawRequestId = Meta.CreateWithdrawal(withdrawRequestCents, HandleWithdrawal, withdrawRequestId);
    }

    private void HandleWithdrawal(MetaClient.WithdrawalReply reply)
    {
        if (reply.TimedOut && withdrawTries < WithdrawalSendTries)
        {
            SendWithdrawal();
            return;
        }

        withdrawing = false;

        if (reply.TimedOut)
        {
            status.text = "No answer from the server. Look at your coins and the history before trying again.";
            Refresh();
            return;
        }

        if (reply.Error != MetaError.None)
        {
            status.text = WithdrawalErrorText(reply.Error);
            if (screen.activeSelf) Refresh();
            return;
        }

        ShowWithdrawal(reply.Withdrawal);
    }

    private void ShowWithdrawal(MetaWithdrawal withdrawal)
    {
        string money = ShopInventory.MoneyText(withdrawal.AmountMinor, withdrawal.Currency);

        switch (withdrawal.State)
        {
            case WithdrawalState.Paid:
                status.text = $"<color={Good}>Withdrew {CoinBalance.Format(withdrawal.CoinCents)} coins: {money} is in your wallet account.</color>";
                ForgetPendingWithdrawal();
                amountInput.text = "";
                break;

            case WithdrawalState.Refunded:
                status.text = $"<color={Bad}>The wallet refused the withdrawal. Your {CoinBalance.Format(withdrawal.CoinCents)} coins are back.</color>";
                ForgetPendingWithdrawal();
                break;

            default:
                // The coins are taken; the wallet has not said yet whether it paid. Asked again shortly.
                status.text = "The wallet has not confirmed the withdrawal yet. Checking...";
                pendingWithdrawalId = withdrawal.WithdrawalId;
                PlayerPrefs.SetString(PendingWithdrawalKey, pendingWithdrawalId);
                PlayerPrefs.Save();
                nextWithdrawalPoll = Time.unscaledTime + WithdrawalPollSeconds;
                break;
        }

        if (screen.activeSelf) Refresh();
    }

    private void ForgetPendingWithdrawal()
    {
        pendingWithdrawalId = null;
        if (!PlayerPrefs.HasKey(PendingWithdrawalKey)) return;
        PlayerPrefs.DeleteKey(PendingWithdrawalKey);
        PlayerPrefs.Save();
    }

    private void Update()
    {
        if (screen == null || !screen.activeSelf || Meta == null) return;

        if (retryRefreshAt > 0f && Time.unscaledTime >= retryRefreshAt) Refresh();

        if (pendingWithdrawalId == null || askingWithdrawal || Time.unscaledTime < nextWithdrawalPoll) return;

        askingWithdrawal = true;
        string asked = pendingWithdrawalId;
        Meta.WithdrawalStatus(asked, reply =>
        {
            askingWithdrawal = false;
            nextWithdrawalPoll = Time.unscaledTime + WithdrawalPollSeconds;
            if (asked != pendingWithdrawalId || reply.TimedOut) return;

            if (reply.Error == MetaError.UnknownWithdrawal)
            {
                ForgetPendingWithdrawal();   // another account's, or a database that was reset
                UpdateQuote();
                return;
            }

            if (reply.Error == MetaError.None) ShowWithdrawal(reply.Withdrawal);
        });
    }

    private static string WithdrawalErrorText(MetaError error)
    {
        switch (error)
        {
            case MetaError.NotEnoughCoins: return "You do not have that many coins. Nothing was taken.";
            case MetaError.AmountOutOfRange: return "That amount is outside what can be withdrawn. Nothing was taken.";
            case MetaError.WithdrawalsUnavailable:
            case MetaError.WalletUnavailable:
            case MetaError.UnknownRequest: return "Withdrawals are not available on this server.";
            case MetaError.RateLimited: return "Too many requests. Wait a moment and try again.";
            case MetaError.NotSignedIn:
            case MetaError.BadSession: return "Your session has expired. Sign in again.";
            default: return $"The server refused the withdrawal ({error}). Nothing was taken.";
        }
    }

    // ================= History =================

    private void TurnPage(int delta)
    {
        AudioManager.ButtonTap();
        page += delta;
        BuildHistory();
    }

    private void BuildHistory()
    {
        Clear(historyList);

        int pages = Mathf.Max(1, (lines.Length + RowsPerPage - 1) / RowsPerPage);
        page = Mathf.Clamp(page, 0, pages - 1);
        prevPage.interactable = page > 0;
        nextPage.interactable = page < pages - 1;
        prevPage.gameObject.SetActive(pages > 1);
        nextPage.gameObject.SetActive(pages > 1);
        pageLabel.text = pages > 1 ? $"{page + 1}/{pages}" : "";
        historyNote.text = !linesLoaded ? "Loading..." : lines.Length == 0 ? "Nothing has moved your coins yet." : "";

        for (int i = page * RowsPerPage; i < Mathf.Min(lines.Length, (page + 1) * RowsPerPage); i++)
        {
            int row = i - page * RowsPerPage;
            MetaTransaction t = lines[i];

            Image bg = OnlineUI.NewImage("Line", historyList, new Color(1f, 1f, 1f, 0.06f));
            OnlineUI.Place(bg.rectTransform, new Vector2(0f, 152f - row * 78f), new Vector2(1000f, 70f));

            string when = DateTimeOffset.FromUnixTimeSeconds(t.UnixSeconds).ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
            Text what = OnlineUI.NewLabel(bg.transform, $"<b>{KindText(t)}</b>\n<size=22><color=#FFFFFFA6>{when}  -  balance {CoinBalance.Format(t.BalanceAfterCents)}</color></size>",
                                          28, new Vector2(-150f, 0f), new Vector2(660f, 66f), TextAnchor.MiddleLeft);
            what.horizontalOverflow = HorizontalWrapMode.Overflow;

            bool plus = t.AmountCents >= 0;
            OnlineUI.NewLabel(bg.transform, $"<color={(plus ? Good : Bad)}>{(plus ? "+" : "-")}{CoinBalance.Format(Math.Abs(t.AmountCents))}</color>",
                              32, new Vector2(350f, 0f), new Vector2(260f, 66f), TextAnchor.MiddleRight, true);
        }
    }

    private static string KindText(MetaTransaction t)
    {
        switch (t.Kind)
        {
            case "topup": return string.IsNullOrEmpty(t.ItemId) ? "Deposit" : $"Deposit ({t.ItemId})";
            case "withdrawal": return "Withdrawal to the wallet";
            case "withdrawal-refund": return "Withdrawal refused, coins returned";
            case "purchase": return string.IsNullOrEmpty(t.ItemId) ? "Shop purchase" : $"Shop: {t.ItemId}";
            case "entry": return "Room entry fee";
            case "payout": return "Match payout";
            case "refund": return "Entry fee refunded";
            case "forfeit": return "Entry fee forfeited";
            default: return string.IsNullOrEmpty(t.Kind) ? "Coins moved" : t.Kind;
        }
    }

    // ================= UI =================

    private void OpenWalletPage()
    {
        AudioManager.ButtonTap();
        if (haveWallet && !string.IsNullOrEmpty(wallet.AccountUrl)) PendingTopUp.OpenUrl(wallet.AccountUrl);
    }

    private static RectTransform NewPanel(Transform parent, string name)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        OnlineUI.Place(rt, new Vector2(0f, -70f), new Vector2(1040f, 520f));
        return rt;
    }

    private void BuildUI()
    {
        var root = new GameObject("OnlineWalletUI");
        root.transform.SetParent(transform, false);
        Transform canvas = OnlineUI.CreateCanvas("OnlineWalletCanvas", root.transform, 60).transform;

        RectTransform p = OnlineUI.Modal(canvas, "WalletScreen", new Vector2(1100f, 960f));
        screen = p.parent.gameObject;

        OnlineUI.NewLabel(p, "WALLET", 56, new Vector2(0f, 425f), new Vector2(1000f, 80f), TextAnchor.MiddleCenter, true);

        banner = OnlineUI.NewLabel(p, "TEST WALLET - test currency only, no real money", 28, new Vector2(0f, 372f), new Vector2(1000f, 40f), TextAnchor.MiddleCenter, true);
        banner.color = OnlineUI.Warning;

        Text coins = OnlineUI.NewLabel(p, "", 40, new Vector2(-260f, 318f), new Vector2(500f, 60f), TextAnchor.MiddleLeft);
        CoinBalance.Bind(coins, "Coins: <b>{0}</b>");
        account = OnlineUI.NewLabel(p, "", 32, new Vector2(250f, 318f), new Vector2(520f, 60f), TextAnchor.MiddleRight);

        string[] tabNames = { "DEPOSIT", "WITHDRAW", "HISTORY" };
        for (int i = 0; i < tabNames.Length; i++)
        {
            var which = (Tab)i;
            tabButtons[i] = OnlineUI.NewButton(p, tabNames[i], new Vector2(-340f + i * 340f, 240f), new Vector2(320f, 74f), OnlineUI.Gray, () =>
            {
                AudioManager.ButtonTap();
                SelectTab(which);
            });
        }

        // Deposit.
        depositPanel = NewPanel(p, "Deposit");
        depositList = (RectTransform)new GameObject("Packs", typeof(RectTransform)).transform;
        depositList.SetParent(depositPanel, false);
        OnlineUI.Place(depositList, Vector2.zero, new Vector2(1040f, 520f));

        // Withdraw.
        withdrawPanel = NewPanel(p, "Withdraw");
        withdrawOff = OnlineUI.NewLabel(withdrawPanel, "", 32, Vector2.zero, new Vector2(960f, 80f));
        withdrawOff.color = OnlineUI.Muted;

        withdrawControls = new GameObject("Controls", typeof(RectTransform));
        withdrawControls.transform.SetParent(withdrawPanel, false);
        OnlineUI.Place((RectTransform)withdrawControls.transform, Vector2.zero, new Vector2(1040f, 520f));
        Transform c = withdrawControls.transform;

        rateLabel = OnlineUI.NewLabel(c, "", 28, new Vector2(0f, 200f), new Vector2(1000f, 50f));
        rateLabel.color = OnlineUI.Muted;

        amountInput = OnlineUI.NewInput(c, "Coins to withdraw", new Vector2(-170f, 110f), new Vector2(520f, 84f));
        amountInput.contentType = InputField.ContentType.DecimalNumber;
        amountInput.characterLimit = 12;
        amountInput.onValueChanged.AddListener(_ => UpdateQuote());

        OnlineUI.NewButton(c, "MIN", new Vector2(170f, 110f), new Vector2(130f, 84f), OnlineUI.Gray, () => SetAmount(SmallestCents()));
        OnlineUI.NewButton(c, "HALF", new Vector2(310f, 110f), new Vector2(130f, 84f), OnlineUI.Gray, () => SetAmount(CoinBalance.Cents / 2));
        OnlineUI.NewButton(c, "ALL", new Vector2(450f, 110f), new Vector2(130f, 84f), OnlineUI.Gray, () => SetAmount(CoinBalance.Cents));

        quoteLabel = OnlineUI.NewLabel(c, "", 32, new Vector2(0f, 10f), new Vector2(1000f, 90f));
        withdrawButton = OnlineUI.NewButton(c, "WITHDRAW", new Vector2(0f, -100f), new Vector2(420f, 96f), OnlineUI.Green, Withdraw);

        Text note = OnlineUI.NewLabel(c, "Only whole units are paid; coins that would not pay anything stay with you.",
                                      26, new Vector2(0f, -200f), new Vector2(1000f, 40f));
        note.color = OnlineUI.Muted;

        // History.
        historyPanel = NewPanel(p, "History");
        historyList = (RectTransform)new GameObject("Lines", typeof(RectTransform)).transform;
        historyList.SetParent(historyPanel, false);
        OnlineUI.Place(historyList, new Vector2(0f, 40f), new Vector2(1040f, 420f));
        historyNote = OnlineUI.NewLabel(historyPanel, "", 32, new Vector2(0f, 40f), new Vector2(960f, 80f));
        historyNote.color = OnlineUI.Muted;
        prevPage = OnlineUI.NewButton(historyPanel, "<", new Vector2(-120f, -215f), new Vector2(90f, 64f), OnlineUI.Gray, () => TurnPage(-1));
        nextPage = OnlineUI.NewButton(historyPanel, ">", new Vector2(120f, -215f), new Vector2(90f, 64f), OnlineUI.Gray, () => TurnPage(1));
        pageLabel = OnlineUI.NewLabel(historyPanel, "", 30, new Vector2(0f, -215f), new Vector2(140f, 60f));

        status = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, -355f), new Vector2(1040f, 60f));
        status.color = OnlineUI.Warning;

        openWalletButton = OnlineUI.NewButton(p, "OPEN WALLET PAGE", new Vector2(-190f, -425f), new Vector2(400f, 74f), OnlineUI.Blue, OpenWalletPage);
        OnlineUI.NewButton(p, "CLOSE", new Vector2(250f, -425f), new Vector2(300f, 74f), OnlineUI.Gray, Close);
    }
}
