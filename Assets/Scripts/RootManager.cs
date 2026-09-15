using UnityEngine;

// Ad functionality intentionally disabled.
// This class is kept as a lightweight singleton because other gameplay scripts
// may still reference RootManager.Instance or ShowInterAds_Native().
public class RootManager : MonoBehaviour
{
    public static RootManager Instance { get; private set; }

    // Kept only for scene/prefab serialization compatibility.
    public GameObject loadingAdsUI;

    // Compatibility fields kept so other reconstructed scripts do not immediately
    // fail if they still reference the old ad configuration.
    public bool InterNative;
    public bool OnInterPlayGame;
    public bool InterInApp;
    public bool InterPlayGame;
    public bool InterEndGame;
    public bool OnBanner;
    public bool PoseReward;
    public bool isNativeNew;
    public bool LoadingShow;
    public bool EndInter_AoA;

    public int PercentClick;
    public int Interdelay;
    public int TimeAdsStart;
    public int TypeAppOpen;

    // Compatibility-only flag. Ads are disabled, so it remains false.
    public bool IsShowingAd { get; set; } = false;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else if (Instance != this)
        {
            Destroy(gameObject);
        }
    }

    // Kept because other scripts may call this method.
    // Ads are intentionally skipped.
    public void ShowInterAds_Native()
    {
        if (loadingAdsUI != null)
            loadingAdsUI.SetActive(false);
    }

    // Kept for compatibility with reconstructed callers.
    public void SetNumber(int value)
    {
        PercentClick = value;
    }
}
