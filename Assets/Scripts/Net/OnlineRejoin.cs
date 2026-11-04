using System;
using System.Globalization;
using UnityEngine;

// Remembers, across an app restart, that an online round was running a moment ago. The server
// holds a dropped player's seat (ReconnectWindowSeconds, 30 s, counted from when it notices the
// drop), so a player whose app closed or crashed mid-round can still get back in: Home offers to
// rejoin while the mark is fresh (OnlineLobby.OfferRejoin). OnlineRound refreshes the mark every
// few seconds and clears it when the round ends.
public static class OnlineRejoin
{
    private const string Key = "OnlineRejoinAtUtc";

    /// <summary>How long after the last mark a rejoin is still offered.</summary>
    public const float WindowSeconds = 40f;

    /// <summary>Marks the round as running now.</summary>
    public static void Mark()
    {
        PlayerPrefs.SetString(Key, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        PlayerPrefs.Save();
    }

    public static void Clear()
    {
        if (!PlayerPrefs.HasKey(Key)) return;
        PlayerPrefs.DeleteKey(Key);
        PlayerPrefs.Save();
    }

    /// <summary>A round was running less than <see cref="WindowSeconds"/> ago.</summary>
    public static bool Pending
    {
        get
        {
            if (!long.TryParse(PlayerPrefs.GetString(Key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                return false;

            double age = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
            return age >= 0 && age < WindowSeconds;
        }
    }
}
