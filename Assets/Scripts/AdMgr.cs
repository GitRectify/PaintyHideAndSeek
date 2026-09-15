using System;
using UnityEngine;

public class AdMgr : MonoBehaviour
{
    public static AdMgr Instance { get; private set; }

    // RootManager currently writes to this value.
    public int InterDelayCache { get; set; }

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

    // No rewarded ads are actually loaded.
    public bool IsRewardReady => true;

    // No app-open ad exists.
    public bool IsAppOpenAdsReady => false;

    // Skip interstitial ad and immediately continue game.
    public void OnInterstitialView(Action onShowAdCompleted)
    {
        onShowAdCompleted?.Invoke();
    }

    // Skip app-open ad and immediately continue.
    public void ShowAdIfReady(Action onHiddenAppOpenAds)
    {
        onHiddenAppOpenAds?.Invoke();
    }

    // Skip banner.
    public void set_OnBannerView(bool value)
    {
    }

    // Skip MREC.
    public void set_OnMrec(bool value)
    {
    }

    // Skip rewarded ad.
    // Treat it as successful so gameplay that expects a reward can continue.
    public void OnRewardView(Action onRewardSuccess, Action onRewardFail)
    {
        onRewardSuccess?.Invoke();
    }
}