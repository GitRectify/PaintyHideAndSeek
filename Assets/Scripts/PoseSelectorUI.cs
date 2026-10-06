using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// NOTE: Pose-selector panel bound to pre-placed scene UI (unlike the many runtime-built panels
// seen elsewhere) - finds a "PosePanel" under a paint canvas, binds numbered "SlotN" picks
// (falling back through a few known ScrollView layout paths), and applies a chosen pose via an
// Animator int parameter. Slots 2-8 can be gated behind either a simple rewarded-ad check or,
// when RootManager.PoseReward (+0x57) is set, per-slot unlock flags GameController.isReward3..9.
// ApplyPick/SlotNoads are confirmed byte-identical - implemented once here. "Btn_DoiDang" is
// Vietnamese for "change pose".
// Fields (names, order, offsets, attributes) and every method's accessibility match dump.cs
// (TypeDefIndex 9704). All string literals are confirmed against Dumpstringliteral.json.
public class PoseSelectorUI : MonoBehaviour
{
    [Header("Scene refs (found by name)")]
    public string characterRootName = "Player";
    public string doiDangButtonName = "Btn_DoiDang";
    public string paintCanvasName = "PaintUICanvas";
    public string posePanelName = "PosePanel";

    [Header("Selection overlay (Assets/Texture2D)")]
    public Sprite pickSprite;

    [Header("Btn_DoiDang state icons (Assets/Texture2D)")]
    public Sprite gateOnSprite;
    public Sprite gateOffSprite;

    [Header("Reward-gated slots")]
    [Tooltip("1-based slot numbers that require watching a rewarded ad before the pose applies (3,4 = Slot3, Slot4). Slots not listed change for free. Deselecting a gated slot is always free.")]
    public int[] rewardSlots = { 3, 4 };
    [Tooltip("Editor only: simulate a completed reward (no real rewarded ads run in the Editor) so gated slots stay testable in Play mode. Ignored in a device build.")]
    public bool simulateRewardInEditor = true;

    [Header("Pose blend (adjusting pose transition smoothness)")]
    [Tooltip("Blend time when switching poses (in seconds). Changing this value automatically updates the transition settings for both the Player and Player2 Animators (within the Editor; saved to the .controller file). A higher value results in a smoother/slower transition, while a lower value makes it faster/snappier. This applies to both the player and the bot, as they share the same controller.")]
    [Range(0.05f, 0.6f)]
    public float poseBlendDuration = 0.25f;

    // CONFIRMED: .cctor fills this from the metadata blob
    // <PrivateImplementationDetails>.EE33C09BF561A6B24599D7DC2A136CF890BDFAEAB12A5F4DE406D6750BED8DB8
    // (dump.cs: "Metadata offset 0x70E870"). The 36 bytes at that offset in global-metadata.dat,
    // read as little-endian int32s, are 4, 1, 3, 2, 5, 6, 7, 8, 9. Slot index -> Animator "Pose" value.
    private static readonly int[] PoseForSlot = { 4, 1, 3, 2, 5, 6, 7, 8, 9 };

    private Animator anim;
    private Button gateBtn;
    private Image gateImg;
    private Transform gateTf;
    private GameObject panelRoot;
    private Font font;
    private int activeSlot = -1;
    private readonly List<Image> slotPicks = new List<Image>();

    private static readonly int PoseHash = Animator.StringToHash("Pose");

    public bool HasPose => activeSlot >= 0;

    private void Start()
    {
        GameObject playerGo = PlayerRef.Resolve(characterRootName);
        if (playerGo != null)
        {
            anim = playerGo.GetComponentInChildren<Animator>(true);
        }

        if (anim == null)
        {
            Debug.LogWarning($"[PoseSelector] No Animator on '{characterRootName}'.");
        }

        EnsureEventSystem();
        BindAll();
        Close();
    }

    private void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null)
        {
            return;
        }

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();

        Type inputModuleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (inputModuleType != null)
        {
            go.AddComponent(inputModuleType);
        }
        else
        {
            go.AddComponent<StandaloneInputModule>();
        }
    }

    // Re-verified against raw BindAll/BindGateButton. Confirmed: neither method adds any onClick
    // listener - they only ensure Button components and target graphics exist. So the slot, close
    // and gate buttons must reach PickSlot/Close/Toggle through listeners set in the scene
    // (Inspector), not through code.
    private void BindAll()
    {
        slotPicks.Clear();

        GameObject canvasGo = FindInScene(paintCanvasName);
        Transform panel = null;
        if (canvasGo != null)
        {
            panel = canvasGo.transform.Find(posePanelName);
        }

        if (panel == null)
        {
            GameObject panelGo = FindInScene(posePanelName);
            if (panelGo != null)
            {
                panel = panelGo.transform;
            }
        }

        if (panel == null)
        {
            Debug.LogWarning($"[PoseSelector] '{posePanelName}' not found under '{paintCanvasName}'.");
            BindGateButton();
            RefreshPicks();
            return;
        }

        panelRoot = panel.gameObject;

        Transform closeBtnT = panel.Find("CloseBtn");
        if (closeBtnT != null)
        {
            Button closeButton = SceneUI.GetOrAdd<Button>(closeBtnT.gameObject);
            Image closeImg = closeBtnT.GetComponent<Image>();
            if (closeImg != null)
            {
                closeButton.targetGraphic = closeImg;
            }
        }

        for (int oneBased = 1; oneBased <= PoseForSlot.Length; oneBased++)
        {
            Transform slot = FindSlot(panel, oneBased);
            if (slot == null)
            {
                Debug.LogWarning($"[PoseSelector] 'Slot{oneBased}' missing.");
                slotPicks.Add(null);
                continue;
            }

            Image pick = EnsurePick(slot);
            pick.gameObject.SetActive(false);
            slotPicks.Add(pick);

            Button slotButton = SceneUI.GetOrAdd<Button>(slot.gameObject);
            Image slotImg = slot.GetComponent<Image>();
            if (slotImg != null)
            {
                slotButton.targetGraphic = slotImg;
            }
        }

        BindGateButton();
        RefreshPicks();
    }

    public void Close()
    {
        if (panelRoot != null)
        {
            panelRoot.SetActive(false);
        }

        if (gateImg != null && gateOffSprite != null)
        {
            gateImg.sprite = gateOffSprite;
        }
    }

    private void Update()
    {
        if (panelRoot == null || !panelRoot.activeSelf)
        {
            return;
        }

        if (gateTf != null && gateTf.gameObject.activeInHierarchy)
        {
            return;
        }

        Close();
    }

    public void AuthorUI()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }
        EnsureEventSystem();
        BindAll();
        Close();
    }

    private void ResolveSprites()
    {
        // Confirmed empty - decompiles to a bare `return;` with no other statements.
    }

    // Instance method per dump.cs, though the body never touches instance state. Scene-wide lookup:
    // GameObject.Find first, then a scan of every loaded Transform filtered to valid scenes.
    private GameObject FindInScene(string n)
    {
        GameObject go = GameObject.Find(n);
        if (go != null)
        {
            return go;
        }

        Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t.name == n && t.gameObject.scene.IsValid())
            {
                return t.gameObject;
            }
        }
        return null;
    }

    // Instance method per dump.cs (a null panel throws). Tries "SlotN" directly under panel, then two known ScrollView content
    // paths ("List/Content/SlotN", "Content/SlotN"), then falls back to a full recursive
    // name search. Confirmed: the recursive fallback compares against the bare "SlotN" name,
    // not against either of the path-prefixed variants (raw re-uses the un-prefixed
    // concat-result variable for that final comparison).
    private Transform FindSlot(Transform panel, int oneBased)
    {
        string slotName = "Slot" + oneBased;

        Transform found = panel.Find(slotName);
        if (found == null)
        {
            found = panel.Find("List/Content/" + slotName);
        }
        if (found == null)
        {
            found = panel.Find("Content/" + slotName);
        }
        if (found != null)
        {
            return found;
        }

        Transform[] all = panel.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].name == slotName)
            {
                return all[i];
            }
        }
        return null;
    }

    private Image EnsurePick(Transform slot)
    {
        bool created;
        GameObject go = SceneUI.GetOrCreate("Pick", slot, out created);
        Image img = SceneUI.GetOrAdd<Image>(go);
        img.raycastTarget = false;

        if (pickSprite != null)
        {
            img.sprite = pickSprite;
        }

        if (created)
        {
            // Packed constant confirmed via IEEE-754 decode: 0x3f800000 x4 = (1,1,1,1) = Color.white.
            img.color = Color.white;
            RectTransform rt = img.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        go.transform.SetAsLastSibling();
        return img;
    }

    private void BindGateButton()
    {
        GameObject go = FindInScene(doiDangButtonName);
        if (go == null)
        {
            Debug.LogWarning($"[PoseSelector] '{doiDangButtonName}' not found.");
            return;
        }

        gateTf = go.transform;
        gateBtn = go.GetComponent<Button>();
        if (gateBtn == null)
        {
            gateBtn = go.AddComponent<Button>();
        }

        Image img = go.GetComponent<Image>();
        if (img != null)
        {
            gateBtn.targetGraphic = img;
        }
        gateImg = img;
    }

    // Confirmed from raw: a null slotPicks throws (natural NRE on .Count); null entries are skipped.
    private void RefreshPicks()
    {
        for (int index = 0; index < slotPicks.Count; index++)
        {
            Image pick = slotPicks[index];
            if (pick != null)
            {
                pick.gameObject.SetActive(index == activeSlot);
            }
        }
    }

    public void Toggle()
    {
        RootManager root = RootManager.Instance;
        if (root == null)
        {
            throw new NullReferenceException("RootManager instance not available");
        }

        root.ShowInterAds_Native();

        if (panelRoot == null)
        {
            return;
        }

        if (panelRoot.activeSelf)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public void Open()
    {
        if (panelRoot != null)
        {
            panelRoot.transform.SetAsLastSibling();
            panelRoot.SetActive(true);
        }

        if (gateImg != null && gateOnSprite != null)
        {
            gateImg.sprite = gateOnSprite;
        }

        RefreshPicks();
    }

    // Re-verified against raw PickSlot. The flag at RootManager+0x57 is `PoseReward` (a Firebase
    // Remote Config flag, per the RootManager block in dump.cs). It switches between two flows:
    //   PoseReward == false: slots 2-8 always cost a rewarded ad (closure DisplayClass31_0 captures
    //     this + slotIndex; onSuccess = <PickSlot>b__7, onFail = RewardFail). Other slots apply
    //     directly. A dead search through rewardSlots (result never used) is omitted.
    //   PoseReward == true: slots 0-1 apply directly; slots 2-8 check a per-slot "already unlocked"
    //     bool on GameController (raw offsets 0x38..0x3E, one byte per slot) and either apply the
    //     pick or go through SlotAds with a cached, captureless callback <>c.<PickSlot>b__31_0..6.
    //
    // FIX (confirmed): the very first test-mode check in raw is an UNSIGNED comparison
    // ((uint)slotIndex < 2), true only for slotIndex == 0 or 1. A negative slotIndex does NOT
    // take this shortcut in raw - it falls through the case-chain below and ultimately hits a
    // bare `return` with no GameController access and no ApplyPick. A prior draft had this as
    // a signed `slotIndex < 2`, which would incorrectly route any negative slotIndex straight
    // to ApplyPick. Fixed to match the unsigned semantics exactly.
    public void PickSlot(int slotIndex)
    {
        RootManager root = RootManager.Instance;
        if (root == null)
        {
            throw new NullReferenceException("RootManager instance not available");
        }

        if (!root.PoseReward)
        {
            if (slotIndex >= 2 && slotIndex <= 8)
            {
                // if (AdMgr.Instance == null)
                // {
                //     throw new NullReferenceException("AdMgr instance not available");
                // }
                // if (!AdMgr.Instance.IsRewardReady)
                // {
                //     return;
                // }
                // // onSuccess = <>c__DisplayClass31_0.<PickSlot>b__7 (decompiled): ApplyPick(slotIndex).
                // AdMgr.Instance.OnRewardView(() => 
                    ApplyPick(slotIndex);
                // , RewardFail);
                return;
            }

            ApplyPick(slotIndex);
            return;
        }

        if (slotIndex == 0 || slotIndex == 1)
        {
            ApplyPick(slotIndex);
            return;
        }

        GameController controller = GameController.Instance;
        if (controller == null)
        {
            throw new NullReferenceException("GameController instance not available");
        }

        // CONFIRMED: raw reads GameController offsets 0x38..0x3E (slot 2 -> 0x38 ... slot 8 -> 0x3E),
        // which dump.cs names isReward3..isReward9 - the same flags GameController.ResetPoseReward
        // clears.
        bool unlocked;
        Action callback;
        switch (slotIndex)
        {
            case 2: unlocked = controller.isReward3; callback = PickSlot2Callback; break;
            case 3: unlocked = controller.isReward4; callback = PickSlot3Callback; break;
            case 4: unlocked = controller.isReward5; callback = PickSlot4Callback; break;
            case 5: unlocked = controller.isReward6; callback = PickSlot5Callback; break;
            case 6: unlocked = controller.isReward7; callback = PickSlot6Callback; break;
            case 7: unlocked = controller.isReward8; callback = PickSlot7Callback; break;
            case 8: unlocked = controller.isReward9; callback = PickSlot8Callback; break;
            default:
                return;
        }

        if (unlocked)
        {
            ApplyPick(slotIndex);
        }
        else
        {
            SlotAds(slotIndex, callback);
        }
    }

    // These are PoseSelectorUI.<>c.<PickSlot>b__31_0 .. b__31_6 (slot 2 .. slot 8), cached in the
    // <>c singleton's static fields at +0x08 .. +0x38 and passed to SlotAds as onSuccess.
    // Decompiled: each permanently unlocks its slot for the session - sets GameController's
    // isRewardN flag (raw offsets 0x38..0x3E) and hides the matching poseRewardN lock overlay
    // (0x40..0x70). GameController.ResetPoseReward undoes both. Raw fetches the singleton twice; a
    // null GameController or a null overlay throws.
    private static void PickSlot2Callback()
    {
        SingletonMonoBehavior<GameController>.Instance.isReward3 = true;
        SingletonMonoBehavior<GameController>.Instance.poseReward3.SetActive(false);
    }

    private static void PickSlot3Callback()
    {
        SingletonMonoBehavior<GameController>.Instance.isReward4 = true;
        SingletonMonoBehavior<GameController>.Instance.poseReward4.SetActive(false);
    }

    private static void PickSlot4Callback()
    {
        SingletonMonoBehavior<GameController>.Instance.isReward5 = true;
        SingletonMonoBehavior<GameController>.Instance.poseReward5.SetActive(false);
    }

    private static void PickSlot5Callback()
    {
        SingletonMonoBehavior<GameController>.Instance.isReward6 = true;
        SingletonMonoBehavior<GameController>.Instance.poseReward6.SetActive(false);
    }

    private static void PickSlot6Callback()
    {
        SingletonMonoBehavior<GameController>.Instance.isReward7 = true;
        SingletonMonoBehavior<GameController>.Instance.poseReward7.SetActive(false);
    }

    private static void PickSlot7Callback()
    {
        SingletonMonoBehavior<GameController>.Instance.isReward8 = true;
        SingletonMonoBehavior<GameController>.Instance.poseReward8.SetActive(false);
    }

    private static void PickSlot8Callback()
    {
        SingletonMonoBehavior<GameController>.Instance.isReward9 = true;
        SingletonMonoBehavior<GameController>.Instance.poseReward9.SetActive(false);
    }

    // Re-verified against raw SlotAds: closure DisplayClass32_0 captures (onSuccess, this,
    // slotIndex); a null AdMgr throws; not-ready returns; onFail = RewardFail.
    // onSuccess = <>c__DisplayClass32_0.<SlotAds>b__0 (decompiled): invokes the caller's onSuccess
    // FIRST (if set), then ApplyPick(slotIndex).
    public void SlotAds(int slotIndex, Action onSuccess)
    {
        // if (AdMgr.Instance == null)
        // {
        //     throw new NullReferenceException("AdMgr instance not available");
        // }
        // if (!AdMgr.Instance.IsRewardReady)
        // {
        //     return;
        // }

        // AdMgr.Instance.OnRewardView(
        //     () =>
        //     {
                onSuccess?.Invoke();
                ApplyPick(slotIndex);
            // },
            // RewardFail);
    }

    private bool IsRewardSlot(int slotIndex)
    {
        if (rewardSlots == null)
        {
            return false;
        }
        for (int i = 0; i < rewardSlots.Length; i++)
        {
            if (rewardSlots[i] == slotIndex + 1)
            {
                return true;
            }
        }
        return false;
    }

    // NOTE: confirmed byte-identical to SlotNoads below - implemented once here.
    private void ApplyPick(int slotIndex)
    {
        if (activeSlot == slotIndex)
        {
            slotIndex = -1;
        }
        activeSlot = slotIndex;

        int value = 0;
        if (activeSlot >= 0)
        {
            value = PoseForSlot[activeSlot];
        }

        if (anim != null)
        {
            anim.SetInteger(PoseHash, value);
        }

        RefreshPicks();
        AudioManager.PoseChange();
    }

    public void SlotNoads(int slotIndex)
    {
        ApplyPick(slotIndex);
    }

    private void RewardFail()
    {
        // Intentional no-op (confirmed empty body).
    }

    public void ClearSelection()
    {
        activeSlot = -1;
        if (anim != null)
        {
            anim.SetInteger(PoseHash, 0);
        }
        RefreshPicks();
    }
}