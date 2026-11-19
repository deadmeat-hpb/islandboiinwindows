using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;

namespace DynamicIsland;

/// <summary>Draws the media content inside the island. Coordinates are physical pixels.</summary>
static class MediaView
{
    const string IconFont = "Segoe Fluent Icons";
    static readonly string[] Glyphs = ["", "", ""]; // previous, pause, next ("" = play)

    static Color White(float a) => ColorHelper.FromArgb((byte)Math.Clamp(a * 255, 0, 255), 255, 255, 255);

    /// <summary>Compact pill: album art on the left, a little waveform on the right.</summary>
    public static void DrawCompact(CanvasDrawingSession ds, Rect r, float s, MediaSource m, double time, float a)
    {
        float size = (float)r.Height - 10 * s;
        DrawArt(ds, m.Art, new Rect(r.X + 7 * s, r.Y + 5 * s, size, size), 5 * s, a);

        float cy = (float)(r.Y + r.Height / 2), x0 = (float)r.Right - 14 * s - 4 * 6 * s;
        for (int i = 0; i < 4; i++)
        {
            float h = m.Playing ? (0.3f + 0.7f * MathF.Abs(MathF.Sin((float)time * (5 + i * 1.7f) + i))) * 14 * s : 3 * s;
            ds.FillRoundedRectangle(x0 + i * 6 * s, cy - h / 2, 3 * s, h, 1.5f * s, 1.5f * s, White(0.85f * a));
        }
    }

    /// <summary>Expanded card: art, title, artist, progress bar and transport buttons.</summary>
    public static void DrawExpanded(CanvasDrawingSession ds, Rect r, float s, MediaSource m, float a)
    {
        float x = (float)r.X, y = (float)r.Y, w = (float)r.Width;
        using var title = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Display", FontSize = 17 * s, FontWeight = FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };
        using var sub = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text", FontSize = 14 * s,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };

        if (!m.HasSession)
        {
            ds.DrawText("Nothing playing", new Rect(x + 24 * s, y + 24 * s, w - 48 * s, 26 * s), White(a), title);
            ds.DrawText("Play something in any media app", new Rect(x + 24 * s, y + 52 * s, w - 48 * s, 22 * s), White(0.65f * a), sub);
            return;
        }

        DrawArt(ds, m.Art, new Rect(x + 20 * s, y + 20 * s, 64 * s, 64 * s), 12 * s, a);
        ds.DrawText(m.Title, new Rect(x + 98 * s, y + 28 * s, w - 118 * s, 24 * s), White(a), title);
        ds.DrawText(m.Artist, new Rect(x + 98 * s, y + 54 * s, w - 118 * s, 20 * s), White(0.65f * a), sub);
        ds.DrawText(m.CurrentLyric, new Rect(x + 98 * s, y + 76 * s, w - 118 * s, 20 * s), White(0.9f * a), sub); // synced lyric, just above the progress bar

        float px = x + 20 * s, pw = w - 40 * s, py = y + 100 * s;
        ds.FillRoundedRectangle(px, py, pw, 4 * s, 2 * s, 2 * s, White(0.25f * a));
        ds.FillRoundedRectangle(px, py, pw * (float)m.Progress, 4 * s, 2 * s, 2 * s, White(0.9f * a));

        using var icons = new CanvasTextFormat
        {
            FontFamily = IconFont, FontSize = 22 * s,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        var centers = ButtonCenters(r, s);
        for (int i = 0; i < 3; i++)
        {
            string glyph = i == 1 && !m.Playing ? "" : Glyphs[i];
            ds.DrawText(glyph, new Rect(centers[i].X - 20 * s, centers[i].Y - 20 * s, 40 * s, 40 * s), White(a), icons);
        }
    }

    /// <summary>0 = previous, 1 = play/pause, 2 = next, -1 = none.</summary>
    public static int HitButton(Rect r, float s, Vector2 p)
    {
        var centers = ButtonCenters(r, s);
        for (int i = 0; i < 3; i++)
            if (Vector2.Distance(centers[i], p) < 22 * s) return i;
        return -1;
    }

    static Vector2[] ButtonCenters(Rect r, float s)
    {
        float cx = (float)(r.X + r.Width / 2), cy = (float)r.Y + 138 * s;
        return [new(cx - 64 * s, cy), new(cx, cy), new(cx + 64 * s, cy)];
    }

    public static void DrawArt(CanvasDrawingSession ds, CanvasBitmap? art, Rect dest, float radius, float a)
    {
        if (art is null) { ds.FillRoundedRectangle(dest, radius, radius, White(0.15f * a)); return; }
        using var clip = CanvasGeometry.CreateRoundedRectangle(ds, dest, radius, radius);
        using (ds.CreateLayer(a, clip)) ds.DrawImage(art, dest, art.Bounds);
    }
}
