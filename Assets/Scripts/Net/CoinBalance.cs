using System;
using System.Collections.Generic;
using System.Globalization;
using Meccha.Client;
using UnityEngine;
using UnityEngine.UI;

// The player's coin balance, for every label that shows it. There is one currency and one
// balance: the server's, as it last reported it (MecchaNetClient.CoinCents). Nothing here keeps a
// number of its own, so no two labels can disagree, and nothing on the phone can change it: a
// purchase, a deposit, a withdrawal, an entry fee and a reward are all decided on the server, and
// the label follows when the server says what the balance is now.
//
// Before signing in there is no balance to show (Single Play stays offline and has no coins), so
// a label shows Unknown, never a made-up number.
//
// Use: CoinBalance.Bind(label) once; the label then keeps itself up to date (CoinLabel).
public static class CoinBalance
{
    /// <summary>What a label shows while the balance is not known: not signed in yet.</summary>
    public const string Unknown = "-";

    /// <summary>Raised when the balance changes, and when the player signs in or out.</summary>
    public static event Action Changed;

    /// <summary>True once the server has reported a balance this session.</summary>
    public static bool Known
    {
        get
        {
            MecchaNetClient net = MecchaNetClient.Instance;
            return net != null && !string.IsNullOrEmpty(net.SessionToken);
        }
    }

    /// <summary>The balance in cents (1/100 coin); 0 while it is not known.</summary>
    public static long Cents => Known ? MecchaNetClient.Instance.CoinCents : 0;

    /// <summary>The balance as every label shows it: "1,234", "1,234.50", or Unknown.</summary>
    public static string Text => Known ? Format(Cents) : Unknown;

    /// <summary>
    /// An amount of coins as text, the same on every screen and every phone: whole coins without
    /// decimals, anything else to the cent, a comma between thousands whatever the device language.
    /// </summary>
    public static string Format(long cents)
    {
        string sign = cents < 0 ? "-" : "";
        decimal coins = Math.Abs(cents) / 100m;
        return sign + coins.ToString(cents % 100 == 0 ? "N0" : "N2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Makes a label show the balance from now on. <paramref name="format"/> places it in a
    /// sentence: "Your coins: {0}".
    /// </summary>
    public static CoinLabel Bind(Text label, string format = "{0}")
    {
        if (label == null) return null;

        Watcher.Ensure();
        CoinLabel bound = label.GetComponent<CoinLabel>();
        if (bound == null) bound = label.gameObject.AddComponent<CoinLabel>();
        bound.Set(label, format);
        return bound;
    }

    /// <summary>
    /// Binds every text called <paramref name="objectName"/> under <paramref name="root"/>, shown or
    /// hidden. The scene's coin pills are all called "CoinText" and were saved with a number typed
    /// into them; one that no script happened to know about kept showing that number. Returns the
    /// labels bound.
    /// </summary>
    public static List<Text> BindNamed(Transform root, string objectName = "CoinText")
    {
        var bound = new List<Text>();
        if (root == null) return bound;

        foreach (Text label in root.GetComponentsInChildren<Text>(true))
        {
            if (label.gameObject.name != objectName) continue;
            Bind(label);
            bound.Add(label);
        }

        return bound;
    }

    // Follows the SDK's client: it is made the first time the player goes online, can be replaced,
    // and signs in some time after it is made. The event is the quick path; the per-frame compare
    // also catches the sign-in and sign-out, which change what is Known without a balance event.
    private sealed class Watcher : MonoBehaviour
    {
        private static Watcher instance;

        private MecchaNetClient hooked;
        private bool known;
        private long cents;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateAtStart() => Ensure();

        public static void Ensure()
        {
            if (instance != null) return;

            var go = new GameObject("CoinBalance");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Watcher>();
        }

        private void Update()
        {
            MecchaNetClient net = MecchaNetClient.Instance;
            if (net != hooked)
            {
                if (hooked != null) hooked.OnBalanceChanged -= HandleBalance;
                hooked = net;
                if (net != null) net.OnBalanceChanged += HandleBalance;
            }

            if (Known != known || Cents != cents) Note();
        }

        private void HandleBalance(long _) => Note();

        private void Note()
        {
            known = Known;
            cents = Cents;
            Changed?.Invoke();
        }

        private void OnDestroy()
        {
            if (hooked != null) hooked.OnBalanceChanged -= HandleBalance;
            if (instance == this) instance = null;
        }
    }
}
