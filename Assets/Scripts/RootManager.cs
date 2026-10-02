using System.Collections.Generic;
using UnityEngine;

// Ad functionality intentionally disabled.
// This class is kept as a lightweight singleton because other gameplay scripts
// may still reference RootManager.Instance or ShowInterAds_Native().
public class RootManager : MonoBehaviour
{
    public static RootManager Instance { get; private set; }

    // Kept only for scene/prefab serialization compatibility.
    public bool isShowAds;      // 0x20
    public bool isCapInter;     // 0x21 - interstitial cooldown passed (see GameController.TimerCapInter)
    public bool isShowAoA;      // 0x22
    public bool isNativeNew;    // 0x23 - Remote Config "IsNativeNew"
    public bool isInterNet;     // 0x24

    public GameObject loadingAdsUI;

    public int number;          // 0x30 - set via SetNumber: 0 = menu, 1 = in a round, 2 = result screen
    public int Interdelay;
    public int TimeAdsStart;
    public int TypeAppOpen;
    public int PercentClick;

    [SerializeField]
    private List<bool> nativeBag = new List<bool>();
    private int nativeBagIndex;

    // Remote Config flags (all set in Remote()).
    public bool EndInter_AoA;
    public bool OnBanner;
    public bool InterNative;
    public bool PoseReward;
    public bool LoadingShow;
    public bool InterInApp;
    public bool InterPlayGame;
    public bool InterEndGame;
    public bool OnInterPlayGame;

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
        if (Instance != null)
        {
            PercentClick = value;
        }
    }
}
