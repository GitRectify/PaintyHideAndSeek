using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Part of OnlineRound (OnlineRound.cs has the overview). Connection: riding out a dropped connection, resuming the held seat, and the rejoin mark.
public partial class OnlineRound
{
    // ================= Fields: connection =================

    [Tooltip("After signing in again, how long to wait for the server to give the seat back.")]
    [SerializeField] private float resumeWaitSeconds = 3f;

    [Tooltip("How long offline before the banner points at Home (the SDK keeps retrying).")]
    [SerializeField] private float offerLeaveAfterSeconds = 60f;

    private int matchId;
    private bool matchEndShown;
    private float offlineSince = -1f;   // when the drop was noticed; -1 while connected
    private float resumeDeadline;       // signed in again: the seat's MatchFound is due by this
    private float rejoinMarkAt;

    // ================= Connection =================

    private void WatchConnection()
    {
        float now = Time.unscaledTime;

        if (!Net.IsConnected)
        {
            if (offlineSince < 0f) offlineSince = now;
            resumeDeadline = 0f;
            ShowBanner(now - offlineSince > offerLeaveAfterSeconds
                ? "Still reconnecting... Tap Home to leave."
                : "Connection lost. Reconnecting...", 0.5f);
            return;
        }

        if (resumeDeadline > 0f && now > resumeDeadline)
        {
            // Signed in again, but the seat was not given back: it ran out, or the server restarted.
            resumeDeadline = 0f;
            offlineSince = -1f;
            if (resultShown)
            {
                ShowBanner("Reconnected.", 2f);   // the match was over anyway
                return;
            }

            lobby.EndRoundWithNotice("The match went on without you: you were away too long.");
        }
    }

    // Every handshake, reconnects included. After a drop the seat's MatchFound follows at once.
    private void HandleReauthenticated()
    {
        if (offlineSince >= 0f) resumeDeadline = Time.unscaledTime + resumeWaitSeconds;
    }

    /// <summary>
    /// The server gave the seat back (MatchFound for this match, from OnlineLobby). The SDK has
    /// cleared every remote body; the next snapshot builds them again, and the server re-sends the
    /// phase, the loadout and everyone's paint, so the remote canvases start over. The player's own
    /// strokes from around the drop are sent again.
    /// </summary>
    public void HandleResumed()
    {
        if (!active) return;

        float noticedAt = offlineSince >= 0f ? offlineSince : Time.unscaledTime;
        float since = noticedAt - (NetTuning.DisconnectTimeoutMs / 1000f + 2f);

        resumeDeadline = 0f;
        offlineSince = -1f;
        ClearCanvases();
        QueuePaintReplay(since);
        ResendPaintMaterial();
        ShowBanner("Reconnected.", 2f);
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused && active && !resultShown) OnlineRejoin.Mark();
    }

#if UNITY_EDITOR
    // Editor-only drop tests, from this component's menu in the Inspector (Play mode, on the
    // OnlineLobby object). The link is closed as a lost connection would be — the server holds
    // the seat — and the SDK is told to connect again after the given time, like its own retry.
    [ContextMenu("Test: drop the connection for 20 s (resumes)")]
    private void TestShortDrop() => StartCoroutine(SimulateDrop(20f));

    [ContextMenu("Test: drop the connection for 60 s (seat runs out)")]
    private void TestLongDrop() => StartCoroutine(SimulateDrop(60f));

    private System.Collections.IEnumerator SimulateDrop(float seconds)
    {
        if (Net == null) yield break;
        Debug.Log($"[Online] Test: dropping the connection for {seconds:0} s");
        Net.Disconnect();
        yield return new WaitForSecondsRealtime(seconds);
        if (Net != null) Net.Connect();
    }
#endif
}
