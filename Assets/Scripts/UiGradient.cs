using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>A top-to-bottom colour blend on any UI graphic (sliced rounded shapes too).</summary>
[DisallowMultipleComponent]
public class UiGradient : BaseMeshEffect
{
    public Color top = Color.white;
    public Color bottom = Color.white;

    private static readonly List<UIVertex> verts = new List<UIVertex>();

    public void Set(Color topColor, Color bottomColor)
    {
        if (top == topColor && bottom == bottomColor) return;
        top = topColor;
        bottom = bottomColor;
        if (graphic != null) graphic.SetVerticesDirty();
    }

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || vh.currentVertCount == 0) return;
        verts.Clear();
        vh.GetUIVertexStream(verts);
        float min = float.MaxValue, max = float.MinValue;
        foreach (UIVertex v in verts)
        {
            min = Mathf.Min(min, v.position.y);
            max = Mathf.Max(max, v.position.y);
        }
        float span = Mathf.Max(0.001f, max - min);
        for (int i = 0; i < verts.Count; i++)
        {
            UIVertex v = verts[i];
            Color c = Color.Lerp(bottom, top, (v.position.y - min) / span);
            v.color = (Color32)((Color)v.color * c);
            verts[i] = v;
        }
        vh.Clear();
        vh.AddUIVertexTriangleStream(verts);
    }
}
