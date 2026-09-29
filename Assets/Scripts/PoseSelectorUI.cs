using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// NOTE: Pose-selector panel bound to pre-placed scene UI (unlike the many runtime-built panels
// seen elsewhere) - finds a "PosePanel" under a paint canvas, binds numbered "SlotN" picks
// (falling back through a few known ScrollView layout paths), and applies a chosen pose via an
// Animator int parameter. Slots 2-8 can be gated behind either a simple rewarded-ad check or,
// in a "test mode" (RootManager field, unconfirmed name/offset 0x57), per-slot unlock flags on
// GameController (7 new unconfirmed bool fields). ApplyPick/SlotNoads are confirmed
// byte-identical (same exact-duplicate-method pattern seen elsewhere) - implemented once here.
// Also confirms "Btn_DoiDang" - Vietnamese for "change pose" - continuing the pattern of
// Vietnamese-named UI hooks seen in JoystickMover ("Xoay Nhan Vat") and Mode1FirstPersonGun
// ("Nam").
public class PoseSelectorUI : MonoBehaviour
{
    [SerializeField] private string characterRootName = "Player";
    [SerializeField] private string doiDangButtonName = "Btn_DoiDang";
    [SerializeField] private string paintCanvasName = "PaintUICanvas";
    [SerializeField] private string posePanelName = "PosePanel";
    [SerializeField] private int[] rewardSlots = { 3, 4 };
    [SerializeField] private bool simulateRewardInEditor = true;
    [SerializeField] private float poseBlendDuration = 0.25f;

    [SerializeField] private Sprite pickSprite;
    [SerializeField] private Sprite gateOnSprite;
    [SerializeField] private Sprite gateOffSprite;
    [SerializeField] private Font font;

    private Animator anim;
    private GameObject panelRoot;
    private Transform gateTf;
    private Button gateBtn;
    private Image gateImg;
    public int activeSlot = -1;
    private readonly List<Image> slotPicks = new List<Image>();

    // NOTE: embedded as a compiler-generated <PrivateImplementationDetails> data blob in the
    // decompiled .cctor (RuntimeHelpers.InitializeArray) - the 9 actual int values aren't
    // recoverable from the decompiled text itself. This is the per-slot Animator "Pose" integer
    // value looked up by activeSlot.
    private static readonly int[] PoseAnimValues = new int[9]; // TODO: real values not decompiled.
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

        for (int oneBased = 1; oneBased <= PoseAnimValues.Length; oneBased++)
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

    private void Close()
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

    // NOTE: static (no field access on the instance in the decompiled body) - a 10th occurrence
    // of the cross-cutting scene-lookup helper.
    private static GameObject FindInScene(string n)
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

    // NOTE: static. Tries "SlotN" directly under panel, then two known ScrollView content
    // paths ("List/Content/SlotN", "Content/SlotN"), then falls back to a full recursive
    // name search. Confirmed: the recursive fallback compares against the bare "SlotN" name,
    // not against either of the path-prefixed variants (raw re-uses the un-prefixed
    // concat-result variable for that final comparison).
    private static Transform FindSlot(Transform panel, int oneBased)
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
        if (panelRoot == null)
            return;

        if (panelRoot.activeSelf)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    private void Open()
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

    // NOTE: root.PoseTestMode (offset 0x57, unconfirmed name) switches between two very
    // different flows. In the non-test-mode branch, slots 2-8 gate on a rewarded ad. There was
    // also a dead code path here - a search through rewardSlots whose result was never used -
    // omitted since it has no observable effect.
    //
    // FIX (confirmed): the very first test-mode check in raw is an UNSIGNED comparison
    // ((uint)slotIndex < 2), true only for slotIndex == 0 or 1. A negative slotIndex does NOT
    // take this shortcut in raw - it falls through the case-chain below and ultimately hits a
    // bare `return` with no GameController access and no ApplyPick. A prior draft had this as
    // a signed `slotIndex < 2`, which would incorrectly route any negative slotIndex straight
    // to ApplyPick. Fixed to match the unsigned semantics exactly.
    public void PickSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= PoseAnimValues.Length)
            return;

        ApplyPick(slotIndex);
    }

    // NOTE: these seven callbacks are each cached once (compiler lambda-caching pattern for
    // captureless lambdas - confirmed captureless since the cache lives on a shared, stateless
    // <>c-style holder) and passed as the "onSuccess" callback into SlotAds for slots 2-8
    // respectively. None of them capture this instance or a slot index, so whatever they do
    // can't be slot-specific via closure state. Bodies were not decompiled (only their
    // existence/caching mechanism was visible) - left as explicit TODOs rather than guessed.
    // private static void PickSlot2Callback() { /* TODO: not decompiled. */ }
    // private static void PickSlot3Callback() { /* TODO: not decompiled. */ }
    // private static void PickSlot4Callback() { /* TODO: not decompiled. */ }
    // private static void PickSlot5Callback() { /* TODO: not decompiled. */ }
    // private static void PickSlot6Callback() { /* TODO: not decompiled. */ }
    // private static void PickSlot7Callback() { /* TODO: not decompiled. */ }
    // private static void PickSlot8Callback() { /* TODO: not decompiled. */ }

    // NOTE: the success-callback body passed to AdMgr.OnRewardView here (`<SlotAds>b__0`) was
    // not itself decompiled - only that its closure captures (onSuccess, this, slotIndex).
    // Reconstructed as calling ApplyPick(slotIndex) then onSuccess - a strong inference from
    // exactly those captured variables, but not a byte-confirmed transcription.
    // private void SlotAds(int slotIndex, Action onSuccess)
    // {
    //     if (AdMgr.Instance == null)
    //     {
    //         throw new NullReferenceException("AdMgr instance not available");
    //     }
    //     if (!AdMgr.Instance.IsRewardReady)
    //     {
    //         return;
    //     }

    //     AdMgr.Instance.OnRewardView(
    //         () =>
    //         {
    //             ApplyPick(slotIndex);
    //             onSuccess?.Invoke();
    //         },
    //         RewardFail);
    // }

    public bool IsRewardSlot(int slotIndex)
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
            value = PoseAnimValues[activeSlot];
        }

        if (anim != null)
        {
            anim.SetInteger(PoseHash, value);
        }

        RefreshPicks();
        AudioManager.PoseChange();
    }

    private void SlotNoads(int slotIndex)
    {
        ApplyPick(slotIndex);
    }

    // private void RewardFail()
    // {
    //     // Intentional no-op (confirmed empty body).
    // }

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