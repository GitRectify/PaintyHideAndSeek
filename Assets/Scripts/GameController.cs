// using System.Collections;
// using UnityEngine;
// using UnityEngine.UI;

// public class GameController : SingletonMonoBehavior<GameController>
// {
//     // Fields — accessibility unconfirmed (decompiled as plain field access either way);
//     // declared public as the natural fit for Inspector-assigned MonoBehaviour references.
//     public GameObject popupRate;
//     public GameObject btnRate;
//     public GameObject btnRemoveAds1;
//     public GameObject btnRemoveAds2;
//     public Text[] txtCoin;
//     public GameObject head;

//     public GameObject poseReward3;
//     public GameObject poseReward4;
//     public GameObject poseReward5;
//     public GameObject poseReward6;
//     public GameObject poseReward7;
//     public GameObject poseReward8;
//     public GameObject poseReward9;

//     public bool isReward3;
//     public bool isReward4;
//     public bool isReward5;
//     public bool isReward6;
//     public bool isReward7;
//     public bool isReward8;
//     public bool isReward9;

//     // No field-initializer defaults confirmed in this dump — the ctor only calls the
//     // SingletonMonoBehavior<GameController> base constructor, nothing else. No custom
//     // constructor needed in clean C#.

//     public void Start()
//     {
//         // NOTE: RootManager.Instance's null-check throws naturally on failure (no explicit
//         // early-return in the original) — preserved as a natural crash rather than a guard.
//         // CORRECTION from the earlier draft: OnBannerView is a plain bool property setter, not
//         // a delegate/Action as previously guessed from call-surface alone. It's set directly from
//         // a newly-confirmed RootManager field at raw offset 0x55 (a byte/bool-like field, not
//         // previously catalogued in the outstanding RootManager offset list) — name is a guess.
//         AdMgr.Instance.OnBannerView = RootManager.Instance.BannerAdsReady; // TODO: field name at RootManager+0x55 unconfirmed

//         // NOTE: GameData.GameSession1 is confirmed INT-typed here (compared to 0, set to literal
//         // 1) — same "named like a bool, typed as int" pattern already confirmed for ShowAds and
//         // isRate. Extends that known GameData quirk to a third property.
//         if (GameData.GameSession1 == 0)
//         {
//             StartCoroutine(TimerCapInter(1f));
//             GameData.GameSession1 = 1;
//         }

//         CheckRemoveAds();
//         UpdateTextCoin();

//         // NOTE: GameData.IsSession1 is also int-typed (compared to exactly 1, later incremented)
//         // — not a bool despite the earlier guess. The popupRate block only fires when
//         // IsSession1 == 1 specifically, not on any truthy value.
//         if (GameData.IsSession1 == 1)
//         {
//             popupRate.SetActive(GameData.isRate == 0);
//         }

//         // btnRate.SetActive / IsSession1 increment run unconditionally after the block above,
//         // not nested inside it — confirmed via trace, not a flattening on my part.
//         btnRate.SetActive(GameData.isRate == 0);
//         GameData.IsSession1 = GameData.IsSession1 + 1;
//     }

//     private void CappingInter()
//     {
//         StartCoroutine(TimerCapInter(1f));
//     }

//     // NOTE: only the compiler-generated wrapper that constructs the iterator state machine was
//     // decompiled here (it stores `vaTimer` into the state machine and nothing else) — the actual
//     // loop body (MoveNext) was never in the pasted dump, so its contents are NOT decompiled.
//     // Retracting my earlier draft's guessed loop body entirely rather than presenting invented
//     // logic as reconstructed. Confirmed separately: this method does NOT capture `this` at all
//     // (no instance field gets written into the state machine, only vaTimer) — consistent with
//     // Start()'s call site passing a null `this` for this same call, which is harmless precisely
//     // because it's unused.
//     private IEnumerator TimerCapInter(float vaTimer)
//     {
//         // TODO: not decompiled — body unknown.
//         yield break;
//     }

//     private void CheckRemoveAds()
//     {
//         // NOTE: GameData.ShowAds confirmed int-typed (see earlier caveat: buying remove-ads sets
//         // ShowAds = 1). Both buttons must be non-null or this throws naturally in the original
//         // (explicit null-guards in the decompile all lead to the same throw path either way).
//         bool adsRemoved = GameData.ShowAds != 0;
//         btnRemoveAds1.SetActive(!adsRemoved);
//         btnRemoveAds2.SetActive(!adsRemoved);
//     }

//     private void UpdateTextCoin()
//     {
//         // NOTE: txtCoin being null throws naturally here (the original explicitly checks and
//         // throws rather than silently skipping) — no guard needed in clean C#.
//         for (int i = 0; i < txtCoin.Length; i++)
//         {
//             // NOTE: decompiled with a defensive null-coalesce to "" (StringLiteral_1) if
//             // Coin.ToString() somehow returned null — practically unreachable for Int32.ToString,
//             // but preserved for fidelity.
//             string coinText = GameData.Coin.ToString();
//             txtCoin[i].text = coinText ?? "";
//         }
//     }

//     private void ShowRate()
//     {
//         popupRate.SetActive(true);
//     }

//     // NOTE: isReward6 is confirmed set to false TWICE (first and last in this block), while
//     // isReward3/4/5/7/8/9 are each set exactly once, in this exact non-sequential order
//     // (6,7,8,9,3,4,5,6). Confirmed via trace against the real decompile, not a transcription
//     // error on my part — reads like a genuine copy-paste oddity in the original source. Preserved
//     // exactly rather than collapsed to a single assignment.
//     private void ResetPoseReward()
//     {
//         isReward6 = false;
//         isReward7 = false;
//         isReward8 = false;
//         isReward9 = false;
//         isReward3 = false;
//         isReward4 = false;
//         isReward5 = false;
//         isReward6 = false;

//         poseReward3.SetActive(true);
//         poseReward4.SetActive(true);
//         poseReward5.SetActive(true);
//         poseReward6.SetActive(true);
//         poseReward7.SetActive(true);
//         poseReward8.SetActive(true);
//         poseReward9.SetActive(true);
//     }

//     private void OnHead()
//     {
//         // NOTE: confirmed as HandleFireBase's static Instance check (matches the previously
//         // catalogued bare-singleton pattern) — throws naturally if Instance or head is null.
//         HandleFireBase.Instance.LogEventWithString("PlayNowMode");
//         head.SetActive(true);
//     }

//     private void OffHead()
//     {
//         // NOTE: this is a DIFFERENT string from OnHead's ("SeekerMode" vs "PlayNowMode") — my
//         // earlier draft's placeholder wrongly implied they might be the same.
//         HandleFireBase.Instance.LogEventWithString("SeekerMode");
//         head.SetActive(false);
//     }
// }