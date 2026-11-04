using System.Collections.Generic;
using Meccha.Client;
using Meccha.Shared.Game;
using Meccha.Shared.Protocol;
using UnityEngine;
using UnityEngine.UI;

// Part of OnlineRound (OnlineRound.cs has the overview). Paint: the player's own strokes (sent, logged, replayed after a resume) and every other painter's canvas, rebuilt from the strokes the server relays.
public partial class OnlineRound
{
    // ================= Fields: paint =================

    // The player's own paint of the last moments, so what never reached the server can be sent
    // again after a resume, paced to the server's paint budget and ahead of anything newer.
    private struct PaintOp
    {
        public float At;
        public bool Reset;
        public Vector2 Uv;
        public int Brush;
        public Color Color;
        public int Mode;
    }

    private const float PaintLogSeconds = 20f;
    private const int PaintLogCap = 4000;
    private readonly List<PaintOp> paintLog = new List<PaintOp>();
    private readonly Queue<PaintOp> paintReplay = new Queue<PaintOp>();
    private float replayAllowance;

    // Each remote painter's canvas, rebuilt from the strokes the server relays.
    private sealed class RemoteCanvas
    {
        public Color32[] Pixels;
        public Texture2D Texture;
        public bool Dirty;
        public NetEntityView AppliedTo;
    }

    private readonly Dictionary<byte, RemoteCanvas> canvases = new Dictionary<byte, RemoteCanvas>();

    // ================= Entry points: paint =================

    /// <summary>A dab on the local player's canvas (ChameleonPaint), radius in canvas texels.</summary>
    public static void SendStroke(Vector2 uv, int brushPx, Color color, bool erase)
    {
        if (!Active || Net == null) return;
        _instance.SendPaint(new PaintOp { Uv = uv, Brush = brushPx, Color = erase ? Color.white : color, Mode = erase ? 2 : 0 });
    }

    public static void SendPaintReset()
    {
        if (!Active || Net == null) return;
        _instance.SendPaint(new PaintOp { Reset = true });
    }

    /// <summary>
    /// A one-tap fill has no stroke to send, so it goes as a reset followed by a grid of large
    /// dabs in the fill colour: the server's camouflage average and every other player's view of
    /// this body then match the filled canvas. About 80 dabs, well inside the paint budget.
    /// </summary>
    public static void SendFill(Color color)
    {
        if (!Active || Net == null) return;

        OnlineRound self = _instance;
        self.SendPaint(new PaintOp { Reset = true });
        int size = GameTuning.PaintTextureSize;
        int step = Mathf.Max(8, self.fillSpacing);
        int brush = Mathf.Clamp(self.fillBrushPx, 2, 126);

        for (int y = step / 2; y < size; y += step)
        {
            for (int x = step / 2; x < size; x += step)
            {
                self.SendPaint(new PaintOp { Uv = new Vector2((float)x / size, (float)y / size), Brush = brush, Color = color });
            }
        }
    }

    // ---- The player's own paint ----

    private void SendPaint(PaintOp op)
    {
        op.At = Time.unscaledTime;
        paintLog.Add(op);
        if (paintLog.Count > PaintLogCap) paintLog.RemoveRange(0, paintLog.Count - PaintLogCap);

        if (op.Reset)
        {
            paintReplay.Clear();   // nothing painted before a reset matters any more
            Net.SendPaintReset();
            return;
        }

        // Behind a replay still going out, so the server stamps in the order the player painted.
        if (paintReplay.Count > 0)
        {
            paintReplay.Enqueue(op);
            return;
        }

        Net.QueuePaintStroke(op.Uv, op.Brush, op.Color, op.Mode);
    }

    // What was painted from shortly before the drop was noticed (the link was already dead for up
    // to DisconnectTimeoutMs) until now. A dab that did arrive is stamped twice, which with this
    // brush looks the same.
    private void QueuePaintReplay(float since)
    {
        paintReplay.Clear();

        int start = paintLog.FindIndex(op => op.At >= since);
        if (start < 0) return;

        int lastReset = paintLog.FindLastIndex(op => op.Reset);
        if (lastReset >= start)
        {
            Net.SendPaintReset();
            start = lastReset + 1;
        }

        for (int i = start; i < paintLog.Count; i++) paintReplay.Enqueue(paintLog[i]);
        replayAllowance = GameTuning.PaintStrokeBurst / 2f;
    }

    // Fed to the SDK at the server's paint rate: queued all at once, a long replay would overflow
    // the SDK's backlog, which drops the oldest strokes.
    private void PumpPaintReplay()
    {
        if (paintReplay.Count == 0 || !Net.IsConnected || !Net.InMatch) return;

        replayAllowance = Mathf.Min(GameTuning.PaintStrokeBurst / 2f,
                                    replayAllowance + Time.unscaledDeltaTime * GameTuning.MaxPaintStrokesPerSecond);

        while (paintReplay.Count > 0 && replayAllowance >= 1f)
        {
            PaintOp op = paintReplay.Dequeue();
            Net.QueuePaintStroke(op.Uv, op.Brush, op.Color, op.Mode);
            replayAllowance -= 1f;
        }
    }

    // Keeps the last PaintLogSeconds; while offline, everything since shortly before the drop.
    private void PrunePaintLog()
    {
        float cutoff = (offlineSince >= 0f ? offlineSince : Time.unscaledTime) - PaintLogSeconds;
        int drop = 0;
        while (drop < paintLog.Count && paintLog[drop].At < cutoff) drop++;
        if (drop > 0) paintLog.RemoveRange(0, drop);
    }

    // ================= Remote paint =================

    // Each remote painter's canvas is rebuilt here from the strokes the server relays, stamped with
    // the same soft round brush as ChameleonPaint.StampAtUV, and shown on their body
    // (NetEntityView.PaintTexture). The server only relays a hider's strokes to a seeker who has
    // seen that hider during the seek.

    private void HandleRemoteStroke(PaintStroke stroke)
    {
        RemoteCanvas canvas = CanvasFor(stroke.EntityId);
        int size = GameTuning.PaintTextureSize;

        int cx = Mathf.Clamp(stroke.U << 1, 0, size - 1);
        int cy = Mathf.Clamp(stroke.V << 1, 0, size - 1);
        int r = Mathf.Max(1, stroke.RadiusHalf << 1);

        Color col;
        if (stroke.Mode == 2) col = Color.white;
        else
        {
            Quantize.Rgb565ToColor(stroke.Color, out float cr, out float cg, out float cb);
            col = new Color(cr, cg, cb, 1f);
        }

        int x0 = Mathf.Max(0, cx - r), x1 = Mathf.Min(size - 1, cx + r);
        int y0 = Mathf.Max(0, cy - r), y1 = Mathf.Min(size - 1, cy + r);
        Color32[] buf = canvas.Pixels;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > r) continue;

                float a = Mathf.Clamp01((1f - d / r) * 1.7f);
                float inv = 1f - a;
                int idx = y * size + x;
                Color32 o = buf[idx];
                buf[idx] = new Color32((byte)(col.r * 255f * a + o.r * inv),
                                       (byte)(col.g * 255f * a + o.g * inv),
                                       (byte)(col.b * 255f * a + o.b * inv), 255);
            }
        }

        canvas.Dirty = true;
    }

    private void HandleRemoteReset(byte slot)
    {
        RemoteCanvas canvas = CanvasFor(slot);
        Color32 white = Color.white;
        for (int i = 0; i < canvas.Pixels.Length; i++) canvas.Pixels[i] = white;
        canvas.Dirty = true;
    }

    private RemoteCanvas CanvasFor(byte slot)
    {
        if (canvases.TryGetValue(slot, out RemoteCanvas canvas)) return canvas;

        int size = GameTuning.PaintTextureSize;
        canvas = new RemoteCanvas
        {
            Pixels = new Color32[size * size],
            Texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp },
        };

        Color32 white = Color.white;
        for (int i = 0; i < canvas.Pixels.Length; i++) canvas.Pixels[i] = white;
        canvas.Dirty = true;
        canvases[slot] = canvas;
        return canvas;
    }

    // Uploads at most once a frame per canvas, however many strokes arrived.
    private void ApplyCanvases()
    {
        foreach (KeyValuePair<byte, RemoteCanvas> pair in canvases)
        {
            RemoteCanvas canvas = pair.Value;

            if (canvas.Dirty)
            {
                canvas.Texture.SetPixels32(canvas.Pixels);
                canvas.Texture.Apply(false);
                canvas.Dirty = false;
            }

            NetEntityView view = NetEntityRegistry.Get(pair.Key);
            if (view != null && !view.IsLocal && canvas.AppliedTo != view)
            {
                view.PaintTexture = canvas.Texture;
                canvas.AppliedTo = view;
            }
        }
    }

    private void ClearCanvases()
    {
        foreach (RemoteCanvas canvas in canvases.Values)
            if (canvas.Texture != null) Destroy(canvas.Texture);
        canvases.Clear();
    }
}
