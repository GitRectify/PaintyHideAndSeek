using System;
using System.Collections;
using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// The picture of a shop item, as the server names it (MetaItem.Image, SDK 0.9.8):
///   * "builtin:key": art the game carries (Resources/Paintbox/Art, the real map thumbnails);
///   * a file name: served by the game server (http://host:ImagePort/shop-images/name), so an
///     operator adds a picture with the item, no game update needed;
///   * an http(s) URL: fetched as it is.
/// A picture that is fetched shows the item kind's own art until it arrives, and keeps it if the
/// fetch fails. Fetched pictures are kept for the session.
/// </summary>
public static class ShopArt
{
    private static readonly Dictionary<string, string> BuiltinArt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "coins", "coins" },
        { "weapon-0", "marker" }, { "weapon-1", "coralmarker" }, { "weapon-2", "marker" }, { "weapon-3", "coralmarker" },
        { "weapon-4", "marker" }, { "weapon-5", "coralmarker" }, { "weapon-6", "marker" },
        { "avatar-0", "avatar" }, { "avatar-1", "avatar2" },
        { "pose-3", "stand" }, { "pose-4", "crouch" }, { "pose-5", "prone" }, { "pose-6", "reach" }, { "pose-7", "reach" }, { "pose-8", "stand" },
        { "next-player", "turnworld" },
        { "map-1", "studio" }, { "map-2", "aerial" }, { "map-3", "greenhouse" }, { "map-4", "loft" }, { "map-5", "workshop" },
    };

    private static readonly Dictionary<string, Sprite> fetched = new Dictionary<string, Sprite>(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<Action<Sprite>>> waiting = new Dictionary<string, List<Action<Sprite>>>(StringComparer.Ordinal);
    private static Runner runner;

    /// <summary>
    /// The item's picture: <paramref name="done"/> gets one at once (the game's own art), and again
    /// when a fetched picture arrives.
    /// </summary>
    public static void Load(MetaItem item, Action<Sprite> done)
    {
        Sprite own = Own(item);
        done?.Invoke(own);

        string url = UrlOf(item.Image);
        if (url == null) return;
        Fetch(url, sprite => { if (sprite != null) done?.Invoke(sprite); });
    }

    /// <summary>The game's own picture for an item: its builtin key, else its kind's.</summary>
    public static Sprite Own(MetaItem item)
    {
        string image = item.Image ?? "";
        if (image.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
        {
            Sprite builtin = Builtin(image.Substring(8));
            if (builtin != null) return builtin;
        }
        return KindArt(item.Kind, (int)item.GameId);
    }

    /// <summary>A builtin picture by key: a map's real thumbnail, the game's art, or null.</summary>
    public static Sprite Builtin(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.StartsWith("map-", StringComparison.OrdinalIgnoreCase) && int.TryParse(key.Substring(4), out int map))
        {
            Sprite thumb = MapThumbnail(map);
            if (thumb != null) return thumb;
        }
        return Paintbox.Art(BuiltinArt.TryGetValue(key, out string art) ? art : key);
    }

    public static Sprite KindArt(ItemKind kind, int gameId)
    {
        switch (kind)
        {
            case ItemKind.Weapon: return Builtin("weapon-" + gameId) ?? Paintbox.Art("marker");
            case ItemKind.Ammo:
            case ItemKind.Bullets: return Paintbox.Art("pods");
            case ItemKind.Shoes: return Paintbox.Art("shoes");
            case ItemKind.Avatar: return Builtin("avatar-" + gameId) ?? Paintbox.Art("avatar");
            case ItemKind.Pose: return Builtin("pose-" + gameId) ?? Paintbox.Art("stand");
            case ItemKind.Map: return Builtin("map-" + gameId) ?? Paintbox.Art("studio");
            case ItemKind.Feature: return gameId == GameFeatures.NextPlayer ? Paintbox.Art("turnworld") : Paintbox.Art("paintworld");
            default: return Paintbox.Art("paintworld");
        }
    }

    private static Sprite MapThumbnail(int mapNumber)
    {
        MapSelectPopup picker = MapSelectPopup.Instance;
        return picker != null ? picker.ThumbnailAt(mapNumber - 1) : null;
    }

    /// <summary>Where a picture is fetched from, or null for builtin art and none.</summary>
    public static string UrlOf(string image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        image = image.Trim();
        if (image.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)) return null;
        if (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return image;

        // A file the game server serves next to its API.
        MecchaNetClient net = MecchaNetClient.Instance;
        int port = ShopInventory.ImagePort;
        if (net == null || string.IsNullOrEmpty(net.Host) || port <= 0) return null;
        string host = net.Host.Contains(":") && !net.Host.StartsWith("[") ? "[" + net.Host + "]" : net.Host;
        return $"http://{host}:{port}/shop-images/{Uri.EscapeDataString(image)}";
    }

    private static void Fetch(string url, Action<Sprite> done)
    {
        if (fetched.TryGetValue(url, out Sprite known))
        {
            done(known);
            return;
        }

        if (waiting.TryGetValue(url, out List<Action<Sprite>> list))
        {
            list.Add(done);
            return;
        }

        waiting[url] = new List<Action<Sprite>> { done };
        if (runner == null)
        {
            var go = new GameObject("ShopArt");
            UnityEngine.Object.DontDestroyOnLoad(go);
            runner = go.AddComponent<Runner>();
        }
        runner.StartCoroutine(Download(url));
    }

    private static IEnumerator Download(string url)
    {
        Sprite sprite = null;
        using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(url))
        {
            request.timeout = 10;
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                Texture2D texture = DownloadHandlerTexture.GetContent(request);
                if (texture != null && texture.width > 0 && texture.width <= 4096 && texture.height <= 4096)
                {
                    texture.wrapMode = TextureWrapMode.Clamp;
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                }
            }
            else
            {
                Debug.LogWarning($"[Shop] Picture {url} could not be fetched: {request.error}");
            }
        }

        fetched[url] = sprite;   // a failed one is not asked for again this session
        if (waiting.TryGetValue(url, out List<Action<Sprite>> list))
        {
            waiting.Remove(url);
            foreach (Action<Sprite> done in list) done(sprite);
        }
    }

    private sealed class Runner : MonoBehaviour { }
}
