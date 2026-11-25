using Meccha.Shared.Game;
using UnityEngine;

// The confirmation a player gets when bought coins reach their account, over whatever is showing
// (Home, the shop, the wallet, a lobby): how many coins were added and what the balance is now.
// It appears when the server says the top-up is credited (PendingTopUp.Settled), never on the
// phone's own guess, and the balance it shows is the server's. Since 2026-11-24 it is one of the
// game's notices (Notices, a reward), gone by itself; a failed or expired payment is still told by
// the screen the player is on.
public sealed class CoinNotice : MonoBehaviour
{
    private static CoinNotice instance;

    /// <summary>How many times a credit was confirmed this session (the play test reads it).</summary>
    public static int Shown { get; private set; }

    /// <summary>The confirmation on screen now, or "" (the play test reads it).</summary>
    public static string Visible
    {
        get
        {
            string all = Notices.Visible;
            foreach (string line in all.Split('\n'))
                if (line.StartsWith(L.T("Purchase complete"), System.StringComparison.Ordinal)) return line;
            return "";
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (instance != null) return;
        var go = new GameObject("CoinNotice");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<CoinNotice>();
    }

    private void OnEnable() => PendingTopUp.Settled += HandleSettled;
    private void OnDisable() => PendingTopUp.Settled -= HandleSettled;

    private void HandleSettled(PendingTopUp.Entry entry, PaymentState state)
    {
        if (state != PaymentState.Credited) return;
        Show(entry.coinCents);
    }

    /// <summary>Confirms coins added to the account: shown at once, over everything.</summary>
    public static void Show(long coinCents)
    {
        Shown++;
        string balance = CoinBalance.Known ? L.F("Balance: {0} coins", CoinBalance.Text) : "";
        Notices.Show(NoticeKind.Reward, L.T("Purchase complete"),
                     L.F("{0} coins have been added to your account.", CoinBalance.Format(coinCents)) + (balance.Length > 0 ? " " + balance : ""),
                     6f);
    }
}
