using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// The round's minimap (2026-11-25; the prototype's, under the role chip): the room seen from
/// above, with the player's own arrow on it, moving and turning with them. A tap opens it large over
/// the game; a tap on the large map (or its X, or Back) closes it.
///
/// The picture is taken once a round (again only when the player climbs a floor higher or drops
/// one lower) by a camera looking straight down from a little over the player's head, so ceilings
/// and upper floors are cut away and the room's layout shows. Every character is left out of the
/// picture: it shows the room, never where anyone hides. Single Play and online rounds alike: it
/// needs only the room the scene has loaded.
/// </summary>
public sealed class PaintboxMinimap : MonoBehaviour
{
    // Design units (a 1080-tall screen), top left under the role chip.
    public const float Width = 270f, Height = 190f;
    public const float Top = PaintboxHud.EdgeY + 76f;

    /// <summary>How far down from the top the minimap reaches (what sits under it starts below this).</summary>
    public const float Bottom = Top + Height;

    private const int TextureWidth = 512;
    private const float RecaptureHeights = 0.8f;   // a body height up or down: a new floor
    private const float RecaptureWait = 2f;

    private RectTransform small;
    private RawImage smallMap, largeMap;
    private RectTransform smallArrow, largeArrow, largeFrame;
    private GameObject largeRoot;
    private CanvasGroup group;

    private RenderTexture texture;
    private Camera shot;
    private GameModeManager gmm;
    private CameraController cam;

    // What the picture shows, to put the arrow on it.
    private Vector2 centre;              // world x, z
    private float halfHeight;            // world units from the centre to the top edge
    private float aspect = Width / Height;
    private bool haveShot;
    private GameObject shotRoom;
    private float shotFeetY, shotAt, bodyHeight = 8f;
    private float shownSince = -1f;

    // ================= Set up =================

    /// <summary>Builds the minimap on the HUD's design layer (PaintboxHud's overlay) and its large view on a canvas of its own.</summary>
    public static PaintboxMinimap Create(RectTransform overlay, Transform owner)
    {
        PaintboxMinimap map = overlay.gameObject.AddComponent<PaintboxMinimap>();
        map.Build(overlay, owner);
        return map;
    }

    private void Build(RectTransform overlay, Transform owner)
    {
        gmm = FindAnyObjectByType<GameModeManager>(FindObjectsInactive.Include);
        cam = FindAnyObjectByType<CameraController>(FindObjectsInactive.Include);

        // The small map: glass edged, rounded, the picture inside with a soft tint, the arrow on top.
        Button open = Paintbox.Button(overlay, "", Paintbox.Tone.Glass, null, OpenLarge, 20f, 26f);
        open.name = "Minimap";
        small = (RectTransform)open.transform;
        Paintbox.Pin(small, new Vector2(0f, 1f), new Vector2(PaintboxHud.EdgeX, -Top), new Vector2(Width, Height));
        PaintboxFrame frame = open.GetComponent<PaintboxFrame>();
        Transform content = frame.Body.Find("Content");
        if (content != null) Destroy(content.gameObject);
        group = Paintbox.Ensure<CanvasGroup>(small.gameObject);
        smallMap = MapArea(frame.Body, 6f, 20f, out smallArrow, 30f);

        // Large: dimmed screen, a big framed map in the middle, X in its corner; any tap closes it.
        Transform canvas = OnlineUI.CreateCanvas("MinimapCanvas", owner, 46).transform;
        Image dim = Paintbox.Image(canvas, "MinimapLarge", null, Paintbox.Dim, true);
        Paintbox.Fill(dim.rectTransform);
        largeRoot = dim.gameObject;
        Button closeAll = dim.gameObject.AddComponent<Button>();
        closeAll.transition = Selectable.Transition.None;
        closeAll.onClick.AddListener(CloseLarge);

        PaintboxFrame big = Paintbox.Frame(dim.rectTransform, "Panel", Paintbox.Glass, Paintbox.RadiusLarge, 4f, 6f, Paintbox.GlassEdge, Paintbox.GlassShadow);
        largeFrame = big.Root;
        Paintbox.Pin(largeFrame, new Vector2(0.5f, 0.5f), new Vector2(0f, -10f), new Vector2(Height * 3.4f * aspect, Height * 3.4f));
        largeMap = MapArea(big.Body, 10f, 30f, out largeArrow, 64f);

        Text title = Paintbox.Label(dim.rectTransform, "Map", 40f, Color.white, Paintbox.Weight.Title, TextAnchor.LowerCenter);
        title.rectTransform.anchorMin = title.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        title.rectTransform.pivot = new Vector2(0.5f, 0f);
        title.rectTransform.anchoredPosition = new Vector2(0f, largeFrame.sizeDelta.y * 0.5f - 4f);
        title.rectTransform.sizeDelta = new Vector2(600f, 60f);

        Button x = Paintbox.IconButton(largeFrame, "X", Paintbox.Tone.Paper, CloseLarge, 76f);
        x.name = "MinimapClose";
        Paintbox.Pin((RectTransform)x.transform, Vector2.one, new Vector2(24f, 24f), new Vector2(76f, 76f));
        BackKey.Mark(x);
        largeRoot.SetActive(false);
    }

    // A rounded window onto the picture: the map fills it, the arrow is on top.
    private static RawImage MapArea(RectTransform parent, float inset, float radius, out RectTransform arrow, float arrowSize)
    {
        Image window = Paintbox.Image(parent, "Window", UiArt.Rounded, Color.white);
        UiArt.Apply(window, UiArt.Rounded, radius);
        Paintbox.Fill(window.rectTransform, inset, inset, inset, inset);
        window.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        window.raycastTarget = false;

        var mapGo = new GameObject("Map", typeof(RectTransform), typeof(RawImage));
        mapGo.transform.SetParent(window.transform, false);
        RawImage map = mapGo.GetComponent<RawImage>();
        map.raycastTarget = false;
        map.color = new Color(0.82f, 0.82f, 0.86f, 1f);   // a little dimmed, so the arrow stands out
        Paintbox.Fill(map.rectTransform);

        Image marker = Paintbox.Image(window.rectTransform, "Me", UiArt.Circle, Paintbox.Yellow);
        marker.preserveAspect = true;
        marker.raycastTarget = false;
        arrow = marker.rectTransform;
        arrow.anchorMin = arrow.anchorMax = arrow.pivot = new Vector2(0.5f, 0.5f);
        arrow.sizeDelta = new Vector2(arrowSize, arrowSize);
        Outline edge = marker.gameObject.AddComponent<Outline>();
        edge.effectColor = Paintbox.Ink;
        edge.effectDistance = new Vector2(2.5f, -2.5f);
        Image pointer = Paintbox.IconImage(arrow, "ArrowUp", arrowSize * 0.68f, Paintbox.Ink);
        pointer.raycastTarget = false;
        return map;
    }

    private void OnDestroy()
    {
        if (texture != null) { texture.Release(); Destroy(texture); }
        if (shot != null) Destroy(shot.gameObject);
    }

    // ================= Every frame =================

    /// <summary>Shown with the HUD; PaintboxHud calls this each frame it lays the HUD out.</summary>
    public void Tick(bool hudShown)
    {
        bool free = cam != null && cam.FreeLook;
        bool show = hudShown && !free;
        if (small.gameObject.activeSelf != show) small.gameObject.SetActive(show);
        if (!hudShown && largeRoot.activeSelf) largeRoot.SetActive(false);
        if (!hudShown)
        {
            shownSince = -1f;
            return;
        }
        if (shownSince < 0f) shownSince = Time.unscaledTime;

        Transform body = PlayerRef.Active;
        if (body == null) return;

        // The picture: once the body is in place, and again on another floor (or in another room).
        GameObject room = LevelManager.Instance != null ? LevelManager.Instance.CurrentRoomInstance : null;
        bool settled = Time.unscaledTime - shownSince > 0.6f && !OnlineRound.AwaitingPlacement;
        if (settled && (!haveShot || room != shotRoom ||
                        (Mathf.Abs(body.position.y - shotFeetY) > bodyHeight * RecaptureHeights && Time.unscaledTime - shotAt > RecaptureWait)))
            Capture(room, body);

        if (group.alpha != (haveShot ? 1f : 0.6f)) group.alpha = haveShot ? 1f : 0.6f;
        if (!haveShot) return;

        Place(smallArrow, smallMap.rectTransform, body);
        if (largeRoot.activeSelf) Place(largeArrow, largeMap.rectTransform, body);
    }

    // The arrow where the body is on the picture, turned the way it faces (north is up).
    private void Place(RectTransform arrow, RectTransform map, Transform body)
    {
        Vector3 p = body.position;
        float u = (p.x - centre.x) / (2f * halfHeight * aspect);
        float v = (p.z - centre.y) / (2f * halfHeight);
        Vector2 size = map.rect.size;
        Vector2 at = new Vector2(Mathf.Clamp(u, -0.5f, 0.5f) * size.x, Mathf.Clamp(v, -0.5f, 0.5f) * size.y);
        if ((arrow.anchoredPosition - at).sqrMagnitude > 0.01f) arrow.anchoredPosition = at;

        Vector3 f = body.forward;
        float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        arrow.localRotation = Quaternion.Euler(0f, 0f, -yaw);
    }

    // ================= Opening large =================

    private void OpenLarge()
    {
        if (!haveShot) return;
        AudioManager.ButtonTap();
        largeRoot.SetActive(true);
        largeRoot.transform.SetAsLastSibling();
    }

    private void CloseLarge()
    {
        if (!largeRoot.activeSelf) return;
        AudioManager.ButtonTap();
        largeRoot.SetActive(false);
    }

    /// <summary>Whether the large map is open (Back closes it).</summary>
    public bool LargeOpen => largeRoot != null && largeRoot.activeSelf;

    // ================= The picture =================

    private void Capture(GameObject room, Transform body)
    {
        shotRoom = room;
        shotAt = Time.unscaledTime;
        shotFeetY = body.position.y;

        // How tall a body is: the cut sits a little over the head, under the ceilings.
        Renderer[] own = body.GetComponentsInChildren<Renderer>();
        if (own.Length > 0)
        {
            Bounds b = own[0].bounds;
            for (int i = 1; i < own.Length; i++) b.Encapsulate(own[i].bounds);
            if (b.size.y > 0.5f) bodyHeight = b.size.y;
        }

        if (!RoomBounds(room, out Bounds area)) return;

        centre = new Vector2(area.center.x, area.center.z);
        halfHeight = Mathf.Max(area.extents.z, area.extents.x / aspect) * 1.04f;
        if (halfHeight < 0.5f) return;

        if (texture == null)
        {
            texture = new RenderTexture(TextureWidth, Mathf.RoundToInt(TextureWidth / aspect), 16, RenderTextureFormat.ARGB32)
            {
                name = "Minimap",
                antiAliasing = 2,
            };
            texture.Create();
        }

        if (shot == null)
        {
            shot = new GameObject("MinimapCamera").AddComponent<Camera>();
            shot.enabled = false;
            shot.orthographic = true;
            shot.clearFlags = CameraClearFlags.SolidColor;
            shot.backgroundColor = new Color(0.13f, 0.10f, 0.16f, 1f);
            shot.cullingMask = ~(1 << 5);   // not the UI layer
            shot.allowHDR = false;
            shot.allowMSAA = true;
            shot.useOcclusionCulling = false;
        }

        float cutY = shotFeetY + bodyHeight * 1.5f;
        shot.transform.SetPositionAndRotation(new Vector3(centre.x, cutY, centre.y), Quaternion.Euler(90f, 0f, 0f));
        shot.orthographicSize = halfHeight;
        shot.aspect = aspect;
        shot.nearClipPlane = 0.01f;
        shot.farClipPlane = Mathf.Max(1f, cutY - area.min.y + bodyHeight);
        shot.targetTexture = texture;

        // Nobody in the picture: every character's renderers are off while it is taken.
        var hidden = new List<Renderer>();
        foreach (Animator a in FindObjectsByType<Animator>(FindObjectsSortMode.None))
        {
            foreach (Renderer r in a.GetComponentsInChildren<Renderer>())
            {
                if (r.forceRenderingOff || !r.enabled) continue;
                r.forceRenderingOff = true;
                hidden.Add(r);
            }
        }

        try
        {
            var request = new RenderPipeline.StandardRequest { destination = texture };
            if (RenderPipeline.SupportsRenderRequest(shot, request)) RenderPipeline.SubmitRenderRequest(shot, request);
            else shot.Render();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[Minimap] The room could not be drawn: " + ex.Message);
        }
        finally
        {
            foreach (Renderer r in hidden) if (r != null) r.forceRenderingOff = false;
        }

        smallMap.texture = largeMap.texture = texture;
        haveShot = true;
    }

    // The room's footprint: its renderers (not the characters', not the free camera's walls).
    private static bool RoomBounds(GameObject room, out Bounds area)
    {
        area = default;
        bool any = false;
        IEnumerable<Renderer> renderers = room != null
            ? room.GetComponentsInChildren<Renderer>()
            : (IEnumerable<Renderer>)FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);

        foreach (Renderer r in renderers)
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (r is not MeshRenderer and not SkinnedMeshRenderer) continue;
            if (r.GetComponentInParent<Animator>() != null) continue;
            Bounds b = r.bounds;
            if (b.size.x > 2000f || b.size.z > 2000f) continue;   // a sky or a backdrop, not the room
            if (!any) { area = b; any = true; }
            else area.Encapsulate(b);
        }
        return any && area.size.x > 0.5f && area.size.z > 0.5f;
    }
}
