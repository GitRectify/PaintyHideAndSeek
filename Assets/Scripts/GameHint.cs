using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A short line near the top of the screen for a moment, in any round, Single Play or online: why
/// a button did nothing (2026-11-23: "No room to stand up here." under a table). Built the first
/// time it is needed, on its own canvas, so Single Play needs nothing of the online screens.
/// </summary>
public sealed class GameHint : MonoBehaviour
{
    private const int CanvasOrder = 41;          // over the round's HUD and banner, under the screens
    private static GameHint instance;

    private Text label;
    private float hideAt;

    /// <summary>Shows <paramref name="text"/> (already translated) for a couple of seconds.</summary>
    public static void Show(string text, float seconds = 2f)
    {
        if (instance == null) instance = new GameObject("GameHint").AddComponent<GameHint>();
        instance.label.text = text;
        instance.label.gameObject.SetActive(true);
        instance.hideAt = Time.unscaledTime + seconds;
    }

    private void Awake()
    {
        // As the online round's banner looks, a little lower, so the two never cover each other.
        Canvas canvas = OnlineUI.CreateCanvas("GameHintCanvas", transform, CanvasOrder);
        label = OnlineUI.NewLabel(canvas.transform, "", 36, Vector2.zero, new Vector2(1400f, 90f), TextAnchor.MiddleCenter, true);
        label.name = "GameHintText";
        label.raycastTarget = false;
        RectTransform rt = label.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -250f);

        Outline outline = label.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new Vector2(2f, -2f);
        label.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (label.gameObject.activeSelf && Time.unscaledTime > hideAt) label.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }
}
