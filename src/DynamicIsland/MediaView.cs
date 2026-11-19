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
        float full = (float)r.Height - 10 * s, size = full * (0.8f + 0.2f * ArtIn), inset = (full - size) / 2;
        Slide(m, 10 * s, a * ArtIn, (dx, al, art, _, _) => DrawArt(ds, art, new Rect(r.X + 7 * s + inset + dx, r.Y + 5 * s + inset, size, size), 5 * s, art == m.Art ? al * m.ArtFadeT : al));

        float cy = (float)(r.Y + r.Height / 2), x0 = (float)r.Right - 14 * s - 4 * 6 * s;
        for (int i = 0; i < 4; i++)
        {
            float h = m.Playing ? (0.3f + 0.7f * MathF.Abs(MathF.Sin((float)time * (5 + i * 1.7f) + i))) * 14 * s : 3 * s;
            ds.FillRoundedRectangle(x0 + i * 6 * s, cy - h / 2, 3 * s, h, 1.5f * s, 1.5f * s, White(0.85f * a));
        }
    }

    /// <summary>Clock pill/card: big bold time on the left, the date small and grey on the right.</summary>
    public static void DrawClock(CanvasDrawingSession ds, Rect r, float s, float a, float timeSize)
    {
        var now = DateTime.Now;
        float pad = MathF.Max(14, timeSize * 0.6f) * s;
        using var time = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.SemiBold), FontSize = timeSize * s, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var date = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.Medium), FontSize = MathF.Max(12, timeSize * 0.4f) * s, FontWeight = FontWeights.Medium,
            HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap,
        };
        var box = new Rect(r.X + pad, r.Y, Math.Max(0, r.Width - 2 * pad), r.Height);
        Fonts.DrawTabular(ds, now.ToString("HH:mm"), box, White(a), time);
        ds.DrawText(now.ToString("ddd M/d", System.Globalization.CultureInfo.CurrentCulture), box, White(0.55f * a), date);
    }

    /// <summary>Expanded card: art, title, artist, progress bar and transport buttons.</summary>
    public static void DrawExpanded(CanvasDrawingSession ds, Rect r, float s, MediaSource m, double time, float a)
    {
        float x = (float)r.X, y = (float)r.Y, w = (float)r.Width;
        using var title = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.SemiBold), FontSize = 17 * s, FontWeight = FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };
        using var sub = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.Normal), FontSize = 14 * s,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };

        if (!m.HasSession)
        {
            DrawClock(ds, r, s, a, 40); // nothing playing: the card is a clock
            return;
        }

        // Title + artist take the left half when there's a lyric; the synced line sits in the right half, beside them.
        // Lyrics arrive: the title block narrows (and starts scrolling if it no longer fits) while the lyric list slides in
        // from below, as one motion; reversed when they go away.
        bool hasLyrics = m.Lyrics.Count > 0;
        if (hasLyrics != _hadLyrics) { (_hadLyrics, _lyricsAt) = (hasLyrics, time); }
        float lp = EaseOut((float)Math.Clamp((time - _lyricsAt) / 0.4, 0, 1)), L = hasLyrics ? lp : 1 - lp;
        float textW = w - 118 * s, tw = textW * (1 - 0.5f * L);
        TitleScrolls = false;
        Slide(m, 28 * s, a, (dx, al, art, t, ar) =>
        {
            var ar0 = ArtRect(r, s);
            DrawArt(ds, art, new Rect(ar0.X + dx, ar0.Y, ar0.Width, ar0.Height), 12 * s, art == m.Art ? al * m.ArtFadeT : al);
            Marquee(ds, t, new Rect(x + 98 * s + dx, y + 28 * s, tw, 24 * s), White(al), title, time, s);
            ds.DrawText(ar, new Rect(x + 98 * s + dx, y + 54 * s, tw, 20 * s), White(0.65f * al), sub);
        });
        if (L > 0.01f && (hasLyrics || _lastLyrics.Count > 0))
        {
            if (hasLyrics) _lastLyrics = m.Lyrics; // keep drawing the old list while it slides out
            var box = new Rect(x + 98 * s + textW * 0.5f + 12 * s, y + 14 * s + (1 - L) * 18 * s, textW * 0.5f - 12 * s, 60 * s);
            DrawLyricList(ds, box, s, _lastLyrics, hasLyrics ? m.LyricIndex : _lastIndex, time, a * L);
        }

        var bar = ProgressBar(r, s);
        float px = (float)bar.X, pw = (float)bar.Width, py = (float)bar.Y, f = Scrub ?? (float)m.Progress;
        ds.FillRoundedRectangle(px, py, pw, 4 * s, 2 * s, 2 * s, White(0.25f * a));
        ds.FillRoundedRectangle(px, py, pw * f, 4 * s, 2 * s, 2 * s, White(0.9f * a));
        if (Scrub is not null) ds.FillCircle(px + pw * f, py + 2 * s, 6 * s, White(a)); // thumb while scrubbing

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

    /// <summary>
    /// Song change: the previous song slides left and fades out while the new one slides in from the right (ease-out).
    /// <paramref name="draw"/> gets (x offset, alpha, art, title, artist).
    /// </summary>
    static void Slide(MediaSource m, float distance, float a, Action<float, float, CanvasBitmap?, string, string> draw)
    {
        float t = m.TransitionT, e = 1 - MathF.Pow(1 - t, 3);
        if (t < 1 && m.PrevTitle.Length > 0) draw(-e * distance, (1 - e) * a, m.PrevArt, m.PrevTitle, m.PrevArtist);
        draw((1 - e) * distance, e * a, m.Art, m.Title, m.Artist);
    }

    /// <summary>True while the expanded title is too long and scrolling, so the window keeps redrawing.</summary>
    public static bool TitleScrolls;

    /// <summary>Text that fits is drawn as is; longer text pauses 2 s, then scrolls left and loops.</summary>
    static void Marquee(CanvasDrawingSession ds, string text, Rect box, Color c, CanvasTextFormat f, double time, float s)
    {
        using var layout = new CanvasTextLayout(ds, text, f, 100000, (float)box.Height);
        float textW = (float)layout.LayoutBounds.Width;
        if (textW <= box.Width) { ds.DrawTextLayout(layout, (float)box.X, (float)box.Y, c); return; }
        TitleScrolls = true;
        float gap = 48 * s, speed = 30 * s, loop = textW + gap;
        float offset = (float)Math.Max(0, time % (2 + loop / speed) - 2) * speed;
        using (ds.CreateLayer(1, box))
        {
            ds.DrawTextLayout(layout, (float)box.X - offset, (float)box.Y, c);
            ds.DrawTextLayout(layout, (float)box.X - offset + loop, (float)box.Y, c);
        }
    }

    static bool _hadLyrics;
    static double _lyricsAt = -10, _scrollAt = -10;
    static int _lastIndex = -1, _scrollFrom = -1;
    static IReadOnlyList<(TimeSpan At, string Text)> _lastLyrics = [];

    static float EaseOut(float x) => 1 - MathF.Pow(1 - x, 3);

    /// <summary>True while the lyric list is sliding or scrolling, so the card keeps redrawing.</summary>
    public static bool LyricsAnimating(double time) => time - _lyricsAt < 0.45 || time - _scrollAt < 0.35;

    /// <summary>
    /// Apple Music-style lyric list: the current line bright in the middle, the previous and next lines faded above and below.
    /// A new line scrolls the list up over ~0.3 s; a jump (seek, new song) moves straight there.
    /// </summary>
    static void DrawLyricList(CanvasDrawingSession ds, Rect box, float s, IReadOnlyList<(TimeSpan At, string Text)> lines, int index, double time, float a)
    {
        if (index != _lastIndex)
        {
            (_scrollFrom, _scrollAt) = (index == _lastIndex + 1 ? _lastIndex : index, time); // only a step to the next line animates
            _lastIndex = index;
        }
        float k = EaseOut((float)Math.Clamp((time - _scrollAt) / 0.3, 0, 1));
        float pos = _scrollFrom + (index - _scrollFrom) * k; // fractional line in the middle

        using var format = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.SemiBold), FontSize = 13 * s, FontWeight = FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        float lh = 20 * s, mid = (float)(box.Y + box.Height / 2);
        using (ds.CreateLayer(1f, box))
            for (int i = (int)MathF.Floor(pos) - 2; i <= (int)MathF.Ceiling(pos) + 2; i++)
            {
                if (i < 0 || i >= lines.Count) continue;
                float d = i - pos, dist = MathF.Abs(d);
                float alpha = 1 - 0.65f * MathF.Min(dist, 1);                 // neighbours faded
                alpha *= Math.Clamp((1.9f - dist) / 0.6f, 0, 1);               // soft fade toward the top and bottom edges
                if (alpha <= 0.01f) continue;
                string text = lines[i].Text.Length > 0 ? lines[i].Text : "♪";
                ds.DrawText(text, new Rect(box.X, mid + d * lh - lh / 2, box.Width, lh), White(alpha * a), format);
            }
    }

    /// <summary>Where the progress bar is being dragged to (0..1), or null when it isn't.</summary>
    public static float? Scrub;

    /// <summary>0..1 fade/scale-in of the pill's album art after the minimize flow lands; 1 otherwise.</summary>
    public static float ArtIn = 1;

    /// <summary>The visible progress bar in the expanded card: beside the art, bottom-aligned with it.</summary>
    public static Rect ProgressBar(Rect r, float s) => new(r.X + 98 * s, r.Y + 80 * s, r.Width - 118 * s, 4 * s);

    /// <summary>The album art in the expanded card.</summary>
    public static Rect ArtRect(Rect r, float s) => new(r.X + 20 * s, r.Y + 20 * s, 64 * s, 64 * s);

    /// <summary>0..1 along the progress bar if <paramref name="p"/> is on it (with a few px of slack), else null.</summary>
    public static float? HitProgress(Rect r, float s, Vector2 p)
    {
        var bar = ProgressBar(r, s);
        if (p.X < bar.X - 6 * s || p.X > bar.Right + 6 * s || p.Y < bar.Y - 8 * s || p.Y > bar.Bottom + 8 * s) return null;
        return (float)Math.Clamp((p.X - bar.X) / bar.Width, 0, 1);
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
        float cx = (float)(r.X + r.Width / 2), cy = (float)r.Y + 118 * s;
        return [new(cx - 64 * s, cy), new(cx, cy), new(cx + 64 * s, cy)];
    }

    public static void DrawArt(CanvasDrawingSession ds, CanvasBitmap? art, Rect dest, float radius, float a)
    {
        if (art is null) { ds.FillRoundedRectangle(dest, radius, radius, White(0.15f * a)); return; }
        using var clip = CanvasGeometry.CreateRoundedRectangle(ds, dest, radius, radius);
        using (ds.CreateLayer(a, clip)) ds.DrawImage(art, dest, art.Bounds);
    }
}
