using Meccha.Shared.Game;
using UnityEngine;
using UnityEngine.UI;

// Next Player (GameFeatures.NextPlayer), unlocked for good in the shop. Out of an online round the
// camera follows the players still in by itself, one after another, for everyone. Switching between
// them with the Next player button is the feature: until the account owns it the button offers it
// for the server's price (a "feature" item in its catalog), and once bought it switches at once,
// in this match and every later one. The Shop sells it too (Extras tab). A server that does not
// sell it leaves the button switching freely, as before it was a feature.
public partial class OnlineRound
{
    private Transform roundCanvas;
    private RectTransform nextOffer;
    private Text nextOfferStatus;
    private Button nextOfferBuy;
    private bool nextOfferBusy;
    private string nextButtonShown;

    // Bought on this session: open at once, before the refreshed listing that says so arrives.
    private static string nextPlayerBoughtOn;

    /// <summary>Whether Next player switches now: the account owns Next Player, or the server does not sell it.</summary>
    private static bool NextPlayerOpen =>
        (nextPlayerBoughtOn != null && Net != null && nextPlayerBoughtOn == Net.SessionToken) ||
        (ShopInventory.Loaded && (ShopInventory.OwnsFeature(GameFeatures.NextPlayer) || ShopInventory.FeatureItem(GameFeatures.NextPlayer) == null));

    private float nextButtonCheckAt;

    // Twice a second while spectating (the listing and the balance change rarely), at once when forced.
    private void RefreshNextButton(bool force = false)
    {
        if (!force && Time.unscaledTime < nextButtonCheckAt) return;
        nextButtonCheckAt = Time.unscaledTime + 0.5f;

        string text;
        if (NextPlayerOpen) text = "Next player";
        else
        {
            ShopItem item = ShopInventory.FeatureItem(GameFeatures.NextPlayer);
            text = item != null ? $"Next player\n<size=26>Unlock: {OnlineUI.CoinsText(ShopInventory.PriceCents(item))}</size>" : "Next player";
        }

        if (text == nextButtonShown) return;
        nextButtonShown = text;
        OnlineUI.SetButtonText(nextButton, text);
    }

    // Asks to buy Next Player: what it does, its price and the balance, Unlock or Not now.
    private void OfferNextPlayer()
    {
        if (!ShopInventory.Loaded)
        {
            // The listing comes with the round; ask again if it has not arrived.
            ShowBanner("The shop is still loading. Try again in a moment.", 3f);
            ShopInventory.Refresh(null);
            return;
        }

        ShopItem item = ShopInventory.FeatureItem(GameFeatures.NextPlayer);
        if (item == null || roundCanvas == null) return;

        CloseNextPlayerOffer();
        nextOffer = OnlineUI.Modal(roundCanvas, "NextPlayerOffer", new Vector2(900f, 560f));
        string price = OnlineUI.CoinsText(ShopInventory.PriceCents(item));

        OnlineUI.NewLabel(nextOffer, "Next Player", 52, new Vector2(0f, 200f), new Vector2(820f, 80f), TextAnchor.MiddleCenter, true);
        OnlineUI.NewLabel(nextOffer, "Switch between the players still in after you are out. Yours for good, in every match.",
                          32, new Vector2(0f, 85f), new Vector2(800f, 120f), TextAnchor.MiddleCenter, false);
        nextOfferStatus = OnlineUI.NewLabel(nextOffer, $"You have {OnlineUI.CoinsText(ShopInventory.CoinCents)}.", 30,
                                            new Vector2(0f, -15f), new Vector2(800f, 60f), TextAnchor.MiddleCenter, false);
        nextOfferStatus.color = OnlineUI.Muted;

        nextOfferBuy = OnlineUI.NewButton(nextOffer, $"Unlock for {price}", new Vector2(-200f, -170f), new Vector2(360f, 110f),
                                          OnlineUI.Green, () => BuyNextPlayer(item));
        OnlineUI.NewButton(nextOffer, "Not now", new Vector2(200f, -170f), new Vector2(360f, 110f), OnlineUI.Gray, () =>
        {
            AudioManager.ButtonTap();
            CloseNextPlayerOffer();
        });
    }

    private void BuyNextPlayer(ShopItem item)
    {
        AudioManager.ButtonTap();
        if (nextOfferBusy) return;
        nextOfferBusy = true;
        if (nextOfferBuy != null) nextOfferBuy.interactable = false;
        if (nextOfferStatus != null) nextOfferStatus.text = "Unlocking...";

        ShopInventory.Purchase(item, (bought, message) =>
        {
            nextOfferBusy = false;
            if (this == null || !active) return;

            if (!bought)
            {
                // Said in the panel, which stays open (Not now closes it).
                if (nextOfferStatus != null) nextOfferStatus.text = $"<color=#FFC759>{message}</color>";
                if (nextOfferBuy != null) nextOfferBuy.interactable = true;
                return;
            }

            CloseNextPlayerOffer();
            ShowBanner(message, 3f);
            OwnNextPlayerNow();
            if (localOut && watching != 0xFF)
            {
                if (camCtrl != null) camCtrl.SetFreeLook(false);
                Watch(NextToWatch(watching));
            }
        });
    }

    // The purchase answered Completed: the listing that says so is on its way (Purchase refreshes
    // it), and the button switches from now on.
    private void OwnNextPlayerNow()
    {
        nextPlayerBoughtOn = Net != null ? Net.SessionToken : null;
        RefreshNextButton(force: true);
    }

    private void CloseNextPlayerOffer()
    {
        if (nextOffer != null) Destroy(nextOffer.parent.gameObject);   // the dim behind the panel, with it
        nextOffer = null;
        nextOfferStatus = null;
        nextOfferBuy = null;
    }
}
