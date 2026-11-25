using System;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shop (design B, 2026-11-24). Everything in it is the server's (ShopInventory): the
/// categories as tabs in the server's order, each category's items with their name, picture,
/// description, price and numbers, ownership and rounds; the operator changes all of it from the
/// admin panel and the game shows it at the next listing, no update needed. Any number of
/// categories (the tabs scroll) and items (the grid scrolls and fits as many columns as the screen
/// holds). A category with several kinds of item gets filter chips (weapons: All, Weapons, Ammo).
///
/// A card opens the item's page (its picture, what it does, its numbers, and Buy / Equip); buying
/// asks for confirmation with the balance before and after, then shows a receipt. Coin packs are
/// the "coins" category's (the server's top-up packages), paid at the wallet provider's checkout.
/// Opened over anything (Home, the setup, a round) through ShopUI, which keeps its entry points.
/// </summary>
public sealed class ShopScreen : MonoBehaviour
{
    private const int CanvasOrder = 58;           // over the HUD and the setup, under notices (90)
    private const float Gap = 22f;
    private const float TallCardMin = 300f;
    private const float WideCardMin = 560f;

    private static ShopScreen instance;

    private Canvas canvas;
    private RectTransform page, gridView, detailView, tabsContent, chipsRow, gridContent, modalLayer;
    private ScrollRect gridScroll, tabsScroll;
    private GridLayoutGroup grid;
    private Text title, sectionTitle, sectionCount, stateText;
    private Button retryButton;
    private RectTransform stateBox;
    private Image spinner;

    private string currentCategory;
    private int currentFilter = -1;               // -1: all kinds; else an ItemKind
    private string shownDetail;                   // the item whose page is open, or null
    private Action onClosed;
    private bool built, dirty, busy;
    private float lastWidth = -1f;
    private string tabsFingerprint = "";
    private bool wideCards;
    private int itemCount;

    /// <summary>Whether the shop is on screen.</summary>
    public static bool IsOpen => instance != null && instance.canvas != null && instance.canvas.gameObject.activeSelf;

    /// <summary>Opens the shop on its Coins: the category the server marks "coins" (the coin balance's tap).</summary>
    public const string CoinsCategory = "@coins";
    private bool wantCoins;

    /// <summary>The open shop goes to its Coins (the balance tapped inside it).</summary>
    public static void ShowCoins()
    {
        if (!IsOpen) return;
        instance.CloseModal();
        instance.CloseDetail();
        foreach (MetaShopCategory c in ShopInventory.Categories)
            if (c.Special == "coins") { instance.SelectCategory(c.Id); return; }
    }

    /// <summary>The category on show (the play tests read it).</summary>
    public static string CurrentCategory => instance != null ? instance.currentCategory : null;

    /// <summary>Opens the shop on its first category (or <paramref name="category"/>); <paramref name="closed"/> runs when it closes.</summary>
    public static void Open(Action closed, string category = null)
    {
        if (instance == null)
        {
            var go = new GameObject("ShopScreen");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ShopScreen>();
        }
        instance.Show(closed, category);
    }

    /// <summary>Closes the shop if it is open (a match is starting under it).</summary>
    public static void CloseIfOpen()
    {
        if (IsOpen) instance.Close();
    }

    /// <summary>Shows an item's page in the open shop (the play tests use it).</summary>
    public static void ShowItem(string itemId)
    {
        if (IsOpen && ShopInventory.TryItem(itemId, out MetaItem item)) instance.OpenDetail(item);
    }

    // ================= Showing and closing =================

    private void Show(Action closed, string category)
    {
        AudioManager.ButtonTap();
        if (!built) Build();
        onClosed = closed;
        canvas.gameObject.SetActive(true);
        CloseModal();
        CloseDetail();
        wantCoins = category == CoinsCategory;
        if (!wantCoins && !string.IsNullOrEmpty(category)) currentCategory = category;
        if (currentCategory == null && ShopInventory.Categories.Count > 0) currentCategory = ShopInventory.Categories[0].Id;
        currentFilter = -1;
        dirty = true;
        listCheckAt = Time.unscaledTime + ListCheckSeconds;   // this opening asks for the listing itself

        ShopInventory.Changed += MarkDirty;
        Shoes.Changed += MarkDirty;
        PendingTopUp.Settled += HandleTopUpSettled;

        // Prices, ownership, rounds and the balance as the server has them now.
        ShowState(ShopInventory.Loaded ? null : "Loading the shop...", false);
        ShopInventory.Refresh(error =>
        {
            if (!IsOpen) return;
            if (error != null && !ShopInventory.Loaded) ShowState(error, true);
            else if (error != null) Notices.Show(NoticeKind.Error, error);
            dirty = true;
        });
    }

    private void Close()
    {
        AudioManager.ButtonTap();
        ShopInventory.Changed -= MarkDirty;
        Shoes.Changed -= MarkDirty;
        PendingTopUp.Settled -= HandleTopUpSettled;
        CloseModal();
        canvas.gameObject.SetActive(false);
        Action closed = onClosed;
        onClosed = null;
        closed?.Invoke();
    }

    private void MarkDirty() => dirty = true;

    // A credit is confirmed by CoinNotice over every screen; the shop says only how a failed one ended.
    private void HandleTopUpSettled(PendingTopUp.Entry entry, PaymentState state)
    {
        if (state != PaymentState.Credited) Notices.Show(NoticeKind.Error, PendingTopUp.MessageFor(entry, state));
    }

    private float saleCheckAt, listCheckAt;
    private bool saleRefreshing;

    // While the shop is open it asks for the listing again now and then, so items and categories an
    // operator adds or changes in the admin panel show up without closing it (2026-11-25).
    private const float ListCheckSeconds = 15f;

    private void Update()
    {
        if (!IsOpen) return;
        if (spinner != null && spinner.gameObject.activeInHierarchy) spinner.rectTransform.Rotate(0f, 0f, -300f * Time.unscaledDeltaTime);

        if (Time.unscaledTime >= listCheckAt && !saleRefreshing && !busy)
        {
            if (listCheckAt > 0f) ShopInventory.Refresh(_ => { });   // redraws only if the listing changed
            listCheckAt = Time.unscaledTime + ListCheckSeconds;
        }

        // A sale that ends while the shop is open: its prices again, from the server (SDK 0.9.9).
        if (Time.unscaledTime >= saleCheckAt && !saleRefreshing)
        {
            saleCheckAt = Time.unscaledTime + 5f;
            long ends = ShopInventory.NextSaleEndUnix;
            if (ends > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ends)
            {
                saleRefreshing = true;
                ShopInventory.Refresh(_ => { saleRefreshing = false; dirty = true; });
            }
        }

        float width = gridScroll != null ? ((RectTransform)gridScroll.transform).rect.width : 0f;
        if (Mathf.Abs(width - lastWidth) > 1f)
        {
            lastWidth = width;
            FitGrid();
            FitTabs();
        }

        if (!dirty) return;
        dirty = false;
        Refresh();
    }

    // ================= Building =================

    private void Build()
    {
        built = true;
        canvas = OnlineUI.CreateCanvas("ShopScreenCanvas", transform, CanvasOrder);
        Paintbox.Backdrop(canvas.transform);
        page = Paintbox.SafeArea(Paintbox.NewRect(canvas.transform, "Page"));

        title = Paintbox.TopBar(page, "Shop", Back, out RectTransform right);
        Paintbox.CoinPill(right, out RectTransform pill);
        pill.sizeDelta = new Vector2(240f, 76f);

        BuildGridView();
        BuildDetailView();

        modalLayer = Paintbox.Fill(Paintbox.NewRect(canvas.transform, "Modal"));
        modalLayer.gameObject.SetActive(false);
        canvas.gameObject.SetActive(false);
    }

    private void Back()
    {
        if (modalLayer.gameObject.activeSelf) { AudioManager.ButtonTap(); CloseModal(); return; }
        if (shownDetail != null) { AudioManager.ButtonTap(); CloseDetail(); return; }
        Close();
    }

    private void BuildGridView()
    {
        gridView = Paintbox.Fill(Paintbox.NewRect(page, "Catalog"), Paintbox.Gutter, Paintbox.Gutter, 124f, 0f);

        // The tabs: one per server category, scrolling sideways when there are more than fit.
        RectTransform tabs = Paintbox.TopBand(Paintbox.NewRect(gridView, "Tabs"), 0f, 86f);
        tabsScroll = tabs.gameObject.AddComponent<ScrollRect>();
        tabsScroll.horizontal = true;
        tabsScroll.vertical = false;
        tabsScroll.movementType = ScrollRect.MovementType.Clamped;
        tabsScroll.scrollSensitivity = 30f;
        tabs.gameObject.AddComponent<RectMask2D>().padding = new Vector4(0f, -8f, 0f, -8f);
        Image hit = tabs.gameObject.AddComponent<Image>();
        hit.color = Color.clear;
        tabsContent = Paintbox.NewRect(tabs, "Content");
        tabsContent.anchorMin = new Vector2(0f, 0f);
        tabsContent.anchorMax = new Vector2(0f, 1f);
        tabsContent.pivot = new Vector2(0f, 0.5f);
        tabsContent.offsetMin = tabsContent.offsetMax = Vector2.zero;
        HorizontalLayoutGroup tl = tabsContent.gameObject.AddComponent<HorizontalLayoutGroup>();
        tl.spacing = 10f;
        tl.padding = new RectOffset(4, 4, 6, 12);
        tl.childControlWidth = tl.childControlHeight = true;
        tl.childForceExpandWidth = true;
        tl.childForceExpandHeight = true;
        ContentSizeFitter tf = tabsContent.gameObject.AddComponent<ContentSizeFitter>();
        tf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        tabsScroll.content = tabsContent;
        tabsScroll.viewport = tabs;

        Image rule = Paintbox.Divider(gridView);
        Paintbox.TopBand(rule.rectTransform, 92f, 3f);

        // The section's heading: its name, and its filter chips or how many items it has.
        RectTransform head = Paintbox.TopBand(Paintbox.NewRect(gridView, "Heading"), 108f, 74f);
        sectionTitle = Paintbox.Label(head, "", Paintbox.HeadingSize, Paintbox.Ink, Paintbox.Weight.Title);
        sectionTitle.horizontalOverflow = HorizontalWrapMode.Overflow;
        Paintbox.Fill(sectionTitle.rectTransform, 0f, 600f, 0f, 0f);
        sectionCount = Paintbox.Label(head, "", Paintbox.TinySize, Paintbox.Muted, Paintbox.Weight.Bold, TextAnchor.MiddleRight);
        Paintbox.Fill(sectionCount.rectTransform, 600f, 0f, 0f, 0f);
        chipsRow = Paintbox.NewRect(head, "Filters");
        chipsRow.anchorMin = new Vector2(1f, 0f);
        chipsRow.anchorMax = new Vector2(1f, 1f);
        chipsRow.pivot = new Vector2(1f, 0.5f);
        chipsRow.sizeDelta = new Vector2(800f, 0f);
        chipsRow.anchoredPosition = Vector2.zero;
        HorizontalLayoutGroup cl = chipsRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        cl.childAlignment = TextAnchor.MiddleRight;
        cl.spacing = 10f;
        cl.padding = new RectOffset(0, 4, 4, 10);
        cl.childControlWidth = cl.childControlHeight = true;
        cl.childForceExpandWidth = false;
        cl.childForceExpandHeight = true;

        // The grid, scrolling up and down.
        RectTransform view = Paintbox.Fill(Paintbox.NewRect(gridView, "Items"), 0f, 0f, 196f, 0f);
        gridScroll = view.gameObject.AddComponent<ScrollRect>();
        gridScroll.horizontal = false;
        gridScroll.vertical = true;
        gridScroll.movementType = ScrollRect.MovementType.Elastic;
        gridScroll.scrollSensitivity = 40f;
        view.gameObject.AddComponent<RectMask2D>().padding = new Vector4(-6f, 0f, -6f, 0f);
        Image viewHit = view.gameObject.AddComponent<Image>();
        viewHit.color = Color.clear;
        gridContent = Paintbox.NewRect(view, "Content");
        gridContent.anchorMin = new Vector2(0f, 1f);
        gridContent.anchorMax = new Vector2(1f, 1f);
        gridContent.pivot = new Vector2(0.5f, 1f);
        gridContent.offsetMin = gridContent.offsetMax = Vector2.zero;
        grid = gridContent.gameObject.AddComponent<GridLayoutGroup>();
        grid.spacing = new Vector2(Gap, Gap);
        grid.padding = new RectOffset(4, 4, 6, 40);
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.childAlignment = TextAnchor.UpperLeft;
        ContentSizeFitter gf = gridContent.gameObject.AddComponent<ContentSizeFitter>();
        gf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        gridScroll.content = gridContent;
        gridScroll.viewport = view;

        // Loading, an error, or nothing to show.
        stateBox = Paintbox.Fill(Paintbox.NewRect(view, "State"), 0f, 0f, 40f, 0f);
        spinner = Paintbox.IconImage(stateBox, "LoaderCircle", 64f, Paintbox.Muted);
        Paintbox.Pin(spinner.rectTransform, new Vector2(0.5f, 0.75f), Vector2.zero, new Vector2(64f, 64f));
        stateText = Paintbox.Label(stateBox, "", Paintbox.BodySize, Paintbox.Muted, Paintbox.Weight.Regular, TextAnchor.MiddleCenter);
        Paintbox.Pin(stateText.rectTransform, new Vector2(0.5f, 0.55f), Vector2.zero, new Vector2(1100f, 120f));
        retryButton = Paintbox.Button(stateBox, "Try again", Paintbox.Tone.Primary, "RefreshCw", Retry);
        Paintbox.Pin((RectTransform)retryButton.transform, new Vector2(0.5f, 0.35f), Vector2.zero, new Vector2(300f, 84f));
        stateBox.gameObject.SetActive(false);
    }

    private void Retry()
    {
        AudioManager.ButtonTap();
        ShowState("Loading the shop...", false);
        ShopInventory.Refresh(error =>
        {
            if (!IsOpen) return;
            if (error != null && !ShopInventory.Loaded) ShowState(error, true);
            dirty = true;
        });
    }

    private void ShowState(string message, bool canRetry)
    {
        if (stateBox == null) return;
        stateBox.gameObject.SetActive(message != null);
        if (message == null) return;
        stateText.text = message;
        spinner.gameObject.SetActive(!canRetry && message.StartsWith("Loading", StringComparison.Ordinal));
        retryButton.gameObject.SetActive(canRetry);
    }

    // ================= The catalog =================

    private void Refresh()
    {
        RebuildTabs();
        if (shownDetail != null && ShopInventory.TryItem(shownDetail, out MetaItem open)) FillDetail(open);
        RebuildGrid();
    }

    private void RebuildTabs()
    {
        IReadOnlyList<MetaShopCategory> cats = ShopInventory.Categories;
        if (wantCoins && cats.Count > 0)
        {
            // Opened from the coin balance: the Coins, once the server has said which they are.
            foreach (MetaShopCategory c in cats) if (c.Special == "coins") { currentCategory = c.Id; break; }
            wantCoins = false;
        }
        if (currentCategory == null || !HasCategory(cats, currentCategory))
            currentCategory = cats.Count > 0 ? cats[0].Id : null;

        var fp = new System.Text.StringBuilder(currentCategory);
        foreach (MetaShopCategory c in cats) fp.Append('|').Append(c.Id).Append(c.Name).Append(c.Icon);
        if (fp.ToString() == tabsFingerprint) return;
        tabsFingerprint = fp.ToString();

        foreach (Transform child in tabsContent) Destroy(child.gameObject);
        foreach (MetaShopCategory c in cats)
        {
            MetaShopCategory category = c;
            bool on = c.Id == currentCategory;
            Button tab = Paintbox.Button(tabsContent, string.IsNullOrEmpty(c.Name) ? c.Id : c.Name,
                                         on ? Paintbox.Tone.Secondary : Paintbox.Tone.Paper, string.IsNullOrEmpty(c.Icon) ? c.Id : c.Icon,
                                         () => SelectCategory(category.Id), 27f);
            tab.name = "Tab_" + c.Id;
            LayoutElement le = tab.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 220f;
            le.preferredWidth = 260f;
            le.flexibleWidth = 1f;
            if (!on) Quiet(tab.GetComponent<PaintboxFrame>());
        }

        FitTabs();
    }

    // Spread across the strip when they fit; scrolling sideways when they don't.
    private void FitTabs()
    {
        if (tabsScroll == null) return;
        var strip = (RectTransform)tabsScroll.transform;
        if (strip.rect.width < 10f) return;
        float wanted = tabsContent.childCount * 270f;
        bool fits = wanted <= strip.rect.width;
        tabsContent.GetComponent<ContentSizeFitter>().horizontalFit =
            fits ? ContentSizeFitter.FitMode.Unconstrained : ContentSizeFitter.FitMode.PreferredSize;
        tabsContent.anchorMin = new Vector2(0f, 0f);
        tabsContent.anchorMax = new Vector2(fits ? 1f : 0f, 1f);
        if (fits) tabsContent.offsetMin = tabsContent.offsetMax = Vector2.zero;
        LayoutRebuilder.MarkLayoutForRebuild(tabsContent);
    }

    // An unpicked tab or chip: no outline or shadow, its words muted.
    private static void Quiet(PaintboxFrame frame)
    {
        if (frame == null) return;
        frame.Fill.color = Color.clear;
        if (frame.Edge != null) frame.Edge.enabled = false;
        if (frame.Shade != null) frame.Shade.enabled = false;
        frame.ShadowDepth = 0f;
        foreach (Text t in frame.Body.GetComponentsInChildren<Text>(true)) t.color = Paintbox.Muted;
        foreach (Image i in frame.Body.GetComponentsInChildren<Image>(true)) if (i.name == "Icon") i.color = Paintbox.Muted;
    }

    private static bool HasCategory(IReadOnlyList<MetaShopCategory> cats, string id)
    {
        foreach (MetaShopCategory c in cats) if (c.Id == id) return true;
        return false;
    }

    private static MetaShopCategory CategoryById(string id)
    {
        foreach (MetaShopCategory c in ShopInventory.Categories) if (c.Id == id) return c;
        return new MetaShopCategory { Id = id, Name = id };
    }

    private void SelectCategory(string id)
    {
        AudioManager.ButtonTap();
        if (id == currentCategory) return;
        currentCategory = id;
        currentFilter = -1;
        tabsFingerprint = "";
        gridContent.anchoredPosition = Vector2.zero;
        dirty = true;
    }

    private void SelectFilter(int kind)
    {
        AudioManager.ButtonTap();
        currentFilter = kind;
        gridContent.anchoredPosition = Vector2.zero;
        dirty = true;
    }

    // What a category's grid shows, in the server's order: everything but ammo packs.
    private static List<MetaItem> GridItems(string categoryId)
    {
        List<MetaItem> items = ShopInventory.ItemsIn(categoryId);
        items.RemoveAll(i => i.Kind == ItemKind.Ammo);
        return items;
    }

    // A weapon's ammo packs, wherever the server lists them.
    private static List<MetaItem> AmmoPacksFor(int weaponId)
    {
        var packs = new List<MetaItem>();
        foreach (MetaShopCategory c in ShopInventory.Categories)
            foreach (MetaItem i in ShopInventory.ItemsIn(c.Id))
                if (i.Kind == ItemKind.Ammo && (int)i.GameId == weaponId && !packs.Exists(p => p.Id == i.Id)) packs.Add(i);
        return packs;
    }

    private void RebuildGrid()
    {
        foreach (Transform child in gridContent) Destroy(child.gameObject);
        foreach (Transform child in chipsRow) Destroy(child.gameObject);

        MetaShopCategory category = CategoryById(currentCategory);
        sectionTitle.text = string.IsNullOrEmpty(category.Name) ? category.Id : category.Name;
        bool coins = category.Special == "coins";
        wideCards = coins;

        if (!ShopInventory.Loaded)
        {
            sectionCount.text = "";
            if (!stateBox.gameObject.activeSelf) ShowState("Loading the shop...", false);
            return;
        }

        int shown = 0;
        if (coins)
        {
            if (!ShopInventory.TopUpsLoaded) ShowState("Loading the coin packs...", false);
            else
            {
                foreach (MetaTopUpPackage package in ShopInventory.Packages)
                {
                    PackCard(package);
                    shown++;
                }
                ShowState(shown == 0 ? "Coin packs aren't available on this server right now." : null, false);
            }
        }

        // The category's items, and its kinds for the filter chips. Ammo is bought on its
        // weapon's page (2026-11-25), so its packs are not cards of their own.
        List<MetaItem> items = GridItems(currentCategory);
        var kinds = new List<ItemKind>();
        foreach (MetaItem item in items) if (!kinds.Contains(item.Kind)) kinds.Add(item.Kind);
        if (currentFilter >= 0 && !kinds.Contains((ItemKind)currentFilter)) currentFilter = -1;

        foreach (MetaItem item in items)
        {
            if (currentFilter >= 0 && (int)item.Kind != currentFilter) continue;
            ItemCard(item);
            shown++;
        }
        if (!coins) ShowState(shown == 0 ? "Nothing here yet. Check back soon!" : null, false);

        if (kinds.Count > 1 && !coins)
        {
            sectionCount.text = "";
            Chip("All", -1);
            foreach (ItemKind k in kinds) Chip(KindName(k), (int)k);
        }
        else sectionCount.text = L.F(shown == 1 ? "{0} ITEM" : "{0} ITEMS", shown);

        itemCount = shown;
        FitGrid();
    }

    private void Chip(string text, int kind)
    {
        bool on = currentFilter == kind;
        Button chip = Paintbox.Button(chipsRow, text, on ? Paintbox.Tone.Secondary : Paintbox.Tone.Paper, null, () => SelectFilter(kind), 25f, 22f);
        chip.name = "Filter_" + text;
        LayoutElement le = chip.gameObject.AddComponent<LayoutElement>();
        le.minWidth = 120f;
        le.preferredWidth = Mathf.Max(120f, text.Length * 16f + 60f);
        if (!on) Quiet(chip.GetComponent<PaintboxFrame>());
    }

    private static string KindName(ItemKind kind)
    {
        switch (kind)
        {
            case ItemKind.Weapon: return "Weapons";
            case ItemKind.Ammo: return "Ammo";
            case ItemKind.Avatar: return "Characters";
            case ItemKind.Pose: return "Poses";
            case ItemKind.Shoes: return "Shoes";
            case ItemKind.Map: return "Maps";
            case ItemKind.Feature: return "Features";
            default: return kind.ToString();
        }
    }

    // As many columns as the width holds, each card stretched to share it.
    private void FitGrid()
    {
        if (grid == null) return;
        float width = ((RectTransform)gridScroll.transform).rect.width - grid.padding.left - grid.padding.right;
        if (width <= 10f) return;
        float min = wideCards ? WideCardMin : TallCardMin;
        int columns = Mathf.Max(1, Mathf.FloorToInt((width + Gap) / (min + Gap)));
        float cell = (width - Gap * (columns - 1)) / columns;
        float height = wideCards ? Mathf.Clamp(cell * 0.46f, 240f, 300f) : Mathf.Clamp(cell * 0.86f, 290f, 380f);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = columns;
        grid.cellSize = new Vector2(Mathf.Floor(cell), Mathf.Floor(height));
    }

    // ================= Cards =================

    private void ItemCard(MetaItem item)
    {
        PaintboxFrame frame = Paintbox.CardFrame(gridContent, "Item_" + item.Id);
        Button button = frame.Root.gameObject.AddComponent<Button>();
        button.targetGraphic = frame.Fill;
        button.transition = Selectable.Transition.None;
        Navigation none = button.navigation;
        none.mode = Navigation.Mode.None;
        button.navigation = none;
        MetaItem picked = item;
        button.onClick.AddListener(() => { AudioManager.ButtonTap(); OpenDetail(picked); });
        frame.Root.gameObject.AddComponent<PaintboxPress>();

        // The picture in a soft well at the top.
        Image well = Paintbox.Rounded(frame.Body, "Picture", Paintbox.Card, 20f);
        RectTransform w = well.rectTransform;
        w.anchorMin = new Vector2(0f, 0f);
        w.anchorMax = new Vector2(1f, 1f);
        w.offsetMin = new Vector2(10f, 136f);
        w.offsetMax = new Vector2(-10f, -10f);
        Image art = Paintbox.Image(w, "Art", null, Color.white);
        art.preserveAspect = true;
        Paintbox.Fill(art.rectTransform, 8f, 8f, 6f, 6f);
        ShopArt.Load(item, sprite => { if (art != null) { art.sprite = sprite; art.enabled = sprite != null; } });

        // What it is now: equipped, owned, rounds.
        string tag = null, tagIcon = null;
        Color tagFill = Paintbox.Wash, tagInk = Paintbox.WashInk;
        bool owned = ShopInventory.IsOwned(item);
        // A weapon's rounds first (its foot says whether it is equipped); then equipped, owned.
        if (owned && item.Kind == ItemKind.Weapon && item.GameId != WeaponSpecs.Standard)
        {
            tag = L.F("{0} rounds", Mathf.Max(0, item.OwnedAmmo));
            tagIcon = "Layers";
            if (item.OwnedAmmo <= 0) { tagFill = Paintbox.BadWash; tagInk = Paintbox.Bad; }
        }
        else if (ShopInventory.IsEquipped(item)) { tag = item.Kind == ItemKind.Shoes ? "Worn" : "Equipped"; tagIcon = "CircleCheck"; }
        else if (owned) { tag = "Owned"; tagIcon = "Check"; }
        else if (item.Kind == ItemKind.Ammo) { tag = L.F("x{0}", Mathf.Max(1, item.Ammo)); tagIcon = "Layers"; tagFill = Paintbox.Hot; tagInk = Paintbox.HotInk; }
        if (tag != null)
        {
            RectTransform t = Paintbox.Tag(w, tag, tagFill, tagInk, tagIcon);
            t.anchorMin = t.anchorMax = t.pivot = new Vector2(1f, 1f);
            t.anchoredPosition = new Vector2(-8f, -8f);
        }

        // On sale (SDK 0.9.9): how much off, in the picture's other corner.
        if (item.OnSale && !(owned && item.Kind != ItemKind.Ammo))
        {
            RectTransform sale = Paintbox.Tag(w, $"-{item.PercentOff}%", Paintbox.Bad, Color.white, "Tag");
            sale.name = "SaleTag";
            sale.anchorMin = sale.anchorMax = sale.pivot = new Vector2(0f, 1f);
            sale.anchoredPosition = new Vector2(8f, -8f);
        }

        // Name, a line about it, and its price or state.
        Text name = Paintbox.Label(frame.Body, DisplayName(item), 28f, Paintbox.Ink, Paintbox.Weight.Bold);
        Paintbox.Fit(name, 18);
        name.horizontalOverflow = HorizontalWrapMode.Overflow;
        RectTransform n = name.rectTransform;
        n.anchorMin = new Vector2(0f, 0f);
        n.anchorMax = new Vector2(1f, 0f);
        n.pivot = new Vector2(0.5f, 0f);
        n.offsetMin = new Vector2(20f, 90f);
        n.offsetMax = new Vector2(-16f, 128f);
        name.horizontalOverflow = HorizontalWrapMode.Wrap;

        Text about = Paintbox.Label(frame.Body, item.Description ?? "", 21f, Paintbox.Muted);
        RectTransform a = about.rectTransform;
        a.anchorMin = new Vector2(0f, 0f);
        a.anchorMax = new Vector2(1f, 0f);
        a.pivot = new Vector2(0.5f, 0f);
        a.offsetMin = new Vector2(20f, 50f);
        a.offsetMax = new Vector2(-16f, 90f);
        about.alignment = TextAnchor.UpperLeft;

        RectTransform foot = Paintbox.NewRect(frame.Body, "Foot");
        foot.anchorMin = new Vector2(0f, 0f);
        foot.anchorMax = new Vector2(1f, 0f);
        foot.pivot = new Vector2(0.5f, 0f);
        foot.offsetMin = new Vector2(20f, 12f);
        foot.offsetMax = new Vector2(-16f, 48f);
        PriceOrState(foot, item, 25f);
    }

    // The price with its coin, or what the player has (Free, Owned, Equipped).
    private static void PriceOrState(RectTransform parent, MetaItem item, float size)
    {
        bool owned = ShopInventory.IsOwned(item);
        if (owned && item.Kind != ItemKind.Ammo)
        {
            Text state = Paintbox.Label(parent, ShopInventory.IsEquipped(item) ? (item.Kind == ItemKind.Shoes ? "Worn" : "Equipped")
                                               : ShopInventory.CanEquip(item) ? "Owned · tap to equip" : "Owned",
                                        size, Paintbox.Good, Paintbox.Weight.Bold);
            Paintbox.Fill(state.rectTransform);
            return;
        }

        if (item.OnSale)
        {
            SalePrice(parent, item.PriceCents, item.OriginalPriceCents, size);
            return;
        }

        if (item.PriceCents <= 0)
        {
            Text free = Paintbox.Label(parent, "Free", size, Paintbox.Ink, Paintbox.Weight.Bold);
            Paintbox.Fill(free.rectTransform);
            return;
        }

        Paintbox.CoinAmount(parent, CoinBalance.Format(item.PriceCents), size, Paintbox.Ink, out RectTransform row);
        Paintbox.Fill(row);
    }

    // A price on sale (SDK 0.9.9): the sale price with its coin, in red, then the usual one struck through.
    private static void SalePrice(RectTransform parent, long cents, long usualCents, float size)
    {
        Paintbox.CoinAmount(parent, cents <= 0 ? L.T("Free") : CoinBalance.Format(cents), size, Paintbox.Bad, out RectTransform row);
        row.name = "SalePrice";
        Paintbox.Fill(row);
        Text was = Paintbox.Label(row, CoinBalance.Format(usualCents), size * 0.8f, Paintbox.Muted, Paintbox.Weight.Regular);
        was.name = "UsualPrice";
        was.horizontalOverflow = HorizontalWrapMode.Overflow;
        Image strike = Paintbox.Image(was.rectTransform, "Strike", null, Paintbox.Muted);
        strike.raycastTarget = false;
        RectTransform s = strike.rectTransform;
        s.anchorMin = new Vector2(0f, 0.5f);
        s.anchorMax = new Vector2(1f, 0.5f);
        s.pivot = new Vector2(0.5f, 0.5f);
        s.anchoredPosition = Vector2.zero;
        s.sizeDelta = new Vector2(6f, Mathf.Max(2f, size * 0.09f));
    }

    // The sale's words over an item's price: its name, how much off and when it ends.
    private static string SaleLine(MetaItem item)
    {
        string ends = DateTimeOffset.FromUnixTimeSeconds(item.SaleEndsUnix).ToLocalTime().ToString("MMM d \u00b7 HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string what = string.IsNullOrEmpty(item.SaleName) ? L.T("Sale") : item.SaleName;
        return L.F("{0} \u00b7 {1}% off \u00b7 until {2}", what, item.PercentOff, ends);
    }

    private void PackCard(MetaTopUpPackage package)
    {
        PaintboxFrame frame = Paintbox.CardFrame(gridContent, "Pack_" + package.Id);
        Button button = frame.Root.gameObject.AddComponent<Button>();
        button.targetGraphic = frame.Fill;
        button.transition = Selectable.Transition.None;
        Navigation none = button.navigation;
        none.mode = Navigation.Mode.None;
        button.navigation = none;
        MetaTopUpPackage picked = package;
        button.onClick.AddListener(() => BuyPack(picked));
        frame.Root.gameObject.AddComponent<PaintboxPress>();

        Image well = Paintbox.Rounded(frame.Body, "Picture", Paintbox.Card, 20f);
        RectTransform w = well.rectTransform;
        w.anchorMin = new Vector2(0f, 0f);
        w.anchorMax = new Vector2(0.42f, 1f);
        w.offsetMin = new Vector2(10f, 10f);
        w.offsetMax = new Vector2(0f, -10f);
        Image art = Paintbox.Image(w, "Art", Paintbox.Art("coins"), Color.white);
        art.preserveAspect = true;
        Paintbox.Fill(art.rectTransform, 6f, 6f, 6f, 6f);

        bool waiting = PendingTopUp.TryFor(package.Id, out _);
        RectTransform info = Paintbox.NewRect(frame.Body, "Info");
        info.anchorMin = new Vector2(0.42f, 0f);
        info.anchorMax = new Vector2(1f, 1f);
        info.offsetMin = new Vector2(22f, 20f);
        info.offsetMax = new Vector2(-18f, -20f);

        Text coins = Paintbox.Label(info, L.F("{0} coins", CoinBalance.Format(package.CoinCents)), 34f, Paintbox.Ink, Paintbox.Weight.Bold);
        Paintbox.Fit(coins, 20);
        Paintbox.TopBand(coins.rectTransform, 30f, 48f);
        Text kind = Paintbox.Label(info, waiting ? "Waiting for payment · tap to reopen" : "Online coin pack", 22f, Paintbox.Muted);
        Paintbox.TopBand(kind.rectTransform, 80f, 32f);
        Image rule = Paintbox.Divider(info);
        Paintbox.TopBand(rule.rectTransform, 122f, 2f);
        Text price = Paintbox.Label(info, ShopInventory.MoneyText(package.PriceMinor, package.Currency), 27f, Paintbox.Ink, Paintbox.Weight.Bold);
        Paintbox.TopBand(price.rectTransform, 134f, 40f);
    }

    private void BuyPack(MetaTopUpPackage package)
    {
        AudioManager.ButtonTap();
        if (busy) return;
        busy = true;
        Notices.Show(NoticeKind.Purchase, "Opening the payment...", 2f);
        ShopInventory.StartTopUp(package, (opened, message) =>
        {
            busy = false;
            Notices.Show(opened ? NoticeKind.Purchase : NoticeKind.Error, message);
            dirty = true;
        });
    }

    private static string DisplayName(MetaItem item) => string.IsNullOrEmpty(item.Name) ? item.Id : item.Name;

    // ================= An item's page =================

    private Image detailArt;
    private Text detailKicker, detailTitle, detailAbout, detailHowTitle, detailHow, detailPriceLabel;
    private RectTransform detailStats, detailPrice, detailActions;

    private void BuildDetailView()
    {
        detailView = Paintbox.Fill(Paintbox.NewRect(page, "Detail"), Paintbox.Gutter, Paintbox.Gutter, 124f, 0f);

        PaintboxFrame left = Paintbox.Panel(detailView, "PicturePanel");
        left.Root.anchorMin = new Vector2(0f, 0f);
        left.Root.anchorMax = new Vector2(0.4f, 1f);
        left.Root.offsetMin = new Vector2(0f, 120f);
        left.Root.offsetMax = new Vector2(-12f, 0f);
        Image well = Paintbox.Rounded(left.Body, "Well", Paintbox.Card, 32f);
        Paintbox.Fill(well.rectTransform, 14f, 14f, 14f, 14f);
        detailArt = Paintbox.Image(well.rectTransform, "Art", null, Color.white);
        detailArt.preserveAspect = true;
        Paintbox.Fill(detailArt.rectTransform, 16f, 16f, 16f, 16f);

        // Previous / Next: the other items of the same category, without going back to the grid.
        detailPrev = Paintbox.IconButton(left.Body, "ChevronLeft", Paintbox.Tone.Paper, () => StepDetail(-1), 76f);
        detailPrev.name = "DetailPrevious";
        Paintbox.Pin((RectTransform)detailPrev.transform, new Vector2(0f, 0.5f), new Vector2(26f, 0f), new Vector2(76f, 76f));
        ((RectTransform)detailPrev.transform).pivot = new Vector2(0f, 0.5f);
        detailNext = Paintbox.IconButton(left.Body, "ChevronRight", Paintbox.Tone.Paper, () => StepDetail(1), 76f);
        detailNext.name = "DetailNext";
        Paintbox.Pin((RectTransform)detailNext.transform, new Vector2(1f, 0.5f), new Vector2(-26f, 0f), new Vector2(76f, 76f));
        ((RectTransform)detailNext.transform).pivot = new Vector2(1f, 0.5f);
        detailPlace = Paintbox.Label(left.Body, "", 24f, Paintbox.Muted, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
        detailPlace.name = "DetailPlace";
        Paintbox.Pin(detailPlace.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, 30f), new Vector2(200f, 36f));

        PaintboxFrame right = Paintbox.Panel(detailView, "InfoPanel");
        right.Root.anchorMin = new Vector2(0.4f, 0f);
        right.Root.anchorMax = new Vector2(1f, 1f);
        right.Root.offsetMin = new Vector2(12f, 120f);
        right.Root.offsetMax = Vector2.zero;

        RectTransform info = Paintbox.Fill(Paintbox.NewRect(right.Body, "Info"), 40f, 40f, 34f, 24f);
        VerticalLayoutGroup v = info.gameObject.AddComponent<VerticalLayoutGroup>();
        v.childAlignment = TextAnchor.UpperLeft;
        v.spacing = 10f;
        v.childControlWidth = v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;

        detailKicker = Line(info, "", Paintbox.TinySize, Paintbox.Muted, Paintbox.Weight.Bold, 30f);
        detailTitle = Line(info, "", 54f, Paintbox.Ink, Paintbox.Weight.Title, 70f);
        Paintbox.Fit(detailTitle, 30);
        detailAbout = Line(info, "", 28f, Paintbox.Muted, Paintbox.Weight.Regular, 76f);
        Image rule = Paintbox.Divider(info);
        rule.gameObject.AddComponent<LayoutElement>().preferredHeight = 3f;

        detailStats = Paintbox.NewRect(info, "Numbers");
        GridLayoutGroup sg = detailStats.gameObject.AddComponent<GridLayoutGroup>();
        sg.cellSize = new Vector2(330f, 64f);
        sg.spacing = new Vector2(16f, 8f);
        sg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        sg.constraintCount = 3;
        ContentSizeFitter sf = detailStats.gameObject.AddComponent<ContentSizeFitter>();
        sf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // A weapon's ammo: its packs, one picked; bought from the foot (2026-11-25).
        detailAmmoTitle = Line(info, "AMMO", Paintbox.TinySize, Paintbox.Muted, Paintbox.Weight.Bold, 30f);
        detailAmmo = Paintbox.NewRect(info, "AmmoPacks");
        detailAmmo.gameObject.AddComponent<LayoutElement>().preferredHeight = 72f;
        HorizontalLayoutGroup ah = detailAmmo.gameObject.AddComponent<HorizontalLayoutGroup>();
        ah.childAlignment = TextAnchor.MiddleLeft;
        ah.spacing = 12f;
        ah.childControlWidth = ah.childControlHeight = true;
        ah.childForceExpandWidth = ah.childForceExpandHeight = false;
        detailAmmoNote = Line(info, "", 24f, Paintbox.Muted, Paintbox.Weight.Regular, 34f);

        detailHowTitle = Line(info, "HOW TO USE", Paintbox.TinySize, Paintbox.Muted, Paintbox.Weight.Bold, 30f);
        detailHow = Line(info, "", 26f, Paintbox.Muted, Paintbox.Weight.Regular, 70f);

        // The foot: the price on the left, what can be done on the right.
        RectTransform foot = Paintbox.Fill(Paintbox.NewRect(detailView, "Foot"));
        foot.anchorMax = new Vector2(1f, 0f);
        foot.offsetMin = new Vector2(0f, 14f);
        foot.offsetMax = new Vector2(0f, 104f);
        Image footRule = Paintbox.Divider(foot);
        Paintbox.TopBand(footRule.rectTransform, -8f, 3f);
        detailPriceLabel = Paintbox.Label(foot, "PRICE", Paintbox.TinySize, Paintbox.Muted, Paintbox.Weight.Bold);
        Paintbox.Pin(detailPriceLabel.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(110f, 40f));
        detailPriceLabel.rectTransform.pivot = new Vector2(0f, 0.5f);
        detailPrice = Paintbox.NewRect(foot, "Price");
        Paintbox.Pin(detailPrice, new Vector2(0f, 0.5f), new Vector2(110f, 0f), new Vector2(520f, 50f));
        detailPrice.pivot = new Vector2(0f, 0.5f);

        detailActions = Paintbox.NewRect(foot, "Actions");
        detailActions.anchorMin = new Vector2(0.35f, 0f);
        detailActions.anchorMax = new Vector2(1f, 1f);
        detailActions.offsetMin = new Vector2(0f, 4f);
        detailActions.offsetMax = new Vector2(-4f, -4f);
        HorizontalLayoutGroup al = detailActions.gameObject.AddComponent<HorizontalLayoutGroup>();
        al.childAlignment = TextAnchor.MiddleRight;
        al.spacing = 16f;
        al.childControlWidth = al.childControlHeight = true;
        al.childForceExpandWidth = al.childForceExpandHeight = false;

        detailView.gameObject.SetActive(false);
    }

    private static Text Line(Transform parent, string text, float size, Color color, Paintbox.Weight weight, float height)
    {
        Text t = Paintbox.Label(parent, text, size, color, weight);
        t.alignment = TextAnchor.UpperLeft;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        LayoutElement le = t.gameObject.AddComponent<LayoutElement>();
        le.minHeight = height;
        return t;
    }

    private Button detailPrev, detailNext;
    private Text detailPlace, detailAmmoTitle, detailAmmoNote;
    private RectTransform detailAmmo;
    private string ammoChoice;   // the ammo pack picked on a weapon's page

    // The items Previous and Next go through: the category's grid, in its order.
    private List<MetaItem> DetailSiblings(MetaItem item) =>
        item.Kind == ItemKind.Ammo ? new List<MetaItem>() : GridItems(ShopInventory.CategoryOf(item));

    private void StepDetail(int step)
    {
        if (shownDetail == null || !ShopInventory.TryItem(shownDetail, out MetaItem item)) return;
        List<MetaItem> siblings = DetailSiblings(item);
        int at = siblings.FindIndex(i => i.Id == item.Id);
        int to = at + step;
        if (at < 0 || to < 0 || to >= siblings.Count) return;
        AudioManager.ButtonTap();
        OpenDetail(siblings[to]);
    }

    private void OpenDetail(MetaItem item)
    {
        shownDetail = item.Id;
        gridView.gameObject.SetActive(false);
        detailView.gameObject.SetActive(true);
        title.text = "Item detail";
        FillDetail(item);
    }

    private void CloseDetail()
    {
        shownDetail = null;
        if (detailView == null) return;
        detailView.gameObject.SetActive(false);
        gridView.gameObject.SetActive(true);
        title.text = "Shop";
    }

    private uint ammoPacks = 1;
    private string ammoFor;

    private void FillDetail(MetaItem item)
    {
        MetaShopCategory category = CategoryById(ShopInventory.CategoryOf(item));
        string kicker = (string.IsNullOrEmpty(category.Name) ? category.Id : category.Name).ToUpperInvariant();
        if (item.Kind == ItemKind.Ammo) kicker += " / " + L.T("AMMO");
        detailKicker.text = kicker;
        detailTitle.text = DisplayName(item);
        detailAbout.text = item.Description ?? "";
        ShopArt.Load(item, sprite => { if (detailArt != null && shownDetail == item.Id) { detailArt.sprite = sprite; detailArt.enabled = sprite != null; } });

        foreach (Transform child in detailStats) Destroy(child.gameObject);
        foreach (Transform child in detailPrice) Destroy(child.gameObject);
        foreach (Transform child in detailActions) Destroy(child.gameObject);
        if (ammoFor != item.Id) { ammoFor = item.Id; ammoPacks = 1; }

        detailHow.text = HowToUse(item);
        bool owned = ShopInventory.IsOwned(item);

        // Previous / Next within the category.
        List<MetaItem> siblings = DetailSiblings(item);
        int place = siblings.FindIndex(i => i.Id == item.Id);
        bool stepping = place >= 0 && siblings.Count > 1;
        detailPrev.gameObject.SetActive(stepping);
        detailNext.gameObject.SetActive(stepping);
        detailPlace.gameObject.SetActive(stepping);
        if (stepping)
        {
            detailPrev.interactable = place > 0;
            detailNext.interactable = place < siblings.Count - 1;
            detailPlace.text = $"{place + 1} / {siblings.Count}";
        }

        // A weapon's ammo packs.
        foreach (Transform child in detailAmmo) Destroy(child.gameObject);
        List<MetaItem> packs = item.Kind == ItemKind.Weapon && item.GameId != WeaponSpecs.Standard ? AmmoPacksFor((int)item.GameId) : new List<MetaItem>();
        bool ammoRow = packs.Count > 0;
        detailAmmoTitle.gameObject.SetActive(ammoRow);
        detailAmmo.gameObject.SetActive(ammoRow);
        detailAmmoNote.gameObject.SetActive(ammoRow);
        if (ammoRow)
        {
            if (!packs.Exists(pk => pk.Id == ammoChoice)) { ammoChoice = packs[0].Id; ammoPacks = 1; }
            foreach (MetaItem pack in packs)
            {
                MetaItem chosen = pack;
                string price = pack.PriceCents <= 0 ? L.T("Free") : L.F("{0} coins", CoinBalance.Format(pack.PriceCents));
                Button chip = PaintboxPage.Chip(detailAmmo, L.F("{0} rounds · {1}", Mathf.Max(1, pack.Ammo), price), "Layers", pack.Id == ammoChoice, () =>
                {
                    AudioManager.ButtonTap();
                    if (ammoChoice != chosen.Id) { ammoChoice = chosen.Id; ammoPacks = 1; }
                    FillDetail(item);
                }, 24f);
                chip.name = "AmmoPack_" + pack.Id;
            }
            int have = Mathf.Max(0, item.OwnedAmmo);
            detailAmmoNote.text = owned ? L.F("You have {0} of the {1} rounds it holds.", have, Mathf.Max(0, item.AmmoCapacity))
                                        : L.T("Get the weapon to load more rounds into it.");
        }

        switch (item.Kind)
        {
            case ItemKind.Weapon:
            {
                WeaponSpec spec = ShopInventory.SpecOf(item);
                Stat("Cooldown", L.F("{0} s", spec.CooldownSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)), "Timer");
                Stat("Fire rate", L.F("{0} / s", spec.FireRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)), "Zap");
                Stat("Bullet size", "×" + spec.RadiusScale.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture), "Target");
                if (item.GameId == WeaponSpecs.Standard) Stat("Rounds", "Unlimited", "Layers");
                else
                {
                    Stat("Rounds with it", Mathf.Max(0, item.Ammo).ToString(), "Package");
                    Stat("Holds up to", Mathf.Max(0, item.AmmoCapacity).ToString(), "Boxes");
                    Stat("Your rounds", owned ? Mathf.Max(0, item.OwnedAmmo).ToString() : "-", "Layers");
                }
                break;
            }
            case ItemKind.Ammo:
            {
                int have = ShopInventory.AmmoOf((int)item.GameId);
                Stat("Rounds per pack", Mathf.Max(1, item.Ammo).ToString(), "Package");
                Stat("Your rounds", L.F("{0} / {1}", Mathf.Max(0, have), Mathf.Max(0, item.AmmoCapacity)), "Layers");
                Stat("For", ShopInventory.WeaponName((int)item.GameId), "Crosshair");
                break;
            }
            case ItemKind.Shoes:
                Stat("Speed", Shoes.SpeedText((int)item.GameId), "Footprints");
                break;
        }
        detailStats.gameObject.SetActive(detailStats.childCount > 0 || item.Kind == ItemKind.Weapon || item.Kind == ItemKind.Ammo || item.Kind == ItemKind.Shoes);

        // The price, and what can be done. On sale, the label over it says which sale and until when.
        detailPriceLabel.gameObject.SetActive(true);
        bool saleShown = item.OnSale && !(owned && item.Kind != ItemKind.Ammo);
        detailPriceLabel.text = saleShown ? SaleLine(item) : L.T("PRICE");
        detailPriceLabel.color = saleShown ? Paintbox.Bad : Paintbox.Muted;
        detailPriceLabel.horizontalOverflow = saleShown ? HorizontalWrapMode.Overflow : HorizontalWrapMode.Wrap;
        // On sale the sale's line goes over the price; otherwise PRICE sits beside it.
        detailPriceLabel.rectTransform.anchoredPosition = saleShown ? new Vector2(0f, 24f) : Vector2.zero;
        detailPriceLabel.rectTransform.sizeDelta = saleShown ? new Vector2(640f, 34f) : new Vector2(110f, 40f);
        detailPrice.anchoredPosition = saleShown ? new Vector2(0f, -16f) : new Vector2(110f, 0f);
        if (item.Kind == ItemKind.Ammo) AmmoActions(item);
        else if (owned && ammoRow && ShopInventory.TryItem(ammoChoice, out MetaItem pick))
        {
            // An owned weapon: its rounds are bought here, and it is equipped here.
            detailPriceLabel.text = L.T("AMMO");
            AmmoActions(pick, true);
            if (ShopInventory.IsEquipped(item))
            {
                Button on = Action("Equipped", Paintbox.Tone.Paper, "CircleCheck", null);
                on.interactable = false;
            }
            else if (ShopInventory.CanEquip(item)) Action("Equip", Paintbox.Tone.Secondary, "Check", () => Equip(item));
        }
        else ItemActions(item, owned);
    }

    private void ItemActions(MetaItem item, bool owned)
    {
        if (owned)
        {
            Text state = Paintbox.Label(detailPrice, item.PriceCents <= 0 ? "Free" : "Owned", 30f, Paintbox.Good, Paintbox.Weight.Bold);
            Paintbox.Fill(state.rectTransform);

            if (item.Kind == ItemKind.Weapon && item.GameId != WeaponSpecs.Standard && TryAmmoFor((int)item.GameId, out MetaItem ammo))
            {
                MetaItem a = ammo;
                Action("Buy ammo", Paintbox.Tone.Paper, "Layers", () => { AudioManager.ButtonTap(); OpenDetail(a); });
            }

            if (ShopInventory.CanEquip(item))
            {
                if (ShopInventory.IsEquipped(item))
                {
                    Button on = Action(item.Kind == ItemKind.Shoes ? "Worn" : "Equipped", Paintbox.Tone.Paper, "CircleCheck", null);
                    on.interactable = false;
                }
                else Action(item.Kind == ItemKind.Shoes ? "Wear" : "Equip", Paintbox.Tone.Secondary, "Check", () => Equip(item));
            }
            else
            {
                Button done = Action("Owned", Paintbox.Tone.Paper, "Check", null);
                done.interactable = false;
            }
            return;
        }

        if (item.PriceCents <= 0)
        {
            Text free = Paintbox.Label(detailPrice, "Free", 30f, Paintbox.Ink, Paintbox.Weight.Bold);
            Paintbox.Fill(free.rectTransform);
        }
        else if (item.OnSale) SalePrice(detailPrice, item.PriceCents, item.OriginalPriceCents, 30f);
        else
        {
            Paintbox.CoinAmount(detailPrice, CoinBalance.Format(item.PriceCents), 30f, Paintbox.Ink, out RectTransform row);
            Paintbox.Fill(row);
        }
        Action("Buy item", Paintbox.Tone.Primary, "ShoppingBag", () => AskBuy(item, 1));
    }

    private void AmmoActions(MetaItem item) => AmmoActions(item, false);

    // An ammo pack's price for the packs picked, the - n + of them and its Buy: on the pack's own
    // page, or (onWeapon) on its weapon's page, where a refill comes back to the weapon.
    private void AmmoActions(MetaItem item, bool onWeapon)
    {
        bool hasWeapon = ShopInventory.TryWeapon((int)item.GameId, out MetaItem weapon) && ShopInventory.IsOwned(weapon);
        int have = Mathf.Max(0, ShopInventory.AmmoOf((int)item.GameId));
        int perPack = Mathf.Max(1, item.Ammo);
        int room = Mathf.Max(0, item.AmmoCapacity - have);
        uint most = (uint)Mathf.Max(0, Mathf.Min(item.MaxQuantity == 0 ? 99 : (int)item.MaxQuantity, room / perPack));
        if (ammoPacks > most) ammoPacks = Math.Max(1u, most);

        if (item.OnSale) SalePrice(detailPrice, item.PriceCents * ammoPacks, item.OriginalPriceCents * ammoPacks, 30f);
        else
        {
            Paintbox.CoinAmount(detailPrice, CoinBalance.Format(item.PriceCents * ammoPacks), 30f, Paintbox.Ink, out RectTransform row);
            Paintbox.Fill(row);
        }

        if (!hasWeapon && !onWeapon)
        {
            MetaItem w = weapon;
            if (weapon.Id != null) Action(L.F("Get the {0} first", ShopInventory.WeaponName((int)item.GameId)), Paintbox.Tone.Secondary, "Crosshair", () => { AudioManager.ButtonTap(); OpenDetail(w); });
            return;
        }

        if (most == 0)
        {
            Button full = Action("Weapon full", Paintbox.Tone.Paper, "Check", null);
            full.interactable = false;
            return;
        }

        // How many packs: - n +.
        MetaItem page = onWeapon && ShopInventory.TryItem(shownDetail, out MetaItem shown) ? shown : item;
        Button less = Paintbox.IconButton(detailActions, "Minus", Paintbox.Tone.Paper, () => { AudioManager.ButtonTap(); ammoPacks = Math.Max(1u, ammoPacks - 1); FillDetail(page); }, 76f);
        less.gameObject.AddComponent<LayoutElement>().preferredWidth = 76f;
        less.interactable = ammoPacks > 1;
        Text count = Paintbox.Label(detailActions, L.F("{0} x {1} rounds", ammoPacks, perPack), 26f, Paintbox.Ink, Paintbox.Weight.Bold, TextAnchor.MiddleCenter);
        count.gameObject.AddComponent<LayoutElement>().preferredWidth = 230f;
        Button more = Paintbox.IconButton(detailActions, "Plus", Paintbox.Tone.Paper, () => { AudioManager.ButtonTap(); ammoPacks = Math.Min(most, ammoPacks + 1); FillDetail(page); }, 76f);
        more.gameObject.AddComponent<LayoutElement>().preferredWidth = 76f;
        more.interactable = ammoPacks < most;
        uint packs = ammoPacks;
        Action(onWeapon ? "Buy rounds" : "Buy ammo", Paintbox.Tone.Primary, "ShoppingBag", () => AskBuy(item, packs));
    }

    private static bool TryAmmoFor(int weaponId, out MetaItem ammo)
    {
        foreach (MetaShopCategory c in ShopInventory.Categories)
            foreach (MetaItem i in ShopInventory.ItemsIn(c.Id))
                if (i.Kind == ItemKind.Ammo && (int)i.GameId == weaponId) { ammo = i; return true; }
        ammo = default;
        return false;
    }

    private Button Action(string text, Paintbox.Tone tone, string icon, UnityEngine.Events.UnityAction onClick)
    {
        Button b = Paintbox.Button(detailActions, text, tone, icon, onClick, 28f);
        b.name = "Action_" + text;
        LayoutElement le = b.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = Mathf.Max(250f, text.Length * 17f + 110f);
        le.preferredHeight = 80f;
        return b;
    }

    private void Stat(string label, string value, string icon)
    {
        PaintboxFrame cell = Paintbox.Frame(detailStats, "Stat", Paintbox.Tab, 18f, 0f, 0f);
        Image glyph = Paintbox.IconImage(cell.Body, icon, 30f, Paintbox.Muted);
        Paintbox.Pin(glyph.rectTransform, new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(30f, 30f));
        glyph.rectTransform.pivot = new Vector2(0f, 0.5f);
        Text l = Paintbox.Label(cell.Body, label, 20f, Paintbox.Muted);
        Paintbox.Fill(l.rectTransform, 58f, 12f, 4f, 30f);
        Paintbox.Fit(l, 14);
        Text v = Paintbox.Label(cell.Body, value, 24f, Paintbox.Ink, Paintbox.Weight.Bold);
        Paintbox.Fill(v.rectTransform, 58f, 12f, 28f, 4f);
        Paintbox.Fit(v, 15);
    }

    private static string HowToUse(MetaItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Weapon: return "Equip it, then hold Fire in a Seeker round. Tap the weapon button to switch weapons during a match.";
            case ItemKind.Ammo: return "The rounds go to the weapon straight away, in the middle of a match too.";
            case ItemKind.Pose: return "Strike it from the pose button while you hide in an online match.";
            case ItemKind.Avatar: return "Equip it to play as this character in online matches.";
            case ItemKind.Shoes: return "Wear them to run faster, online and in Single Play.";
            case ItemKind.Map: return "Pick this map when you find an online match.";
            case ItemKind.Feature: return item.GameId == GameFeatures.NextPlayer
                ? "Once you are out of a match, switch between the players still in."
                : "Unlocked for good on your account.";
            default: return "Added to your online inventory.";
        }
    }

    private void Equip(MetaItem item)
    {
        AudioManager.ButtonTap();
        ShopInventory.Equip(item, (ok, message) =>
        {
            Notices.Show(ok ? NoticeKind.Purchase : NoticeKind.Error, message);
            dirty = true;
        });
    }

    // ================= Buying =================

    private void AskBuy(MetaItem item, uint quantity)
    {
        AudioManager.ButtonTap();
        long price = item.PriceCents * (item.Kind == ItemKind.Ammo ? quantity : 1u);
        long balance = CoinBalance.Cents;
        bool short_ = CoinBalance.Known && balance < price;

        RectTransform panel = OpenModal("Confirm purchase", new Vector2(780f, short_ ? 560f : 600f));
        ModalHeader(panel, item);

        float y = 150f;
        if (item.Kind == ItemKind.Ammo)
        {
            Row(panel, ref y, "Quantity", L.F("{0} x {1} rounds", quantity, Mathf.Max(1, item.Ammo)), Paintbox.Ink);
        }
        if (item.OnSale)
        {
            long usual = item.OriginalPriceCents * (item.Kind == ItemKind.Ammo ? quantity : 1u);
            Row(panel, ref y, "Price", L.F("{0} coins (was {1}, -{2}%)", CoinBalance.Format(price), CoinBalance.Format(usual), item.PercentOff), Paintbox.Bad);
        }
        else Row(panel, ref y, "Price", L.F("{0} coins", CoinBalance.Format(price)), Paintbox.Ink);
        Row(panel, ref y, "Current balance", L.F("{0} coins", CoinBalance.Text), Paintbox.Ink);
        if (short_)
        {
            Row(panel, ref y, "You need", L.F("{0} coins more", CoinBalance.Format(price - balance)), Paintbox.Bad);
        }
        else
        {
            Row(panel, ref y, "Remaining balance", L.F("{0} coins", CoinBalance.Format(balance - price)), Paintbox.Good);
            Text note = Paintbox.Label(panel, "Your item will be added to your online inventory.", 22f, Paintbox.Muted);
            Paintbox.TopBand(note.rectTransform, y + 12f, 36f, 40f, 40f);
        }

        RectTransform buttons = ModalButtons(panel);
        Button cancel = Paintbox.Button(buttons, "Cancel", Paintbox.Tone.Paper, null, () => { AudioManager.ButtonTap(); CloseModal(); }, 28f);
        cancel.gameObject.AddComponent<LayoutElement>().preferredWidth = 200f;
        if (short_)
        {
            Button get = Paintbox.Button(buttons, "Get coins", Paintbox.Tone.Primary, "Coins", () =>
            {
                AudioManager.ButtonTap();
                CloseModal();
                CloseDetail();
                foreach (MetaShopCategory c in ShopInventory.Categories)
                    if (c.Special == "coins") { SelectCategory(c.Id); break; }
            }, 28f);
            get.gameObject.AddComponent<LayoutElement>().preferredWidth = 280f;
            return;
        }

        Button confirm = null;
        confirm = Paintbox.Button(buttons, "Confirm purchase", Paintbox.Tone.Primary, "Check", () =>
        {
            if (busy) return;
            AudioManager.ButtonTap();
            busy = true;
            confirm.interactable = false;
            Paintbox.SetText(confirm, "Buying...");
            ShopInventory.Purchase(item, quantity, (bought, status, message) =>
            {
                busy = false;
                if (!IsOpen) return;
                if (bought) ShowReceipt(item, quantity, price, message);
                else
                {
                    CloseModal();
                    Notices.Show(NoticeKind.Error, message);
                }
                dirty = true;
            });
        }, 28f);
        confirm.name = "ConfirmPurchase";
        confirm.gameObject.AddComponent<LayoutElement>().preferredWidth = 340f;
    }

    private void ShowReceipt(MetaItem item, uint quantity, long paid, string message)
    {
        RectTransform panel = OpenModal("Receipt", new Vector2(780f, 560f));

        Image badge = Paintbox.Image(panel, "Done", UiArt.Circle, Paintbox.Good);
        Paintbox.Pin(badge.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(96f, 96f));
        Paintbox.IconImage(badge.rectTransform, "Check", 56f, Color.white);

        Text head = Paintbox.Label(panel, "Purchase complete", 44f, Paintbox.Ink, Paintbox.Weight.Title, TextAnchor.MiddleCenter);
        Paintbox.TopBand(head.rectTransform, 146f, 60f, 30f, 30f);
        Text what = Paintbox.Label(panel, message, 27f, Paintbox.Muted, Paintbox.Weight.Regular, TextAnchor.MiddleCenter);
        Paintbox.TopBand(what.rectTransform, 206f, 44f, 30f, 30f);

        float y = 270f;
        Row(panel, ref y, "Paid", L.F("{0} coins", CoinBalance.Format(paid)), Paintbox.Ink);
        Row(panel, ref y, "New balance", L.F("{0} coins", CoinBalance.Text), Paintbox.Good);

        Notices.Show(NoticeKind.Purchase, message, 3f);

        RectTransform buttons = ModalButtons(panel);
        if (ShopInventory.CanEquip(item) && ShopInventory.TryItem(item.Id, out MetaItem now) && !ShopInventory.IsEquipped(now))
        {
            Button equip = Paintbox.Button(buttons, item.Kind == ItemKind.Shoes ? "Wear now" : "Equip now", Paintbox.Tone.Secondary, "Check", () =>
            {
                CloseModal();
                if (ShopInventory.TryItem(item.Id, out MetaItem latest)) Equip(latest);
            }, 28f);
            equip.gameObject.AddComponent<LayoutElement>().preferredWidth = 260f;
        }
        Button done = Paintbox.Button(buttons, "Done", Paintbox.Tone.Primary, null, () => { AudioManager.ButtonTap(); CloseModal(); }, 28f);
        done.name = "ReceiptDone";
        done.gameObject.AddComponent<LayoutElement>().preferredWidth = 220f;
    }

    // ================= Dialogs =================

    private RectTransform OpenModal(string name, Vector2 size)
    {
        CloseModal();
        modalLayer.gameObject.SetActive(true);
        modalLayer.SetAsLastSibling();
        Image dim = Paintbox.Image(modalLayer, "Dim", null, Paintbox.Dim, true);
        Paintbox.Fill(dim.rectTransform);
        PaintboxFrame panel = Paintbox.Frame(modalLayer, name, Paintbox.Paper, Paintbox.RadiusLarge, Paintbox.Border, Paintbox.Shadow);
        Paintbox.Pin(panel.Root, new Vector2(0.5f, 0.5f), Vector2.zero, size);
        return panel.Body;
    }

    private void CloseModal()
    {
        if (modalLayer == null) return;
        foreach (Transform child in modalLayer) Destroy(child.gameObject);
        modalLayer.gameObject.SetActive(false);
    }

    private static void ModalHeader(RectTransform panel, MetaItem item)
    {
        Image well = Paintbox.Rounded(panel, "Thumb", Paintbox.Card, 20f);
        Paintbox.Pin(well.rectTransform, new Vector2(0f, 1f), new Vector2(40f, -36f), new Vector2(96f, 96f));
        Image art = Paintbox.Image(well.rectTransform, "Art", null, Color.white);
        art.preserveAspect = true;
        Paintbox.Fill(art.rectTransform, 6f, 6f, 6f, 6f);
        ShopArt.Load(item, sprite => { if (art != null) { art.sprite = sprite; art.enabled = sprite != null; } });

        Text kicker = Paintbox.Label(panel, CategoryById(ShopInventory.CategoryOf(item)).Name?.ToUpperInvariant() ?? "", Paintbox.TinySize, Paintbox.Muted, Paintbox.Weight.Bold);
        Paintbox.TopBand(kicker.rectTransform, 44f, 30f, 156f, 40f);
        Text name = Paintbox.Label(panel, DisplayName(item), 44f, Paintbox.Ink, Paintbox.Weight.Title);
        Paintbox.Fit(name, 24);
        Paintbox.TopBand(name.rectTransform, 74f, 60f, 156f, 40f);
    }

    private static void Row(RectTransform panel, ref float y, string label, string value, Color valueColor)
    {
        Text l = Paintbox.Label(panel, label, 27f, Paintbox.Muted);
        Paintbox.TopBand(l.rectTransform, y, 52f, 40f, 40f);
        Text v = Paintbox.Label(panel, value, 28f, valueColor, Paintbox.Weight.Bold, TextAnchor.MiddleRight);
        Paintbox.TopBand(v.rectTransform, y, 52f, 40f, 40f);
        Image rule = Paintbox.Divider(panel, null, 2f);
        Paintbox.TopBand(rule.rectTransform, y + 52f, 2f, 40f, 40f);
        y += 60f;
    }

    private static RectTransform ModalButtons(RectTransform panel)
    {
        RectTransform row = Paintbox.NewRect(panel, "Buttons");
        row.anchorMin = new Vector2(0f, 0f);
        row.anchorMax = new Vector2(1f, 0f);
        row.pivot = new Vector2(0.5f, 0f);
        row.offsetMin = new Vector2(40f, 34f);
        row.offsetMax = new Vector2(-40f, 118f);
        HorizontalLayoutGroup h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.childAlignment = TextAnchor.MiddleRight;
        h.spacing = 16f;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        return row;
    }
}
