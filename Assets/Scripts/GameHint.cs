/// <summary>
/// A short line for a moment, in any round, Single Play or online: why a button did nothing
/// (2026-11-23: "No room to stand up here." under a table). Since 2026-11-24 it is one of the
/// game's notices (Notices), so every message on every screen looks and moves the same.
/// </summary>
public static class GameHint
{
    /// <summary>Shows <paramref name="text"/> (already translated) for a couple of seconds.</summary>
    public static void Show(string text, float seconds = 2f) => Notices.Show(NoticeKind.Info, text, null, seconds);
}
