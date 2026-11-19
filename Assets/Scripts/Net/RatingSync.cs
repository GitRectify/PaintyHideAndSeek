using System;
using Meccha.Client;
using Meccha.Shared.Protocol;
using UnityEngine;

// The player's rating of the game (the rate popup, RateUI), saved on the server so the operators
// read it in the admin panel (MetaClient.SubmitRating, SDK 0.9.6).
//
// Rating never signs anyone in: Single Play stays offline. A rating made while signed in goes at
// once; one made otherwise waits here (PlayerPrefs) and goes at the next sign-in, which a player
// who has played online before makes quietly at start (NetBootstrap). The player's own stars and
// words, and everyone's average as the server last told it, are kept here too, for the popup.
public static class RatingSync
{
    private const string PendingKey = "rating.pending";        // 1 while the latest rating is not on the server
    private const string StarsKey = "rating.stars";            // the player's own, 0 = not rated
    private const string CommentKey = "rating.comment";
    private const string AverageKey = "rating.average";        // everyone's, in hundredths (437 = 4.37)
    private const string CountKey = "rating.count";            // how many players have rated; -1 = not known yet

    /// <summary>Raised when the player's rating or everyone's average changes.</summary>
    public static event Action Changed;

    /// <summary>The player's own stars (1-5), 0 when they have not rated the game.</summary>
    public static int MyStars => PlayerPrefs.GetInt(StarsKey, 0);

    public static string MyComment => PlayerPrefs.GetString(CommentKey, "");

    /// <summary>Whether the server has the player's latest rating.</summary>
    public static bool Saved => MyStars > 0 && PlayerPrefs.GetInt(PendingKey, 0) == 0;

    /// <summary>Everyone's average stars, as the server last said; 0 with no ratings.</summary>
    public static float Average => PlayerPrefs.GetInt(AverageKey, 0) / 100f;

    /// <summary>How many players have rated, as the server last said; -1 when it has not said yet.</summary>
    public static int Count => PlayerPrefs.GetInt(CountKey, -1);

    private static bool sending;

    /// <summary>Keeps the rating and sends it now if signed in, else at the next sign-in.</summary>
    public static void Submit(int stars, string comment)
    {
        if (stars < 1 || stars > 5) return;

        PlayerPrefs.SetInt(StarsKey, stars);
        PlayerPrefs.SetString(CommentKey, (comment ?? "").Trim());
        PlayerPrefs.SetInt(PendingKey, 1);
        PlayerPrefs.Save();
        Changed?.Invoke();

        Send();
    }

    /// <summary>
    /// At every sign-in (NetBootstrap): a rating still waiting goes now; otherwise the player's
    /// own and everyone's average are read back (another device may have rated).
    /// </summary>
    public static void HandleSignedIn()
    {
        if (PlayerPrefs.GetInt(PendingKey, 0) == 1 && MyStars > 0) Send();
        else Refresh();
    }

    /// <summary>Asks the server for the player's rating and everyone's average, if signed in.</summary>
    public static void Refresh()
    {
        MetaClient meta = SignedInMeta();
        if (meta == null) return;

        meta.GetRating(reply =>
        {
            if (reply.TimedOut || reply.Error != MetaError.None) return;   // shown as last known
            Keep(reply.Rating, true);
        });
    }

    private static void Send()
    {
        MetaClient meta = SignedInMeta();
        if (meta == null || sending) return;

        sending = true;
        int stars = MyStars;
        string comment = MyComment;

        meta.SubmitRating(stars, comment, reply =>
        {
            sending = false;
            if (reply.TimedOut) return;   // still waiting: the next sign-in tries again

            if (reply.Error == MetaError.None)
            {
                // A newer rating made while this one was on its way stays waiting, and goes next.
                bool newer = MyStars != stars || MyComment != comment;
                if (!newer) PlayerPrefs.SetInt(PendingKey, 0);
                Keep(reply.Rating, !newer);
                if (newer) Send();
                return;
            }

            // The server will never take it (it keeps no ratings, or calls it invalid): stop asking.
            if (reply.Error == MetaError.RatingsUnavailable || reply.Error == MetaError.InvalidRating)
            {
                PlayerPrefs.SetInt(PendingKey, 0);
                PlayerPrefs.Save();
            }
            Debug.Log($"[Online] Rating not saved: {reply.Error}");
        });
    }

    // What the server has. mine: its copy of the player's rating replaces the one kept here.
    private static void Keep(MetaRating rating, bool mine)
    {
        if (mine && rating.Stars >= 1 && rating.Stars <= 5)
        {
            PlayerPrefs.SetInt(StarsKey, rating.Stars);
            PlayerPrefs.SetString(CommentKey, rating.Comment ?? "");
        }
        PlayerPrefs.SetInt(AverageKey, Mathf.Clamp(rating.AverageHundredths, 0, 500));
        PlayerPrefs.SetInt(CountKey, Mathf.Max(0, rating.Count));
        PlayerPrefs.Save();
        Changed?.Invoke();
    }

    private static MetaClient SignedInMeta()
    {
        MecchaNetClient net = MecchaNetClient.Instance;
        MetaClient meta = MetaClient.Instance;
        return net != null && meta != null && !string.IsNullOrEmpty(net.SessionToken) ? meta : null;
    }
}
