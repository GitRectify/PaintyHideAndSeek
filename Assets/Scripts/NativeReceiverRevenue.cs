using System.Globalization;
using UnityEngine;

// Receives a pipe-delimited "value|currency" string from the native Android ad-revenue callback
// bridge (same family as NativeCallback) and forwards it to both Firebase Analytics (as an
// "ad_impression" event) and the Adjust SDK's ad-revenue tracking.
public class NativeReceiverRevenue : MonoBehaviour
{
    public void OnNativeRevenue(string data)
    {
        Debug.Log("RAW REVENUE: " + data);

        // CORRECTION on the null-handling note: the decompile DOES have explicit `data != null`
        // and `split-result != null` checks (not a bare unguarded call) — it's just that both
        // checks' failure paths lead straight to the same throw as an unguarded call would.
        // Observable behavior is identical either way, so the natural-NRE version below is still
        // correct and preserves the "crash on null" convention — this just corrects why.
        string[] parts = data.Split('|'); // NOTE: decompiled with an explicit StringSplitOptions.None (0) arg — behaviorally identical to this simpler overload.
        if (parts.Length < 2)
        {
            Debug.LogWarning("Native revenue data invalid: " + data);
            return;
        }

        double value = double.Parse(parts[0], CultureInfo.InvariantCulture);
        string currency = parts[1];

        Debug.Log("NATIVE REVENUE: " + value + " " + currency);

        Firebase.Analytics.Parameter[] parameters = new Firebase.Analytics.Parameter[4];
        parameters[0] = new Firebase.Analytics.Parameter("value", value);
        parameters[1] = new Firebase.Analytics.Parameter("currency", currency);
        parameters[2] = new Firebase.Analytics.Parameter("ad_source", "admob_native_overlay");
        parameters[3] = new Firebase.Analytics.Parameter("ad_platform", "admob");

        Firebase.Analytics.FirebaseAnalytics.LogEvent("ad_impression", parameters);

        AdjustSdk.AdjustAdRevenue adRevenue = new AdjustSdk.AdjustAdRevenue("admob_sdk");
        adRevenue.SetRevenue(value, currency);
        AdjustSdk.Adjust.TrackAdRevenue(adRevenue);
    }
}