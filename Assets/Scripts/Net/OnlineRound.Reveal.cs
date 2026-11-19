using System;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Part of OnlineRound (OnlineRound.cs has the overview). Where the hiders hid (2026-11-20): when the
// hiders win, a seeker can look at each hider still in, where they ended the match, with the same
// Next player button and the same paid unlock (Next Player, GameFeatures.NextPlayer) a caught hider
// spectates with. The button waits over the results, bottom left; each tap puts the results away
// and takes the camera to the next hider, tagged "HIDER" over their body and at the top of the
// screen. "Back to results" puts everything back. A hider's spectating is unchanged.
public partial class OnlineRound
{
    private const int RevealCanvasOrder = 58;   // over the result popup (40) and the results table (57)

    private bool hidersWon;        // this match's outcome, from RoundEnd / MatchEnd / the results
    private bool revealOffered;    // a seeker, the hiders won: the button can show them
    private bool revealing;
    private bool revealWasFirstPerson;
    private byte revealTagSlot = 0xFF;
    private Text revealLabel;
    private Button revealBack;
    private Canvas roundCanvasComponent;
    private int roundCanvasOrder = -1;
    private Action showResultsAgain;
    private MatchResultTable? lastResultsTable;

    private void BuildReveal(Transform canvas)
    {
        roundCanvasComponent = canvas.GetComponent<Canvas>();

        revealLabel = OnlineUI.NewLabel(canvas, "", 46, Vector2.zero, new Vector2(1200f, 80f), TextAnchor.MiddleCenter, true);
        revealLabel.name = "RevealHider";
        revealLabel.color = new Color(1f, 0.78f, 0.35f, 1f);
        RectTransform lrt = revealLabel.rectTransform;
        lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(0.5f, 1f);
        lrt.anchoredPosition = new Vector2(0f, -40f);
        Outline outline = revealLabel.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);
        revealLabel.gameObject.SetActive(false);

        revealBack = OnlineUI.NewButton(canvas, "BACK TO RESULTS", Vector2.zero, new Vector2(380f, 84f), OnlineUI.Gray, () =>
        {
            AudioManager.ButtonTap();
            EndReveal(showResults: true);
        });
        RectTransform brt = (RectTransform)revealBack.transform;
        brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0f, 1f);
        brt.anchoredPosition = new Vector2(24f, -24f);
        revealBack.gameObject.SetActive(false);
    }

    // A new round, or the end of one: nothing of the last match's reveal is left.
    private void ResetReveal()
    {
        EndReveal(showResults: false);
        hidersWon = false;
        revealOffered = false;
        showResultsAgain = null;
        lastResultsTable = null;
        PlaceNextButton(corner: false);
        if (roundCanvasComponent != null && roundCanvasOrder >= 0) roundCanvasComponent.sortingOrder = roundCanvasOrder;
    }

    private void NoteOutcome(MatchOutcome outcome)
    {
        if (outcome == MatchOutcome.HidersWin) hidersWon = true;
    }

    // The match is decided (ShowResult): a seeker who lost to the hiders gets the button, over the
    // results, while any hider is left to show.
    private void OfferReveal(Action showResults)
    {
        showResultsAgain = showResults;
        if (!hunter || !hidersWon || NextHider(0xFF) == 0xFF || nextButton == null) return;

        revealOffered = true;
        if (bulletButton != null) bulletButton.gameObject.SetActive(false);   // it would sit over the results too
        if (roundCanvasComponent != null)
        {
            if (roundCanvasOrder < 0) roundCanvasOrder = roundCanvasComponent.sortingOrder;
            roundCanvasComponent.sortingOrder = RevealCanvasOrder;
        }
        PlaceNextButton(corner: true);
        nextButton.gameObject.SetActive(true);
        RefreshNextButton(force: true);
    }

    // The results bottom centre has their own buttons: over them the button sits bottom left.
    private void PlaceNextButton(bool corner)
    {
        if (nextButton == null) return;
        RectTransform rt = (RectTransform)nextButton.transform;
        Vector2 anchor = corner ? Vector2.zero : new Vector2(0.5f, 0f);
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = corner ? new Vector2(40f, 40f) : new Vector2(0f, 60f);
    }

    /// <summary>The Next player button after the match: the next hider, or the offer to unlock it first.</summary>
    private void RevealNext()
    {
        if (!NextPlayerOpen)
        {
            OfferNextPlayer();   // bought, it comes back here (BuyNextPlayer)
            return;
        }

        byte next = NextHider(revealing ? watching : (byte)0xFF);
        if (next == 0xFF)
        {
            ShowBanner("No hiders left to show.", 2f);
            return;
        }

        if (!revealing) BeginReveal();
        watching = next;
        NetEntityView view = NetEntityRegistry.Get(next);
        if (camCtrl != null) camCtrl.SetFollowTarget(view != null ? view.transform : null);

        int index = 0, count = 0;
        CountHiders(next, ref index, ref count);
        revealLabel.text = count > 1 ? $"HIDER {index}/{count}: {NameOf(next)}" : $"HIDER: {NameOf(next)}";
    }

    private void BeginReveal()
    {
        revealing = true;
        CloseNextPlayerOffer();
        if (results != null) results.HideAll();
        OnlineMatches.HideResults();
        if (camCtrl != null)
        {
            revealWasFirstPerson = camCtrl.IsFirstPerson;
            camCtrl.SetFreeLook(false);
            camCtrl.SetFirstPerson(false);   // the following camera, round the hider, not over a shoulder
        }
        revealLabel.gameObject.SetActive(true);
        revealBack.gameObject.SetActive(true);
    }

    private void EndReveal(bool showResults)
    {
        if (!revealing) return;
        revealing = false;
        watching = 0xFF;
        if (camCtrl != null)
        {
            camCtrl.SetFollowTarget(null);
            camCtrl.SetFirstPerson(revealWasFirstPerson);
        }
        if (revealLabel != null) revealLabel.gameObject.SetActive(false);
        if (revealBack != null) revealBack.gameObject.SetActive(false);
        RestoreRevealTag();

        if (!showResults) return;
        showResultsAgain?.Invoke();
        if (lastResultsTable is MatchResultTable table) OnlineMatches.ShowResults(table, () => lobby.PlayAgainFromRound(), () => lobby.GoHomeFromRound());
    }

    // The hiders still in at the end (in slot order, wrapping): not caught, with a body to show.
    private byte NextHider(byte after)
    {
        if (roster == null || Net == null) return 0xFF;

        byte next = 0xFF, first = 0xFF;
        foreach (MecchaNetClient.RosterEntry e in roster)
        {
            if (!IsHiderToShow(e)) continue;
            if (first == 0xFF || e.Slot < first) first = e.Slot;
            if (after != 0xFF && e.Slot > after && (next == 0xFF || e.Slot < next)) next = e.Slot;
        }
        return next != 0xFF ? next : first;
    }

    private void CountHiders(byte slot, ref int index, ref int count)
    {
        foreach (MecchaNetClient.RosterEntry e in roster)
        {
            if (!IsHiderToShow(e)) continue;
            count++;
            if (e.Slot <= slot) index++;
        }
    }

    private bool IsHiderToShow(MecchaNetClient.RosterEntry e)
    {
        if (e.Role != Role.Fugitive || e.Slot == Net.LocalSlot || eliminated.Contains(e.Slot)) return false;
        NetEntityView view = NetEntityRegistry.Get(e.Slot);
        return view != null && !view.Eliminated;
    }

    // The tag over the hider shown says so (RefreshNameTags shows it to the seeker while revealing).
    private bool RevealTag(byte slot, BotNameTag tag)
    {
        if (!revealing || slot != watching) return false;
        if (revealTagSlot != slot)
        {
            RestoreRevealTag();
            revealTagSlot = slot;
            tag.SetName("HIDER · " + NameOf(slot));
        }
        return true;
    }

    private void RestoreRevealTag()
    {
        if (revealTagSlot == 0xFF) return;
        if (nameTags.TryGetValue(revealTagSlot, out BotNameTag tag) && tag != null) tag.SetName(NameOf(revealTagSlot));
        revealTagSlot = 0xFF;
    }
}
