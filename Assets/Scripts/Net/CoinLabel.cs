using UnityEngine;
using UnityEngine.UI;

// Keeps one text showing the player's coin balance (CoinBalance.Bind adds it). It refreshes when
// the balance changes and whenever its screen is shown again, so a screen that was hidden while
// the balance moved never comes back with the old number.
public sealed class CoinLabel : MonoBehaviour
{
    private Text label;
    private string format = "{0}";

    public void Set(Text target, string textFormat)
    {
        label = target;
        format = string.IsNullOrEmpty(textFormat) ? "{0}" : textFormat;
        Refresh();
    }

    private void OnEnable()
    {
        CoinBalance.Changed += Refresh;
        Refresh();
    }

    private void OnDisable() => CoinBalance.Changed -= Refresh;

    public void Refresh()
    {
        if (label != null) label.text = string.Format(format, CoinBalance.Text);
    }
}
