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
    private Text readyPanel;
    private ushort readyShown;
    private bool readyKnown;

    private void BuildMatchControls(Transform canvas)
    {
        // Exit, top left: nothing else of the round's HUD is there.
        exitButton = OnlineUI.NewButton(canvas, "EXIT", Vector2.zero, new Vector2(180f, 76f), OnlineUI.Red, AskExit);
        exitButton.name = "ExitMatch";
        RectTransform ert = (RectTransform)exitButton.transform;
        ert.anchorMin = ert.anchorMax = ert.pivot = new Vector2(0f, 1f);
        ert.anchoredPosition = new Vector2(24f, -24f);
        exitButton.gameObject.SetActive(false);

        RectTransform panel = OnlineUI.Modal(canvas, "ExitMatchConfirm", new Vector2(900f, 460f));
        exitConfirm = panel.parent.gameObject;
        OnlineUI.NewLabel(panel, "Leave this match?", 50, new Vector2(0f, 150f), new Vector2(820f, 80f), TextAnchor.MiddleCenter, true);
        exitConfirmText = OnlineUI.NewLabel(panel, "", 32, new Vector2(0f, 30f), new Vector2(820f, 150f));
        Button leave = OnlineUI.NewButton(panel, "LEAVE", new Vector2(-180f, -140f), new Vector2(300f, 96f), OnlineUI.Red, ConfirmExit);
        leave.name = "ConfirmExit";
        OnlineUI.NewButton(panel, "STAY", new Vector2(180f, -140f), new Vector2(300f, 96f), OnlineUI.Gray, () =>
        {
            AudioManager.ButtonTap();
            exitConfirm.SetActive(false);
        });
        exitConfirm.SetActive(false);

        // Who is ready, under Exit.
        readyPanel = OnlineUI.NewLabel(canvas, "", 30, Vector2.zero, new Vector2(520f, 400f), TextAnchor.UpperLeft, false);
        readyPanel.name = "HidersReady";
        RectTransform rrt = readyPanel.rectTransform;
        rrt.anchorMin = rrt.anchorMax = rrt.pivot = new Vector2(0f, 1f);
        rrt.anchoredPosition = new Vector2(28f, -120f);
        Outline outline = readyPanel.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);
        readyPanel.gameObject.SetActive(false);
    }

    // Exit while the round is played (after it, Exit and Results: UpdateAfterMatch); the ready list
    // in the hide phase, for hiders.
    private void UpdateMatchControls()
    {
        if (exitButton != null)
        {
            bool playing = !resultShown;
            if (playing && !exitButton.gameObject.activeSelf) exitButton.gameObject.SetActive(true);
            if (!playing && exitConfirm.activeSelf) exitConfirm.SetActive(false);
        }

        if (readyPanel != null)
        {
            bool show = !hunter && !resultShown && Net != null && Net.Phase == MatchPhase.Hide;
            if (readyPanel.gameObject.activeSelf != show)
            {
                readyPanel.gameObject.SetActive(show);
                if (show) RefreshReadyPanel();   // then again at each change the server sends
            }
        }
    }

    private void HideMatchControls()
    {
        if (exitButton != null) exitButton.gameObject.SetActive(false);
        if (exitConfirm != null) exitConfirm.SetActive(false);
        if (readyPanel != null) readyPanel.gameObject.SetActive(false);
        readyShown = 0;
        readyKnown = false;
    }

    // ---- Shop ----

    // The shop over the match (the HUD's Shop button), which goes on under it. A pose bought is in
    // the pose picker at once (the server adds it to this match's loadout), bullets join the stock,
    // and a weapon bought or equipped is carried straight away.
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

    // "Ready to start: 2/5" and one line per hider: ready, or still hiding.
    private void RefreshReadyPanel()
    {
        if (readyPanel == null || roster == null || Net == null) return;

        ushort mask = Net.HidersReadyMask;
        CountReady(mask, out int ready, out int total);

        var text = new StringBuilder();
        text.Append($"<b>Ready to start: {ready}/{total}</b>");
        var lines = new List<MecchaNetClient.RosterEntry>(roster);
        lines.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        foreach (MecchaNetClient.RosterEntry e in lines)
        {
            if (e.Role != Role.Fugitive) continue;
            bool isReady = (mask & (1 << e.Slot)) != 0;
            string who = e.Slot == Net.LocalSlot ? $"{NameOf(e.Slot)} (you)" : NameOf(e.Slot);
            text.Append('\n').Append(isReady ? $"<color=#7CF29A>{who}: ready</color>" : $"<color=#FFFFFFB0>{who}: hiding...</color>");
        }
        readyPanel.text = text.ToString();
    }
}
