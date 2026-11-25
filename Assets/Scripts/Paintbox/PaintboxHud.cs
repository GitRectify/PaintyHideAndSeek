using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Design B's match HUD (2026-11-25), laid out as the prototype's: the role chip top left, the
/// round's pill top centre (hiders left, the clock, seekers left), the menu top right, the stick
/// bottom left and the actions bottom right (the main one large and yellow, the others around
/// it), Start along the bottom, the pose picker as a tray, turning as a tray under the character
/// and painting as a sheet on the right edge.
/// The scene's own buttons keep their scripts and events: they are only moved and sized here,
/// every frame, in design units (a 1080-tall screen) on whatever canvas each one is on. The menu
/// button opens the round menu (Resume, Settings, Shop and the balance online, Leave).
/// </summary>
[DefaultExecutionOrder(-40)]
public sealed class PaintboxHud : MonoBehaviour
{
    public static PaintboxHud Instance { get; private set; }

    /// <summary>The match HUD is on screen.</summary>
    public static bool InRound => Instance != null && Instance.showing;

    /// <summary>The round menu is open.</summary>
    public static bool MenuOpen => Instance != null && Instance.menuRoot != null && Instance.menuRoot.activeSelf;

    // Design sizes (a 1080-tall screen): the prototype's sizes x 1.8, so they are good to tap.
    public const float EdgeX = 36f, EdgeY = 30f;
    private const float Main = 124f;
    private static readonly Vector2 Slot = new Vector2(104f, 90f);
    private static readonly Vector2 Climb = new Vector2(112f, 90f);
    private static readonly Vector2 BottomRight = new Vector2(1f, 0f);
    private static readonly Vector2 BottomLeft = Vector2.zero;
    private static readonly Vector2 BottomCentre = new Vector2(0.5f, 0f);
    private static readonly Vector2 Centre = new Vector2(0.5f, 0.5f);

    // Where each action sits, from the bottom right corner (its own bottom right corner).
    private static readonly Vector2 MainAt = new Vector2(-EdgeX, EdgeY);
    private static readonly Vector2 LeftOfMain = new Vector2(-(EdgeX + 140f), EdgeY);
    private static readonly Vector2 AboveLeft = new Vector2(-(EdgeX + 130f), EdgeY + 112f);
    private static readonly Vector2 AboveMain = new Vector2(-EdgeX, EdgeY + Main + 18f);
    private static readonly Vector2 ClimbUpAt = new Vector2(-(EdgeX + 262f), EdgeY + 112f);
    private static readonly Vector2 ClimbDownAt = new Vector2(-(EdgeX + 262f), EdgeY);

    /// <summary>The pose tray and the paint sheet start this far down (under the menu and the pill).</summary>
    public const float TrayTop = 140f;
    private const float TrayBottom = 290f;      // over the actions
    private const float RotateTrayY = EdgeY + 100f;
    private const float PaintSheetWidth = 470f;

    private Transform paintUi, fireUi, climbUi, joyUi, paintSheetUi;
    private GameModeManager gmm;
    private CameraController cam;
    private ChameleonPaint painter;
    private bool showing;

    private Button menuButton, shopButton;
    private CanvasGroup shopHidden;
    private RectTransform overlay, roleChip, rotateTray, readyRow;
    private Text roleText;
    private Image roleIcon;
    private string roleShown;
    private Vector2 paintDockShown = new Vector2(float.NaN, 0f);

    private GameObject menuRoot, endRoot;
    private RectTransform menuCard;
    private GameObject menuShopRow;
    private Button menuLeave;

    // ================= Set up =================

    /// <summary>Takes over the match HUD under <paramref name="paintUi"/> (the PaintUICanvas): before its buttons are skinned.</summary>
    public static void Attach(Transform paintUi)
    {
        if (paintUi == null) return;
        if (Instance == null) Instance = new GameObject("PaintboxHud").AddComponent<PaintboxHud>();   // in the scene: gone with it
        Instance.Bind(paintUi);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Bind(Transform ui)
    {
        paintUi = ui;
        fireUi = Find("FireButtonCanvas");
        climbUi = Find("ClimbButtonsCanvas");
        joyUi = Find("JoystickCanvas");
        paintSheetUi = Find("ChameleonPaintCanvas");
        gmm = FindAnyObjectByType<GameModeManager>(FindObjectsInactive.Include);
        cam = FindAnyObjectByType<CameraController>(FindObjectsInactive.Include);
        painter = FindAnyObjectByType<ChameleonPaint>(FindObjectsInactive.Include);

        menuButton = ui.Find("Btn_Setting")?.GetComponent<Button>();
        shopButton = ui.Find("Btn_Shop")?.GetComponent<Button>();
        if (shopButton != null)
        {
            // Online, the Shop is in the round menu now, beside the balance: the HUD's own button is not shown.
            shopHidden = Paintbox.Ensure<CanvasGroup>(shopButton.gameObject);
            shopHidden.alpha = 0f;
            shopHidden.interactable = false;
            shopHidden.blocksRaycasts = false;
        }

        overlay = PaintboxUnits.Create(ui, "PbHud");
        overlay.SetAsFirstSibling();   // behind the scene's buttons (the rotate tray is under its two)
        BuildRoleChip();
        BuildRotateTray();
        BuildMenu();
        RouteMenuButton();
        LayoutNow();
    }

    private static Transform Find(string name)
    {
        foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t.name == name && t.gameObject.scene.IsValid()) return t;
        return null;
    }

    // ================= Every frame =================

    private void LateUpdate()
    {
        showing = paintUi != null && paintUi.gameObject.activeInHierarchy && gmm != null && gmm.RoundActive;
        if (!showing)
        {
            Notices.RightClear = 0f;
            if (menuRoot != null && menuRoot.activeSelf) menuRoot.SetActive(false);
            if (endRoot != null && endRoot.activeSelf) endRoot.SetActive(false);
            return;
        }

        LayoutNow();
        // Notices keep clear of the paint sheet and the pose tray on the right.
        Transform poses = paintUi.Find("PosePanel");
        Notices.RightClear = painter != null && painter.DrawingActive ? EdgeX + PaintSheetWidth + 24f
                           : poses != null && poses.gameObject.activeSelf ? EdgeX + 440f + 24f : 0f;
        UpdateRoleChip();
        UpdateRotateTray();
        if (menuRoot.activeSelf) RefreshMenu();
    }

    /// <summary>Lays the HUD out at once and re-makes resized buttons: for a picture taken at another size.</summary>
    public static void Relayout()
    {
        if (Instance != null && Instance.paintUi != null) Instance.LayoutNow();
        foreach (PaintboxHudButton b in FindObjectsByType<PaintboxHudButton>(FindObjectsSortMode.None)) b.Refit();
        foreach (PaintboxGridFit g in FindObjectsByType<PaintboxGridFit>(FindObjectsSortMode.None)) g.Refit();
    }

    /// <summary>Canvas units per design unit on <paramref name="canvas"/> (its height over 1080).</summary>
    public static float Unit(Transform canvas)
    {
        var rt = canvas as RectTransform;
        float h = rt != null ? rt.rect.height : 0f;
        return h > 1f ? h / 1080f : 1f;
    }

    private static void Put(Transform parent, string path, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size, float unit)
    {
        if (parent == null) return;
        Put(parent.Find(path) as RectTransform, anchor, pivot, position, size, unit);
    }

    /// <summary>Anchored at <paramref name="anchor"/>, at a design offset and size: only what changed is set.</summary>
    public static void Put(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size, float unit)
    {
        if (rt == null) return;
        if (rt.anchorMin != anchor) rt.anchorMin = anchor;
        if (rt.anchorMax != anchor) rt.anchorMax = anchor;
        if (rt.pivot != pivot) rt.pivot = pivot;
        Vector2 p = position * unit, s = size * unit;
        if ((rt.anchoredPosition - p).sqrMagnitude > 0.01f) rt.anchoredPosition = p;
        if ((rt.sizeDelta - s).sqrMagnitude > 0.01f) rt.sizeDelta = s;
        if (rt.localScale != Vector3.one) rt.localScale = Vector3.one;
    }

    private void LayoutNow()
    {
        bool seeker = gmm != null && gmm.PlayerIsSeeker;

        // The menu, top right; the hider's actions bottom right; Start along the bottom.
        float k = Unit(paintUi);
        Put(paintUi, "Btn_Setting", Vector2.one, Vector2.one, new Vector2(-EdgeX, -EdgeY), new Vector2(88f, 88f), k);
        Put(paintUi, "PaintGateButton", BottomRight, BottomRight, MainAt, new Vector2(Main, Main), k);
        Put(paintUi, "Btn_DoiDang", BottomRight, BottomRight, LeftOfMain, Slot, k);
        Put(paintUi, "Btn_XoayNhanVat", BottomRight, BottomRight, AboveLeft, Slot, k);
        Put(paintUi, "ButtonCamera", BottomRight, BottomRight, AboveMain, Slot, k);
        bool dock = OnlineRound.WeaponDockShown;
        Put(paintUi, "ButtonStart", BottomCentre, BottomCentre, new Vector2(0f, EdgeY + (dock ? 124f : 0f)), new Vector2(430f, 84f), k);
        Put(paintUi, "ButtonRotationLeft", BottomCentre, Centre, new Vector2(-62f, RotateTrayY + 70f), new Vector2(100f, 100f), k);
        Put(paintUi, "ButtonRotationRight", BottomCentre, Centre, new Vector2(52f, RotateTrayY + 70f), new Vector2(100f, 100f), k);
        LayoutPoseTray(paintUi.Find("PosePanel") as RectTransform, k);

        // The seeker's: Fire, Lie down beside it, Jump above. A hider's Jump is by the stick.
        if (fireUi != null)
        {
            float f = Unit(fireUi);
            Put(fireUi, "FireBtn", BottomRight, BottomRight, MainAt, new Vector2(Main, Main), f);
            Put(fireUi, "btnnam", BottomRight, BottomRight, LeftOfMain, Slot, f);
            if (seeker) Put(fireUi, "Jump", BottomRight, BottomRight, AboveLeft, Slot, f);
            else Put(fireUi, "Jump", BottomLeft, BottomLeft, new Vector2(EdgeX + 276f, EdgeY + 128f), new Vector2(100f, 90f), f);
        }

        // Climbing, left of the actions.
        if (climbUi != null)
        {
            float c = Unit(climbUi);
            Put(climbUi, "ClimbUp", BottomRight, BottomRight, ClimbUpAt, Climb, c);
            Put(climbUi, "ClimbDown", BottomRight, BottomRight, ClimbDownAt, Climb, c);
        }

        // The stick, bottom left.
        if (joyUi != null)
        {
            float j = Unit(joyUi);
            var stick = joyUi.Find("Fixed Joystick") as RectTransform;
            Put(stick, BottomLeft, Centre, new Vector2(EdgeX + 130f, EdgeY + 130f), new Vector2(260f, 260f), j);
            if (stick != null)
            {
                Put(stick.Find("JoyOutline") as RectTransform, Centre, Centre, Vector2.zero, new Vector2(260f, 260f), j);
                Put(stick.Find("Handle") as RectTransform, Centre, Centre, Vector2.zero, new Vector2(104f, 104f), j);
            }
        }

        DockPaintSheet();
    }

    // The pose picker: a tray down the right edge, between the menu and the actions.
    private static void LayoutPoseTray(RectTransform tray, float k)
    {
        if (tray == null) return;
        Vector2 min = new Vector2(1f, 0f), max = new Vector2(1f, 1f), pivot = new Vector2(1f, 0.5f);
        if (tray.anchorMin != min) tray.anchorMin = min;
        if (tray.anchorMax != max) tray.anchorMax = max;
        if (tray.pivot != pivot) tray.pivot = pivot;
        Vector2 size = new Vector2(440f * k, -(TrayTop + TrayBottom) * k);
        Vector2 at = new Vector2(-EdgeX * k, (TrayBottom - TrayTop) * 0.5f * k);
        if ((tray.sizeDelta - size).sqrMagnitude > 0.01f) tray.sizeDelta = size;
        if ((tray.anchoredPosition - at).sqrMagnitude > 0.01f) tray.anchoredPosition = at;
    }

    // Painting: the sheet on the right edge, sliding in from it.
    private void DockPaintSheet()
    {
        if (painter == null || paintSheetUi == null) return;
        float k = Unit(paintSheetUi);
        var open = new Vector2(-EdgeX * k, -TrayTop * k);
        if (open == paintDockShown) return;
        paintDockShown = open;
        painter.DockPanel(open, Vector2.right, (PaintSheetWidth + EdgeX + 40f) * k, k);
    }

    // ================= The role chip =================

    private void BuildRoleChip()
    {
        roleChip = Paintbox.Tag(overlay, "Hider", Paintbox.Hot, Paintbox.HotInk, "PaintRoller", 30f);
        roleChip.name = "RoleChip";
        roleChip.anchorMin = roleChip.anchorMax = roleChip.pivot = new Vector2(0f, 1f);
        roleChip.anchoredPosition = new Vector2(EdgeX, -EdgeY);
        roleChip.localRotation = Quaternion.Euler(0f, 0f, 3f);   // the prototype's slight tilt
        HorizontalLayoutGroup layout = roleChip.GetComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(18, 22, 10, 10);
        layout.spacing = 10f;
        Outline edge = roleChip.gameObject.AddComponent<Outline>();
        edge.effectColor = Paintbox.HotInk;
        edge.effectDistance = new Vector2(3f, -3f);
        roleText = roleChip.GetComponentInChildren<Text>(true);
        roleIcon = roleChip.Find("Icon")?.GetComponent<Image>();
    }

    private void UpdateRoleChip()
    {
        bool seeker = gmm != null && gmm.PlayerIsSeeker;
        bool free = cam != null && cam.FreeLook;
        string role = free ? "Free camera" : seeker ? "Seeker" : "Hider";
        if (role == roleShown) return;
        roleShown = role;
        roleText.text = role;
        if (roleIcon != null) roleIcon.sprite = Paintbox.Icon(free ? "Camera" : seeker ? "ScanEye" : "PaintRoller");
    }

    // ================= Turning =================

    // A paper tray under the two turn buttons: what they do, and Done (the Rotate button's own toggle).
    private void BuildRotateTray()
    {
        PaintboxFrame tray = Paintbox.Frame(overlay, "RotateTray", Paintbox.Paper, Paintbox.Radius, 3f, 5f, Paintbox.Ink, Paintbox.Ink);
        rotateTray = tray.Root;
        Paintbox.Pin(rotateTray, BottomCentre, new Vector2(0f, RotateTrayY), new Vector2(600f, 140f));

        Text title = Paintbox.Label(tray.Body, "Rotate", 34f, Paintbox.Ink, Paintbox.Weight.Bold);
        title.rectTransform.anchorMin = new Vector2(0f, 0.5f);
        title.rectTransform.anchorMax = new Vector2(0f, 1f);
        title.rectTransform.pivot = new Vector2(0f, 0.5f);
        title.rectTransform.offsetMin = new Vector2(30f, -4f);
        title.rectTransform.offsetMax = new Vector2(220f, -14f);
        title.alignment = TextAnchor.LowerLeft;
        Text hint = Paintbox.Label(tray.Body, "Hold to turn", 22f, Paintbox.Muted);
        hint.rectTransform.anchorMin = new Vector2(0f, 0f);
        hint.rectTransform.anchorMax = new Vector2(0f, 0.5f);
        hint.rectTransform.pivot = new Vector2(0f, 0.5f);
        hint.rectTransform.offsetMin = new Vector2(30f, 14f);
        hint.rectTransform.offsetMax = new Vector2(220f, 2f);
        hint.alignment = TextAnchor.UpperLeft;
        Paintbox.Fit(hint, 14);

        Button done = Paintbox.Button(tray.Body, "Done", Paintbox.Tone.Primary, "Check", () =>
        {
            AudioManager.ButtonTap();
            paintUi.Find("Btn_XoayNhanVat")?.GetComponent<Button>()?.onClick.Invoke();
        }, 26f);
        done.name = "RotateDone";
        Paintbox.Pin((RectTransform)done.transform, new Vector2(1f, 0.5f), new Vector2(-22f, 0f), new Vector2(150f, 76f));
        rotateTray.gameObject.SetActive(false);
    }

    private void UpdateRotateTray()
    {
        Transform left = paintUi.Find("ButtonRotationLeft");
        bool open = left != null && left.gameObject.activeInHierarchy;
        if (rotateTray.gameObject.activeSelf != open) rotateTray.gameObject.SetActive(open);
    }

    // ================= The round menu =================

    private void RouteMenuButton()
    {
        if (menuButton == null) return;
        // The menu button opened the settings; it opens the round menu now, which has them.
        for (int i = 0; i < menuButton.onClick.GetPersistentEventCount(); i++)
            menuButton.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
        menuButton.onClick.AddListener(OpenMenu);
    }

    private void BuildMenu()
    {
        Transform canvas = OnlineUI.CreateCanvas("RoundMenuCanvas", transform, 47).transform;

        PaintboxDialog menu = PaintboxDialog.Build(canvas, "RoundMenu", "Menu", "Round menu", new Vector2(640f, 600f));
        menuRoot = menu.Root;
        menuCard = (RectTransform)menu.Root.transform.GetChild(0);
        menu.Body.gameObject.SetActive(false);
        menu.Buttons.gameObject.SetActive(false);

        RectTransform stack = Paintbox.NewRect(menu.Card, "Stack");
        stack.anchorMin = new Vector2(0f, 1f);
        stack.anchorMax = new Vector2(1f, 1f);
        stack.pivot = new Vector2(0.5f, 1f);
        stack.offsetMin = new Vector2(40f, -600f);
        stack.offsetMax = new Vector2(-40f, -196f);
        VerticalLayoutGroup v = stack.gameObject.AddComponent<VerticalLayoutGroup>();
        v.spacing = 16f;
        v.childControlWidth = v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        v.childAlignment = TextAnchor.UpperCenter;

        Row(MenuButton(stack, "Resume", Paintbox.Tone.Primary, "Play", CloseMenu), "MenuResume");
        Row(MenuButton(stack, "Settings", Paintbox.Tone.Paper, "Settings", () =>
        {
            CloseMenu();
            FindAnyObjectByType<SettingsUI>(FindObjectsInactive.Include)?.Show();
        }), "MenuSettings");

        // Online: the Shop, and the balance beside it.
        RectTransform shopRow = Paintbox.NewRect(stack, "MenuShopRow");
        shopRow.gameObject.AddComponent<LayoutElement>().preferredHeight = 86f;
        HorizontalLayoutGroup h = shopRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 16f;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        Button shop = MenuButton(shopRow, "Shop", Paintbox.Tone.Paper, "ShoppingBag", () =>
        {
            CloseMenu();
            if (shopButton != null) shopButton.onClick.Invoke();
        });
        shop.name = "MenuShop";
        shop.GetComponent<LayoutElement>().flexibleWidth = 1f;
        Paintbox.CoinPill(shopRow, out RectTransform pill);
        pill.name = "MenuBalance";
        pill.gameObject.AddComponent<LayoutElement>().preferredWidth = 220f;
        menuShopRow = shopRow.gameObject;

        menuLeave = MenuButton(stack, "Leave match", Paintbox.Tone.Paper, "LogOut", Leave);
        menuLeave.name = "MenuLeave";
        foreach (Text t in menuLeave.GetComponentsInChildren<Text>(true)) t.color = Paintbox.Bad;
        foreach (Image i in menuLeave.GetComponentsInChildren<Image>(true)) if (i.name == "Icon") i.color = Paintbox.Bad;
        menuRoot.SetActive(false);

        // Single Play's own question before the round ends.
        PaintboxDialog end = PaintboxDialog.Build(canvas, "EndRoundConfirm", "DoorOpen", "End this round?", new Vector2(860f, 420f));
        endRoot = end.Root;
        end.Body.text = "Leave this round and go back to Home.";
        Button endYes = end.DangerButton("End round", "LogOut", () =>
        {
            AudioManager.ButtonTap();
            endRoot.SetActive(false);
            FindAnyObjectByType<HomeUI>(FindObjectsInactive.Include)?.GoHome();
        }, 240f);
        endYes.name = "EndRoundYes";
        end.Button("Stay", Paintbox.Tone.Primary, "Play", () =>
        {
            AudioManager.ButtonTap();
            endRoot.SetActive(false);
        }, 190f);
        endRoot.SetActive(false);
    }

    private static Button MenuButton(Transform parent, string text, Paintbox.Tone tone, string icon, UnityAction onClick)
    {
        Button b = Paintbox.Button(parent, text, tone, icon, onClick, 30f);
        b.gameObject.AddComponent<LayoutElement>().preferredHeight = 86f;
        return b;
    }

    private static void Row(Button b, string name) => b.name = name;

    private void RefreshMenu()
    {
        bool online = OnlineRound.Active;
        if (menuShopRow.activeSelf != online) menuShopRow.SetActive(online);
        string leave = online ? "Leave match" : "End round";
        Text label = menuLeave.GetComponentInChildren<Text>(true);
        if (label.text != leave) label.text = leave;

        int rows = online ? 4 : 3;
        float height = 196f + rows * 86f + (rows - 1) * 16f + 40f;
        if (Mathf.Abs(menuCard.sizeDelta.y - height) > 0.5f) menuCard.sizeDelta = new Vector2(menuCard.sizeDelta.x, height);
    }

    /// <summary>Opens the round menu (the menu button, Esc); false when no round is on screen.</summary>
    public static bool ToggleMenu()
    {
        if (!InRound) return false;
        if (MenuOpen) Instance.CloseMenu();
        else Instance.OpenMenu();
        return true;
    }

    private void OpenMenu()
    {
        AudioManager.ButtonTap();
        RefreshMenu();
        menuRoot.SetActive(true);
    }

    private void CloseMenu()
    {
        if (!menuRoot.activeSelf) return;
        AudioManager.ButtonTap();
        menuRoot.SetActive(false);
    }

    private void Leave()
    {
        CloseMenu();
        if (OnlineRound.Active) OnlineRound.AskExitMatch();
        else
        {
            endRoot.SetActive(true);
            endRoot.transform.SetAsLastSibling();
        }
    }

    // ================= The pose tray =================

    /// <summary>
    /// The pose picker (PoseSelectorUI's PosePanel) as the prototype's tray: paper, "Pose" and a
    /// close button on top, the poses as cards two to a row. A tap on a card still applies it.
    /// </summary>
    public static void StylePoseTray(Transform panel)
    {
        if (panel == null || panel.Find("PbTitle") != null) return;

        PaintboxSkin.Panel(panel.GetComponent<Image>(), 34f, 6f, Paintbox.Ink);
        Text title = Paintbox.Label(panel, "Pose", 38f, Paintbox.Ink, Paintbox.Weight.Bold);
        title.name = "PbTitle";
        Paintbox.TopBand(title.rectTransform, 22f, 60f, 30f, 100f);

        var close = panel.Find("CloseBtn") as RectTransform;
        if (close != null)
        {
            Paintbox.Pin(close, Vector2.one, new Vector2(-20f, -18f), new Vector2(66f, 66f));
            close.localScale = Vector3.one;
            PaintboxSkin.Hud(close, "X", null, false, Paintbox.Tone.Paper, "X");
        }

        var list = panel.Find("List") as RectTransform;
        if (list == null) return;
        Paintbox.Fill(list, 18f, 18f, 96f, 18f);
        Image back = list.Find("Image")?.GetComponent<Image>();
        if (back != null) back.color = Color.clear;

        var content = list.Find("Content") as RectTransform;
        if (content == null) return;
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = new Vector2(0f, content.offsetMin.y);
        content.offsetMax = new Vector2(0f, 0f);
        content.anchoredPosition = Vector2.zero;
        GridLayoutGroup grid = Paintbox.Ensure<GridLayoutGroup>(content.gameObject);
        grid.spacing = new Vector2(12f, 12f);
        grid.padding = new RectOffset(4, 4, 4, 12);
        grid.cellSize = new Vector2(190f, 150f);
        PaintboxGridFit fit = content.gameObject.AddComponent<PaintboxGridFit>();
        fit.MinWidth = 150f;
        fit.Aspect = 0.78f;
        ContentSizeFitter size = Paintbox.Ensure<ContentSizeFitter>(content.gameObject);
        size.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        size.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        foreach (Transform slot in content)
        {
            if (!slot.name.StartsWith("Slot")) continue;
            Image card = slot.GetComponent<Image>();
            if (card != null)
            {
                card.color = Paintbox.Card;
                UiArt.Apply(card, UiArt.Rounded, 22f);
                Outline o = card.gameObject.AddComponent<Outline>();
                o.effectColor = Paintbox.Ink;
                o.effectDistance = new Vector2(2.5f, -2.5f);
            }
            var icon = slot.Find("Icon") as RectTransform;
            if (icon != null) Paintbox.Fill(icon, 12f, 12f, 12f, 12f);

            // Picked: a yellow wash and ring with a tick in the corner.
            Image pick = slot.Find("Pick")?.GetComponent<Image>();
            if (pick != null)
            {
                pick.color = new Color(Paintbox.Yellow.r, Paintbox.Yellow.g, Paintbox.Yellow.b, 0.32f);
                UiArt.Apply(pick, UiArt.Rounded, 22f);
                Outline ring = pick.gameObject.AddComponent<Outline>();
                ring.effectColor = Paintbox.Yellow;
                ring.effectDistance = new Vector2(4f, -4f);
                Image mark = Paintbox.Image(pick.rectTransform, "PbTick", UiArt.Circle, Paintbox.Yellow);
                mark.preserveAspect = true;
                Paintbox.Pin(mark.rectTransform, Vector2.one, new Vector2(-6f, -6f), new Vector2(38f, 38f));
                Paintbox.IconImage(mark.rectTransform, "Check", 24f, Paintbox.Ink);
            }
        }
    }

    // ================= The paint sheet =================

    /// <summary>
    /// ChameleonPaint's picker as the prototype's paint sheet on the right edge: "Paint" and a
    /// close button, the colour and shade bars, the material and brush sliders with their names,
    /// the four tools (Brush, Fill, Erase, Sample) and Done. Laid out in design units: the sheet is
    /// scaled to the canvas (ChameleonPaint.DockPanel, from PaintboxHud's layout).
    /// </summary>
    public static void StylePaintSheet(Transform canvas, ChameleonPaint painter)
    {
        var panel = canvas != null ? canvas.Find("PickerPanel") as RectTransform : null;
        if (panel == null || panel.Find("PbSheet") != null) return;

        // The canvas made 1080 units tall, so the sheet's words are sharp; the brush cursor and the
        // sampling loupe keep their size on screen.
        float shrink = Paintbox.Canvas1080(canvas.gameObject);
        if (shrink > 1.01f)
        {
            foreach (string keep in new[] { "CursorIcon", "EyedropLoupe" })
            {
                var rt = canvas.Find(keep) as RectTransform;
                if (rt == null) continue;
                rt.localScale = rt.localScale / shrink;
                rt.anchoredPosition /= shrink;
            }
        }

        panel.anchorMin = panel.anchorMax = panel.pivot = Vector2.one;
        panel.sizeDelta = new Vector2(PaintSheetWidth, 806f);
        panel.localScale = Vector3.one;
        Image own = panel.GetComponent<Image>();
        if (own != null) own.color = Color.clear;
        Image old = panel.Find("Panel")?.GetComponent<Image>();
        if (old != null) old.enabled = false;

        PaintboxFrame sheet = Paintbox.Frame(panel, "PbSheet", Paintbox.Paper, Paintbox.RadiusLarge, 4f, 6f, Paintbox.Ink, Paintbox.Ink);
        Paintbox.Fill(sheet.Root);
        sheet.Root.SetAsFirstSibling();

        Text title = Paintbox.Label(panel, "Paint", 44f, Paintbox.Ink, Paintbox.Weight.Title);
        Paintbox.TopBand(title.rectTransform, 18f, 68f, 28f, 110f);

        var close = panel.Find("CloseBtn") as RectTransform;
        if (close != null)
        {
            Paintbox.Pin(close, Vector2.one, new Vector2(-20f, -18f), new Vector2(70f, 70f));
            close.localScale = Vector3.one;
            PaintboxSkin.Hud(close, "X", null, false, Paintbox.Tone.Paper, "X");
        }

        SheetLabel(panel, "Colour", 98f);
        Bar(panel, "HueBar", 134f);
        SheetLabel(panel, "Shade", 210f);
        Bar(panel, "LightnessBar", 246f);

        SheetLabel(panel, "Metallic", 326f);
        Slider(panel, "MetallicBox", "MetallicHandle", 370f);
        TopLeft(panel.Find("MetallicIcon") as RectTransform, new Vector2(388f, 355f), new Vector2(52f, 52f));
        SheetLabel(panel, "Roughness", 410f);
        Slider(panel, "RoughnessBox", "RoughnessHandle", 454f);
        TopLeft(panel.Find("RoughnessIcon") as RectTransform, new Vector2(388f, 439f), new Vector2(52f, 52f));
        SheetLabel(panel, "Brush size", 494f);
        Slider(panel, "SizeTrack", "SizeHandle", 538f);
        var ring = panel.Find("SizeRing") as RectTransform;
        TopLeft(ring, new Vector2(388f, 523f), new Vector2(52f, 52f));
        if (ring != null && ring.Find("SizeDot") is RectTransform dot) dot.sizeDelta = new Vector2(26f, 26f);
        foreach (string value in new[] { "MetallicBox/MetallicVal", "RoughnessBox/RoughnessVal" })
        {
            Text t = panel.Find(value)?.GetComponent<Text>();
            if (t != null) t.enabled = false;
        }

        // The tools, as the prototype orders them; the one in use shows its "on" colour.
        string[,] tools = { { "BrushBtn", "Brush", "Paintbrush" }, { "FillBtn", "Fill", "PaintBucket" }, { "EraseBtn", "Erase", "Eraser" }, { "EyeBtn", "Sample", "Pipette" } };
        for (int i = 0; i < 4; i++)
        {
            var tool = panel.Find(tools[i, 0]) as RectTransform;
            if (tool == null) continue;
            TopLeft(tool, new Vector2(28f + i * 106f, 588f), new Vector2(94f, 96f));
            PaintboxHudButton skin = PaintboxHudButton.Apply(tool.gameObject, tools[i, 2], tools[i, 1], false, Paintbox.Tone.Paper, "BaseIcon", "Icon");
            if (skin != null) skin.OnWhenChildShown = "Icon";
        }

        Button done = Paintbox.Button(panel, "Done", Paintbox.Tone.Primary, "Check", () =>
        {
            AudioManager.ButtonTap();
            if (painter != null) painter.CloseDrawing();
        }, 30f);
        done.name = "PaintDone";
        TopLeft((RectTransform)done.transform, new Vector2(28f, 706f), new Vector2(414f, 76f));
    }

    private static void SheetLabel(RectTransform panel, string text, float top)
    {
        Text label = Paintbox.Label(panel, text, 22f, Paintbox.Muted, Paintbox.Weight.Bold);
        Paintbox.TopBand(label.rectTransform, top, 32f, 30f, 30f);
    }

    private static void TopLeft(RectTransform rt, Vector2 at, Vector2 size)
    {
        if (rt == null) return;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(at.x, -at.y);
        rt.sizeDelta = size;
    }

    private static void Bar(RectTransform panel, string name, float top)
    {
        var bar = panel.Find(name) as RectTransform;
        TopLeft(bar, new Vector2(28f, top), new Vector2(414f, 64f));
        if (bar != null && bar.Find("Pointer") is RectTransform pointer) pointer.sizeDelta = new Vector2(40f, 40f);
    }

    private static void Slider(RectTransform panel, string name, string handle, float top)
    {
        var track = panel.Find(name) as RectTransform;
        TopLeft(track, new Vector2(28f, top), new Vector2(330f, 22f));
        if (track != null && track.Find(handle) is RectTransform knob) knob.sizeDelta = new Vector2(44f, 44f);
    }

    // ================= The round's pill =================

    /// <summary>
    /// The round's pill, top centre (the prototype's): hiders left, the clock and seekers left on
    /// dark glass. MatchHUD's ring and figures go on being kept up to date, but are not drawn.
    /// </summary>
    public static void StyleRoundPill(Transform matchHud)
    {
        if (matchHud == null || matchHud.Find("PbUnits") != null) return;

        foreach (Transform t in matchHud.GetComponentsInChildren<Transform>(true))
        {
            if (t.name.StartsWith("Fug") || t.name.StartsWith("Hun") || t.name == "RingTrack" || t.name == "RingFill")
            {
                Image image = t.GetComponent<Image>();
                if (image != null) image.enabled = false;
            }
        }
        Text oldTimer = matchHud.Find("RingTrack/TimerText")?.GetComponent<Text>();
        if (oldTimer != null) oldTimer.enabled = false;   // still counting: the play tests read it

        RectTransform units = PaintboxUnits.Create(matchHud, "PbUnits");
        Image pill = Paintbox.Rounded(units, "RoundPill", Paintbox.Glass, 38f);
        RectTransform prt = pill.rectTransform;
        prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 1f);
        prt.anchoredPosition = new Vector2(0f, -EdgeY);
        prt.sizeDelta = new Vector2(400f, 76f);
        Outline edge = pill.gameObject.AddComponent<Outline>();
        edge.effectColor = Paintbox.GlassEdge;
        edge.effectDistance = new Vector2(3f, -3f);
        HorizontalLayoutGroup row = pill.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(24, 24, 8, 8);
        row.spacing = 16f;
        row.childAlignment = TextAnchor.MiddleCenter;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = false;
        ContentSizeFitter fit = pill.gameObject.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        PaintboxRoundPill values = pill.gameObject.AddComponent<PaintboxRoundPill>();
        values.Hiders = Part(prt, "Hiders", "PaintRoller", 32f, Paintbox.Hex(0xbdeeda));
        PillDivider(prt);
        values.Clock = Part(prt, "Clock", "Timer", 40f, Color.white);
        values.Clock.name = "RoundClock";
        PillDivider(prt);
        values.Seekers = Part(prt, "Seekers", "ScanEye", 32f, Paintbox.Hex(0xffc29d));
    }

    private static Text Part(RectTransform pill, string name, string icon, float size, Color ink)
    {
        RectTransform part = Paintbox.NewRect(pill, name);
        HorizontalLayoutGroup h = part.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 8f;
        h.childAlignment = TextAnchor.MiddleCenter;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = h.childForceExpandHeight = false;
        Image glyph = Paintbox.Image(part, "Icon", Paintbox.Icon(icon), ink);
        glyph.preserveAspect = true;
        LayoutElement le = glyph.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = le.preferredHeight = size * 0.8f;
        Text text = Paintbox.Label(part, "", size, ink, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        return text;
    }

    private static void PillDivider(RectTransform pill)
    {
        Image line = Paintbox.Image(pill, "Divider", null, new Color(1f, 1f, 1f, 0.32f));
        LayoutElement le = line.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = 2f;
        le.preferredHeight = 44f;
    }
}

/// <summary>The round pill's numbers: hiders left, the time left (mm:ss), seekers left.</summary>
public sealed class PaintboxRoundPill : MonoBehaviour
{
    public Text Hiders, Clock, Seekers;
    private GameModeManager gmm;
    private int shownSecs = -2, shownHiders = -1, shownSeekers = -1;

    private void LateUpdate()
    {
        if (gmm == null) gmm = FindAnyObjectByType<GameModeManager>();
        if (gmm == null) return;

        int hiders = gmm.FugitivesLeft, seekers = gmm.HuntersLeft;
        int secs = gmm.NetworkRound && gmm.NetTimerWaiting ? -1 : Mathf.Max(0, gmm.TimerSeconds);
        if (hiders != shownHiders) { shownHiders = hiders; Hiders.text = hiders.ToString(); }
        if (seekers != shownSeekers) { shownSeekers = seekers; Seekers.text = seekers.ToString(); }
        if (secs != shownSecs)
        {
            shownSecs = secs;
            Clock.text = secs < 0 ? "--:--" : $"{secs / 60:00}:{secs % 60:00}";
            Clock.color = secs >= 0 && secs <= 10 ? Paintbox.Hot : Color.white;   // the last seconds
        }
    }
}

/// <summary>
/// A glass pill behind a status line over the game ("HIDE! Seekers search in 30s"): the
/// prototype's flash. As wide as the words, and not drawn when there are none.
/// </summary>
public sealed class PaintboxTextPill : MonoBehaviour
{
    private Text source;
    private Image pill;

    public static void Attach(Text text)
    {
        if (text == null || text.transform.parent == null || text.transform.parent.Find(text.name + "Pill") != null) return;
        Image image = Paintbox.Image(text.transform.parent, text.name + "Pill", null, Paintbox.Glass);
        UiArt.Apply(image, UiArt.Rounded, 30f);
        Outline edge = image.gameObject.AddComponent<Outline>();
        edge.effectColor = Paintbox.GlassEdge;
        edge.effectDistance = new Vector2(3f, -3f);
        image.transform.SetSiblingIndex(text.transform.GetSiblingIndex());   // just behind the words
        PaintboxTextPill p = image.gameObject.AddComponent<PaintboxTextPill>();
        p.source = text;
        p.pill = image;
        p.LateUpdate();
    }

    private void LateUpdate()
    {
        if (source == null) { Destroy(gameObject); return; }
        bool show = source.isActiveAndEnabled && !string.IsNullOrWhiteSpace(source.text) && source.color.a > 0.05f;
        if (pill.enabled != show) pill.enabled = show;
        if (!show) return;

        RectTransform s = source.rectTransform, r = pill.rectTransform;
        Vector2 box = s.rect.size;
        float w = Mathf.Min(source.preferredWidth, box.x), h = Mathf.Min(source.preferredHeight, box.y);
        float padX = source.fontSize * 0.9f, padY = source.fontSize * 0.28f;

        // The words' centre: the box's centre, moved up or down as the text is aligned.
        Vector2 centre = s.anchoredPosition + (new Vector2(0.5f, 0.5f) - s.pivot) * box;
        int row = (int)source.alignment / 3;   // 0 upper, 1 middle, 2 lower
        if (row == 0) centre.y += (box.y - h) * 0.5f;
        else if (row == 2) centre.y -= (box.y - h) * 0.5f;

        r.anchorMin = s.anchorMin;
        r.anchorMax = s.anchorMax;
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = centre;
        r.sizeDelta = new Vector2(w + padX * 2f, h + padY * 2f);
    }
}
