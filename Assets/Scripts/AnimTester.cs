using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output. Fields (names, order, offsets) and every
// method's accessibility match dump.cs (TypeDefIndex 9635). All string literals are confirmed
// against Dumpstringliteral.json. The two BuildUI button lambdas (<>c__DisplayClass13_0.b__0 and
// <>c__DisplayClass13_1.b__1) were decoded from the ARM64 bytes in libil2cpp.so.
//
// A debug overlay: two "action" buttons that fire Animator triggers, plus four toggle buttons that
// set the Animator's "Pose" int (1-4, or 0 when the active one is pressed again).
public class AnimTester : MonoBehaviour
{
    public string characterRootName = "Player";                 // 0x20
    private Animator anim;                                       // 0x28
    private Font font;                                           // 0x30
    private int activePose;                                      // 0x38
    private readonly List<Image> poseBtnImgs = new List<Image>(); // 0x40

    // Decoded from the .cctor's raw bit patterns.
    private static readonly Color Normal = new Color(0.18f, 0.2f, 0.26f, 0.95f);  // statics 0x00
    private static readonly Color Active = new Color(0.2f, 0.62f, 0.34f, 1f);     // statics 0x10
    private static readonly int PoseHash = Animator.StringToHash("Pose");         // statics 0x20
    private static readonly string[] ActLabels = new string[] { "Attack", "Gun Walk" };      // 0x28
    private static readonly string[] ActTrigs = new string[] { "GunAttack", "GunWalking" };  // 0x30

    // A null PlayerRef result just skips the GetComponent.
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

    // Adds the new Input System's UI module when that package is present, otherwise the legacy
    // StandaloneInputModule.
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

    // All buttons use the Normal colour; RefreshPoseHighlights recolours the pose buttons at the end.
    // Only the four pose buttons go into poseBtnImgs. The pose buttons continue the row count after
    // the action buttons, so they sit below them. A null root/canvas/scaler/header/button throws.
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
        Text header = MakeLabelObj("Header", parent, "ANIM TEST", 26, TextAnchor.UpperRight,
            new Color(1f, 1f, 1f, 0.7f), new Vector2(-20f, -90f), new Vector2(200f, 34f));
        header.raycastTarget = false;

        // Action buttons: "Btn_Attack" / "Btn_Gun Walk", each firing its Animator trigger.
        int row = 0;
        for (int i = 0; i < ActLabels.Length; i++)
        {
            string trig = ActTrigs[i];
            Image img = MakeButton("Btn_" + ActLabels[i], parent, ActLabels[i], row, 200f, 70f, 8f, -130f, Normal);
            Button btn = SceneUI.GetOrAdd<Button>(img.gameObject);
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                if (anim != null)
                {
                    anim.SetTrigger(trig);
                }
            });
            row++;
        }

        // Pose buttons "Btn_Pose 1".."Btn_Pose 4", labelled "Pose 1".."Pose 4".
        for (int p = 1; p <= 4; p++)
        {
            Image img = MakeButton("Btn_Pose " + p.ToString(), parent, "Pose " + p.ToString(), row, 200f, 70f, 8f, -130f, Normal);
            poseBtnImgs.Add(img);
            int pn = p;
            Button btn = SceneUI.GetOrAdd<Button>(img.gameObject);
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => TogglePose(pn));
            row++;
        }

        RefreshPoseHighlights();
    }

    // Pressing the active pose again turns it off (0).
    private void TogglePose(int n)
    {
        activePose = (activePose != n) ? n : 0;
        if (anim != null)
        {
            anim.SetInteger(PoseHash, activePose);
        }
        RefreshPoseHighlights();
    }

    // Pose button index i is highlighted when activePose == i + 1. Null entries are skipped.
    private void RefreshPoseHighlights()
    {
        for (int index = 0; index < poseBtnImgs.Count; index++)
        {
            if (poseBtnImgs[index] != null)
            {
                poseBtnImgs[index].color = (activePose == index + 1) ? Active : Normal;
            }
        }
    }

    // Anchored to the top-right corner. Only a newly created button is styled; an existing one just
    // gets its targetGraphic re-set. A null Image/Button/label throws.
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

        // Label "L" stretched over the whole button.
        Text text = MakeLabelObj("L", go.transform, label, 30, TextAnchor.MiddleCenter, Color.white, Vector2.zero, Vector2.zero);
        RectTransform labelRt = text.rectTransform;
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;
        text.raycastTarget = false;
        return image;
    }

    // The font is always re-applied; the rest only on a newly created label. Position/size (with
    // top-right anchors) are applied only when sizeDelta != Vector2.zero - raw tests sizeDelta
    // alone, not pos. A null Text throws.
    private Text MakeLabelObj(string name, Transform parent, string content, int size, TextAnchor anchor, Color color, Vector2 pos, Vector2 sizeDelta)
    {
        GameObject go = SceneUI.GetOrCreate(name, parent, out bool created);
        Text text = SceneUI.GetOrAdd<Text>(go);
        text.font = font;
        if (created)
        {
            text.text = content;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            if (sizeDelta != Vector2.zero)
            {
                RectTransform rt = text.rectTransform;
                rt.anchorMax = new Vector2(1f, 1f);
                rt.anchorMin = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.anchoredPosition = pos;
                rt.sizeDelta = sizeDelta;
            }
        }
        return text;
    }
}
