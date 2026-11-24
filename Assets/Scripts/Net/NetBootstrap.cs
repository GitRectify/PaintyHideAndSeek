using Meccha.Client;
using Meccha.Shared.Protocol;
using UnityEngine;

// Makes the persistent "Net" object holding MecchaNetClient (game socket) and MetaClient
// (profile/shop socket), configured from OnlineConfig. Adapted from the SDK sample's NetBootstrap.
//
// Unlike the sample, nothing is made at launch for a player who has never gone online:
// MecchaNetClient connects as soon as it starts and keeps reconnecting in the background, and
// Single Play stays fully offline. The object is made the first time the player asks for
// something online, then lives for the rest of the session.
//
// A player who has signed in before is signed in again automatically, with the identity this
// device signed in with before (QuietSignIn): when Home first shows, and again whenever Home comes
// back while they are not signed in (the server may have been down at launch), at most once a
// minute. It shows no screen, asks nothing, and gives up after a few seconds when the server
// cannot be reached: the balance then stays unknown ("-") and Single Play is as offline as ever
// (nothing is tried during a Single Play round). Once signed in, the profile is fetched too, so
// Multiple Play opens on it without a "Loading your profile" screen (OnlineSignIn).
public static class NetBootstrap
{
    private const string SignedInBeforeKey = "Online.SignedInBefore";

    /// <summary>How long the quiet sign-in may take before it stops trying.</summary>
    public const float QuietSignInSeconds = 15f;

    /// <summary>The least time between two quiet sign-in attempts.</summary>
    public const float QuietRetrySeconds = 60f;

    /// <summary>How long a profile fetched at sign-in is used as it is (OnlineSignIn).</summary>
    public const float ProfileFreshSeconds = 120f;

    /// <summary>Off in the play test, which signs in when it means to.</summary>
    public static bool QuietSignInEnabled = true;

    /// <summary>For the play test: the server to use instead of NetConfig.json, and "has signed in before" without the saved mark.</summary>
    public static OnlineConfig TestConfig;
    public static bool TestSignedInBefore;

    private static float nextQuietAt;
    private static MetaProfile signedInProfile;
    private static float signedInProfileAt = -1f;

    /// <summary>
    /// The profile fetched right after the latest sign-in, if it is recent and the player has
    /// chosen their names; Multiple Play starts from it instead of asking again.
    /// </summary>
    public static bool TryFreshProfile(out MetaProfile profile)
    {
        profile = signedInProfile;
        MecchaNetClient net = MecchaNetClient.Instance;
        return signedInProfileAt >= 0f && Time.unscaledTime - signedInProfileAt < ProfileFreshSeconds &&
               net != null && !string.IsNullOrEmpty(net.SessionToken) && !string.IsNullOrEmpty(profile.UserId);
    }

    /// <summary>The profile changed (names saved): what Multiple Play starts from next.</summary>
    public static void NoteProfile(MetaProfile profile)
    {
        signedInProfile = profile;
        signedInProfileAt = Time.unscaledTime;
        Shoes.Note(profile);    // the pair the account wears
    }

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
    /// When Home shows: signs a returning player in without any screen, so the balance is known
    /// and Multiple Play opens at once. Does nothing for a player who has never gone online, when
    /// an online round is waiting to be rejoined (that has its own prompt), when they are signed
    /// in or something online is under way (the SDK reconnects by itself then), or within a minute
    /// of the last try.
    /// </summary>
    public static void QuietSignIn()
    {
        if (!QuietSignInEnabled || !SignedInBefore || OnlineRejoin.Pending) return;
        if (Time.unscaledTime < nextQuietAt) return;

        MecchaNetClient existing = MecchaNetClient.Instance;
        if (existing != null && (Asked || existing.IsConnected || !string.IsNullOrEmpty(existing.SessionToken))) return;
        if (existing != null && existing.GetComponent<QuietSignInWatch>() != null) return;   // trying now

        nextQuietAt = Time.unscaledTime + QuietRetrySeconds;
        MecchaNetClient net = Create();
        net.gameObject.AddComponent<QuietSignInWatch>();
        if (existing != null) net.Connect();   // an earlier try gave up and disconnected it
        Debug.Log("[Online] Signing in automatically");
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
        net.OnAuthenticated += FetchProfile;
        net.OnAuthenticated += RatingSync.HandleSignedIn;   // a rating made offline goes now

        MetaClient meta = go.AddComponent<MetaClient>();
        meta.Configure(config.metaPort, config.metaTls, config.metaThumbprint);

        // The game's language goes with each sign-in, for the admin panel (SDK 0.9.5); a change
        // in Settings reaches the server at the next one.
        meta.Language = L.Code(L.Current);
        L.Changed += () => { if (meta != null) meta.Language = L.Code(L.Current); };

        Debug.Log($"[Online] Server {config.host} (UDP {config.port}, WS {config.webSocketPort}, meta {config.metaPort})");
        return net;
    }

    // Every sign-in: the profile, for Multiple Play to open on (TryFreshProfile).
    private static void FetchProfile()
    {
        signedInProfileAt = -1f;
        MetaClient meta = MetaClient.Instance;
        if (meta == null) return;
        meta.GetProfile(reply =>
        {
            if (!reply.TimedOut && reply.Error == MetaError.None) NoteProfile(reply.Profile);
        });
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
    //
    // When the address it has does not answer (or a phone or emulator has only its own 192.168.15.150),
    // it looks for the server on the LAN once (LanSearch) and switches to it, as the sign-in
    // screen does: the PC's address on a LAN can change from one day to the next.
    private sealed class QuietSignInWatch : MonoBehaviour
    {
        private const float LanSearchAfterSeconds = 4f;

        private float giveUpAt, lanSearchAt;
        private LanSearch lanSearch;
        private bool lanSearched;

        private void Start()
        {
            giveUpAt = Time.unscaledTime + QuietSignInSeconds;
            MecchaNetClient net = MecchaNetClient.Instance;
            bool pointedAtItself = net != null && OnlineConfig.OnHandheld && OnlineConfig.IsThisDevice(net.Host);
            lanSearchAt = Time.unscaledTime + (pointedAtItself ? 0f : LanSearchAfterSeconds);
        }

        private void OnDestroy() => lanSearch?.Dispose();

        private void Update()
        {
            MecchaNetClient net = MecchaNetClient.Instance;

            // Signed in, or the player has asked for something online meanwhile: theirs to drive now.
            if (net == null || Asked || !string.IsNullOrEmpty(net.SessionToken))
            {
                Destroy(this);
                return;
            }

            PumpLanSearch(net);

            if (Time.unscaledTime < giveUpAt) return;

            net.Disconnect();   // also cancels the retry it had scheduled
            Debug.Log("[Online] The server was not reached; staying offline until something online is asked for");
            Destroy(this);
        }

        private void PumpLanSearch(MecchaNetClient net)
        {
            if (lanSearch == null)
            {
                if (lanSearched || net.IsConnected || Time.unscaledTime < lanSearchAt) return;
                lanSearched = true;
                lanSearch = new LanSearch(net.Port, net.ConnectionKey);
                return;
            }

            lanSearch.Poll();
            bool found = lanSearch.TryGetBest(out LanSearch.Found server);
            if (!found && !lanSearch.Done) return;

            lanSearch.Dispose();
            lanSearch = null;
            if (!found || net.IsConnected) return;

            OnlineConfig.UseLanServer(server);
            giveUpAt = Mathf.Max(giveUpAt, Time.unscaledTime + 8f);
        }
    }
}
