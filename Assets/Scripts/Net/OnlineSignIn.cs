using System;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Multiple Play's first step: connect, sign in, and on the first run choose the public player name
// and avatar (the diagram's "Update Avatar & userId when first"). Adapted from the sign-in part of
// the SDK sample's OnlineFlow; the role, room and lobby screens come in later phases.
//
// Every value shown comes from the server: the profile and balance from the meta service. Single
// Play never reaches this.
public class OnlineSignIn : MonoBehaviour
{
    private enum Step { Idle, Connecting, Failed, Profile }

    [Tooltip("How long to wait for the server to sign us in before offering to retry.")]
    [SerializeField] private float connectTimeoutSeconds = 12f;

    private static MecchaNetClient Net => MecchaNetClient.Instance;
    private static MetaClient Meta => MetaClient.Instance;

    private CharacterSelect characters;

    private Step step;
    private float connectDeadline;
    private bool hooked;
    private Action<MetaProfile> onSignedIn;
    private Action onCancelled;

    // Bumped whenever the player backs out, so an answer to a request made on an abandoned screen
    // (a slow GetProfile, say) cannot pop that screen back up.
    private int flowVersion;

    private MetaProfile profile;

    // ---- UI ---------------------------------------------------------------------------------
    private GameObject statusScreen, profileScreen, hostRow;
    private Text statusTitle, statusMessage, profileError;
    private InputField hostInput, userIdInput;
    private Button statusRetry;
    private RectTransform avatarRow;
    private int chosenAvatar;
    private readonly List<Button> avatarButtons = new List<Button>();
    private readonly List<int> avatarIds = new List<int>();

    /// <summary>
    /// Signs in (showing the first-run screen when the player has no name yet), then calls
    /// <paramref name="signedIn"/> with the server's profile; <paramref name="cancelled"/> when the
    /// player backs out.
    /// </summary>
    public static void Open(Action<MetaProfile> signedIn, Action cancelled)
    {
        OnlineSignIn flow = FindAnyObjectByType<OnlineSignIn>();
        if (flow == null) flow = new GameObject("OnlineSignIn").AddComponent<OnlineSignIn>();
        flow.Begin(signedIn, cancelled);
    }

    // ================= Lifecycle =================

    private void Awake()
    {
        characters = FindAnyObjectByType<CharacterSelect>();
        BuildUI();
        HideScreens();
    }

    private void OnDestroy() => Unhook();

    private void Update()
    {
        if (step == Step.Connecting && Time.unscaledTime > connectDeadline)
        {
            Fail($"Couldn't reach the server at {Net.Host}.\nCheck that it is running and the address is right.");
        }
    }

    private void Begin(Action<MetaProfile> signedIn, Action cancelled)
    {
        flowVersion++;
        onSignedIn = signedIn;
        onCancelled = cancelled;

        NetBootstrap.Ensure();
        Hook();
        EnsureSignedIn();
    }

    private void Hook()
    {
        if (hooked || Net == null) return;
        hooked = true;

        Net.OnAuthenticated += HandleAuthenticated;
        Net.OnRejected += HandleRejected;
    }

    private void Unhook()
    {
        if (!hooked || Net == null) return;
        hooked = false;

        Net.OnAuthenticated -= HandleAuthenticated;
        Net.OnRejected -= HandleRejected;
    }

    // ================= Sign-in =================

    private void EnsureSignedIn()
    {
        if (Net.IsConnected && !string.IsNullOrEmpty(Net.SessionToken))
        {
            LoadProfile();
            return;
        }

        ShowStatus("Connecting", $"Connecting to {Net.Host}...", false);
        step = Step.Connecting;
        connectDeadline = Time.unscaledTime + connectTimeoutSeconds;
        Net.Connect();   // no-op while an attempt is already under way
    }

    private void HandleAuthenticated()
    {
        if (step == Step.Connecting) LoadProfile();
    }

    // Player-facing texts from the SDK reference (MecchaNetClient.OnRejected).
    private void HandleRejected(string reason)
    {
        if (step != Step.Connecting) return;

        if (reason.StartsWith("server busy", StringComparison.Ordinal))
        {
            // The SDK retries by itself; give it another full wait.
            statusMessage.text = "Server busy, retrying...";
            connectDeadline = Time.unscaledTime + connectTimeoutSeconds;
        }
        else if (reason == "session expired")
        {
            // The SDK signs in again by itself.
        }
        else if (reason == "signed in on another device")
        {
            Fail("You signed in on another device.");
        }
        else if (reason.StartsWith("protocol", StringComparison.Ordinal))
        {
            Fail("Please update the app.");
        }
        else
        {
            Fail("Couldn't sign in. Try again.");
        }
    }

    private void Fail(string message)
    {
        step = Step.Failed;
        ShowStatus("Can't play online", message, true);
    }

    private void Retry()
    {
        string typed = hostInput.text.Trim();

        if (typed.Length > 0 && typed != Net.Host)
        {
            // A new address: drop whatever session the old one had and sign in there.
            OnlineConfig.SaveHostOverride(typed);
            OnlineConfig config = OnlineConfig.Load();
            Net.Disconnect();
            Net.Configure(config.host, config.port, config.webSocketPort, config.connectionKey);
        }
        else if (!Net.IsConnected)
        {
            // Cancels the client's own backoff timer (up to 30 s) so the retry happens now.
            Net.Disconnect();
        }

        EnsureSignedIn();
    }

    private void Back()
    {
        AudioManager.ButtonTap();
        flowVersion++;
        step = Step.Idle;
        HideScreens();

        // Not signed in and the player gave up: stop the client's background reconnects too.
        if (Net != null && string.IsNullOrEmpty(Net.SessionToken)) Net.Disconnect();

        onCancelled?.Invoke();
    }

    private void LoadProfile()
    {
        step = Step.Connecting;
        connectDeadline = float.MaxValue;   // signed in; the meta call has its own timeout
        ShowStatus("Connecting", "Loading your profile...", false);

        int version = flowVersion;
        Meta.GetProfile(reply =>
        {
            if (version != flowVersion) return;

            if (reply.TimedOut || reply.Error != MetaError.None)
            {
                Fail(MetaErrorText(reply.Error, reply.TimedOut));
                return;
            }

            profile = reply.Profile;
            if (string.IsNullOrEmpty(profile.UserId)) ShowProfileScreen();
            else Finish();
        });
    }

    private void Finish()
    {
        step = Step.Idle;
        HideScreens();
        onSignedIn?.Invoke(profile);
    }

    // ---- First run: user id and avatar --------------------------------------------------------

    private void ShowProfileScreen()
    {
        step = Step.Profile;
        HideScreens();
        profileScreen.SetActive(true);
        profileError.text = "";
        userIdInput.text = "";

        int[] owned = profile.OwnedAvatars != null && profile.OwnedAvatars.Length > 0
            ? profile.OwnedAvatars
            : new[] { profile.AvatarId };

        int current = characters != null ? characters.CurrentIndex : 0;
        chosenAvatar = Array.IndexOf(owned, current) >= 0 ? current : owned[0];
        BuildAvatarButtons(owned);
    }

    private void BuildAvatarButtons(int[] owned)
    {
        foreach (Button b in avatarButtons) Destroy(b.gameObject);
        avatarButtons.Clear();
        avatarIds.Clear();

        const float width = 240f;
        float x0 = -(owned.Length - 1) * (width + 20f) * 0.5f;

        for (int i = 0; i < owned.Length; i++)
        {
            int id = owned[i];
            Button b = OnlineUI.NewButton(avatarRow, AvatarName(id), new Vector2(x0 + i * (width + 20f), 0f),
                                          new Vector2(width, 90f), OnlineUI.Gray, () => PickAvatar(id));
            avatarButtons.Add(b);
            avatarIds.Add(id);
        }

        PickAvatar(chosenAvatar);
    }

    private void PickAvatar(int id)
    {
        chosenAvatar = id;
        for (int i = 0; i < avatarButtons.Count; i++)
        {
            avatarButtons[i].targetGraphic.color = avatarIds[i] == id ? OnlineUI.Green : OnlineUI.Gray;
        }
    }

    // Avatar id = CharacterSelect index (Player = 0, Player2 = 1).
    private string AvatarName(int id)
    {
        if (characters != null && characters.buttonLabels != null && id >= 0 && id < characters.buttonLabels.Length)
            return characters.buttonLabels[id];
        return "Avatar " + (id + 1);
    }

    private void SubmitProfile()
    {
        AudioManager.ButtonTap();
        profileError.text = "Saving...";
        int version = flowVersion;

        Meta.UpdateProfile(userIdInput.text.Trim(), chosenAvatar, reply =>
        {
            if (version != flowVersion) return;

            if (!reply.TimedOut && reply.Error == MetaError.None)
            {
                profile = reply.Profile;
                Finish();
                return;
            }

            profileError.text = ProfileErrorText(reply);
        });
    }

    // ================= UI =================

    private void BuildUI()
    {
        // Above CharacterSelect's buttons (45) and the result screen (40).
        Transform canvas = OnlineUI.CreateCanvas("OnlineSignInCanvas", transform, 50).transform;

        // Connecting / failed.
        RectTransform p = OnlineUI.Modal(canvas, "StatusScreen", new Vector2(980f, 600f));
        statusScreen = p.parent.gameObject;
        statusTitle = OnlineUI.NewLabel(p, "", 52, new Vector2(0f, 220f), new Vector2(900f, 80f), TextAnchor.MiddleCenter, true);
        statusMessage = OnlineUI.NewLabel(p, "", 34, new Vector2(0f, 80f), new Vector2(880f, 180f));
        hostRow = new GameObject("HostRow", typeof(RectTransform));
        hostRow.transform.SetParent(p, false);
        OnlineUI.Place((RectTransform)hostRow.transform, new Vector2(0f, -70f), new Vector2(880f, 90f));
        OnlineUI.NewLabel(hostRow.transform, "Server", 32, new Vector2(-330f, 0f), new Vector2(180f, 80f), TextAnchor.MiddleRight);
        hostInput = OnlineUI.NewInput(hostRow.transform, "IP address or host name", new Vector2(80f, 0f), new Vector2(560f, 84f));
        statusRetry = OnlineUI.NewButton(p, "RETRY", new Vector2(-180f, -210f), new Vector2(300f, 100f), OnlineUI.Green, Retry);
        OnlineUI.NewButton(p, "BACK", new Vector2(180f, -210f), new Vector2(300f, 100f), OnlineUI.Gray, Back);

        // First run: user id + avatar.
        p = OnlineUI.Modal(canvas, "ProfileScreen", new Vector2(1060f, 760f));
        profileScreen = p.parent.gameObject;
        OnlineUI.NewLabel(p, "Choose your player name", 52, new Vector2(0f, 300f), new Vector2(980f, 80f), TextAnchor.MiddleCenter, true);
        Text rules = OnlineUI.NewLabel(p, $"Everyone sees this name. {UserIds.MinLength}-{UserIds.MaxLength} letters (English, Russian or Chinese), digits, _ or ., starting with a letter.",
                                       28, new Vector2(0f, 230f), new Vector2(960f, 70f));
        rules.color = OnlineUI.Muted;
        userIdInput = OnlineUI.NewInput(p, "Player name", new Vector2(0f, 135f), new Vector2(660f, 92f));
        userIdInput.characterLimit = UserIds.MaxLength;
        Text avatarTitle = OnlineUI.NewLabel(p, "Avatar", 34, new Vector2(0f, 30f), new Vector2(600f, 60f));
        avatarTitle.color = OnlineUI.Muted;
        avatarRow = (RectTransform)new GameObject("AvatarRow", typeof(RectTransform)).transform;
        avatarRow.SetParent(p, false);
        OnlineUI.Place(avatarRow, new Vector2(0f, -60f), new Vector2(1000f, 100f));
        profileError = OnlineUI.NewLabel(p, "", 30, new Vector2(0f, -170f), new Vector2(960f, 70f));
        profileError.color = OnlineUI.Warning;
        OnlineUI.NewButton(p, "CONFIRM", new Vector2(-180f, -285f), new Vector2(300f, 100f), OnlineUI.Green, SubmitProfile);
        OnlineUI.NewButton(p, "BACK", new Vector2(180f, -285f), new Vector2(300f, 100f), OnlineUI.Gray, Back);
    }

    private void HideScreens()
    {
        statusScreen.SetActive(false);
        profileScreen.SetActive(false);
    }

    private void ShowStatus(string title, string message, bool failed)
    {
        HideScreens();
        statusScreen.SetActive(true);
        statusTitle.text = title;
        statusMessage.text = message;
        hostRow.SetActive(failed);
        statusRetry.gameObject.SetActive(failed);
        if (failed && Net != null) hostInput.text = Net.Host;
    }

    // ================= Text =================

    private static string MetaErrorText(MetaError error, bool timedOut)
    {
        if (timedOut) return "The server's profile service did not answer.\nIt may be blocked by a firewall (TCP port 7779 by default).";

        switch (error)
        {
            case MetaError.NotSignedIn:
            case MetaError.BadSession: return "Your session has expired. Tap Retry to sign in again.";
            case MetaError.VersionMismatch: return "This app version does not match the server. Please update.";
            case MetaError.RateLimited: return "Too many requests. Wait a moment and try again.";
            default: return $"The server could not answer ({error}).";
        }
    }

    private static string ProfileErrorText(MetaClient.ProfileReply reply)
    {
        if (reply.TimedOut) return MetaErrorText(MetaError.None, true);

        switch (reply.Error)
        {
            case MetaError.UserIdTaken: return "That name is already taken. Try another.";
            case MetaError.AvatarNotOwned: return "You don't own that avatar.";
            case MetaError.InvalidUserId:
                switch (reply.Problem)
                {
                    case UserIdProblem.Empty: return "Enter a name.";
                    case UserIdProblem.TooShort: return $"Use at least {UserIds.MinLength} characters.";
                    case UserIdProblem.TooLong: return $"Use at most {UserIds.MaxLength} characters.";
                    case UserIdProblem.BadCharacter: return "Use only letters, digits, _ and .";
                    case UserIdProblem.MustStartWithLetter: return "Start with a letter.";
                    case UserIdProblem.Reserved: return "That name is reserved. Try another.";
                    case UserIdProblem.MixedScripts: return "Don't mix Latin and Cyrillic letters in one name.";
                    default: return "That name isn't allowed.";
                }
            default: return MetaErrorText(reply.Error, false);
        }
    }
}
