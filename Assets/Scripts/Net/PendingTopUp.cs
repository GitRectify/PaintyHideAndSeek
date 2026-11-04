using System;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;

// Coin top-ups the player has opened and the server has not settled yet. The phone never reports a
// payment: the wallet provider tells the server, and the client learns of it by asking
// (MetaClient.TopUpStatus). A payment outlives the app — the player pays in the browser, and Android
// may close the game meanwhile — so the open ones are saved in PlayerPrefs and asked about again:
//   * every few seconds for a while after a payment is opened,
//   * when the app comes back to the front (the player is back from the checkout),
//   * after each sign-in (a restart included; nothing here signs in by itself, so Single Play
//     stays offline).
// Each ends when the server says Credited, Failed or Expired (Settled is raised) or does not know
// it. The coins are the server's either way; this only makes sure the player is told.
public sealed class PendingTopUp : MonoBehaviour
{
    private const string Key = "PendingTopUps";
    private const int MaxSaved = 8;
    private const double KeepDays = 2;          // the server expires an unpaid one after 60 minutes
    private const float PollSeconds = 3f;       // the SDK's WatchTopUp cadence
    private const float WatchSeconds = 900f;    // then wait for the next sign-in or return to the app

    [Serializable]
    public struct Entry
    {
        public string paymentId;
        public string packageId;
        public string checkoutUrl;
        public string profileId;
        public long coinCents;
        public long priceMinor;
        public string currency;
        public long createdUtcTicks;
    }

    [Serializable]
    private class Saved
    {
        public List<Entry> entries = new List<Entry>();
    }

    /// <summary>A payment the server settled: Credited, Failed or Expired.</summary>
    public static event Action<Entry, PaymentState> Settled;

    private static PendingTopUp instance;
    private static List<Entry> entries;

    private float watchUntil;
    private float nextPoll;
    private int asking;
    private string lastSession;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateWatcher()
    {
        if (instance != null) return;
        var go = new GameObject("PendingTopUp");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<PendingTopUp>();
    }

    // ================= Saved payments =================

    private static List<Entry> Entries
    {
        get
        {
            if (entries != null) return entries;

            entries = new List<Entry>();
            try
            {
                Saved saved = JsonUtility.FromJson<Saved>(PlayerPrefs.GetString(Key, ""));
                if (saved?.entries != null) entries.AddRange(saved.entries);
            }
            catch (ArgumentException)
            {
                // Unreadable: start over. The server still credits whatever was paid.
            }

            long oldest = DateTime.UtcNow.AddDays(-KeepDays).Ticks;
            entries.RemoveAll(e => string.IsNullOrEmpty(e.paymentId) || e.createdUtcTicks < oldest);
            return entries;
        }
    }

    private static void Save()
    {
        PlayerPrefs.SetString(Key, JsonUtility.ToJson(new Saved { entries = Entries }));
        PlayerPrefs.Save();
    }

    private static string CurrentProfile => MecchaNetClient.Instance != null ? MecchaNetClient.Instance.ProfileId ?? "" : "";

    /// <summary>Records a payment just opened and starts asking about it.</summary>
    public static Entry Add(MetaTopUp topUp, MetaTopUpPackage package)
    {
        var entry = new Entry
        {
            paymentId = topUp.PaymentId,
            packageId = package.Id,
            checkoutUrl = topUp.CheckoutUrl ?? "",
            profileId = CurrentProfile,
            coinCents = topUp.CoinCents > 0 ? topUp.CoinCents : package.CoinCents,
            priceMinor = package.PriceMinor,
            currency = package.Currency,
            createdUtcTicks = DateTime.UtcNow.Ticks,
        };

        List<Entry> list = Entries;
        list.RemoveAll(e => e.paymentId == entry.paymentId);   // the same request answered twice
        list.Add(entry);
        while (list.Count > MaxSaved) list.RemoveAt(0);         // a dropped one is still credited
        Save();

        Kick();
        ShopInventory.NotifyChanged();
        return entry;
    }

    /// <summary>An open payment of the signed-in player for this coin pack.</summary>
    public static bool TryFor(string packageId, out Entry entry)
    {
        string profile = CurrentProfile;
        foreach (Entry e in Entries)
        {
            if (e.packageId == packageId && e.profileId == profile)
            {
                entry = e;
                return true;
            }
        }

        entry = default;
        return false;
    }

    public static bool IsWaiting(string packageId) => TryFor(packageId, out _);

    private static void Remove(string paymentId)
    {
        if (Entries.RemoveAll(e => e.paymentId == paymentId) == 0) return;
        Save();
        ShopInventory.NotifyChanged();
    }

    /// <summary>Sends the player to the payment's checkout page.</summary>
    public static void OpenCheckout(Entry entry)
    {
        if (string.IsNullOrEmpty(entry.checkoutUrl)) return;

        // The development wallet (FakeWalletProvider) has no page to open: it is paid with
        // Tools/Pay-FakeTopUp.ps1, which signs the callback the real provider would send.
        if (Uri.TryCreate(entry.checkoutUrl, UriKind.Absolute, out Uri uri) &&
            uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log("[TopUp] Development wallet: pay this top-up with\n" +
                      $"powershell -ExecutionPolicy Bypass -File Tools/Pay-FakeTopUp.ps1 -PaymentId {entry.paymentId} " +
                      $"-AmountMinor {entry.priceMinor} -Currency {entry.currency}\n" +
                      "(add -Status failed to fail it)");
            Kick();
            return;
        }

        Application.OpenURL(entry.checkoutUrl);
        Kick();
    }

    /// <summary>Asks about the open payments now, and every few seconds for a while.</summary>
    public static void Kick()
    {
        if (instance == null) CreateWatcher();
        instance.watchUntil = Time.unscaledTime + WatchSeconds;
        instance.nextPoll = 0f;
    }

    /// <summary>What to tell the player about a settled payment.</summary>
    public static string MessageFor(Entry entry, PaymentState state)
    {
        switch (state)
        {
            case PaymentState.Credited: return $"Added {ShopInventory.CoinsText(entry.coinCents)} coins!";
            case PaymentState.Failed: return "The payment failed. You were not charged.";
            case PaymentState.Expired: return "The payment expired before it was paid.";
            default: return "";
        }
    }

    // ================= Asking the server =================

    private void Update()
    {
        MecchaNetClient net = MecchaNetClient.Instance;
        MetaClient meta = MetaClient.Instance;
        if (net == null || meta == null || string.IsNullOrEmpty(net.SessionToken)) return;

        // A new sign-in (a restart, or back after the session expired): ask again.
        if (net.SessionToken != lastSession)
        {
            lastSession = net.SessionToken;
            Kick();
        }

        if (asking > 0 || Time.unscaledTime > watchUntil || Time.unscaledTime < nextPoll) return;
        nextPoll = Time.unscaledTime + PollSeconds;

        string profile = CurrentProfile;
        foreach (Entry entry in Entries.ToArray())
        {
            if (entry.profileId != profile) continue;   // another account's: asked about when it signs in

            asking++;
            Entry asked = entry;
            meta.TopUpStatus(asked.paymentId, reply =>
            {
                asking--;
                HandleStatus(asked, reply);
            });
        }
    }

    private static void HandleStatus(Entry entry, MetaClient.TopUpReply reply)
    {
        if (reply.TimedOut) return;   // asked again on the next poll

        if (reply.Error == MetaError.UnknownPayment)
        {
            Debug.LogWarning($"[TopUp] The server does not know payment {entry.paymentId}; forgetting it.");
            Remove(entry.paymentId);
            return;
        }

        if (reply.Error != MetaError.None) return;

        PaymentState state = reply.TopUp.State;
        if (state != PaymentState.Credited && state != PaymentState.Failed && state != PaymentState.Expired) return;

        if (state == PaymentState.Credited && reply.TopUp.CoinCents > 0) entry.coinCents = reply.TopUp.CoinCents;
        Debug.Log($"[TopUp] Payment {entry.paymentId}: {state}");
        Remove(entry.paymentId);
        Settled?.Invoke(entry, state);
    }

    // Back from the checkout page in the browser.
    private void OnApplicationFocus(bool focused)
    {
        if (focused && Entries.Count > 0) Kick();
    }

    private void OnApplicationPause(bool paused)
    {
        if (!paused && Entries.Count > 0) Kick();
    }
}
