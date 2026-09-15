using System.Collections;
using UnityEngine;

// Google/AdMob advertising intentionally disabled.
// Compatibility stub for reconstructed project.
public class GoogleAdmobManager : MonoBehaviour
{
    public static GoogleAdmobManager Instance { get; private set; }

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

    private void Start()
    {
        // AdMob initialization intentionally disabled.
    }

    public void ShowInter() { }
    public void ShowInter_Inapp() { }
    public void ShowInter_PlayGame() { }
    public void ShowInter_EndGame() { }
    public void ShowInter_AOA() { }
    public void ShowAppOpenAd() { }
    public void CappingInter() { }

    private IEnumerator TimerCapInter(float vaTimer)
    {
        yield break;
    }
}
