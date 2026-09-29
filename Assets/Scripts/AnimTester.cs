using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class AnimTester : MonoBehaviour
{
    // CORRECTION: these are now precisely decoded from the raw bit patterns (the earlier draft
    // had rough, partly-wrong approximations — in particular the "active" color's r/g components
    // were swapped/off).
    private static readonly Color InactiveButtonColor = new Color(0.18f, 0.20f, 0.26f, 0.95f);
    private static readonly Color ActiveButtonColor = new Color(0.20f, 0.62f, 0.34f, 1.0f);
    private static readonly int PoseParamHash = Animator.StringToHash("Pose");
    private static readonly string[] PoseIds = { "Attack", "Gun Walk" };
    private static readonly string[] PoseLabels = { "GunAttack", "GunWalking" };

    public string characterRootName = "Player";
    public Font font;
    public Animator anim;
    public int activePose;
    private readonly List<Image> poseBtnImgs = new List<Image>();

    // No custom ctor needed — the decompiled ctor's only content beyond the base call was setting
    // characterRootName and constructing poseBtnImgs, both now field initializers.

    private void Start()
    {
        GameObject character = PlayerRef.Resolve(characterRootName);
        if (character != null)
        {
            anim = character.GetComponent<Animator>();
        }

        if (anim == null)
        {
            Debug.LogWarning("[AnimTester] No Animator found on '" + characterRootName + "'.");
        }

        EnsureFont();
        EnsureEventSystem();
        BuildUI();
    }

    public void AuthorUI()
    {
        EnsureFont();
        EnsureEventSystem();
        BuildUI();
    }

    private void EnsureFont()
    {
        if (font == null)
        {
            font = UIFont.Default;
        }
    }

    private void EnsureEventSystem()
    {
        if (Object.FindFirstObjectByType<EventSystem>() != null) return;

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();

        System.Type inputModuleType = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (inputModuleType != null)
        {
            go.AddComponent(inputModuleType);
        }
        else
        {
            go.AddComponent<StandaloneInputModule>();
        }
    }

    private void BuildUI()
    {
        poseBtnImgs.Clear();

        GameObject root = SceneUI.GetOrCreate("AnimTesterCanvas", null, out bool created);
        Canvas canvas = SceneUI.GetOrAdd<Canvas>(root);

        if (created)
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 8;

            CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(root);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
        }

        SceneUI.GetOrAdd<GraphicRaycaster>(root);
        Transform parent = root.transform;

        Text title = MakeLabelObj(
            "Header", parent, "ANIM TEST",
            26, TextAnchor.UpperCenter,
            new Color(1f, 1f, 1f, 0.7f),
            new Vector2(-20f, -90f),   // CORRECTION: precisely decoded — earlier draft had (-80, -20)
            new Vector2(200f, 34f));   // CORRECTION: precisely decoded — earlier draft had (200, 70)
        title.raycastTarget = false;

        // ---- Phase 1: one button per PoseIds/PoseLabels entry ----
        // CORRECTION: confirmed these buttons are NOT added to poseBtnImgs (only phase 2's are —
        // see below), which the earlier draft got right. However, the closure backing each
        // button's click handler here is confirmed to capture only `this` and the LABEL STRING
        // (PoseLabels[i]) — not an integer pose number. TogglePose(int) needs an int, so I can't
        // confidently infer this calls TogglePose the way phase 2's closure plausibly does;
        // left as an explicit unconfirmed stub rather than guessing. This corrects the earlier
        // draft, which assumed `TogglePose(capturedPose)` here without that capture evidence.
        int row = 0;
        for (int i = 0; i < PoseIds.Length; i++)
        {
            string capturedLabel = PoseLabels[i]; // confirmed closure capture
            string btnName = "Btn_" + PoseIds[i];

            Image btnImg = MakeButton(btnName, parent, capturedLabel, row, 200f, 70f, 8f, -130f, InactiveButtonColor);
            Button btn = SceneUI.GetOrAdd<Button>(btnImg.gameObject);
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(PhaseOneClickPlaceholder); // TODO: not decompiled — real target/args unconfirmed, see note above

            row++;
        }

        // ---- Phase 2: exactly 4 more auto-numbered buttons, regardless of PoseIds.Length ----
        // CORRECTION: the earlier draft tied this loop's bound to PoseIds.Length (`i <=
        // 4` starting from `PoseIds.Length`), implying a fixed total of 5 buttons. The real
        // decompile runs a SEPARATE counter (starting at 1, independent of PoseIds.Length) for
        // exactly 4 iterations regardless of how many PoseIds there are — so the true total here
        // is PoseIds.Length + 4 buttons, not capped at 5.
        for (int n = 1; n <= 4; n++)
        {
            string btnName = "Btn_Pose " + n;
            string btnLabel = "Pose " + n;

            Image btnImg = MakeButton(btnName, parent, btnLabel, row, 200f, 70f, 8f, -130f, InactiveButtonColor);
            poseBtnImgs.Add(btnImg);

            Button btn = SceneUI.GetOrAdd<Button>(btnImg.gameObject);
            btn.onClick.RemoveAllListeners();
            // NOTE: closure confirmed to capture `this` and this exact int `n` — matches
            // TogglePose(int)'s signature, so this inferred call is reasonably confident, though
            // the closure body itself wasn't decompiled.
            btn.onClick.AddListener(() => TogglePose(n));

            row++;
        }

        RefreshPoseHighlights();
    }

    // TODO: placeholder for phase 1's unconfirmed click handler — see the note in BuildUI above.
    private void PhaseOneClickPlaceholder() { }

    private Text MakeLabelObj(string name, Transform parent, string content, int size, TextAnchor anchor, Color color, Vector2 pos, Vector2 sizeDelta)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        Text text = SceneUI.GetOrAdd<Text>(go);
        if (text == null) return null;

        text.font = font;
        if (!created) return text;

        text.text = content;
        text.fontSize = size;
        text.alignment = anchor;
        text.color = color;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        // NOTE: the position/size are only applied if they differ from Vector2.zero (an epsilon
        // check in the decompile) — matches calls where both pos and sizeDelta are left at zero
        // (e.g. the button label below), which skip this block entirely and keep whatever
        // default RectTransform state SceneUI.GetOrAdd<Text> left it in.
        if ((pos - Vector2.zero).sqrMagnitude >= 1e-10f || (sizeDelta - Vector2.zero).sqrMagnitude >= 1e-10f)
        {
            RectTransform rt = text.rectTransform;
            rt.anchorMax = new Vector2(1f, 1f);
            rt.anchorMin = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;
        }

        return text;
    }

    private Image MakeButton(string name, Transform parent, string label, int row, float w, float h, float gap, float top, Color col)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        Image image = SceneUI.GetOrAdd<Image>(go);
        Button button = SceneUI.GetOrAdd<Button>(go);
        button.targetGraphic = image;

        if (!created)
        {
            return image;
        }

        image.color = col;

        RectTransform rt = image.rectTransform;
        rt.anchorMax = new Vector2(1f, 1f);
        rt.anchorMin = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-20f, top - (h + gap) * row);
        rt.sizeDelta = new Vector2(w, h);

        Transform labelParent = go.transform;
        Text labelText = MakeLabelObj("L", labelParent, label, 30, TextAnchor.MiddleCenter, Color.white, Vector2.zero, Vector2.zero);
        if (labelText != null)
        {
            RectTransform labelRt = labelText.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
            labelText.raycastTarget = false;
        }

        return image;
    }

    private void RefreshPoseHighlights()
    {
        for (int index = 0; index < poseBtnImgs.Count; index++)
        {
            Image img = poseBtnImgs[index];
            if (img != null)
            {
                img.color = (activePose == index + 1) ? ActiveButtonColor : InactiveButtonColor;
            }
        }
    }

    public void TogglePose(int n)
    {
        activePose = (activePose != n) ? n : 0;
        if (anim != null)
        {
            anim.SetInteger(PoseParamHash, activePose);
        }
        RefreshPoseHighlights();
    }
}