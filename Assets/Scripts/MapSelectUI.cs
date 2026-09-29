using UnityEngine;
using UnityEngine.UI;

// NOTE: Builds its own button+label UI at runtime under the home canvas via the SceneUI helper
// (GetOrCreate/GetOrAdd<T>), rather than referencing pre-placed scene objects. Clicking cycles
// LevelManager's current level and keeps its label in sync via LevelManager.onLevelChanged.
public class MapSelectUI : MonoBehaviour
{
    [SerializeField] private string homeCanvasName = "HomeCanvas";
    [SerializeField] private int fontSize = 34;
    [SerializeField] private Vector2 size = new Vector2(380f, 96f);
    [SerializeField] private Vector2 anchoredPos = new Vector2(0f, -150f);

    private Text label;
    private LevelManager lm;
    private GameModeManager gmm;

    private void Start()
    {
        gmm = FindFirstObjectByType<GameModeManager>();
        lm = (LevelManager.Instance != null) ? LevelManager.Instance : FindFirstObjectByType<LevelManager>();

        Build();

        if (lm != null)
        {
            lm.onLevelChanged += RefreshLabel;
        }

        RefreshLabel();
    }

    private void OnDestroy()
    {
        if (lm != null)
        {
            lm.onLevelChanged -= RefreshLabel;
        }
    }

    // NOTE: called externally (not referenced by anything else in this paste) - presumably by
    // whatever authors/rebuilds HUD elements after a scene or config change, re-resolving lm if
    // it wasn't already set and rebuilding/refreshing the button.
    public void AuthorUI()
    {
        if (lm == null)
        {
            lm = FindFirstObjectByType<LevelManager>();
        }
        Build();
        RefreshLabel();
    }

    private void OnClick()
    {
        if (lm == null)
        {
            return;
        }

        lm.Cycle();
        if (gmm != null)
        {
            gmm.RefreshSelectedLevel();
        }
        RefreshLabel();
        AudioManager.PoseChange();
    }

    private void RefreshLabel()
    {
        if (label == null)
        {
            return;
        }
        if (lm == null)
        {
            return;
        }
        label.text = "Map: " + lm.CurrentDisplayName;
    }

    // NOTE: reuses the same "created" out-param slot in raw for both the button's GetOrCreate
    // and the label's GetOrCreate later in this method - verified safe (each call unconditionally
    // overwrites it before either read), so two separate named locals here is a faithful,
    // clearer translation, not a simplification that changes behavior.
    private void Build()
    {
        GameObject homeCanvas = FindInScene(homeCanvasName);
        if (homeCanvas == null)
        {
            Debug.LogWarning($"[MapSelect] '{homeCanvasName}' not found \u2014 map button not built.");
            return;
        }

        bool buttonCreated;
        GameObject buttonGO = SceneUI.GetOrCreate("MapSelectButton", homeCanvas.transform, out buttonCreated);

        Image bg = SceneUI.GetOrAdd<Image>(buttonGO);
        bg.raycastTarget = true;

        if (buttonCreated)
        {
            bg.color = new Color(0.12f, 0.13f, 0.2f, 0.85f);
            RectTransform bgRect = bg.rectTransform;
            bgRect.anchorMax = new Vector2(0.5f, 1f);
            bgRect.anchorMin = new Vector2(0.5f, 1f);
            bgRect.pivot = new Vector2(0.5f, 1f);
            bgRect.anchoredPosition = anchoredPos;
            bgRect.sizeDelta = size;
        }

        Button button = SceneUI.GetOrAdd<Button>(buttonGO);
        button.targetGraphic = bg;
        button.onClick.RemoveListener(OnClick);
        button.onClick.AddListener(OnClick);

        bool labelCreated;
        GameObject labelGO = SceneUI.GetOrCreate("Label", buttonGO.transform, out labelCreated);
        label = SceneUI.GetOrAdd<Text>(labelGO);

        label.font = UIFont.Default;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.white;
        label.fontSize = fontSize;
        label.fontStyle = FontStyle.Bold;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;

        if (labelCreated)
        {
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
        }
    }

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
}