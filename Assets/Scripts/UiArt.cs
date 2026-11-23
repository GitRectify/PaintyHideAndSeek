using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shapes for screens built in code, drawn once at runtime (the project has no UI art for them):
/// rounded rectangles that stay round at any size (nine-sliced), rounded rings, a soft shadow,
/// a circle, a check mark and a play triangle. All white: an Image's colour (or a UiGradient) tints
/// them. Used by the play setup (PlaySetupUI).
/// </summary>
public static class UiArt
{
    private const int R = 32;          // corner radius the rounded shapes are drawn at, in pixels
    private const int Blur = 22;       // the shadow's soft edge

    private static Sprite rounded, ringThin, ringBold, shadow, circle, check, play;

    public static Sprite Rounded => rounded ? rounded : rounded = RoundedShape("UiRounded", 0f);
    public static Sprite RingThin => ringThin ? ringThin : ringThin = RoundedShape("UiRingThin", 2.5f);
    public static Sprite RingBold => ringBold ? ringBold : ringBold = RoundedShape("UiRingBold", 6f);
    public static Sprite Shadow => shadow ? shadow : shadow = ShadowShape();
    public static Sprite Circle => circle ? circle : circle = Shape("UiCircle", 96, (x, y) => new Vector2(x - 0.5f, y - 0.5f).magnitude - 0.47f);
    public static Sprite Check => check ? check : check = Shape("UiCheck", 96, CheckShape);
    public static Sprite Play => play ? play : play = Shape("UiPlay", 96, PlayShape);

    /// <summary>A shape on an Image, sliced, its corners <paramref name="radius"/> canvas units round.</summary>
    public static void Apply(Image image, Sprite sprite, float radius)
    {
        image.sprite = sprite;
        image.type = Image.Type.Sliced;
        image.fillCenter = true;
        float drawn = sprite == Shadow ? R + Blur : R;
        image.pixelsPerUnitMultiplier = drawn / Mathf.Max(1f, radius);
    }

    // ================= Drawing =================

    private static float RoundedDistance(float x, float y, float w, float h, float r)
    {
        float qx = Mathf.Abs(x - w * 0.5f) - (w * 0.5f - r);
        float qy = Mathf.Abs(y - h * 0.5f) - (h * 0.5f - r);
        return new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    // A filled rounded rectangle (ring 0) or just its edge, ring pixels thick.
    private static Sprite RoundedShape(string name, float ring)
    {
        int size = R * 2 + 4;
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = RoundedDistance(x + 0.5f, y + 0.5f, size, size, R);
                float a = Mathf.Clamp01(0.5f - d);
                if (ring > 0f) a *= Mathf.Clamp01(d + ring + 0.5f);
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        return Finish(name, size, size, px, new Vector4(R + 1, R + 1, R + 1, R + 1));
    }

    private static Sprite ShadowShape()
    {
        int edge = R + Blur;
        int size = edge * 2 + 4;
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = RoundedDistance(x + 0.5f - Blur, y + 0.5f - Blur, size - Blur * 2f, size - Blur * 2f, R);
                float t = Mathf.Clamp01(1f - (d + Blur * 0.2f) / Blur);
                px[y * size + x] = new Color(0f, 0f, 0f, t * t * (3f - 2f * t));
            }
        return Finish("UiShadow", size, size, px, new Vector4(edge, edge, edge, edge));
    }

    private static float CheckShape(float x, float y)
    {
        // Two strokes, 0.09 thick: down to the bottom point, then up to the right.
        float a = Segment(new Vector2(x, y), new Vector2(0.24f, 0.52f), new Vector2(0.42f, 0.32f));
        float b = Segment(new Vector2(x, y), new Vector2(0.42f, 0.32f), new Vector2(0.77f, 0.7f));
        return Mathf.Min(a, b) - 0.09f;
    }

    private static float PlayShape(float x, float y)
    {
        // A right-pointing triangle with softly rounded corners.
        Vector2 p = new Vector2(x, y);
        Vector2 a = new Vector2(0.28f, 0.18f), b = new Vector2(0.28f, 0.82f), c = new Vector2(0.86f, 0.5f);
        bool inside = Cross(b - a, p - a) <= 0f && Cross(c - b, p - b) <= 0f && Cross(a - c, p - c) <= 0f;
        float d = Mathf.Min(Segment(p, a, b), Mathf.Min(Segment(p, b, c), Segment(p, c, a)));
        return (inside ? -d : d) + 0.04f;
    }

    private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

    private static float Segment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Vector2.Dot(ab, ab));
        return (p - (a + ab * t)).magnitude;
    }

    private delegate float Distance(float x, float y);

    // A shape in a unit square (signed distance, negative inside), antialiased at its edge.
    private static Sprite Shape(string name, int size, Distance shape)
    {
        var px = new Color32[size * size];
        float pixel = 1f / size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = shape((x + 0.5f) * pixel, (y + 0.5f) * pixel);
                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d / pixel));
            }
        return Finish(name, size, size, px, Vector4.zero);
    }

    private static Sprite Finish(string name, int w, int h, Color32[] px, Vector4 border)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave,
        };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        Sprite s = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        s.name = name;
        s.hideFlags = HideFlags.DontSave;
        return s;
    }
}
