// Copied unchanged from the Meccha Client SDK sample "Online integration" (v1.0.0). Don't import
// the sample's own copy of this file as well, or OnlineConfig is defined twice.

using System;
using UnityEngine;

// Where the multiplayer server lives. Read from Resources/NetConfig.json so a build can be pointed
// at another server without touching code; the host can also be overridden on the device from the
// connection screen (kept in PlayerPrefs), because a phone testing against a PC on the LAN cannot
// reach "127.0.0.1" and rebuilding the APK for every IP change is slow.
[Serializable]
public class OnlineConfig
{
    public string host = "127.0.0.1";
    public int port = 7777;            // UDP game socket
    public int webSocketPort = 7778;   // fallback for networks that drop UDP; 0 disables it
    public int metaPort = 7779;        // TCP profile/shop service
    public string connectionKey = "meccha-1";
    public string region = "default";
    public bool metaTls;
    public string metaThumbprint = "";

    private const string ResourceName = "NetConfig";
    private const string HostOverrideKey = "Online.Host";

    public static OnlineConfig Load()
    {
        var config = new OnlineConfig();

        TextAsset json = Resources.Load<TextAsset>(ResourceName);
        if (json != null)
        {
            try
            {
                JsonUtility.FromJsonOverwrite(json.text, config);
            }
            catch (Exception e)
            {
                // A typo in the file should not take Single Play down with it: keep the defaults.
                Debug.LogWarning($"[Online] Resources/{ResourceName}.json is not valid JSON ({e.Message}); using defaults.");
            }
        }

        string overrideHost = PlayerPrefs.GetString(HostOverrideKey, "");
        if (!string.IsNullOrWhiteSpace(overrideHost)) config.host = overrideHost.Trim();

        return config;
    }

    /// <summary>Remembers a host typed on the device; an empty value goes back to NetConfig.json.</summary>
    public static void SaveHostOverride(string host)
    {
        PlayerPrefs.SetString(HostOverrideKey, (host ?? "").Trim());
        PlayerPrefs.Save();
    }
}
