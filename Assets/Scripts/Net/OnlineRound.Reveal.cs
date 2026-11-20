using System;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Part of OnlineRound (OnlineRound.cs has the overview): the end of the match, and watching other
// players.
//
// The end of the match (2026-11-21): one results screen, OnlineMatches' (everyone's rank, score and
// coins, "You won!" or "You lost." over them), with Next, Home and Close. Single Play's result popup
// is not used online. The round's HUD goes away with the result. Close leaves the map on screen with
// Exit and Results (to see them again) top left; those two are also there until the results arrive
// (they come with the match's end, and are asked for again if they do not).
//
// Watching other players, with Next Player (GameFeatures.NextPlayer, OnlineRound.NextPlayer.cs):
//   * A caught hider watches the players still in until the result (UpdateSpectating).
//   * A seeker the hiders beat sees where each hider still in ended the match: Next Player on the
//     results, then "<" and "Next player" at the bottom, each hider tagged HIDER.
// Both the same way: the camera glides from one player to the next and orbits them at the standard
// framing; a drag turns it round them (a hider's own scene drag in play, a layer of its own after the
// match, when the HUD is gone), the wheel or a pinch zooms, closer than in play. Who is watched is
// named over the buttons. Keys: the movement and turn keys turn the camera, forward and back zoom,
// the aim keys tilt it, the left and right arrows switch.
public partial class OnlineRound
{
    private const int OfferCanvasOrder = 58;    // over the results screen (57) while Next Player is offered on it
    private static readonly Color HiderColor = new Color(1f, 0.78f, 0.35f, 1f);

    private bool hidersWon;            // this match's outcome, from RoundEnd / MatchEnd / the results
    private bool revealOffered;        // a seeker, the hiders won: Next Player shows where they hid
    private bool revealing;
    private bool revealWasFirstPerson;
    private byte lastRevealed = 0xFF;  // Results and back again: the same hider
    private byte revealTagSlot = 0xFF;
    private bool resultWon;
    private bool resultsAutoShown;
    private float resultsAskAt;
    private int resultsAsks;
    private MatchResultTable? lastResultsTable;

    private bool spectateStarted;
    private Text watchLabel, watchHint;
    private Button prevButton, resultsButton;
    private GameObject dragLayer;
    private Canvas roundCanvasComponent;
    private int roundCanvasOrder = -1;
    private readonly List<byte> cycleSlots = new List<byte>();

    private void BuildWatchView(Transform canvas)
    {
        roundCanvasComponent = canvas.GetComponent<Canvas>();
        roundCanvasOrder = roundCanvasComponent != null ? roundCanvasComponent.sortingOrder : -1;

        // After the match the HUD is gone: a drag anywhere off the buttons turns the camera.
        Image layer = OnlineUI.NewImage("WatchDrag", canvas, new Color(0f, 0f, 0f, 0f));
        OnlineUI.Stretch(layer.rectTransform);
        layer.transform.SetAsFirstSibling();
        UIDrag drag = layer.gameObject.AddComponent<UIDrag>();
        drag.cb = OnWatchDrag;
        dragLayer = layer.gameObject;
        dragLayer.SetActive(false);

        watchLabel = OnlineUI.NewLabel(canvas, "", 42, Vector2.zero, new Vector2(1200f, 60f), TextAnchor.MiddleCenter, true);
        watchLabel.name = "Watching";
        BottomCentre(watchLabel.rectTransform, 214f);
        Outline outline = watchLabel.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);
        watchLabel.gameObject.SetActive(false);

        watchHint = OnlineUI.NewLabel(canvas, WatchHintText, 26, Vector2.zero, new Vector2(1400f, 36f), TextAnchor.MiddleCenter, false);
        watchHint.name = "WatchHint";
        watchHint.color = new Color(1f, 1f, 1f, 0.8f);
        BottomCentre(watchHint.rectTransform, 178f);
        Outline hintOutline = watchHint.gameObject.AddComponent<Outline>();
        hintOutline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        hintOutline.effectDistance = new Vector2(2f, -2f);
        watchHint.gameObject.SetActive(false);

        // "<", left of Next player: the one before.
        prevButton = OnlineUI.NewButton(canvas, "<", Vector2.zero, new Vector2(120f, 110f), OnlineUI.Blue, WatchPrevious);
        prevButton.name = "PreviousPlayer";
        prevButton.GetComponentInChildren<Text>().fontSize = 52;
        prevButton.gameObject.SetActive(false);
        PlaceWatchButtons(false);

        // Results, beside Exit after the match: the results again.
        resultsButton = OnlineUI.NewButton(canvas, "RESULTS", Vector2.zero, new Vector2(220f, 76f), OnlineUI.Blue, () =>
        {
            AudioManager.ButtonTap();
            ShowResultsAgain();
        });
        resultsButton.name = "MatchResults";
        RectTransform rrt = (RectTransform)resultsButton.transform;
        rrt.anchorMin = rrt.anchorMax = rrt.pivot = new Vector2(0f, 1f);
        rrt.anchoredPosition = new Vector2(220f, -24f);
        resultsButton.gameObject.SetActive(false);
    }

    private static string WatchHintText => Application.isMobilePlatform
        ? "Drag to look around. Pinch to zoom."
        : "Drag to look around. Scroll to zoom. Left and Right arrow keys switch players.";

    private static void BottomCentre(RectTransform rt, float y)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, y);
    }

    // Next player bottom centre; with "<" the two are centred together.
    private void PlaceWatchButtons(bool withPrevious)
    {
        if (nextButton != null)
        {
            RectTransform nrt = (RectTransform)nextButton.transform;
            nrt.anchorMin = nrt.anchorMax = nrt.pivot = new Vector2(0.5f, 0f);
            nrt.anchoredPosition = new Vector2(withPrevious ? 68f : 0f, 60f);
        }
        if (prevButton != null)
        {
            RectTransform prt = (RectTransform)prevButton.transform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0f);
            prt.anchoredPosition = new Vector2(-208f, 60f);
        }
    }

    private void ShowWatchControls(bool on, bool withPrevious)
    {
        SetShown(nextButton, on);
        SetShown(prevButton, on && withPrevious);
        PlaceWatchButtons(on && withPrevious);
    }

    private void ShowWatchLabel(bool on)
    {
        if (watchLabel != null && watchLabel.gameObject.activeSelf != on) watchLabel.gameObject.SetActive(on);
        if (watchHint != null && watchHint.gameObject.activeSelf != on) watchHint.gameObject.SetActive(on);
    }

    private static void SetShown(Component c, bool on)
    {
        if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
    }

    // A drag on the layer turns the camera round the player watched; two fingers zoom instead
    // (CameraController's pinch).
    private void OnWatchDrag(PointerEventData e)
    {
        if (camCtrl == null || !e.dragging || TouchesDown() > 1) return;
        camCtrl.OrbitYaw(e.delta.x);
        camCtrl.OrbitPitch(e.delta.y);
    }

    private static int TouchesDown()
    {
        Touchscreen screen = Touchscreen.current;
        if (screen == null) return 0;
        int n = 0;
        foreach (var touch in screen.touches)
            if (touch.press.isPressed) n++;
        return n;
    }

    // Keys while watching: the movement and turn keys turn the camera round the player, forward
    // and back zoom, the aim keys tilt it; the left and right arrows switch, as "<" and Next player.
    private void WatchKeys()
    {
        if (camCtrl == null || camCtrl.FreeLook || KeyBindings.Blocked) return;
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;

        float dt = Time.unscaledDeltaTime;
        Vector2 move = KeyBindings.Move;
        float turn = Mathf.Clamp(move.x + (KeyBindings.Held(GameAction.TurnRight) ? 1f : 0f) - (KeyBindings.Held(GameAction.TurnLeft) ? 1f : 0f), -1f, 1f);
        float tilt = (KeyBindings.Held(GameAction.AimUp) ? 1f : 0f) - (KeyBindings.Held(GameAction.AimDown) ? 1f : 0f);
        if (turn != 0f || tilt != 0f) camCtrl.Orbit(turn * 90f * dt, -tilt * 60f * dt);
        if (move.y != 0f) camCtrl.Zoom(-move.y * 30f * dt);

        if (keyboard[Key.RightArrow].wasPressedThisFrame) SwitchWatched(+1);
        else if (keyboard[Key.LeftArrow].wasPressedThisFrame) SwitchWatched(-1);
    }

    // ================= Choosing who =================

    // The next slot (step +1) or the one before (-1) among those included, in slot order, wrapping.
    // From a slot no longer included it goes on from where that one was.
    private byte CycleSlot(byte from, int step, Func<MecchaNetClient.RosterEntry, bool> include)
    {
        cycleSlots.Clear();
        if (roster == null || Net == null) return 0xFF;
        foreach (MecchaNetClient.RosterEntry e in roster)
            if (include(e)) cycleSlots.Add(e.Slot);
        if (cycleSlots.Count == 0) return 0xFF;
        cycleSlots.Sort();

        int n = cycleSlots.Count;
        int i = cycleSlots.IndexOf(from);
        if (i >= 0) return cycleSlots[((i + step) % n + n) % n];
        if (from == 0xFF) return step >= 0 ? cycleSlots[0] : cycleSlots[n - 1];

        if (step >= 0)
        {
            foreach (byte s in cycleSlots) if (s > from) return s;
            return cycleSlots[0];
        }
        for (int k = n - 1; k >= 0; k--) if (cycleSlots[k] < from) return cycleSlots[k];
        return cycleSlots[n - 1];
    }

    private int CountOf(Func<MecchaNetClient.RosterEntry, bool> include)
    {
        int count = 0;
        if (roster == null || Net == null) return 0;
        foreach (MecchaNetClient.RosterEntry e in roster) if (include(e)) count++;
        return count;
    }

    // Anyone still in, other than this player, with a body to show.
    private bool IsWatchable(MecchaNetClient.RosterEntry e)
    {
        if (e.Slot == Net.LocalSlot || eliminated.Contains(e.Slot)) return false;
        NetEntityView view = NetEntityRegistry.Get(e.Slot);
        return view != null && !view.Eliminated;
    }

    // A hider still in at the end.
    private bool IsHiderToShow(MecchaNetClient.RosterEntry e) => e.Role == Role.Fugitive && IsWatchable(e);

    private bool IsHiderToShow(byte slot)
    {
        if (slot == 0xFF || roster == null) return false;
        foreach (MecchaNetClient.RosterEntry e in roster) if (e.Slot == slot) return IsHiderToShow(e);
        return false;
    }

    // "<", Next player and the arrow keys: in play a caught hider's next player, after the match a
    // seeker's next hider; until Next Player is unlocked, its offer.
    private void WatchNext() => SwitchWatched(+1);

    private void WatchPrevious() => SwitchWatched(-1);

    private void SwitchWatched(int step)
    {
        AudioManager.ButtonTap();
        if (resultShown)
        {
            if (revealOffered) RevealStep(step);
            return;
        }
        if (!localOut) return;
        if (!NextPlayerOpen)
        {
            OfferNextPlayer();   // not unlocked yet: offer it, and switch once bought
            return;
        }
        if (camCtrl != null) camCtrl.SetFreeLook(false);   // the button means: show me them
        Watch(CycleSlot(watching, step, IsWatchable));
    }

    // ================= The end of the match =================

    // A new round, or the end of one: nothing of the last match's end is left.
    private void ResetReveal()
    {
        EndReveal();
        hidersWon = false;
        revealOffered = false;
        lastRevealed = 0xFF;
        resultWon = false;
        resultsAutoShown = false;
        resultsAsks = 0;
        lastResultsTable = null;
        CloseNextPlayerOffer();
        ShowWatchControls(false, false);
        ShowWatchLabel(false);
        SetShown(resultsButton, false);
        if (dragLayer != null) dragLayer.SetActive(false);
    }

    private void NoteOutcome(MatchOutcome outcome)
    {
        if (outcome == MatchOutcome.HidersWin) hidersWon = true;
    }

    // The match is decided (ShowResult, once): the HUD goes, the result is said and heard, and the
    // results show as soon as they are here.
    private void EnterAfterMatch(bool won)
    {
        resultWon = won;
        if (home != null) home.SetGameplayUIVisible(false);
        if (paint != null) paint.CloseDrawingNow();
        if (results != null) results.HideAll();
        bulletButton.gameObject.SetActive(false);
        HideFireCooldown();
        CloseNextPlayerOffer();
        if (won) AudioManager.Win(); else AudioManager.Lose();
        ShowBanner(won ? "You won!" : "You lost.", 4f);

        resultsAskAt = Time.unscaledTime + 3f;
        UpdateRevealOffer();
        if (lastResultsTable is MatchResultTable table) ShowMatchResults(table);
    }

    private void UpdateRevealOffer()
    {
        if (!revealOffered) revealOffered = hunter && hidersWon && CountOf(IsHiderToShow) > 0;
    }

    private void ShowMatchResults(MatchResultTable table)
    {
        resultsAutoShown = true;
        if (revealing) EndReveal();

        bool decided = table.Outcome == MatchOutcome.HidersWin || table.Outcome == MatchOutcome.SeekersWin;
        bool won = decided ? (table.Outcome == MatchOutcome.HidersWin) != hunter : resultWon;
        OnlineMatches.ShowMatchEnd(table, new OnlineMatches.MatchEndButtons
        {
            Heading = decided ? (won ? "You won!" : "You lost.") : null,
            Won = won,
            Next = () => lobby.PlayAgainFromRound(),
            Home = () => lobby.GoHomeFromRound(),
            ExtraLabel = revealOffered ? NextPlayerText("NEXT PLAYER") : null,
            Extra = revealOffered ? (Action)(() => RevealStep(+1)) : null,
        });
    }

    // Results, after Close or from looking at the hiders.
    private void ShowResultsAgain()
    {
        if (lastResultsTable is MatchResultTable table)
        {
            ShowMatchResults(table);
            return;
        }

        ShowBanner("Loading the results...", 3f);
        AskResults();
    }

    private void AskResults()
    {
        resultsAsks++;
        resultsAskAt = Time.unscaledTime + 5f;
        if (Net != null && Net.IsConnected) Net.RequestMatchResults((uint)matchId);
    }

    // Every frame once the match is decided.
    private void UpdateAfterMatch()
    {
        if (!resultShown) return;

        bool tableUp = OnlineMatches.ResultsOpen;
        bool offerUp = nextOffer != null;
        SetShown(exitButton, !tableUp);
        SetShown(resultsButton, !tableUp);

        // A seeker's Next player (and "<" while looking), bottom centre.
        if (revealOffered)
        {
            RefreshNextButton();
            ShowWatchControls(!tableUp, revealing && CountOf(IsHiderToShow) > 1);
        }
        ShowWatchLabel(revealing && !tableUp);
        if (dragLayer != null && dragLayer.activeSelf != (revealing && !tableUp && !offerUp)) dragLayer.SetActive(!dragLayer.activeSelf);

        // The hider looked at has gone (left): the next one, or the results when none is left.
        if (revealing && !IsHiderToShow(watching))
        {
            byte next = CycleSlot(watching, +1, IsHiderToShow);
            if (next != 0xFF) WatchHider(next);
            else
            {
                EndReveal();
                ShowResultsAgain();
            }
        }

        if (revealing && !tableUp && !offerUp) WatchKeys();

        // The results come with the match's end; asked again if they did not.
        if (lastResultsTable == null && resultsAsks < 3 && Time.unscaledTime >= resultsAskAt) AskResults();
    }

    // ================= A seeker sees where the hiders hid =================

    /// <summary>Next Player after the match: the next hider (or the one before), or the offer to unlock it first.</summary>
    private void RevealStep(int step)
    {
        if (!revealOffered) return;
        if (!NextPlayerOpen)
        {
            OfferNextPlayer();   // bought, it comes back here (BuyNextPlayer)
            return;
        }

        byte next = !revealing
            ? (IsHiderToShow(lastRevealed) ? lastRevealed : CycleSlot(0xFF, +1, IsHiderToShow))
            : CycleSlot(watching, step, IsHiderToShow);
        if (next == 0xFF)
        {
            ShowBanner("No hiders left to show.", 2f);
            return;
        }
        if (revealing && next == watching)
        {
            ShowBanner("This is the only hider still in.", 2f);
            return;
        }

        if (!revealing) BeginReveal();
        WatchHider(next);
    }

    private void BeginReveal()
    {
        revealing = true;
        CloseNextPlayerOffer();
        OnlineMatches.HideResults();
        if (camCtrl != null)
        {
            revealWasFirstPerson = camCtrl.IsFirstPerson;
            camCtrl.SetFreeLook(false);
            camCtrl.SetFirstPerson(false);          // round the hider, not over a shoulder
            camCtrl.SetWatching(true, frame: true); // the hiders' own framing, closer zoom allowed
        }
    }

    private void WatchHider(byte slot)
    {
        watching = slot;
        lastRevealed = slot;
        NetEntityView view = NetEntityRegistry.Get(slot);
        if (camCtrl != null) camCtrl.SetFollowTarget(view != null ? view.transform : null, glide: true);

        int index = 0, count = 0;
        if (roster != null)
        {
            foreach (MecchaNetClient.RosterEntry e in roster)
            {
                if (!IsHiderToShow(e)) continue;
                count++;
                if (e.Slot <= slot) index++;
            }
        }
        watchLabel.text = count > 1 ? $"HIDER {index}/{count}: {NameOf(slot)}" : $"HIDER: {NameOf(slot)}";
        watchLabel.color = HiderColor;
    }

    private void EndReveal()
    {
        if (!revealing) return;
        revealing = false;
        watching = 0xFF;
        if (camCtrl != null)
        {
            camCtrl.SetFollowTarget(null, glide: true);
            camCtrl.SetWatching(false, frame: false);
            camCtrl.SetFirstPerson(revealWasFirstPerson);
        }
        RestoreRevealTag();
        ShowWatchLabel(false);
        SetShown(prevButton, false);
        if (dragLayer != null) dragLayer.SetActive(false);
    }

    // The tag over the hider looked at says so (RefreshNameTags shows it to the seeker meanwhile).
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

    // The offer over the results screen: this canvas goes over it while the offer is open.
    private void RaiseForOffer(bool raise)
    {
        if (roundCanvasComponent == null || roundCanvasOrder < 0) return;
        roundCanvasComponent.sortingOrder = raise ? OfferCanvasOrder : roundCanvasOrder;
    }
}
