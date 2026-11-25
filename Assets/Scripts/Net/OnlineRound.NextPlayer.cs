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
    // A seeker's Next Player on the results screen says the same.
    private void RefreshNextButton(bool force = false)
    {
        if (!force && Time.unscaledTime < nextButtonCheckAt) return;
        nextButtonCheckAt = Time.unscaledTime + 0.5f;

        string text = NextPlayerText("Next player");
        if (text == nextButtonShown) return;
        nextButtonShown = text;
        OnlineUI.SetButtonText(nextButton, text);
        if (revealOffered) OnlineMatches.SetExtraLabel(NextPlayerText("NEXT PLAYER"));
    }

    // The button's name, with the unlock price under it until the account owns Next Player.
    private static string NextPlayerText(string name)
    {
        if (NextPlayerOpen) return name;
        ShopItem item = ShopInventory.FeatureItem(GameFeatures.NextPlayer);
        return item != null ? $"{name}\n<size=26>Unlock: {OnlineUI.CoinsText(ShopInventory.PriceCents(item))}</size>" : name;
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
        RaiseForOffer(true);   // over the results screen when it is offered there
        // Design B's dialog: what Next Player does, the balance, Not now and Unlock.
        PaintboxDialog offer = PaintboxDialog.Build(roundCanvas, "NextPlayerOffer", "Eye", "Next Player", new Vector2(960f, 540f));
        nextOffer = (RectTransform)offer.Root.transform.GetChild(0);   // the card: its parent is the dim, closed with it
        string price = OnlineUI.CoinsText(ShopInventory.PriceCents(item));
        offer.Body.text = hunter
            ? "See where the hiders hid when they win. Hiders: switch between the players still in after you are out. Yours for good, in every match."
            : "Switch between the players still in after you are out, and as a seeker see where the hiders hid when they win. Yours for good, in every match.";
        nextOfferStatus = Paintbox.Label(offer.Card, $"You have {OnlineUI.CoinsText(ShopInventory.CoinCents)}.", 26f, Paintbox.Ink, Paintbox.Weight.Bold);
        nextOfferStatus.rectTransform.anchorMin = new Vector2(0f, 0f);
        nextOfferStatus.rectTransform.anchorMax = new Vector2(1f, 0f);
        nextOfferStatus.rectTransform.offsetMin = new Vector2(40f, 124f);
        nextOfferStatus.rectTransform.offsetMax = new Vector2(-40f, 166f);
        offer.BackButton("Not now", Paintbox.Tone.Paper, null, () =>
        {
            AudioManager.ButtonTap();
            CloseNextPlayerOffer();
        }, 200f);
        nextOfferBuy = offer.Button($"Unlock for {price}", Paintbox.Tone.Primary, "Eye", () => BuyNextPlayer(item), 380f);
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
                if (nextOfferStatus != null) nextOfferStatus.text = $"<color=#9A4F12>{message}</color>";
                if (nextOfferBuy != null) nextOfferBuy.interactable = true;
                return;
            }

            CloseNextPlayerOffer();
            ShowBanner(message, 3f);
            OwnNextPlayerNow();
            if (resultShown && revealOffered) RevealStep(+1);   // a seeker: straight to the first hider
            else if (localOut && watching != 0xFF)
            {
                if (camCtrl != null) camCtrl.SetFreeLook(false);
                Watch(CycleSlot(watching, +1, IsWatchable));
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
        RaiseForOffer(false);
    }
}
