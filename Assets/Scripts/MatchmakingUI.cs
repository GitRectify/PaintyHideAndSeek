using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// NOTE: Fully runtime-built matchmaking/lobby screen (main panel, progress bar, two columns of
// player slots, top HUD bar) via SceneUI helpers, plus a fake-fill animation that reveals bot
// names into empty slots over `fillSeconds`, then pauses `readyPause` before calling the ready
// callback. Confirms SceneUI.UIRoot() (static, returns Transform) - paired with the
// already-known SceneUI.HUDRoot(). EnsureBuilt/AuthorUI are byte-identical again (same
// exact-duplicate-method pattern as MatchHUD.EnsureBuilt/AuthorUI and
// HideModeResultUI.EnsureBuilt/AuthorUI) - implemented once, AuthorUI forwards.
public class MatchmakingUI : MonoBehaviour
{
    [Serializable]
    public class Slot
    {
        public GameObject go;
        public Image bg;
        public Text label;
    }

    [SerializeField] private Font font;
    [SerializeField] public int hunterSlots = 3;
    [SerializeField] public int totalPlayers = 8;
    [SerializeField] private int coinAmount = 10000;
    [SerializeField] private float fillSeconds = 4.5f;
    [SerializeField] private float readyPause = 0.8f;

    [SerializeField] private Sprite panelSprite;
    [SerializeField] private Sprite titleSprite;
    [SerializeField] private Sprite barTrack;
    [SerializeField] private Sprite barFill;
    [SerializeField] private Sprite dividerV;
    [SerializeField] private Sprite dividerH;
    [SerializeField] private Sprite mapBorder;
    [SerializeField] private Sprite mapSprite;
    [SerializeField] private Sprite columnBoxSprite;
    [SerializeField] private Sprite nameGray;
    [SerializeField] private Sprite coinPanel;
    [SerializeField] private Sprite coinIcon;
    [SerializeField] private Sprite plusIcon;
    [SerializeField] private Sprite settingsIcon;
    [SerializeField] private Sprite adIcon;

    // NOTE: confirmed to exist (read via fixed instance-field offsets in SetSlot/BuildColumn)
    // but real names weren't in the paste - named here by inferred purpose, unconfirmed.
    [SerializeField] private Sprite slotFilledSprite;   // offset 0x58, used when highlight == true
    [SerializeField] private Sprite slotEmptySprite;    // offset 0x60, used when highlight == false
    [SerializeField] private Sprite fugitiveBadgeSprite; // offset 0x48
    [SerializeField] private Sprite hunterBadgeSprite;   // offset 0x50
    [SerializeField] private Sprite fugitiveTitleSprite; // offset 0x38
    [SerializeField] private Sprite hunterTitleSprite;   // offset 0x40

    private readonly List<Slot> fugSlots = new List<Slot>();
    private readonly List<Slot> hunSlots = new List<Slot>();
    private readonly List<Slot> revealOrder = new List<Slot>();

    private GameObject canvasGO;
    private Image barFillImg;
    private Text counterText;
    private Text hintText;

    private bool playerIsHunter;
    private Action onReady;
    private int joined;
    private float fillTimer;
    private float doneTimer;
    private int state; // 0 = idle, 1 = filling, 2 = done/waiting to auto-hide

    // NOTE: plain private static field (no get_/set_ accessors decompiled for it, unlike the
    // singleton Instance properties seen on other classes) - a fixed pool of bot display names.
    private static readonly string[] BotNames =
    {
        "Lieng", "Baconga", "Jack", "Lie", "Bumerang", "Max", "Rex", "Zoe",
        "Kai", "Milo", "Nova", "Pixel", "Echo", "Otto", "Suki", "Bolt", "Wolf", "Coco"
    };

    public int FugitiveCount => totalPlayers - hunterSlots;
    public int HunterCount => hunterSlots;

    private void EnsureBuilt()
    {
        if (canvasGO != null)
        {
            return;
        }

        if (font == null)
        {
            font = UIFont.Default;
        }

        Transform uiRoot = SceneUI.UIRoot();
        if (uiRoot == null)
        {
            throw new NullReferenceException("SceneUI.UIRoot() returned null");
        }

        Transform existing = uiRoot.Find("MatchmakingCanvas");
        if (existing != null)
        {
            Transform panel = existing.Find("Panel");
            if (panel != null)
            {
                BindUI(existing.gameObject);
                return;
            }
        }

        BuildUI();
    }

    public void AuthorUI()
    {
        EnsureBuilt();
    }

    // FIX (confirmed): raw's structure is `if (canvasGO != null) { SetActive(true);
    // if (hintText != null) { ...entire shuffle/fill/interleave/reveal logic... } }
    // goto <throw>;` - there is no else anywhere in that chain, so a null hintText does NOT
    // make raw silently skip the rest of the method. It falls straight through both checks to
    // the function's standard NRE-throw target. A prior draft had a silent `return;` here
    // instead. Fixed to throw. (canvasGO == null needs no separate fix - the SetActive(true)
    // call below already throws NRE naturally on a null reference, matching raw's outcome.)
    //
    // NOTE: fills fugSlots then hunSlots with shuffled bot names (fugitive loop uses index % N,
    // hunter loop CONTINUES the same cycling index rather than restarting at 0 - i.e. hunters
    // draw from later in the shuffled list), overwrites the player's own first slot with "You",
    // then interleaves the remaining slots into revealOrder via a round-robin merge (alternating
    // fug/hun preference each step, falling back to whichever list still has room) before
    // starting the fill animation.
    public void Begin(bool hunter, Action ready)
    {
        EnsureBuilt();
        playerIsHunter = hunter;
        onReady = ready;

        canvasGO.SetActive(true);

        if (hintText == null)
        {
            throw new NullReferenceException("hintText not available");
        }

        hintText.text = hunter
            ? "Find the fugitives and catch them before the round ends."
            : "Hide carefully. Don't let the Seeker find you!";

        List<string> names = new List<string>(BotNames);
        for (int i = names.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            string tmp = names[i];
            names[i] = names[j];
            names[j] = tmp;
        }

        for (int i = 0; i < fugSlots.Count; i++)
        {
            SetSlot(fugSlots[i], names[i % names.Count], false);
        }

        int idx = fugSlots.Count;
        for (int k = 0; k < hunSlots.Count; k++, idx++)
        {
            SetSlot(hunSlots[k], names[idx % names.Count], false);
        }

        List<Slot> ownList = hunter ? hunSlots : fugSlots;
        Slot ownSlot = (ownList.Count > 0) ? ownList[0] : null;
        if (ownSlot != null)
        {
            SetSlot(ownSlot, "You", true);
        }

        revealOrder.Clear();
        if (ownSlot != null)
        {
            revealOrder.Add(ownSlot);
        }

        int fi = (!hunter && ownSlot != null) ? 1 : 0;
        int hi = (hunter && ownSlot != null) ? 1 : 0;
        bool preferFug = true;

        while (fi < fugSlots.Count || hi < hunSlots.Count)
        {
            if (preferFug)
            {
                if (fi < fugSlots.Count)
                {
                    revealOrder.Add(fugSlots[fi]);
                    fi++;
                }
                else
                {
                    revealOrder.Add(hunSlots[hi]);
                    hi++;
                }
            }
            else
            {
                if (hi < hunSlots.Count)
                {
                    revealOrder.Add(hunSlots[hi]);
                    hi++;
                }
                else
                {
                    revealOrder.Add(fugSlots[fi]);
                    fi++;
                }
            }
            preferFug = !preferFug;
        }

        // Any slots not part of revealOrder's initial reveal stay hidden until Reveal() shows them.
        foreach (Slot s in fugSlots)
        {
            s.go.SetActive(false);
        }
        foreach (Slot s in hunSlots)
        {
            s.go.SetActive(false);
        }

        joined = 0;
        Reveal(1);
        fillTimer = 0f;
        doneTimer = 0f;
        state = 1;
        UpdateBarAndCounter();
    }

    private void SetSlot(Slot s, string name, bool highlight)
    {
        s.label.text = name;
        s.bg.sprite = highlight ? slotFilledSprite : slotEmptySprite;
        s.label.color = Color.white;
    }

    private void Reveal(int target)
    {
        int count = revealOrder.Count;
        target = Mathf.Min(target, count);

        for (int i = joined; i < target; i++)
        {
            revealOrder[i].go.SetActive(true);
        }

        joined = target;
    }

    private void UpdateBarAndCounter()
    {
        float frac = (totalPlayers < 1) ? 0f : (float)joined / totalPlayers;

        if (barFillImg != null)
        {
            barFillImg.fillAmount = frac;
        }

        if (counterText == null)
        {
            return;
        }

        counterText.text = $"<color=#3FC522>{joined}</color>/{totalPlayers} Player";
    }

    private void Update()
    {
        if (state == 2)
        {
            doneTimer += Time.deltaTime;
            if (doneTimer >= readyPause)
            {
                state = 0;
                if (canvasGO != null)
                {
                    canvasGO.SetActive(false);
                }

                Action callback = onReady;
                onReady = null;
                callback?.Invoke();
            }
        }
        else if (state == 1)
        {
            fillTimer += Time.deltaTime;
            float duration = Mathf.Max(0.1f, fillSeconds);
            float progress = Mathf.Clamp01(fillTimer / duration);

            int target = Mathf.Clamp(Mathf.FloorToInt(progress * (totalPlayers - 1) + 0.0001f) + 1, 1, totalPlayers);

            if (joined < target)
            {
                Reveal(target);
            }
            UpdateBarAndCounter();

            if (joined < totalPlayers && joined < revealOrder.Count)
            {
                return;
            }

            doneTimer = 0f;
            state = 2;
        }
    }

    private void BindUI(GameObject canvas)
    {
        canvasGO = canvas;
        Transform canvasT = canvas.transform;

        Transform panel = canvasT.Find("Panel");
        if (panel != null)
        {
            Transform barFillT = panel.Find("BarTrack/BarFill");
            if (barFillT != null)
            {
                barFillImg = barFillT.GetComponent<Image>();
            }

            Transform counterT = panel.Find("Counter");
            if (counterT != null)
            {
                counterText = counterT.GetComponent<Text>();
            }

            Transform hintT = panel.Find("Hint");
            if (hintT != null)
            {
                hintText = hintT.GetComponent<Text>();
            }

            BindColumn(panel.Find("FugitiveBox"), fugSlots);
            BindColumn(panel.Find("HunterBox"), hunSlots);
        }

        canvasGO.SetActive(false);
    }

    private void BindColumn(Transform box, List<Slot> list)
    {
        list.Clear();

        if (box == null)
        {
            return;
        }

        int i = 0;
        Transform slotT = box.Find("Slot" + i);
        while (slotT != null)
        {
            Transform nameT = slotT.Find("Name");

            Slot slot = new Slot();
            slot.go = slotT.gameObject;
            slot.bg = slotT.GetComponent<Image>();
            slot.label = (nameT != null) ? nameT.GetComponent<Text>() : null;
            list.Add(slot);

            i++;
            slotT = box.Find("Slot" + i);
        }
    }

    private void BuildUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }

        bool canvasCreated;
        canvasGO = SceneUI.GetOrCreate("MatchmakingCanvas", null, out canvasCreated);

        Canvas canvasComp = SceneUI.GetOrAdd<Canvas>(canvasGO);
        canvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasComp.sortingOrder = 38;

        CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvasGO);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        SceneUI.GetOrAdd<GraphicRaycaster>(canvasGO);
        scaler.matchWidthOrHeight = 1f;

        Transform canvasT = canvasGO.transform;

        Image dim = NewImage("Dim", canvasT, null, new Color(0f, 0f, 0f, 0.4f));
        Stretch(dim);

        Image panel = NewImage("Panel", canvasT, panelSprite, Color.white);
        Place(panel, 0f, 0f, 1331f, 736f);
        Transform panelT = panel.transform;

        Image title = NewImage("Title", panelT, titleSprite, Color.white);
        title.preserveAspect = true;
        Place(title, 0f, 288f, 520f, 73f);

        Text counter = NewText("Counter", panelT, "", 32, Color.white, TextAnchor.MiddleCenter, true);
        counterText = counter;
        Place(counter, 0f, 230f, 540f, 44f);

        Image barTrackImg = NewImage("BarTrack", panelT, barTrack, Color.white);
        Place(barTrackImg, 0f, 190f, 1150f, 22f);

        Image barFillImgLocal = NewImage("BarFill", barTrackImg.transform, barFill, Color.white);
        barFillImgLocal.type = Image.Type.Filled;
        barFillImgLocal.fillMethod = Image.FillMethod.Horizontal;
        barFillImgLocal.fillOrigin = (int)Image.OriginHorizontal.Left;
        barFillImgLocal.fillAmount = 0f;
        Stretch(barFillImgLocal);
        barFillImg = barFillImgLocal;

        BuildColumn(panelT, false, -368f);
        BuildColumn(panelT, true, 72f);

        Image divV = NewImage("DivV", panelT, dividerV, Color.white);
        Place(divV, 290f, -78f, 4f, 400f);

        Image mapBorderImg = NewImage("MapBorder", panelT, mapBorder, Color.white);
        mapBorderImg.preserveAspect = true;
        Place(mapBorderImg, 470f, -62f, 372f, 386f);

        Image map = NewImage("Map", mapBorderImg.transform, mapSprite, Color.white);
        Place(map, 0f, 0f, 344f, 358f);

        Text mapLabel = NewText("MapLabel", panelT, "Map game", 30, Color.white, TextAnchor.MiddleCenter, true);
        Place(mapLabel, 470f, -268f, 360f, 40f);

        Image divH = NewImage("DivH", panelT, dividerH, Color.white);
        Place(divH, 0f, -300f, 1240f, 4f);

        Text hint = NewText("Hint", panelT, "", 27, new Color(1f, 1f, 1f, 0.6f), TextAnchor.MiddleCenter, false);
        hintText = hint;
        Place(hint, 0f, -332f, 1240f, 44f);

        BuildTopBar();

        canvasGO.SetActive(false);
    }

    private void BuildColumn(Transform panel, bool hunter, float cx)
    {
        string boxName = hunter ? "HunterBox" : "FugitiveBox";
        Image box = NewImage(boxName, panel, columnBoxSprite, Color.white);
        Place(box, cx, -78f, 420f, 405f);
        Transform boxT = box.transform;

        Sprite badgeSprite = hunter ? hunterBadgeSprite : fugitiveBadgeSprite;
        Image badge = NewImage("Icon", boxT, badgeSprite, Color.white);
        badge.preserveAspect = true;
        Place(badge, -120f, 158f, 52f, 48f);

        Sprite titleSpr = hunter ? hunterTitleSprite : fugitiveTitleSprite;
        Image label = NewImage("Label", boxT, titleSpr, Color.white);
        label.preserveAspect = true;

        int slotCount;
        if (hunter)
        {
            Place(label, 0f, 158f, 150f, 40f);
            slotCount = hunterSlots;
        }
        else
        {
            Place(label, 14f, 158f, 172f, 46f);
            slotCount = totalPlayers - hunterSlots;
        }

        List<Slot> list = hunter ? hunSlots : fugSlots;
        list.Clear();

        for (int i = 0; i < slotCount; i++)
        {
            Image slotBg = NewImage("Slot" + i, boxT, nameGray, Color.white);
            Place(slotBg, 0f, i * -62f + 92f, 368f, 56f);

            Text nameLabel = NewText("Name", slotBg.transform, "", 30, new Color(0.25f, 0.25f, 0.25f, 1f), TextAnchor.MiddleCenter, true);
            Stretch(nameLabel);

            slotBg.gameObject.SetActive(false);

            Slot slot = new Slot();
            slot.go = slotBg.gameObject;
            slot.bg = slotBg;
            slot.label = nameLabel;
            list.Add(slot);
        }
    }

    private void BuildTopBar()
    {
        Transform canvasT = canvasGO.transform;

        Image coinPanelImg = NewImage("CoinPanel", canvasT, coinPanel, Color.white);
        PlaceTR(coinPanelImg, -436f, -34f, 250f, 60f, new Vector2(0f, 1f));

        Image coinIconImg = NewImage("CoinIcon", canvasT, coinIcon, Color.white);
        coinIconImg.preserveAspect = true;
        PlaceTR(coinIconImg, -466f, -26f, 74f, 76f, new Vector2(0f, 1f));

        Text coinText = NewText("CoinText", canvasT, coinAmount.ToString(), 34, Color.white, TextAnchor.MiddleCenter, true);
        PlaceTR(coinText, -386f, -38f, 200f, 50f, new Vector2(0f, 1f));

        Image plusBtn = NewImage("PlusBtn", canvasT, plusIcon, Color.white);
        plusBtn.preserveAspect = true;
        PlaceTR(plusBtn, -186f, -30f, 56f, 58f, new Vector2(0f, 1f));

        Image settingsBtn = NewImage("SettingsBtn", canvasT, settingsIcon, Color.white);
        settingsBtn.preserveAspect = true;
        PlaceTR(settingsBtn, -30f, -30f, 76f, 78f, new Vector2(1f, 1f));

        Image adBtn = NewImage("AdBtn", canvasT, adIcon, Color.white);
        adBtn.preserveAspect = true;
        PlaceTR(adBtn, -30f, -120f, 76f, 78f, new Vector2(1f, 1f));
    }

    private static Image NewImage(string n, Transform parent, Sprite spr, Color col)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = col;
        if (spr != null)
        {
            img.sprite = spr;
        }
        return img;
    }

    private Text NewText(string n, Transform parent, string content, int size, Color col, TextAnchor anchor, bool bold)
    {
        GameObject go = new GameObject(n);
        go.transform.SetParent(parent, false);
        Text text = go.AddComponent<Text>();
        text.font = font;
        text.text = content;
        text.fontSize = size;
        text.color = col;
        text.alignment = anchor;
        text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        text.supportRichText = true;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    // Anchors and pivots to the center of the parent - used for the main panel and its children.
    private static void Place(Component c, float x, float y, float w, float h)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    // Anchors to the top-right corner of the parent, with a caller-supplied pivot.
    private static void PlaceTR(Component c, float x, float y, float w, float h, Vector2 pivot)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMax = new Vector2(1f, 1f);
        rt.anchorMin = new Vector2(1f, 1f);
        rt.pivot = pivot;
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void Stretch(Component c)
    {
        RectTransform rt = (RectTransform)c.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}