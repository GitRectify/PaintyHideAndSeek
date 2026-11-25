using System;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Multiple Play's first step: connect, sign in, and on the first run choose the username (unique,
// for signing in and being found), the display name (what other players see) and the avatar (the
// diagram's "Update Avatar & userId when first"). The same screen changes both names later
// (OpenAccount, from the room screen's PROFILE). Adapted from the sign-in part of the SDK sample's
// OnlineFlow.
//
// Every value shown comes from the server: the profile and balance from the meta service. Single
// Play never reaches this.
public class OnlineSignIn : MonoBehaviour
{
    private enum Step { Idle, Connecting, Failed, Profile }

    [Tooltip("How long to wait for the server to sign us in before offering to retry.")]
    [SerializeField] private float connectTimeoutSeconds = 12f;

    [Tooltip("How long the address given may stay silent before the server is looked for on this network.")]
    [SerializeField] private float lanSearchAfterSeconds = 4f;

    // LAN discovery (LanSearch): the server on this network when the address given is not it,
    // straight away on a phone or emulator pointed at itself, otherwise once that address has
    // been silent for a few seconds. Once per sign-in attempt.
    private LanSearch lanSearch;
    private bool lanSearched;
    private float lanSearchAt;

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
    private GameObject statusScreen, profileScreen, hostRow, profileTabs;
    private Text statusTitle, statusMessage, profileError, profileTitle, avatarTitle, artName;
    private Image artPicture, statusIcon;
    private PaintboxPage profilePage;
    private Button saveButton;
    private InputField hostInput, userIdInput, displayNameInput;
    private Button statusRetry;
    private RectTransform avatarRow;
    private int chosenAvatar;
    private readonly List<Button> avatarButtons = new List<Button>();
    private readonly List<int> avatarIds = new List<int>();

    // The account screen (OpenAccount): the same screen over the room screen, changing the names.
    private bool editing;
    private Action<MetaProfile> onEdited;
    private Action onEditClosed;

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

    /// <summary>
    /// The profile screen to change the username and display name of a signed-in player.
    /// <paramref name="saved"/> gets the server's profile once it has taken the change;
    /// <paramref name="closed"/> runs when the screen goes, saved or not.
    /// </summary>
    public static void OpenAccount(MetaProfile current, Action<MetaProfile> saved, Action closed)
    {
        OnlineSignIn flow = FindAnyObjectByType<OnlineSignIn>();
        if (flow == null) flow = new GameObject("OnlineSignIn").AddComponent<OnlineSignIn>();
        flow.BeginEdit(current, saved, closed);
    }

    // ================= Lifecycle =================

    private void Awake()
    {
        characters = FindAnyObjectByType<CharacterSelect>();
        BuildUI();
        HideScreens();
    }

    private void OnDestroy()
    {
        Unhook();
        lanSearch?.Dispose();
    }

    private void Update()
    {
        if (step == Step.Connecting) PumpLanSearch();

        if (step == Step.Connecting && Time.unscaledTime > connectDeadline)
        {
            Fail(lanSearched
                ? $"Couldn't reach the server at {Net.Host}, and none answered on this network.\nCheck that it is running and on the same network."
                : $"Couldn't reach the server at {Net.Host}.\nCheck that it is running and the address is right.");
        }
    }

    private void PumpLanSearch()
    {
        if (lanSearch == null)
        {
            if (lanSearched || Net.IsConnected || Time.unscaledTime < lanSearchAt) return;

            lanSearched = true;
            lanSearch = new LanSearch(Net.Port, Net.ConnectionKey);
            statusMessage.text = "Looking for a server on this network...";
            connectDeadline = Mathf.Max(connectDeadline, Time.unscaledTime + LanDiscovery.SearchSeconds + 1f);
            return;
        }

        lanSearch.Poll();
        bool found = lanSearch.TryGetBest(out LanSearch.Found server);
        if (!found && !lanSearch.Done) return;

        lanSearch.Dispose();
        lanSearch = null;
        if (!found)
        {
            Debug.Log("[Online] No server answered on this network");
            return;
        }
        if (Net.IsConnected) return;

        OnlineConfig.UseLanServer(server);
        if (hostInput != null) hostInput.text = server.Host;
        statusMessage.text = $"Found {server.Info.Name} at {server.Host}. Connecting...";
        connectDeadline = Time.unscaledTime + connectTimeoutSeconds;
    }

    private void Begin(Action<MetaProfile> signedIn, Action cancelled)
    {
        flowVersion++;
        editing = false;
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
            // Signed in automatically at launch (NetBootstrap): straight in, no screen at all.
            if (NetBootstrap.TryFreshProfile(out MetaProfile fresh))
            {
                profile = fresh;
                Finish();
                return;
            }
            LoadProfile();
            return;
        }

        ShowStatus("Connecting", $"Connecting to {Net.Host}...", false);
        step = Step.Connecting;
        connectDeadline = Time.unscaledTime + connectTimeoutSeconds;

        // A phone or emulator pointed at itself (the build's 192.168.15.150) looks on the LAN at once.
        lanSearched = false;
        lanSearchAt = Time.unscaledTime + (OnlineConfig.OnHandheld && OnlineConfig.IsThisDevice(Net.Host) ? 0f : lanSearchAfterSeconds);
        Net.Connect();   // no-op while an attempt is already under way
    }

    private void HandleAuthenticated()
    {
        if (step == Step.Connecting) LoadProfile();
    }

    /// <summary>How the server's refusal of a suspended account starts ("Account suspended until ... UTC.").</summary>
    public const string SuspendedPrefix = "Account suspended";

    /// <summary>What the server says when an operator disconnects a player from the admin panel.</summary>
    public const string DisconnectedByOperatorsReason = "Disconnected by the operators.";

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
        else if (reason.StartsWith(SuspendedPrefix, StringComparison.Ordinal))
        {
            // Suspended from the admin panel: the server's text already says until when.
            Fail(reason);
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

    private void BeginEdit(MetaProfile current, Action<MetaProfile> saved, Action closed)
    {
        flowVersion++;
        editing = true;
        profile = current;
        onEdited = saved;
        onEditClosed = closed;
        ShowProfileScreen();
        MultiplayerHub.Showing(HubTab.Profile, () => { if (editing) CloseEdit(); });
    }

    private void CloseEdit()
    {
        MultiplayerHub.Closed(HubTab.Profile);
        editing = false;
        step = Step.Idle;
        HideScreens();
        Action closed = onEditClosed;
        onEdited = null;
        onEditClosed = null;
        closed?.Invoke();
    }

    private void Back()
    {
        AudioManager.ButtonTap();
        flowVersion++;

        if (editing)
        {
            CloseEdit();   // nothing changed; still signed in
            return;
        }

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
            Shoes.Note(profile);
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

    // ---- First run (and the account screen): username, display name, avatar -------------------

    private void ShowProfileScreen()
    {
        step = Step.Profile;
        HideScreens();
        profileScreen.SetActive(true);
        profileScreen.transform.SetAsLastSibling();
        profileError.text = "";
        // The account (Multiplayer's Profile tab) or the first run's page of its own.
        profileTitle.text = editing ? "Multiplayer" : "Choose your names";
        profileTabs.SetActive(editing);
        profilePage.Content.offsetMax = new Vector2(-Paintbox.Gutter, -(editing ? PaintboxPage.TabsTop + PaintboxPage.TabsHeight + 14f : PaintboxPage.TabsTop));
        Paintbox.SetText(saveButton, editing ? "Save changes" : "Continue");
        artName.text = editing ? profile.UserId ?? "" : "";
        int body = editing ? profile.AvatarId : characters != null ? characters.CurrentIndex : 0;
        artPicture.sprite = Paintbox.Art(body == 1 ? "avatar2" : "avatar");
        userIdInput.text = editing ? profile.UserId ?? "" : "";

        // A display name of their own; one never chosen is the username, shown empty ("same as").
        string shown = profile.DisplayName ?? "";
        displayNameInput.text = editing && shown.Length > 0 && shown != profile.UserId ? shown : "";

        // The avatar is chosen once, on the first run; afterwards the character screen picks the body.
        avatarTitle.gameObject.SetActive(!editing);
        avatarRow.gameObject.SetActive(!editing);
        if (editing)
        {
            chosenAvatar = profile.AvatarId;
            return;
        }

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

        for (int i = 0; i < owned.Length; i++)
        {
            int id = owned[i];
            Button b = PaintboxPage.Chip(avatarRow, AvatarName(id), "User", false, () => PickAvatar(id));
            b.gameObject.AddComponent<LayoutElement>().preferredWidth = 260f;
            avatarButtons.Add(b);
            avatarIds.Add(id);
        }

        PickAvatar(chosenAvatar);
    }

    // The avatar chosen: violet with an outline; the others quiet.
    private void PickAvatar(int id)
    {
        chosenAvatar = id;
        for (int i = 0; i < avatarButtons.Count; i++)
        {
            bool on = avatarIds[i] == id;
            PaintboxFrame frame = avatarButtons[i].GetComponent<PaintboxFrame>();
            frame.Fill.color = on ? Paintbox.Violet : Color.clear;
            if (frame.Edge != null) frame.Edge.enabled = on;
            foreach (Text t in avatarButtons[i].GetComponentsInChildren<Text>(true)) t.color = on ? Paintbox.WashInk : Paintbox.Muted;
        }
        if (artPicture != null) artPicture.sprite = Paintbox.Art(id == 1 ? "avatar2" : "avatar");
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

        Meta.UpdateProfile(userIdInput.text.Trim(), chosenAvatar, displayNameInput.text.Trim(), reply =>
        {
            if (version != flowVersion) return;

            if (!reply.TimedOut && reply.Error == MetaError.None)
            {
                profile = reply.Profile;
                NetBootstrap.NoteProfile(profile);
                if (editing)
                {
                    onEdited?.Invoke(profile);
                    CloseEdit();
                }
                else Finish();
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

        // Connecting / failed: a page with a card in its middle (design B).
        PaintboxPage status = PaintboxPage.Build(canvas, "StatusScreen", "Multiplayer", Back, PaintboxPage.Top.None);
        statusScreen = status.Root.gameObject;
        PaintboxFrame card = Paintbox.Frame(status.Content, "Card", Paintbox.Paper, Paintbox.RadiusLarge, Paintbox.Border, 0f, Paintbox.PanelLine);
        Paintbox.Pin(card.Root, new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(1000f, 560f));
        RectTransform c = card.Body;
        Image badge = Paintbox.Rounded(c, "Badge", Paintbox.Wash, 18f);
        Paintbox.Pin(badge.rectTransform, new Vector2(0f, 1f), new Vector2(44f, -40f), new Vector2(72f, 72f));
        badge.rectTransform.pivot = new Vector2(0f, 1f);
        statusIcon = Paintbox.IconImage(badge.rectTransform, "Radio", 42f, Paintbox.WashInk);
        statusTitle = Paintbox.Label(c, "", 52f, Paintbox.Ink, Paintbox.Weight.Title);
        Paintbox.TopBand(statusTitle.rectTransform, 126f, 70f, 44f, 44f);
        statusMessage = Paintbox.Label(c, "", 28f, Paintbox.Muted);
        statusMessage.verticalOverflow = VerticalWrapMode.Overflow;
        Paintbox.TopBand(statusMessage.rectTransform, 200f, 110f, 44f, 44f);
        statusMessage.alignment = TextAnchor.UpperLeft;
        hostRow = new GameObject("HostRow", typeof(RectTransform));
        hostRow.transform.SetParent(c, false);
        Paintbox.TopBand((RectTransform)hostRow.transform, 318f, 84f, 44f, 44f);
        Text serverLabel = Paintbox.Label(hostRow.transform, "Server", 28f, Paintbox.Ink, Paintbox.Weight.Bold);
        Paintbox.Pin(serverLabel.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(150f, 60f));
        serverLabel.rectTransform.pivot = new Vector2(0f, 0.5f);
        hostInput = OnlineUI.NewInput(hostRow.transform, "IP address or host name", Vector2.zero, new Vector2(700f, 80f));
        ((RectTransform)hostInput.transform).anchorMin = new Vector2(0f, 0.5f);
        ((RectTransform)hostInput.transform).anchorMax = new Vector2(1f, 0.5f);
        ((RectTransform)hostInput.transform).offsetMin = new Vector2(160f, -40f);
        ((RectTransform)hostInput.transform).offsetMax = new Vector2(0f, 40f);
        hostInput.GetComponent<Image>().color = Paintbox.Tab;
        RectTransform statusButtons = Paintbox.NewRect(c, "Buttons");
        statusButtons.anchorMin = new Vector2(0f, 0f);
        statusButtons.anchorMax = new Vector2(1f, 0f);
        statusButtons.pivot = new Vector2(0.5f, 0f);
        statusButtons.offsetMin = new Vector2(44f, 36f);
        statusButtons.offsetMax = new Vector2(-44f, 118f);
        HorizontalLayoutGroup sb = statusButtons.gameObject.AddComponent<HorizontalLayoutGroup>();
        sb.childAlignment = TextAnchor.MiddleRight;
        sb.spacing = 16f;
        sb.childControlWidth = sb.childControlHeight = true;
        sb.childForceExpandWidth = false;
        sb.childForceExpandHeight = true;
        Button back = Paintbox.Button(statusButtons, "Back", Paintbox.Tone.Paper, "ArrowLeft", Back, 28f);
        back.gameObject.AddComponent<LayoutElement>().preferredWidth = 200f;
        statusRetry = Paintbox.Button(statusButtons, "Retry", Paintbox.Tone.Primary, "RefreshCw", Retry, 28f);
        statusRetry.gameObject.AddComponent<LayoutElement>().preferredWidth = 220f;

        // First run (and the account: Multiplayer's Profile tab): username, display name, avatar.
        profilePage = PaintboxPage.Build(canvas, "ProfileScreen", "Choose your names", Back,
                                         PaintboxPage.Top.Balance | PaintboxPage.Top.Shop | PaintboxPage.Top.Settings, HubTab.Profile, foot: true);
        profileScreen = profilePage.Root.gameObject;
        profileTitle = profilePage.Title;
        profileTabs = profilePage.Safe.Find("HubTabs").gameObject;
        profilePage.FootLeftButton("Back", Paintbox.Tone.Paper, "ArrowLeft", Back, 200f);
        saveButton = profilePage.FootButton("Save changes", Paintbox.Tone.Primary, "Check", SubmitProfile, 320f);
        saveButton.name = "SaveProfile";

        // The character, large on the left, with the username under it.
        PaintboxFrame art = Paintbox.Panel(profilePage.Content, "Character", 34f);
        art.Root.anchorMin = new Vector2(0f, 0f);
        art.Root.anchorMax = new Vector2(0.34f, 1f);
        art.Root.offsetMin = Vector2.zero;
        art.Root.offsetMax = new Vector2(-12f, 0f);
        Image well = Paintbox.Rounded(art.Body, "Well", Paintbox.Card, 30f);
        Paintbox.Fill(well.rectTransform, 4f, 4f, 4f, 4f);
        artPicture = Paintbox.Image(well.rectTransform, "Picture", Paintbox.Art("avatar"), Color.white);
        artPicture.preserveAspect = true;
        Paintbox.Fill(artPicture.rectTransform, 8f, 8f, 8f, 72f);
        Image nameBar = Paintbox.Rounded(well.rectTransform, "NameBar", Paintbox.Ink, 26f);
        nameBar.rectTransform.anchorMin = new Vector2(0f, 0f);
        nameBar.rectTransform.anchorMax = new Vector2(1f, 0f);
        nameBar.rectTransform.offsetMin = Vector2.zero;
        nameBar.rectTransform.offsetMax = new Vector2(0f, 64f);
        Image userIcon = Paintbox.IconImage(nameBar.rectTransform, "User", 28f, Color.white);
        Paintbox.Pin(userIcon.rectTransform, new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(28f, 28f));
        userIcon.rectTransform.pivot = new Vector2(0f, 0.5f);
        artName = Paintbox.Label(nameBar.rectTransform, "", 26f, Color.white, Paintbox.Weight.Bold);
        Paintbox.Fill(artName.rectTransform, 62f, 16f, 0f, 0f);

        // The form on the right.
        PaintboxFrame form = Paintbox.Panel(profilePage.Content, "Form", 34f);
        form.Root.anchorMin = new Vector2(0.34f, 0f);
        form.Root.anchorMax = new Vector2(1f, 1f);
        form.Root.offsetMin = new Vector2(12f, 0f);
        form.Root.offsetMax = Vector2.zero;
        RectTransform f = Paintbox.NewRect(form.Body, "Fields");
        f.anchorMin = new Vector2(0f, 0.5f);
        f.anchorMax = new Vector2(1f, 0.5f);
        f.offsetMin = new Vector2(40f, -300f);
        f.offsetMax = new Vector2(-40f, 300f);
        VerticalLayoutGroup v = f.gameObject.AddComponent<VerticalLayoutGroup>();
        v.childAlignment = TextAnchor.MiddleLeft;
        v.childControlWidth = v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        v.spacing = 10f;

        FieldLabel(f, "Username", null);
        userIdInput = Field(f, "Username");
        userIdInput.characterLimit = UserIds.MaxLength;
        Rules(f, $"{UserIds.MinLength}-{UserIds.MaxLength} characters. Start with a letter. Use English, Russian or Chinese letters, digits, underscores or periods.");
        Gap(f, 10f);
        FieldLabel(f, "Display name", "Optional");
        displayNameInput = Field(f, "Use my username");
        displayNameInput.characterLimit = DisplayNames.MaxLength;
        Rules(f, $"{DisplayNames.MinLength}-{DisplayNames.MaxLength} characters. What other players see. Leave it empty to use your username.");
        Gap(f, 10f);
        avatarTitle = FieldLabel(f, "Avatar", null);
        avatarRow = Paintbox.NewRect(f, "AvatarRow");
        avatarRow.gameObject.AddComponent<LayoutElement>().preferredHeight = 76f;
        HorizontalLayoutGroup ar = avatarRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        ar.spacing = 14f;
        ar.childAlignment = TextAnchor.MiddleLeft;
        ar.childControlWidth = ar.childControlHeight = true;
        ar.childForceExpandWidth = false;
        ar.childForceExpandHeight = true;
        profileError = Paintbox.Label(f, "", 26f, Paintbox.Bad, Paintbox.Weight.Bold);
        profileError.verticalOverflow = VerticalWrapMode.Overflow;
        profileError.gameObject.AddComponent<LayoutElement>().preferredHeight = 40f;
    }

    private static Text FieldLabel(RectTransform form, string text, string note)
    {
        string words = note == null ? L.T(text) : L.T(text) + " <size=21><color=#674E65>\u00b7 " + L.T(note) + "</color></size>";
        Text t = Paintbox.Label(form, words, 30f, Paintbox.Ink, Paintbox.Weight.Bold);
        t.supportRichText = true;
        t.gameObject.AddComponent<LayoutElement>().preferredHeight = 42f;
        return t;
    }

    private static InputField Field(RectTransform form, string placeholder)
    {
        InputField field = OnlineUI.NewInput(form, placeholder, Vector2.zero, new Vector2(800f, 84f));
        field.GetComponent<Image>().color = Paintbox.Tab;
        field.gameObject.AddComponent<LayoutElement>().preferredHeight = 84f;
        return field;
    }

    private static void Rules(RectTransform form, string text)
    {
        Text t = Paintbox.Label(form, text, 21f, Paintbox.Muted);
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.gameObject.AddComponent<LayoutElement>().preferredHeight = 30f;
    }

    private static void Gap(RectTransform form, float height) =>
        Paintbox.NewRect(form, "Gap").gameObject.AddComponent<LayoutElement>().preferredHeight = height;

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
        statusIcon.sprite = Paintbox.Icon(failed ? "WifiOff" : "Radio");
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
            case MetaError.InvalidDisplayName:
                switch (reply.NameProblem)
                {
                    case DisplayNameProblem.TooShort: return $"Use at least {DisplayNames.MinLength} characters.";
                    case DisplayNameProblem.TooLong: return $"Use at most {DisplayNames.MaxLength} characters.";
                    case DisplayNameProblem.BadCharacter: return "Use only letters, digits, spaces and _ . - '";
                    case DisplayNameProblem.NeedsLetter: return "Include at least one letter.";
                    case DisplayNameProblem.Reserved: return "That display name is reserved. Try another.";
                    default: return "That display name isn't allowed.";
                }
            default: return MetaErrorText(reply.Error, false);
        }
    }
}
