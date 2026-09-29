using System;

// A thin static wrapper over UtilGame's key/value save data (UtilGame itself isn't reversed in this
// session — referenced here by its apparent API: GetDataInt/SetDataInt/GetDataBool/SetDataBool, each
// taking a key, a default value, and a "category" string). Every property here uses the same "none"
// category except CanShowAds, which is stored under its own distinct key ("KEY_REMOVEADS_AAA")
// rather than following the "PropertyName as key" convention every other property uses — kept exactly
// as decompiled, not a mistake on my part. All 19 other keys (and the category string) have now been
// independently verified against their string literals and match the property names exactly.
public static class GameData
{
    private const string Category = "none";

    public static int ShowAds
    {
        get => UtilGame.GetDataInt("ShowAds", 0, Category);
        set => UtilGame.SetDataInt("ShowAds", value, Category);
    }

    public static int NoAds
    {
        get => UtilGame.GetDataInt("NoAds", 0, Category);
        set => UtilGame.SetDataInt("NoAds", value, Category);
    }

    public static int isRate
    {
        get => UtilGame.GetDataInt("isRate", 0, Category);
        set => UtilGame.SetDataInt("isRate", value, Category);
    }

    public static int GameSession1
    {
        get => UtilGame.GetDataInt("GameSession1", 0, Category);
        set => UtilGame.SetDataInt("GameSession1", value, Category);
    }

    public static int IsSession1
    {
        get => UtilGame.GetDataInt("IsSession1", 0, Category);
        set => UtilGame.SetDataInt("IsSession1", value, Category);
    }

    public static int Coin
    {
        get => UtilGame.GetDataInt("Coin", 100, Category);
        set => UtilGame.SetDataInt("Coin", value, Category);
    }

    // NOTE: after saving, the original calls through a cached static delegate field on GameData
    // (read via a raw vtable-style invoke in the decompiled code) if one is set — almost certainly a
    // notification hook other systems (AudioManager?) subscribe to so they can react immediately to
    // the music toggle rather than polling GameData.Music. Reconstructed as a static event; the exact
    // delegate signature/consumer isn't confirmed. The raw invoke passes exactly two values through
    // (a method-code pointer plus one argument slot), consistent with a single-bool-parameter
    // delegate, which supports (but doesn't fully confirm) the Action<bool> reconstruction here.
    public static event Action<bool> OnMusicChanged;

    public static bool Music
    {
        get => UtilGame.GetDataInt("Music", 1, Category) == 1;
        set
        {
            UtilGame.SetDataInt("Music", value ? 1 : 0, Category);
            OnMusicChanged?.Invoke(value);
        }
    }

    public static int IndexPlayer
    {
        get => UtilGame.GetDataInt("IndexPlayer", 0, Category);
        set => UtilGame.SetDataInt("IndexPlayer", value, Category);
    }

    public static bool Sound
    {
        get => UtilGame.GetDataInt("Sound", 1, Category) == 1;
        set => UtilGame.SetDataInt("Sound", value ? 1 : 0, Category);
    }

    public static int Tutorial
    {
        get => UtilGame.GetDataInt("Tutorial", 0, Category);
        set => UtilGame.SetDataInt("Tutorial", value, Category);
    }

    public static int FloorInter
    {
        get => UtilGame.GetDataInt("FloorInter", 0, Category);
        set => UtilGame.SetDataInt("FloorInter", value, Category);
    }

    public static int Rate
    {
        get => UtilGame.GetDataInt("Rate", 0, Category);
        set => UtilGame.SetDataInt("Rate", value, Category);
    }

    public static int AOAFisrt // NOTE: typo ("Fisrt") preserved exactly as in the original key/property name
    {
        get => UtilGame.GetDataInt("AOAFisrt", 0, Category);
        set => UtilGame.SetDataInt("AOAFisrt", value, Category);
    }

    // CORRECTION: this DOES have a getter in the raw decompile (GameData$$get_Native) — my earlier
    // review flagged this as possibly getter-less, which was a mistake on my part, not a genuine
    // ambiguity in the decompile.
    public static int Native
    {
        get => UtilGame.GetDataInt("Native", 0, Category);
        set => UtilGame.SetDataInt("Native", value, Category);
    }

    public static int InterSplash
    {
        get => UtilGame.GetDataInt("InterSplash", 0, Category);
        set => UtilGame.SetDataInt("InterSplash", value, Category);
    }

    public static int AOAInter
    {
        get => UtilGame.GetDataInt("AOAInter", 0, Category);
        set => UtilGame.SetDataInt("AOAInter", value, Category);
    }

    public static bool Vibrate
    {
        get => UtilGame.GetDataInt("Vibrate", 1, Category) == 1;
        set => UtilGame.SetDataInt("Vibrate", value ? 1 : 0, Category);
    }

    public static bool CanShowAds
    {
        get => UtilGame.GetDataBool("KEY_REMOVEADS_AAA", true, Category);
        set => UtilGame.SetDataBool("KEY_REMOVEADS_AAA", value, Category);
    }

    public static int TimeInterAds
    {
        get => UtilGame.GetDataInt("TimeInterAds", 0, Category);
        set => UtilGame.SetDataInt("TimeInterAds", value, Category);
    }
}