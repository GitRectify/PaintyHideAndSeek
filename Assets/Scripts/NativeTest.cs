using UnityEngine;

// Native advertising intentionally disabled.
public class NativeTest : MonoBehaviour
{
    public static NativeTest Instance { get; private set; }

    public bool initialized = false;

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

    public bool IsNativeReady()
    {
        return false;
    }

    public void PreloadNative(string adUnit)
    {
        // Ads disabled.
    }

    public void ShowSmallNative(string adUnit)
    {
        // Ads disabled.
    }

    public void ShowFullNative(string adUnit)
    {
        // Ads disabled.
    }

    public void ShowFullNativeNew(string adUnit)
    {
        // Ads disabled.
    }
}