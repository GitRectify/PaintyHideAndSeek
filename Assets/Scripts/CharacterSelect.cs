// using UnityEngine;
// using UnityEngine.SceneManagement;
// using UnityEngine.UI;

// public class CharacterSelect : MonoBehaviour
// {
//     [Header("Characters")]
//     public Transform[] characters;
//     public string[] buttonLabels = { "Player", "Player 2" };
//     public string prefsKey = "SelectedCharacter";

//     [Header("Home placement (applied to the currently-selected character)")]
//     public Vector3 homePos = new Vector3(-54.7f, -1.04f, 25.4f);
//     public Vector3 homeEuler = Vector3.zero;
//     public Vector3 homeScale = Vector3.one;

//     [Header("Selection buttons")]
//     public bool showHomeButtons;
//     public Vector2 buttonSize = new Vector2(300f, 110f);
//     public float buttonGap = 30f;
//     public Vector2 rowPosition = new Vector2(40f, -40f);

//     private GameModeManager gmm;
//     private GameObject canvasGO;
//     private Image[] btnBgs;

//     private static Sprite _solidSprite;

//     public int CurrentIndex
//     {
//         get
//         {
//             int maxIndex = Mathf.Max(0, characters != null ? characters.Length - 1 : 0);
//             int saved = PlayerPrefs.GetInt(prefsKey, 0);
//             return Mathf.Clamp(saved, 0, maxIndex);
//         }
//     }

//     private void Awake()
//     {
//         Apply();
//     }

//     // Positions/rotates/scales the currently-selected character into the "home" pose and marks it as
//     // the active player, while enabling only that one character's GameObject.
//     private void Apply()
//     {
//         if (characters == null || characters.Length == 0) return;

//         int current = CurrentIndex;

//         for (int i = 0; i < characters.Length; i++)
//         {
//             Transform t = characters[i];
//             if (t == null) continue;

//             if (i == current)
//             {
//                 Quaternion rot = Quaternion.Euler(homeEuler);
//                 t.SetPositionAndRotation(homePos, rot);
//                 t.localScale = homeScale;
//                 PlayerRef.Active = t;
//             }

//             t.gameObject.SetActive(i == current);
//         }
//     }

//     // Saves the new selection and reloads the current scene so it takes effect (Apply() runs again
//     // on Awake next load).
//     public void Select(int i)
//     {
//         // CORRECTION: confirmed via trace that Select's clamp does NOT floor maxIndex at 0 the way
//         // CurrentIndex's does — the earlier draft copied CurrentIndex's `Mathf.Max(0, ...)` step
//         // into here too, which isn't actually there. This is a real, if obscure, difference: if
//         // `characters` is empty (Length == 0) and `i >= 0`, this produces clamped == -1, not 0.
//         // Kept as the literal two-step form since Mathf.Clamp(i, 0, maxIndex) would behave
//         // differently (and not match) when maxIndex is negative.
//         int maxIndex = characters != null ? characters.Length - 1 : 0;
//         int clamped = (i < 0) ? 0 : Mathf.Min(i, maxIndex);

//         if (clamped == CurrentIndex) return;

//         PlayerPrefs.SetInt(prefsKey, clamped);
//         PlayerPrefs.Save();

//         int buildIndex = SceneManager.GetActiveScene().buildIndex;
//         SceneManager.LoadScene(buildIndex);
//     }

//     private void Start()
//     {
//         gmm = Object.FindFirstObjectByType<GameModeManager>();

//         if (showHomeButtons)
//         {
//             BuildButtons();
//             RefreshHighlight();
//         }
//     }

//     // Builds one button per character on a dedicated screen-space canvas, laid out in a row.
//     private void BuildButtons()
//     {
//         GameObject canvas = SceneUI.GetOrCreateCanvas("CharSelectCanvas", out bool created);
//         if (canvas == null) return;

//         // Detach from whatever parent GetOrCreateCanvas may have placed it under, so it always lives
//         // at the scene root.
//         if (canvas.transform.parent != null)
//         {
//             canvas.transform.SetParent(null, false);
//         }
//         canvasGO = canvas;

//         Canvas canvasComp = SceneUI.GetOrAdd<Canvas>(canvas);
//         if (created)
//         {
//             canvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
//             canvasComp.sortingOrder = 45;

//             CanvasScaler scaler = SceneUI.GetOrAdd<CanvasScaler>(canvas);
//             scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
//             scaler.referenceResolution = new Vector2(1080f, 1920f);
//             scaler.matchWidthOrHeight = 1f;
//         }
//         SceneUI.GetOrAdd<GraphicRaycaster>(canvas);

//         int count = characters != null ? characters.Length : 0;
//         btnBgs = new Image[count];

//         for (int i = 0; i < count; i++)
//         {
//             int idx = i; // captured for the click listener

//             GameObject btnGO = SceneUI.GetOrCreate("CharBtn" + i, canvas.transform, out bool _btnCreated);
//             Image bg = SceneUI.GetOrAdd<Image>(btnGO);
//             bg.sprite = Solid();

//             RectTransform rt = bg.rectTransform;
//             rt.anchorMax = new Vector2(0f, 1f);
//             rt.anchorMin = new Vector2(0f, 1f);
//             rt.pivot = new Vector2(0f, 1f);
//             rt.sizeDelta = buttonSize;
//             rt.anchoredPosition = new Vector2(
//                 rowPosition.x + (buttonSize.x + buttonGap) * i,
//                 rowPosition.y);

//             btnBgs[i] = bg;

//             Button btn = SceneUI.GetOrAdd<Button>(btnGO);
//             btn.targetGraphic = bg;
//             btn.onClick.RemoveAllListeners();
//             btn.onClick.AddListener(() => Select(idx));

//             GameObject labelGO = SceneUI.GetOrCreate("L", btnGO.transform, out bool _labelCreated);
//             Text label = SceneUI.GetOrAdd<Text>(labelGO);
//             label.font = UIFont.Default;
//             label.alignment = TextAnchor.MiddleCenter;
//             label.color = Color.white;
//             label.fontSize = 40;
//             label.raycastTarget = false;
//             label.text = (buttonLabels != null && i < buttonLabels.Length) ? buttonLabels[i] : "Char " + (i + 1);

//             RectTransform labelRt = label.rectTransform;
//             labelRt.anchorMin = Vector2.zero;
//             labelRt.anchorMax = Vector2.one;
//             labelRt.offsetMin = Vector2.zero;
//             labelRt.offsetMax = Vector2.zero;
//         }
//     }

//     // Tints the currently-selected button green, everything else a neutral dark gray.
//     // (Colors verified byte-for-byte against the raw decompile.)
//     private void RefreshHighlight()
//     {
//         if (btnBgs == null) return;

//         int current = CurrentIndex;
//         for (int i = 0; i < btnBgs.Length; i++)
//         {
//             if (btnBgs[i] == null) continue;

//             btnBgs[i].color = (i == current)
//                 ? new Color(0.20f, 0.60f, 0.32f, 0.97f)
//                 : new Color(0.18f, 0.20f, 0.26f, 0.95f);
//         }
//     }

//     // Shows the selection canvas only while no round is active (i.e. in the menu/lobby).
//     private void Update()
//     {
//         bool showCanvas = gmm == null || !gmm.RoundActive;

//         if (canvasGO == null) return;
//         if (canvasGO.activeSelf == showCanvas) return;

//         canvasGO.SetActive(showCanvas);
//     }

//     // A shared, lazily-built 4x4 solid-white sprite used as the button background base (tinted per
//     // RefreshHighlight). Cached at the class level (static), matching the original's static field.
//     private static Sprite Solid()
//     {
//         if (_solidSprite != null) return _solidSprite;

//         Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
//         Color[] colors = new Color[16];
//         for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
//         tex.SetPixels(colors);
//         tex.Apply();

//         _solidSprite = Sprite.Create(tex, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);
//         return _solidSprite;
//     }
// }