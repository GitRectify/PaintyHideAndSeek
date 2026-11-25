using System;
using UnityEngine;

// The shop's entry point in the scene (ShopCanvas). Since 2026-11-24 the shop itself is design
// B's ShopScreen, built at runtime from the server's categories and items; this component keeps
// the scene's wiring and the calls every screen makes (Open, OpenFrom, CloseOpen), hides Home
// while the shop is up and puts back what was there when it closes. The scene's old shop panel
// (tabs, the 3x2 grid of ShopItemCards) is no longer shown.
public class ShopUI : MonoBehaviour
{
    [Header("Root")]
    [Tooltip("The scene's old shop panel (ShopCanvas): kept hidden, the shop is ShopScreen now.")]
    [SerializeField] private GameObject root;

    [Header("Home")]
    [Tooltip("Hidden while the shop is open, restored on close.")]
    [SerializeField] private GameObject homeCanvas;

    private bool _homeWasShown;
    private Action _onClosed;

    private void Awake()
    {
        if (root != null && root != gameObject) root.SetActive(false);
    }

    public void Open() => OpenFrom(null);

    /// <summary>
    /// Opens the shop over whatever is on screen (Home, the setup, a round's HUD), above it all but
    /// the notices; Close puts back exactly what was there and calls <paramref name="onClosed"/>.
    /// </summary>
    public void OpenFrom(Action onClosed) => OpenFrom(onClosed, null);

    /// <summary>The same, on one category (the server's id, e.g. "coins").</summary>
    public void OpenFrom(Action onClosed, string category)
    {
        _onClosed = onClosed;
        _homeWasShown = homeCanvas != null && homeCanvas.activeSelf;
        if (homeCanvas != null) homeCanvas.SetActive(false);

        // The paint panel draws above everything in a round, the shop included: it would sit over
        // the tabs and take their taps. The shop puts it away (it opens again from its button).
        ChameleonPaint paint = FindAnyObjectByType<ChameleonPaint>();
        if (paint != null && paint.DrawingActive) paint.CloseDrawingNow();

        ShopScreen.Open(Closed, category);
    }

    /// <summary>Closes the shop if it is open: a match is starting under it.</summary>
    public static void CloseOpen() => ShopScreen.CloseIfOpen();

    public void Close() => ShopScreen.CloseIfOpen();

    // Back to what was there: Home only if it was showing (not over a round, not the rooms).
    private void Closed()
    {
        if (homeCanvas != null && _homeWasShown) homeCanvas.SetActive(true);
        Action closed = _onClosed;
        _onClosed = null;
        closed?.Invoke();
    }
}
