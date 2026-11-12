using System.Collections;
using Meccha.Shared.Game;
using UnityEngine;
using UnityEngine.UI;

// The confirmation a player gets when bought coins reach their account: a card at the top of the
// screen, over whatever is showing (Home, the shop, the wallet, a lobby), saying how many coins
// were added and what the balance is now. It appears when the server says the top-up is
// credited (PendingTopUp.Settled), never on the phone's own guess, and the balance it shows is the
// server's. It goes by itself after a few seconds, or on OK; it never blocks the screen under it.
// A failed or expired payment is still told by the screen the player is on.
public sealed class CoinNotice : MonoBehaviour
{
    private const float ShowSeconds = 6f;
    private const float FadeSeconds = 0.25f;

    private static CoinNotice instance;

    private GameObject card;
    private CanvasGroup group;
    private Text title, amount, balance;
    private Coroutine showing;

    /// <summary>How many times a credit was confirmed this session (the play test reads it).</summary>
    public static int Shown { get; private set; }

    /// <summary>The text the card shows now, or "" when it is hidden (the play test reads it).</summary>
    public static string Visible =>
        instance != null && instance.card != null && instance.card.activeInHierarchy
            ? instance.title.text + "\n" + instance.amount.text + "\n" + instance.balance.text
            : "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        if (instance != null) return;
        var go = new GameObject("CoinNotice");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<CoinNotice>();
    }

    private void Awake()
    {
        Build();
        card.SetActive(false);
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
        if (instance == null) Create();
        instance.Present(coinCents);
    }

    private void Present(long coinCents)
    {
        Shown++;
        title.text = "Purchase complete";
        amount.text = $"{CoinBalance.Format(coinCents)} coins have been added to your account.";
        balance.text = CoinBalance.Known ? $"Balance: {CoinBalance.Text} coins" : "";

        UiTranslator.Track(title);
        UiTranslator.Track(amount);
        UiTranslator.Track(balance);
        AudioManager.ButtonTap();

        if (showing != null) StopCoroutine(showing);
        showing = StartCoroutine(ShowFor(ShowSeconds));
    }

    private IEnumerator ShowFor(float seconds)
    {
        card.SetActive(true);
        card.transform.SetAsLastSibling();
        for (float t = 0f; t < FadeSeconds; t += Time.unscaledDeltaTime)
        {
            group.alpha = t / FadeSeconds;
            yield return null;
        }
        group.alpha = 1f;

        yield return new WaitForSecondsRealtime(seconds);
        yield return Hide();
    }

    private IEnumerator Hide()
    {
        for (float t = FadeSeconds; t > 0f; t -= Time.unscaledDeltaTime)
        {
            group.alpha = t / FadeSeconds;
            yield return null;
        }

        card.SetActive(false);
        showing = null;
    }

    private void Close()
    {
        AudioManager.ButtonTap();
        if (showing != null) StopCoroutine(showing);
        showing = StartCoroutine(Hide());
    }

    // Kept the balance current while it is up (a purchase right after, say).
    private void Update()
    {
        if (card.activeSelf && CoinBalance.Known) balance.text = $"Balance: {CoinBalance.Text} coins";
    }

    private void Build()
    {
        Transform canvas = OnlineUI.CreateCanvas("CoinNoticeCanvas", transform, 90).transform;

        Image bg = OnlineUI.NewImage("CoinNoticeCard", canvas, new Color(0.10f, 0.32f, 0.17f, 0.97f));
        RectTransform rt = bg.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -40f);
        rt.sizeDelta = new Vector2(980f, 250f);
        card = bg.gameObject;
        group = card.AddComponent<CanvasGroup>();

        Outline outline = card.AddComponent<Outline>();
        outline.effectColor = new Color(0.49f, 0.99f, 0.6f, 0.9f);
        outline.effectDistance = new Vector2(3f, -3f);

        title = OnlineUI.NewLabel(card.transform, "", 46, new Vector2(-70f, 72f), new Vector2(780f, 64f), TextAnchor.MiddleLeft, true);
        title.color = new Color(0.49f, 0.99f, 0.6f, 1f);
        amount = OnlineUI.NewLabel(card.transform, "", 34, new Vector2(-70f, 5f), new Vector2(780f, 60f), TextAnchor.MiddleLeft);
        balance = OnlineUI.NewLabel(card.transform, "", 30, new Vector2(-70f, -62f), new Vector2(780f, 50f), TextAnchor.MiddleLeft);
        balance.color = OnlineUI.Muted;

        OnlineUI.NewButton(card.transform, "OK", new Vector2(390f, 0f), new Vector2(150f, 110f), OnlineUI.Green, Close);
    }
}
