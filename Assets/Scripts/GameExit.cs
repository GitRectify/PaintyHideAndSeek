using System;
using Meccha.Client;
using UnityEngine;

/// <summary>
/// Closes the game for good (Settings &gt; Exit Game), not just back to the menu. An online player
/// leaves their match and disconnects first, so the server is not left holding their seat, and the
/// settings are written out. In the Editor it stops Play mode instead.
/// </summary>
public static class GameExit
{
    /// <summary>What closes the application; the play test puts its own here.</summary>
    public static Action CloseApplication = DefaultClose;

    public static void Quit()
    {
        Debug.Log("[Exit] Closing the game");

        MecchaNetClient net = MecchaNetClient.Instance;
        if (net != null)
        {
            try
            {
                if (net.InMatch) net.LeaveMatch();
                net.Disconnect();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Exit] Leaving the server: {e.Message}");
            }
        }

        PlayerPrefs.Save();
        CloseApplication();
    }

    private static void DefaultClose()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
