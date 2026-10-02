using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// Re-verified method by method against raw Ghidra output. Fields (names, order, offsets) and every
// method's accessibility match dump.cs (TypeDefIndex 9618). All string literals are confirmed
// against Dumpstringliteral.json. Fully reversed, including the TimerCapInter coroutine.
public class GameController : SingletonMonoBehavior<GameController>
{
    public Text[] txtCoin;          // 0x20
    public bool seeker;             // 0x28 - not read or written by any GameController method
    public GameObject head;         // 0x30

    // Per-slot "pose unlocked this session" flags for pose slots 2..8 (see PoseSelectorUI.PickSlot
    // and its <PickSlot>b__31_N callbacks, which set these and hide the matching overlay below).
    public bool isReward3;          // 0x38
    public bool isReward4;          // 0x39
    public bool isReward5;          // 0x3A
    public bool isReward6;          // 0x3B
    public bool isReward7;          // 0x3C
    public bool isReward8;          // 0x3D
    public bool isReward9;          // 0x3E

    // Lock overlays drawn over pose slots 2..8 until the slot is unlocked.
    public GameObject poseReward3;  // 0x40
    public GameObject poseReward4;  // 0x48
    public GameObject poseReward5;  // 0x50
    public GameObject poseReward6;  // 0x58
    public GameObject poseReward7;  // 0x60
    public GameObject poseReward8;  // 0x68
    public GameObject poseReward9;  // 0x70

    public GameObject popupRate;    // 0x78
    public GameObject btnRate;      // 0x80
    public int numberRate;          // 0x88 - not read or written by any GameController method
    public GameObject btnRemoveAds1; // 0x90
    public GameObject btnRemoveAds2; // 0x98

    // The ctor only calls the SingletonMonoBehavior<GameController> base ctor - no field defaults.

    // Raw reads RootManager+0x55, which dump.cs names RootManager.OnBanner (a Firebase Remote
    // Config flag), and passes it to AdMgr's OnBannerView setter. A null RootManager
    // or AdMgr throws. popupRate is only touched when IsSession1 == 1 exactly; btnRate and the
    // IsSession1 increment run every time.
    private void Start()
    {
        // AdMgr.Instance.OnBannerView = RootManager.Instance.OnBanner;

        if (GameData.GameSession1 == 0)
        {
            // Raw passes a null `this` here (unlike CappingInter) - harmless, because the
            // TimerCapInter state machine never captures `this`.
            StartCoroutine(TimerCapInter(1f));
            GameData.GameSession1 = 1;
        }

        CheckRemoveAds();
        UpdateTextCoin();

        if (GameData.IsSession1 == 1)
        {
            popupRate.SetActive(GameData.isRate == 0);
        }

        btnRate.SetActive(GameData.isRate == 0);
        GameData.IsSession1 = GameData.IsSession1 + 1;
    }

    public void ShowRate()
    {
        popupRate.SetActive(true);
    }

    // Confirmed from raw: isReward6 is cleared twice (first and last), in this exact order.
    // Preserved as-is. A null overlay throws partway through.
    public void ResetPoseReward()
    {
        isReward6 = false;
        isReward7 = false;
        isReward8 = false;
        isReward9 = false;
        isReward3 = false;
        isReward4 = false;
        isReward5 = false;
        isReward6 = false;

        // poseReward3.SetActive(true);
        // poseReward4.SetActive(true);
        // poseReward5.SetActive(true);
        // poseReward6.SetActive(true);
        // poseReward7.SetActive(true);
        // poseReward8.SetActive(true);
        // poseReward9.SetActive(true);
    }

    public void OnHead()
    {
        HandleFireBase.Instance.LogEventWithString("PlayNowMode");
        head.SetActive(true);
    }

    public void OffHead()
    {
        HandleFireBase.Instance.LogEventWithString("SeekerMode");
        head.SetActive(false);
    }

    // A null txtCoin or a null element throws. The "" fallback (StringLiteral_1) for a null
    // ToString() result is unreachable for Int32 but is in raw.
    public void UpdateTextCoin()
    {
        for (int i = 0; i < txtCoin.Length; i++)
        {
            Text t = txtCoin[i];
            string coinText = GameData.Coin.ToString();
            t.text = coinText ?? "";
        }
    }

    public void CappingInter()
    {
        StartCoroutine(TimerCapInter(1f));
    }

    // Decompiled from GameController.<TimerCapInter>d__27$$MoveNext (fields vaTimer,
    // <timeWaiting>5__2, <waitForSeconds>5__3; no <>4__this). It is the interstitial-ad cooldown:
    // it clears RootManager.isCapInter (+0x21), then counts up one second at a time from vaTimer
    // and sets isCapInter back to true once the count reaches RootManager.TimeAdsStart (+0x38, a
    // Remote Config value, default 60). RootManager.Instance is re-read on every step, and the same
    // read is used for both the limit and the flag write; a null instance throws. After the limit
    // is reached it yields one extra frame (state 2) before finishing.
    private IEnumerator TimerCapInter(float vaTimer)
    {
        RootManager.Instance.isCapInter = false;
        float timeWaiting = vaTimer;
        WaitForSeconds waitForSeconds = new WaitForSeconds(1f);

        while (true)
        {
            RootManager root = RootManager.Instance;
            int limit = root.TimeAdsStart;
            if (limit <= timeWaiting) break;

            timeWaiting += 1f;
            if (limit <= timeWaiting)
            {
                root.isCapInter = true;
            }
            yield return waitForSeconds;
        }

        yield return null;
    }

    // ShowAds != 0 means ads were removed (purchased), so both remove-ads buttons hide.
    // A null button throws.
    public void CheckRemoveAds()
    {
        bool show = GameData.ShowAds == 0;
        btnRemoveAds1.SetActive(show);
        btnRemoveAds2.SetActive(show);
    }
}
