using Meccha.Client;
using UnityEngine;

// Makes the persistent "Net" object holding MecchaNetClient (game socket) and MetaClient
// (profile/shop socket), configured from OnlineConfig. Adapted from the SDK sample's NetBootstrap.
//
// Unlike the sample, nothing is made at launch: MecchaNetClient connects as soon as it starts and
// keeps reconnecting in the background, and Single Play stays fully offline. The object is made
// the first time the player asks for something online, then lives for the rest of the session.
public static class NetBootstrap
{
    public static MecchaNetClient Ensure()
    {
        if (MecchaNetClient.Instance != null) return MecchaNetClient.Instance;

        OnlineConfig config = OnlineConfig.Load();

        // The project has Run In Background off, so an Editor without focus stopped sending and
        // receiving altogether: its player froze for everyone else, and then timed out.
        Application.runInBackground = true;

        var go = new GameObject("Net");
        Object.DontDestroyOnLoad(go);

        // Configure runs before MecchaNetClient.Start, which is where it connects.
        MecchaNetClient net = go.AddComponent<MecchaNetClient>();
        net.Configure(config.host, config.port, config.webSocketPort, config.connectionKey);
        net.Region = config.region;
        net.DisplayName = "Player";   // replaced server-side by the chosen user id once there is one

        MetaClient meta = go.AddComponent<MetaClient>();
        meta.Configure(config.metaPort, config.metaTls, config.metaThumbprint);

        Debug.Log($"[Online] Server {config.host} (UDP {config.port}, WS {config.webSocketPort}, meta {config.metaPort})");
        return net;
    }
}
