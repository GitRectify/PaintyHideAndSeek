using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Meccha.Client;
using Meccha.Shared.Game;
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
///
/// On it (2026-11-25): the player's own arrow (yellow), the free camera while it is on (blue), and
/// the other players, an arrow the way they face while they are in and an X where they fell once
/// they are out; teammates green, the other side red. A hider sees everyone; a seeker sees the
/// seekers and the hiders who are out, never a hider still hiding.
/// </summary>
public sealed class PaintboxMinimap : MonoBehaviour
{
    // Design units (a 1080-tall screen), top left under the role chip; 120% of the first size (2026-11-26).
    public const float Width = 324f, Height = 228f;
    public const float Top = PaintboxHud.EdgeY + 76f;

    /// <summary>How far down from the top the minimap reaches (what sits under it starts below this).</summary>
    public const float Bottom = Top + Height;

    private const int TextureWidth = 512;
    private const float RecaptureHeights = 0.8f;   // a body height up or down: a new floor
    private const float RecaptureWait = 2f;

    private RectTransform small;
    private RawImage smallMap, largeMap;
    private RectTransform smallArrow, largeArrow, largeFrame;
    private RectTransform smallCam, largeCam;   // the free camera, blue
    private readonly List<RectTransform> smallMarks = new List<RectTransform>(), largeMarks = new List<RectTransform>();

    // Marker colours: the player's own (yellow), the free camera (blue), teammates and the other side.
    private static readonly Color CameraBlue = new Color(0.31f, 0.62f, 1f, 1f);
    private static readonly Color Teammate = new Color(0.30f, 0.85f, 0.45f, 1f);
    private static readonly Color Enemy = new Color(0.95f, 0.30f, 0.28f, 1f);
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
        smallMap = MapArea(frame.Body, 6f, 20f, out smallArrow, 26f);
        smallCam = Marker(smallArrow.parent, "FreeCamera", CameraBlue, 22f);

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
        largeMap = MapArea(big.Body, 10f, 30f, out largeArrow, 56f);
        largeCam = Marker(largeArrow.parent, "FreeCamera", CameraBlue, 46f);

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

        arrow = Marker(window.rectTransform, "Me", Paintbox.Yellow, arrowSize);
        arrow.gameObject.SetActive(true);
        return map;
    }

    // The direction marker (2026-11-26): the drop of mini-map-arrow.svg, its tip the way the
    // player faces, rendered to Art/minimap-arrow (white, tinted per marker, the SVG's outline).
    private static Sprite Drop => Paintbox.Art("minimap-arrow");
    private const float DropPivotY = 0.3826f;                              // the drop's round middle: where the player is
    private const float DropTall = 21.3f / 14f, DropWide = 16.3f / 14f;   // its round part is `size` across

    // A marker on the map: the drop pointing the way the player faces, or, once they are out, a
    // filled circle with an ink edge and an X.
    private static RectTransform Marker(Transform window, string name, Color fill, float size)
    {
        Image dot = Paintbox.Image(window, name, Drop, fill);
        dot.preserveAspect = true;
        dot.raycastTarget = false;
        RectTransform rt = dot.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        Outline edge = dot.gameObject.AddComponent<Outline>();
        edge.effectColor = Paintbox.Ink;
        edge.effectDistance = new Vector2(2f, -2f);
        Image glyph = Paintbox.IconImage(rt, "X", size * 0.66f, Paintbox.Ink);
        glyph.name = "Glyph";
        glyph.raycastTarget = false;
        rt.gameObject.AddComponent<MarkerSize>().Size = size;
        ShowAs(rt, false);
        rt.gameObject.SetActive(false);
        return rt;
    }

    private sealed class MarkerSize : MonoBehaviour { public float Size; public int Shown = -1; }

    // The drop while in (a circle if the picture is missing), the circle with its X once out.
    private static void ShowAs(RectTransform marker, bool down)
    {
        MarkerSize ms = marker.GetComponent<MarkerSize>();
        int state = down ? 1 : 0;
        if (ms.Shown == state) return;
        ms.Shown = state;

        Image dot = marker.GetComponent<Image>();
        bool drop = !down && Drop != null;
        dot.sprite = drop ? Drop : UiArt.Circle;
        marker.GetComponent<Outline>().enabled = !drop;          // the drop has its own outline
        marker.Find("Glyph").gameObject.SetActive(down);
        marker.pivot = new Vector2(0.5f, drop ? DropPivotY : 0.5f);
        marker.sizeDelta = drop ? new Vector2(ms.Size * DropWide, ms.Size * DropTall) : new Vector2(ms.Size, ms.Size);
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
        // Shown in the free camera too (2026-11-25), with the camera on it in blue.
        bool free = cam != null && cam.FreeLook;
        bool show = hudShown;
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

        // The free camera, where it is and the way it looks.
        Transform view = free && Camera.main != null ? Camera.main.transform : null;
        ShowCamera(smallCam, smallMap.rectTransform, view);
        ShowCamera(largeCam, largeMap.rectTransform, largeRoot.activeSelf ? view : null);

        // Everyone else.
        CollectOthers(body);
        DrawOthers(smallMarks, smallArrow.parent, smallMap.rectTransform, 22f);
        if (largeRoot.activeSelf) DrawOthers(largeMarks, largeArrow.parent, largeMap.rectTransform, 48f);
        smallArrow.SetAsLastSibling();   // the player's own arrow over the others
        largeArrow.SetAsLastSibling();
    }

    private void ShowCamera(RectTransform marker, RectTransform map, Transform view)
    {
        bool on = view != null;
        if (marker.gameObject.activeSelf != on) marker.gameObject.SetActive(on);
        if (!on) return;
        PlaceAt(marker, map, view.position, view.forward);
        marker.SetAsLastSibling();
    }

    // ================= The other players =================

    private struct Other
    {
        public Vector3 Position, Forward;
        public bool Seeker, Out;
    }

    private readonly List<Other> others = new List<Other>();

    // Who is where: online, the network's bodies; in Single Play, the bots.
    private void CollectOthers(Transform me)
    {
        others.Clear();
        bool iSeek = gmm != null && gmm.PlayerIsSeeker;

        if (OnlineRound.Active)
        {
            MecchaNetClient net = MecchaNetClient.Instance;
            byte local = net != null ? net.LocalSlot : (byte)0xFF;
            for (int slot = 0; slot < 16; slot++)
            {
                if (slot == local) continue;
                NetEntityView v = NetEntityRegistry.Get((byte)slot);
                if (v == null || !v.gameObject.activeInHierarchy || (!v.HasPosition && !v.IsHosted)) continue;
                Add(v.transform, v.Role == Role.Hunter, v.Eliminated, iSeek);
            }
            return;
        }

        // Single Play: the seeker bots and the hider bots of this round.
        foreach (Transform t in BotBodies(typeof(BotSeekController))) if (t != me) Add(t, true, IsDown(t), iSeek);
        foreach (Transform t in BotBodies(typeof(BotHideController))) if (t != me) Add(t, false, IsDown(t), iSeek);
    }

    private void Add(Transform body, bool seeker, bool down, bool iSeek)
    {
        if (body == null || !body.gameObject.activeInHierarchy) return;
        // A seeker never sees where a hider still hiding is.
        if (iSeek && !seeker && !down) return;
        // A body that is out lies where its ragdoll took it: the mesh moved, not the body's transform.
        Vector3 at = down ? OnlineRound.DrawnCentre(body) : body.position;
        others.Add(new Other { Position = at, Forward = body.forward, Seeker = seeker, Out = down });
    }

    private static bool IsDown(Transform t) => t.GetComponent<Ragdoll>() != null;

    private static readonly Dictionary<System.Type, FieldInfo> botLists = new Dictionary<System.Type, FieldInfo>();

    // The bots a Single Play controller runs (its private list of bodies).
    private static IEnumerable<Transform> BotBodies(System.Type controller)
    {
        Object c = FindAnyObjectByType(controller);
        if (c == null) yield break;
        if (!botLists.TryGetValue(controller, out FieldInfo list))
            botLists[controller] = list = controller.GetField("bots", BindingFlags.Instance | BindingFlags.NonPublic);
        if (list?.GetValue(c) is not IEnumerable bots) yield break;
        foreach (object b in bots)
        {
            FieldInfo tf = b?.GetType().GetField("tf");
            if (tf?.GetValue(b) is Transform t && t != null) yield return t;
        }
    }

    private void DrawOthers(List<RectTransform> pool, Transform window, RectTransform map, float size)
    {
        bool iSeek = gmm != null && gmm.PlayerIsSeeker;
        while (pool.Count < others.Count) pool.Add(Marker(window, "Player", Teammate, size));
        for (int i = 0; i < pool.Count; i++)
        {
            RectTransform m = pool[i];
            bool on = i < others.Count;
            if (m.gameObject.activeSelf != on) m.gameObject.SetActive(on);
            if (!on) continue;

            Other o = others[i];
            Color colour = o.Seeker == iSeek ? Teammate : Enemy;
            Image dot = m.GetComponent<Image>();
            if (dot.color != colour) dot.color = colour;
            ShowAs(m, o.Out);
            PlaceAt(m, map, o.Position, o.Out ? Vector3.forward : o.Forward);
        }
    }

    // The arrow where the body is on the picture, turned the way it faces (north is up).
    private void Place(RectTransform arrow, RectTransform map, Transform body) => PlaceAt(arrow, map, body.position, body.forward);

    private void PlaceAt(RectTransform arrow, RectTransform map, Vector3 p, Vector3 f)
    {
        float u = (p.x - centre.x) / (2f * halfHeight * aspect);
        float v = (p.z - centre.y) / (2f * halfHeight);
        Vector2 size = map.rect.size;
        Vector2 at = new Vector2(Mathf.Clamp(u, -0.5f, 0.5f) * size.x, Mathf.Clamp(v, -0.5f, 0.5f) * size.y);
        if ((arrow.anchoredPosition - at).sqrMagnitude > 0.01f) arrow.anchoredPosition = at;

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
