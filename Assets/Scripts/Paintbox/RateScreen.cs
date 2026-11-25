using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Rating the game (design B, 2026-11-24): a page with the game's picture on the left and, on the
/// right, one large row of five stars, the player's choice in words and everyone's average, then
/// a comment form and a thank-you. In Multiplayer it is the hub's Rate tab; from Single Player it
/// is a page of its own. The rating is RatingSync's (saved on the server, or at the next sign-in
/// when offline), as the old popups did.
/// </summary>
public sealed class RateScreen : MonoBehaviour
{
    private const int CanvasOrder = 48;   // with the other Multiplayer pages, under Settings and the Shop
    private const int MaxComment = 500;

    private static RateScreen instance;

    private Canvas canvas;
    private PaintboxPage page;
    private bool hub;
    private int stars;
    private string draft = "";
    private RectTransform panel;
    private enum Step { Rate, Feedback, Thanks }

    /// <summary>The page is on screen.</summary>
    public static bool IsOpen => instance != null && instance.canvas != null && instance.canvas.gameObject.activeSelf;

    /// <summary>Opens the rating, as Multiplayer's Rate tab (<paramref name="asHubTab"/>) or a page of its own.</summary>
    public static void Open(bool asHubTab)
    {
        if (instance == null)
        {
            var go = new GameObject("RateScreenHost");   // the page itself is "RateScreen"
            DontDestroyOnLoad(go);
            instance = go.AddComponent<RateScreen>();
        }
        instance.Show(asHubTab);
    }

    public static void CloseIfOpen()
    {
        if (IsOpen) instance.Close(false);
    }

    private void Show(bool asHubTab)
    {
        if (canvas != null) Destroy(canvas.gameObject);
        hub = asHubTab;
        stars = RatingSync.MyStars;
        draft = RatingSync.MyComment ?? "";
        canvas = OnlineUI.CreateCanvas("RateScreenCanvas", transform, CanvasOrder);
        page = PaintboxPage.Build(canvas.transform, "RateScreen", hub ? "Multiplayer" : "Rate the game", () => Close(true),
                                  hub ? PaintboxPage.Top.Balance | PaintboxPage.Top.Shop | PaintboxPage.Top.Settings : PaintboxPage.Top.Settings,
                                  hub ? HubTab.Rate : (HubTab?)null);
        BuildArt();
        panel = Paintbox.Panel(page.Content, "RatePanel").Root;
        panel.anchorMin = new Vector2(0.395f, 0f);
        panel.anchorMax = new Vector2(1f, 1f);
        panel.offsetMin = panel.offsetMax = Vector2.zero;

        if (hub) MultiplayerHub.Showing(HubTab.Rate, () => Close(false));
        RatingSync.Changed += Redraw;
        RatingSync.Refresh();
        Draw(Step.Rate);
    }

    private void Close(bool tapped)
    {
        if (tapped) AudioManager.ButtonTap();
        RatingSync.Changed -= Redraw;
        if (canvas != null) Destroy(canvas.gameObject);
        canvas = null;
        if (hub) MultiplayerHub.Closed(HubTab.Rate);
    }

    private Step shown;
    private void Redraw()
    {
        if (IsOpen && shown == Step.Rate) Draw(Step.Rate);
    }

    // The game's picture, with a star on a yellow badge in its corner.
    private void BuildArt()
    {
        PaintboxFrame art = Paintbox.Frame(page.Content, "RateArt", Paintbox.Ink, 34f, 0f, 0f);
        art.Root.anchorMin = new Vector2(0f, 0f);
        art.Root.anchorMax = new Vector2(0.38f, 1f);
        art.Root.offsetMin = art.Root.offsetMax = Vector2.zero;
        Image mask = Paintbox.Rounded(art.Fill.rectTransform, "Frame", Color.white, 34f);
        Paintbox.Fill(mask.rectTransform);
        mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        Image picture = Paintbox.Image(mask.rectTransform, "Picture", Paintbox.Art("heroB"), Color.white);
        Paintbox.Fill(picture.rectTransform);
        AspectRatioFitter fit = picture.gameObject.AddComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        fit.aspectRatio = 764f / 508f;

        PaintboxFrame badge = Paintbox.Frame(art.Root, "Badge", Paintbox.Yellow, 22f, Paintbox.Border, 5f);
        Paintbox.Pin(badge.Root, new Vector2(0f, 0f), new Vector2(30f, 30f), new Vector2(96f, 96f));
        badge.Root.pivot = new Vector2(0f, 0f);
        badge.Root.localRotation = Quaternion.Euler(0f, 0f, 6f);
        Paintbox.IconImage(badge.Body, "Star", 52f, Paintbox.Ink).name = "BadgeIcon";
    }

    // ================= The three steps =================

    private void Draw(Step step)
    {
        shown = step;
        foreach (Transform child in panel.Find("Body"))
        {
            if (child.name == "Edge" || child.name == "Fill") continue;
            child.gameObject.SetActive(false);   // gone at once, destroyed at the frame's end
            Destroy(child.gameObject);
        }
        RectTransform body = (RectTransform)panel.Find("Body");
        RectTransform box = Paintbox.NewRect(body, "Content");
        box.anchorMin = new Vector2(0f, 0.5f);
        box.anchorMax = new Vector2(1f, 0.5f);
        box.pivot = new Vector2(0.5f, 0.5f);
        box.offsetMin = new Vector2(44f, -260f);
        box.offsetMax = new Vector2(-44f, 260f);
        VerticalLayoutGroup v = box.gameObject.AddComponent<VerticalLayoutGroup>();
        v.childAlignment = TextAnchor.MiddleLeft;
        v.childControlWidth = v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        v.spacing = 14f;

        Image badgeIcon = page.Content.Find("RateArt/Badge/Body/BadgeIcon")?.GetComponent<Image>();
        if (badgeIcon != null) badgeIcon.sprite = Paintbox.Icon(step == Step.Thanks ? "Check" : "Star");

        switch (step)
        {
            case Step.Rate: DrawRate(box); break;
            case Step.Feedback: DrawFeedback(box); break;
            default: DrawThanks(box); break;
        }
    }

    private void DrawRate(RectTransform box)
    {
        Line(box, "How was your game?", 52f, Paintbox.Ink, Paintbox.Weight.Title, 70f);
        Stars(box);
        Text chosen = Line(box, stars > 0 ? L.F(stars == 1 ? "{0} of 5 star" : "{0} of 5 stars", stars) : "Tap a star to rate", 28f, Paintbox.Ink, Paintbox.Weight.Bold, 40f);
        chosen.name = "Chosen";
        Line(box, Average(), 22f, Paintbox.Muted, Paintbox.Weight.Regular, 32f);
        RectTransform row = Buttons(box);
        Button go = Paintbox.Button(row, "Continue", Paintbox.Tone.Primary, "ArrowRight", () =>
        {
            AudioManager.ButtonTap();
            if (stars <= 0) return;
            RatingSync.Submit(stars, RatingSync.MyComment);   // the stars are saved at once
            GameData.isRate = 1;
            Draw(Step.Feedback);
        }, 28f);
        go.name = "RateContinue";
        go.interactable = stars > 0;
        Width(go, 240f);
        Button later = Paintbox.Button(row, "Later", Paintbox.Tone.Paper, null, () => Close(true), 28f);
        later.name = "RateLater";
        Width(later, 160f);
    }

    private void DrawFeedback(RectTransform box)
    {
        Line(box, "Write feedback", 52f, Paintbox.Ink, Paintbox.Weight.Title, 70f);
        Stars(box);
        Text label = Line(box, L.T("Comment") + " <size=20><color=#674E65>· " + L.T("Optional") + "</color></size>", 26f, Paintbox.Ink, Paintbox.Weight.Bold, 36f);
        label.supportRichText = true;

        InputField field = OnlineUI.NewInput(box, "What did you like? What could be better?", Vector2.zero, new Vector2(800f, 150f));
        field.name = "Comment";
        field.lineType = InputField.LineType.MultiLineNewline;
        field.characterLimit = MaxComment;
        field.text = draft;
        field.textComponent.alignment = TextAnchor.UpperLeft;
        field.textComponent.horizontalOverflow = HorizontalWrapMode.Wrap;
        field.textComponent.rectTransform.offsetMin = new Vector2(20f, 14f);
        field.textComponent.rectTransform.offsetMax = new Vector2(-20f, -14f);
        ((Text)field.placeholder).alignment = TextAnchor.UpperLeft;
        ((Text)field.placeholder).rectTransform.offsetMin = new Vector2(20f, 14f);
        ((Text)field.placeholder).rectTransform.offsetMax = new Vector2(-20f, -14f);
        field.GetComponent<Image>().color = Paintbox.Tab;
        field.gameObject.AddComponent<LayoutElement>().preferredHeight = 150f;
        Text count = Line(box, $"{draft.Length} / {MaxComment}", 20f, Paintbox.Muted, Paintbox.Weight.Regular, 26f);
        field.onValueChanged.AddListener(t => { draft = t; count.text = $"{t.Length} / {MaxComment}"; });

        RectTransform row = Buttons(box);
        Button send = Paintbox.Button(row, "Submit feedback", Paintbox.Tone.Primary, "Send", () =>
        {
            AudioManager.ButtonTap();
            int given = stars > 0 ? stars : RatingSync.MyStars;
            if (given > 0) RatingSync.Submit(given, draft ?? "");
            Draw(Step.Thanks);
        }, 28f);
        send.name = "RateSubmit";
        Width(send, 320f);
        Button back = Paintbox.Button(row, "Back", Paintbox.Tone.Paper, null, () => { AudioManager.ButtonTap(); Draw(Step.Rate); }, 28f);
        Width(back, 150f);
    }

    private void DrawThanks(RectTransform box)
    {
        Image badge = Paintbox.Image(box, "Done", UiArt.Circle, Paintbox.Wash);
        LayoutElement le = badge.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = le.preferredHeight = 86f;
        le.flexibleWidth = 0f;
        RectTransform holder = Paintbox.NewRect(box, "BadgeRow");
        holder.gameObject.AddComponent<LayoutElement>().preferredHeight = 0f;
        badge.transform.SetParent(holder, false);
        badge.rectTransform.anchorMin = badge.rectTransform.anchorMax = badge.rectTransform.pivot = new Vector2(0f, 0f);
        badge.rectTransform.sizeDelta = new Vector2(86f, 86f);
        holder.GetComponent<LayoutElement>().preferredHeight = 90f;
        Paintbox.IconImage(badge.rectTransform, "CheckCheck", 48f, Paintbox.WashInk);

        Line(box, "Thank you!", 54f, Paintbox.Ink, Paintbox.Weight.Title, 70f);
        Line(box, RatingSync.Saved || RatingSync.MyStars == 0 ? "Your feedback was received." : "Saved online at your next sign-in.",
             26f, Paintbox.Muted, Paintbox.Weight.Regular, 36f);
        RectTransform row = Buttons(box);
        Button go = Paintbox.Button(row, "Continue", Paintbox.Tone.Primary, "ArrowRight", () => Close(true), 28f);
        go.name = "RateDone";
        Width(go, 240f);
    }

    // ================= Pieces =================

    // One large row of five stars: filled yellow up to the rating.
    private void Stars(RectTransform box)
    {
        RectTransform row = Paintbox.NewRect(box, "Stars");
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 86f;
        HorizontalLayoutGroup h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 14f;
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = h.childControlHeight = false;
        h.childForceExpandWidth = h.childForceExpandHeight = false;
        for (int i = 1; i <= 5; i++)
        {
            int n = i;
            Image hit = Paintbox.Image(row, "Star" + i, null, Color.clear, true);
            hit.rectTransform.sizeDelta = new Vector2(80f, 80f);
            Image fill = Paintbox.Image(hit.rectTransform, "Fill", Paintbox.StarSprite, i <= stars ? Paintbox.Yellow : Color.clear);
            Paintbox.Fill(fill.rectTransform, 6f, 6f, 6f, 6f);
            Image edge = Paintbox.IconImage(hit.rectTransform, "Star", 78f, i <= stars ? Paintbox.Ink : Paintbox.Muted);
            edge.name = "Outline";
            Button b = hit.gameObject.AddComponent<Button>();
            b.targetGraphic = hit;
            b.onClick.AddListener(() =>
            {
                AudioManager.ButtonTap();
                stars = n;
                Draw(shown);
            });
            hit.gameObject.AddComponent<UiPress>();
        }
    }

    private static string Average()
    {
        if (RatingSync.Count <= 0) return "";
        string average = RatingSync.Average.ToString("0.0", CultureInfo.InvariantCulture);
        return L.F("Player average {0} / 5 · {1} ratings", average, RatingSync.Count);
    }

    private static Text Line(RectTransform box, string text, float size, Color color, Paintbox.Weight weight, float height)
    {
        Text t = Paintbox.Label(box, text, size, color, weight);
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
        return t;
    }

    private static RectTransform Buttons(RectTransform box)
    {
        RectTransform row = Paintbox.NewRect(box, "Buttons");
        LayoutElement le = row.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = 84f;
        le.flexibleHeight = 0f;   // the buttons' own give would stretch the row to fill the panel
        HorizontalLayoutGroup h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 16f;
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        return row;
    }

    private static void Width(Button b, float w) => b.gameObject.AddComponent<LayoutElement>().preferredWidth = w;
}
