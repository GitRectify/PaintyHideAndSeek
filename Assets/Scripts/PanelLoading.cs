using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class PanelLoading : MonoBehaviour
{
    public Image imgFill;
    public float timeLoad;

    private bool isShowAOA;
    private bool isIntAds; // NOTE: set false in Start() and never read/written anywhere else in this file — write-only/unused as far as this dump shows.
    private float speed;
    private float currentTime;

    private void Start()
    {
        isShowAOA = false;
        isIntAds = false;
        // NOTE: this initial speed is effectively dead — Update() unconditionally re-randomizes
        // `speed` to a DIFFERENT range (0.5–2.0, vs. this 0.1–0.8) on every single frame,
        // starting with the very first Update() call after this Start(). Confirmed via trace,
        // not a simplification on my part.
        speed = Random.Range(0.1f, 0.8f);
        currentTime = 0f;

        // Uses the RootManager raw-offset field at +0x22, already flagged elsewhere in this
        // codebase (guessed name "IsShowingAd") — this is now confirmed used from a THIRD file.
        RootManager.Instance.IsShowingAd = false; // TODO: real field name at RootManager+0x22 unconfirmed
    }

    private void Update()
    {
        // Confirmed: speed is re-randomized to a jittery 0.5–2.0 EVERY frame, not once — the
        // loading bar's fill rate genuinely varies randomly frame to frame, not a fixed speed.
        speed = Random.Range(0.5f, 2.0f);

        if (currentTime < timeLoad)
        {
            currentTime += Time.deltaTime * speed;
            imgFill.fillAmount = currentTime / timeLoad;
        }

        if (!isShowAOA)
        {
            // NOTE: decompiled as a raw reinterpret of Image's m_PreserveAspect field storage as
            // a float, compared against 0.95 and (further down) 1.0 — those are fillAmount-style
            // progress thresholds, and this same method sets fillAmount via the proper named
            // setter just above. Strong read: this is really reading back `fillAmount`, and
            // Ghidra's struct layout for UnityEngine.UI.Image has a field-offset mismatch
            // (mislabeling this offset as m_PreserveAspect). Not fully certain — flagging as an
            // inferred correction for a likely decompiler type-info error, not confirmed fact.
            if (imgFill.fillAmount > 0.95f)
            {
                int adType = RootManager.Instance.TypeAppOpen; // offset 0x3c — now independently corroborated from a second file, in addition to RootManager's own Remote()
                RootManager.Instance.IsShowingAd = true; // set before the branch below runs, regardless of adType

                if (adType == 2)
                {
                    // NOTE: AdMgr.ShowAdIfReady(Action) — not previously catalogued for AdMgr.
                    AdMgr.Instance.ShowAdIfReady(null);
                }
                else if (adType == 1)
                {
                    // NOTE: GoogleAdmobManager.ShowInter_AOA() — not previously catalogued for
                    // GoogleAdmobManager (prior known members: Instance, ShowInter_EndGame,
                    // ShowInter_Inapp, ShowInter_PlayGame, ShowAppOpenAd).
                    GoogleAdmobManager.Instance.ShowInter_AOA();
                }
                else if (adType == 0)
                {
                    Debug.Log("vvvvvv");
                    GoogleAdmobManager.Instance.ShowAppOpenAd();
                }
                // NOTE: if adType is anything other than 0/1/2, none of the three branches run —
                // no ad is actually shown — but isShowAOA (both this field and RootManager's
                // IsShowingAd) still get set to true below, silently "marking as shown" with
                // nothing displayed. Confirmed via trace, not asserting this is a bug, just
                // flagging the edge case.

                isShowAOA = true;
            }
        }

        if (imgFill.fillAmount == 1f)
        {
            LoadScene();
        }
    }

    private void LoadScene()
    {
        SceneManager.LoadScene("SampleScene");
    }
}