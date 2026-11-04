using UnityEngine;
using UnityEngine.UI;

public class RotateImage1 : MonoBehaviour
{
    public Image image;
    public float speed = 100f; // CORRECTION: field initializer instead of a custom ctor — the decompiled ctor only did this one assignment before the implicit base call, matching the field-initializer convention used throughout this codebase.

    private void Update()
    {
        RectTransform rt = image.rectTransform;
        rt.Rotate(0f, 0f, speed * Time.deltaTime);
    }
}