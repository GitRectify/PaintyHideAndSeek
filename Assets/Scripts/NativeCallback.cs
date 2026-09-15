using UnityEngine;

// Native ad callbacks intentionally disabled.
// Kept only so scene/prefab references and UnitySendMessage calls do not break.
public class NativeCallback : MonoBehaviour
{
    public void OnNativeShow(string empty)
    {
        // Ads disabled intentionally.
        Debug.Log("Native ad callback ignored: OnNativeShow");
    }

    public void OnNativeClosed(string empty)
    {
        // Ads disabled intentionally.
        Debug.Log("Native ad callback ignored: OnNativeClosed");
    }
}