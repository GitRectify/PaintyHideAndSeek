// Copied unchanged from the Meccha Client SDK sample "Online integration" (v1.0.0). Don't import
// the sample's own copy of this file as well, or OnlineConfig is defined twice.

using System;
using UnityEngine;

// Where the multiplayer server lives. Read from Resources/NetConfig.json so a build can be pointed
// at another server without touching code; the host can also be overridden on the device from the
// connection screen (kept in PlayerPrefs), because a phone testing against a PC on the LAN cannot
// reach "192.168.15.150" and rebuilding the APK for every IP change is slow.
[Serializable]
public class OnlineConfig
{
    public string host = "192.168.15.150";
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

    /// <summary>
    /// True when the address can only be this device itself. On a phone or an emulator that is
    /// never where the server runs, so the server is looked for on the LAN instead (LanSearch).
    /// </summary>
    public static bool IsThisDevice(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        string h = host.Trim();
        return h == "localhost" || h == "::1" || h.StartsWith("127.", StringComparison.Ordinal);
    }

    /// <summary>A game on a phone or emulator (not the Editor or a PC build, which may share the server's machine).</summary>
    public static bool OnHandheld => Application.isMobilePlatform && !Application.isEditor;

    /// <summary>
    /// Switches both connections to a server found on the LAN and remembers its address for the
    /// next launch, as one typed on the connection screen is.
    /// </summary>
    public static void UseLanServer(Meccha.Client.LanSearch.Found server)
    {
        OnlineConfig config = Load();
        SaveHostOverride(server.Host);

        Meccha.Client.MecchaNetClient net = Meccha.Client.MecchaNetClient.Instance;
        if (net != null)
        {
            net.Disconnect();
            net.Configure(server.Host, server.Info.Port, server.Info.WebSocketPort, net.ConnectionKey);
        }

        Meccha.Client.MetaClient meta = Meccha.Client.MetaClient.Instance;
        if (meta != null) meta.Configure(server.Info.MetaPort, config.metaTls, config.metaThumbprint);

        Debug.Log($"[Online] Using {server.Info.Name}, found on this network at {server.Host} (UDP {server.Info.Port}, meta {server.Info.MetaPort})");
        if (net != null) net.Connect();
    }
}
