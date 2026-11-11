using Meccha.Client;
using UnityEngine;

// Makes the persistent "Net" object holding MecchaNetClient (game socket) and MetaClient
// (profile/shop socket), configured from OnlineConfig. Adapted from the SDK sample's NetBootstrap.
//
// Unlike the sample, nothing is made at launch for a player who has never gone online:
// MecchaNetClient connects as soon as it starts and keeps reconnecting in the background, and
// Single Play stays fully offline. The object is made the first time the player asks for
// something online, then lives for the rest of the session.
//
// A player who has signed in before is signed in again quietly when Home first shows
// (QuietSignIn), so the coin balance is there without tapping anything. It shows no screen, asks
// nothing, and gives up after a few seconds when the server cannot be reached: the balance then
// stays unknown ("-") and Single Play is as offline as ever.
public static class NetBootstrap
{
    private const string SignedInBeforeKey = "Online.SignedInBefore";

    /// <summary>How long the quiet sign-in may take before it stops trying.</summary>
    public const float QuietSignInSeconds = 15f;

    /// <summary>Off in the play test, which signs in when it means to.</summary>
    public static bool QuietSignInEnabled = true;

    /// <summary>For the play test: the server to use instead of NetConfig.json, and "has signed in before" without the saved mark.</summary>
    public static OnlineConfig TestConfig;
    public static bool TestSignedInBefore;

    private static bool quietTried;

    /// <summary>True once the player has asked for something online this session (Multiple Play, the shop, the wallet).</summary>
    public static bool Asked { get; private set; }

    /// <summary>True when this device has signed in to the server before.</summary>
    public static bool SignedInBefore => TestSignedInBefore || PlayerPrefs.GetInt(SignedInBeforeKey, 0) == 1;

    public static MecchaNetClient Ensure()
    {
        Asked = true;
        return Create();
    }

    /// <summary>
    /// Once per launch, when Home first shows: signs a returning player in without any screen, so
    /// the balance is known. Does nothing for a player who has never gone online, when an online
    /// round is waiting to be rejoined (that has its own prompt), or when something online is
    /// already under way.
    /// </summary>
    public static void QuietSignIn()
    {
        if (quietTried) return;
        quietTried = true;

        if (!QuietSignInEnabled || MecchaNetClient.Instance != null) return;
        if (!SignedInBefore || OnlineRejoin.Pending) return;

        MecchaNetClient net = Create();
        net.gameObject.AddComponent<QuietSignInWatch>();
        Debug.Log("[Online] Signing in quietly for the coin balance");
    }

    private static MecchaNetClient Create()
    {
        if (MecchaNetClient.Instance != null) return MecchaNetClient.Instance;

        OnlineConfig config = TestConfig ?? OnlineConfig.Load();

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
        net.OnAuthenticated += RememberSignIn;

        MetaClient meta = go.AddComponent<MetaClient>();
        meta.Configure(config.metaPort, config.metaTls, config.metaThumbprint);

        Debug.Log($"[Online] Server {config.host} (UDP {config.port}, WS {config.webSocketPort}, meta {config.metaPort})");
        return net;
    }

    private static void RememberSignIn()
    {
        if (PlayerPrefs.GetInt(SignedInBeforeKey, 0) == 1) return;
        PlayerPrefs.SetInt(SignedInBeforeKey, 1);
        PlayerPrefs.Save();
    }

    // Stops a quiet sign-in that is getting nowhere. The SDK's client retries for ever, which is
    // right once a player is online and wrong for someone who only opened the game: left alone it
    // would knock on an unreachable server every 30 seconds all through Single Play.
    private sealed class QuietSignInWatch : MonoBehaviour
    {
        private float giveUpAt;

        private void Start() => giveUpAt = Time.unscaledTime + QuietSignInSeconds;

        private void Update()
        {
            MecchaNetClient net = MecchaNetClient.Instance;

            // Signed in, or the player has asked for something online meanwhile: theirs to drive now.
            if (net == null || Asked || !string.IsNullOrEmpty(net.SessionToken))
            {
                Destroy(this);
                return;
            }

            if (Time.unscaledTime < giveUpAt) return;

            net.Disconnect();   // also cancels the retry it had scheduled
            Debug.Log("[Online] The server was not reached; staying offline until something online is asked for");
            Destroy(this);
        }
    }
}
