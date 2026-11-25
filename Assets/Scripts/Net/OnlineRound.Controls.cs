using System.Collections.Generic;
using System.Text;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// The online round's own controls, over Single Play's HUD (the paint panel stays above them all):
//   * Exit: leaves the match and goes back to the play setup (Multiple Play's menu), without closing
//     the game. The match goes on: the server keeps the seat, and the player rejoins it from their
//     matches (OnlineMatches) while it runs. It asks first.
//   * Everyone's results at the end (OnlineMatches.ShowMatchEnd, the only results screen), and the
//     seconds left before the next shot on the Shoot button.
//   * The hiders' ready list: in the hide phase every hider sees who has pressed Start (the
//     server's S2C.HidersReady; bots are always ready), and a banner when another one does.
// The Shop is the HUD's own Shop button (HomeMenuUI's, in the right-hand column): one per screen
// (2026-11-23; this class had a second one beside Exit).
public partial class OnlineRound
{
    private Button exitButton;
    private GameObject exitConfirm;
    private Text exitConfirmText;
    private Button readyChip;           // "4/5 ready", under the role chip (hiders, hide phase)
    private Text readyChipText;
    private GameObject readyRoster;     // the chip's dialog: every hider, ready or still hiding
    private RectTransform readyRows;
    private ushort readyShown;
    private bool readyKnown;

    private void BuildMatchControls(Transform canvas)
    {
        // Leave, top left once the match is decided (UpdateAfterMatch): while it is played, Leave
        // is in the round menu (PaintboxHud), which asks the same question.
        exitButton = Paintbox.Button(canvas, "Leave", Paintbox.Tone.Glass, "DoorOpen", AskExit, 28f);
        exitButton.name = "ExitMatch";
        Paintbox.Pin((RectTransform)exitButton.transform, new Vector2(0f, 1f), new Vector2(PaintboxHud.EdgeX, -PaintboxHud.EdgeY), new Vector2(196f, 76f));
        exitButton.gameObject.SetActive(false);

        // Design B's Leave match dialog: Leave (red words) and Stay (yellow).
        PaintboxDialog dialog = PaintboxDialog.Build(canvas, "ExitMatchConfirm", "DoorOpen", "Leave this match?", new Vector2(900f, 440f));
        exitConfirm = dialog.Root;
        exitConfirmText = dialog.Body;
        Button leave = dialog.DangerButton("Leave", "LogOut", ConfirmExit, 200f);
        leave.name = "ConfirmExit";
        dialog.BackButton("Stay", Paintbox.Tone.Primary, "Play", () =>
        {
            AudioManager.ButtonTap();
            exitConfirm.SetActive(false);
        }, 190f);
        exitConfirm.SetActive(false);

        // Who is ready: a glass chip under the role chip; a tap lists every hider.
        readyChip = Paintbox.Button(canvas, "", Paintbox.Tone.Glass, "UsersRound", OpenReadyRoster, 24f, 30f);
        readyChip.name = "HidersReady";
        // Under the role chip and the minimap (2026-11-25).
        Paintbox.Pin((RectTransform)readyChip.transform, new Vector2(0f, 1f), new Vector2(PaintboxHud.EdgeX, -(PaintboxMinimap.Bottom + 14f)), new Vector2(200f, 56f));
        readyChipText = readyChip.GetComponentInChildren<Text>(true);
        readyChipText.gameObject.SetActive(true);   // made without words: they come with the roster
        readyChip.gameObject.SetActive(false);

        PaintboxDialog roster = PaintboxDialog.Build(canvas, "HiderReadiness", "Users", "Hider readiness", new Vector2(720f, 640f));
        readyRoster = roster.Root;
        roster.Body.gameObject.SetActive(false);
        readyRows = Paintbox.NewRect(roster.Card, "Seats");
        readyRows.anchorMin = Vector2.zero;
        readyRows.anchorMax = Vector2.one;
        readyRows.offsetMin = new Vector2(40f, 130f);
        readyRows.offsetMax = new Vector2(-40f, -190f);
        VerticalLayoutGroup v = readyRows.gameObject.AddComponent<VerticalLayoutGroup>();
        v.spacing = 10f;
        v.childControlWidth = v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        roster.BackButton("Done", Paintbox.Tone.Primary, "Check", () =>
        {
            AudioManager.ButtonTap();
            readyRoster.SetActive(false);
        }, 180f);
        readyRoster.SetActive(false);
    }

    // Exit while the round is played (after it, Exit and Results: UpdateAfterMatch); the ready list
    // in the hide phase, for hiders.
    private void UpdateMatchControls()
    {
        if (exitButton != null)
        {
            bool playing = !resultShown;
            if (playing && exitButton.gameObject.activeSelf) exitButton.gameObject.SetActive(false);
            if (!playing && exitConfirm.activeSelf) exitConfirm.SetActive(false);
        }

        if (readyChip != null)
        {
            bool show = !hunter && !resultShown && Net != null && Net.Phase == MatchPhase.Hide && PaintboxHud.InRound;
            if (readyChip.gameObject.activeSelf != show)
            {
                readyChip.gameObject.SetActive(show);
                if (show) RefreshReadyPanel();   // then again at each change the server sends
            }
            if (!show && readyRoster.activeSelf) readyRoster.SetActive(false);
        }
    }

    private void HideMatchControls()
    {
        if (exitButton != null) exitButton.gameObject.SetActive(false);
        if (exitConfirm != null) exitConfirm.SetActive(false);
        if (readyChip != null) readyChip.gameObject.SetActive(false);
        if (readyRoster != null) readyRoster.SetActive(false);
        readyShown = 0;
        readyKnown = false;
    }

    // ---- Shop ----

    // The shop over the match (the HUD's Shop button), which goes on under it. A pose bought is in
    // the pose picker at once (the server adds it to this match's loadout), rounds bought join the
    // weapon's, and a weapon bought or equipped is carried straight away.
    // The shop's listing or the equipped weapon changed: carry the weapon now equipped.
    private void HandleShopChangedInMatch()
    {
        if (!active || Net == null) return;
        SelectEquippedWeapon();
    }

    // ---- Exit ----

    private void AskExit()
    {
        AudioManager.ButtonTap();
        if (resultShown)
        {
            lobby.ExitToRoomsFromRound();
            return;
        }

        bool paid = lobby != null && lobby.PaidRoom;
        MatchPhase phase = Net != null ? Net.Phase : MatchPhase.Lobby;
        exitConfirmText.text = phase == MatchPhase.Lobby
            ? (paid ? "The round has not started yet: your entry is refunded." : "You go back to the room list.")
            : "The match goes on without you and your place is kept: rejoin it from your matches while it runs.";
        exitConfirm.SetActive(true);
        exitConfirm.transform.SetAsLastSibling();
    }

    private void ConfirmExit()
    {
        exitConfirm.SetActive(false);
        lobby.ExitToRoomsFromRound();
    }

    // ---- Everyone's results ----

    // S2C.MatchResults: every player's rank, score and coins, the match's one results screen. It
    // shows when it first comes (with the match's end); a copy sent again only refreshes it if it is
    // up, and never brings back results the player closed or left for the hiders' hiding places.
    private void HandleMatchResults(MatchResultTable table)
    {
        if (!active || table.MatchId != (uint)matchId) return;

        if (table.Outcome == MatchOutcome.HidersWin) hidersWon = true;   // the outcome may come only with the results
        lastResultsTable = table;
        if (!resultShown) return;   // shown with the match's end (EnterAfterMatch)

        UpdateRevealOffer();
        if (revealing) return;
        if (!resultsAutoShown || OnlineMatches.ResultsOpen) ShowMatchResults(table);
    }

    // ---- Who is ready (hiders) ----

    private void HandleHidersReady(ushort mask)
    {
        if (!active || hunter) return;

        // A banner for each other real hider who has just pressed Start.
        if (readyKnown)
        {
            ushort fresh = (ushort)(mask & ~readyShown);
            for (byte slot = 0; slot < 16; slot++)
            {
                if ((fresh & (1 << slot)) == 0 || slot == Net.LocalSlot || IsBotSlot(slot)) continue;
                CountReady(mask, out int ready, out int total);
                ShowBanner($"{NameOf(slot)} is ready ({ready}/{total})", 2.5f);
            }
        }

        readyShown = mask;
        readyKnown = true;
        RefreshReadyPanel();
    }

    private void CountReady(ushort mask, out int ready, out int total)
    {
        ready = total = 0;
        if (roster == null) return;
        foreach (MecchaNetClient.RosterEntry e in roster)
        {
            if (e.Role != Role.Fugitive) continue;
            total++;
            if ((mask & (1 << e.Slot)) != 0) ready++;
        }
    }

    // The chip: "2/5 ready"; the roster: one row per hider, ready or still hiding.
    private void RefreshReadyPanel()
    {
        if (readyChip == null || roster == null || Net == null) return;

        ushort mask = Net.HidersReadyMask;
        CountReady(mask, out int ready, out int total);
        readyChipText.text = $"{ready}/{total} ready";

        for (int i = readyRows.childCount - 1; i >= 0; i--)
        {
            readyRows.GetChild(i).gameObject.SetActive(false);
            Destroy(readyRows.GetChild(i).gameObject);
        }
        var lines = new List<MecchaNetClient.RosterEntry>(roster);
        lines.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        foreach (MecchaNetClient.RosterEntry e in lines)
        {
            if (e.Role != Role.Fugitive) continue;
            bool isReady = (mask & (1 << e.Slot)) != 0;
            string who = e.Slot == Net.LocalSlot ? $"{NameOf(e.Slot)} (you)" : NameOf(e.Slot);

            Image row = Paintbox.Rounded(readyRows, "Seat_" + e.Slot, Paintbox.Card, 18f);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 58f;
            Text name = Paintbox.Label(row.rectTransform, who, 26f, Paintbox.Ink, Paintbox.Weight.Bold);
            Paintbox.Fit(name, 16);
            Paintbox.Fill(name.rectTransform, 22f, 200f, 0f, 0f);
            RectTransform state = Paintbox.Tag(row.rectTransform, isReady ? "Ready" : "Hiding",
                                               isReady ? Paintbox.GoodWash : Paintbox.Tab, isReady ? Paintbox.Good : Paintbox.Muted,
                                               isReady ? "Check" : "Clock", 22f);
            state.name = isReady ? "Ready" : "Hiding";
            state.anchorMin = state.anchorMax = state.pivot = new Vector2(1f, 0.5f);
            state.anchoredPosition = new Vector2(-12f, 0f);
        }
    }

    private void OpenReadyRoster()
    {
        AudioManager.ButtonTap();
        RefreshReadyPanel();
        readyRoster.SetActive(true);
        readyRoster.transform.SetAsLastSibling();
    }
}
