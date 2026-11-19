using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace DynamicIsland;

/// <summary>
/// Draws the Liquid Glass shape. All coordinates are physical pixels.
/// Refraction is faked with concentric "lens" rings: each ring shows the
/// background pulled in from a little further outside, strongest at the rim.
/// </summary>
// shortcut: ring-based refraction, no chromatic fringe; move to a custom pixel shader if it looks banded on big cards.
static class Glass
{
    static CanvasRenderTarget? _blurred;

    static Color White(int a) => Gray(a, 255);
    static Color Gray(int a, byte v) => ColorHelper.FromArgb((byte)Math.Clamp(a, 0, 255), v, v, v);

    public static void Draw(CanvasDrawingSession ds, ICanvasImage? background, float originX, float cx, float top, float w, float h, float s, float expand, Color glow, float glowStrength, float sag, Action<Rect>? content)
    {
        ds.Transform = Matrix3x2.CreateTranslation(-originX, 0) * Matrix3x2.CreateScale(1f / s); // draw in screen pixels; originX = where the canvas sits
        var rect = new Rect(cx - w / 2, top, w, h);
        float r = MathF.Min(h / 2, 30 * s);
        float attach = Math.Clamp(1 - top / (4 * s), 0, 1); // resting at the screen edge: melt into it with curved shoulders
        var outline = attach > 0 ? Attached(ds, rect, r, 12 * s * attach) : RoundRect(ds, rect, r, 0);
        if (sag > 0.5f) { var dripping = Drip(ds, outline, rect, sag); outline.Dispose(); outline = dripping; }
        using var shape = outline;

        if (expand > 0) DrawShadow(ds, shape, s, expand);
        if (glowStrength > 0) DrawGlow(ds, shape, rect, s, glow, glowStrength);

        if (background is not null) DrawRefraction(ds, background, shape, rect, r, s, expand);
        else ds.FillGeometry(shape, Gray(200, 22));

        ds.FillGeometry(shape, Gray((int)(16 + 70 * expand), 12)); // light tint, darker when expanded for legible text
        if (glowStrength > 0) // light from the album art spilling into the glass
        {
            using var light = new CanvasRadialGradientBrush(ds, WithAlpha(glow, 0.10f * glowStrength), WithAlpha(glow, 0))
            {
                Center = new Vector2((float)rect.X + 20 * s, (float)rect.Y + (float)rect.Height / 2),
                RadiusX = (float)rect.Width * 0.7f, RadiusY = (float)rect.Height * 1.2f,
            };
            ds.FillGeometry(shape, light);
        }
        if (content is not null)
            using (ds.CreateLayer(1f, shape)) content(rect);
        DrawRim(ds, shape, rect, r, s, inner: attach == 0);
    }

    static Color WithAlpha(Color c, float a) => ColorHelper.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>Soft halo around the island in the album's colour.</summary>
    static void DrawGlow(CanvasDrawingSession ds, CanvasGeometry shape, Rect rect, float s, Color glow, float strength)
    {
        using var list = new CanvasCommandList(ds);
        using (var c = list.CreateDrawingSession()) c.FillGeometry(shape, Colors.White);
        using var halo = new ShadowEffect { Source = list, BlurAmount = 16 * s, ShadowColor = WithAlpha(glow, 0.16f * strength) };
        ds.DrawImage(halo);
    }

    static void DrawShadow(CanvasDrawingSession ds, CanvasGeometry shape, float s, float expand)
    {
        using var list = new CanvasCommandList(ds);
        using (var c = list.CreateDrawingSession()) c.FillGeometry(shape, Colors.Black);
        using var shadow = new ShadowEffect { Source = list, BlurAmount = 12 * s, ShadowColor = Gray((int)(110 * expand), 0) };
        ds.DrawImage(shadow, 0, 6 * s);
    }

    static void DrawRefraction(CanvasDrawingSession ds, ICanvasImage background, CanvasGeometry shape, Rect rect, float r, float s, float expand)
    {
        // Blur once into a cached bitmap; the lens rings below all sample from it instead of re-running the blur.
        float pad = 40 * s;
        var src = new Rect(rect.X - pad, rect.Y - pad, rect.Width + 2 * pad, rect.Height + 2 * pad);
        if (_blurred is null || _blurred.Device != ds.Device || _blurred.Size.Width < src.Width || _blurred.Size.Height < src.Height)
        {
            _blurred?.Dispose();
            _blurred = new CanvasRenderTarget(ds.Device, (float)src.Width + 256, (float)src.Height + 128, 96); // headroom so morphing rarely reallocates
        }
        using (var blur = new GaussianBlurEffect { Source = background, BlurAmount = (1.2f + 6 * expand) * s, BorderMode = EffectBorderMode.Hard })
        using (var sat = new SaturationEffect { Source = blur, Saturation = 1.4f })
        using (var b = _blurred.CreateDrawingSession())
        {
            b.Clear(Colors.Transparent);
            b.DrawImage(sat, 0, 0, src);
        }
        var glass = _blurred;
        var origin = new Vector2((float)src.X, (float)src.Y);

        var c = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
        float hw = (float)rect.Width / 2, hh = (float)rect.Height / 2;

        // Rim rings bend more strongly toward the edge. Painted outside-in, each clipped to a smaller rounded rect,
        // so the next one covers the inside; cheaper than clipping to exact ring shapes (no geometry combine per frame).
        float band = MathF.Min(12 * s, MathF.Min(hw, hh) * 0.7f);
        const int rings = 3; // 5 cost ~4 ms more per frame on integrated GPUs
        for (int i = 0; i < rings; i++)
        {
            float t = 1 - (i + 0.5f) / rings; // 1 at the edge, 0 inside
            using var clip = i == 0 ? null : RoundRect(ds, rect, r, band * i / rings);
            DrawLens(ds, glass, origin, clip ?? shape, c, hw, hh, band * 1.6f * t * t); // outer ring fills the whole shape, shoulders included
        }

        // Body: a touch of magnification, like thick glass.
        using (var body = RoundRect(ds, rect, r, band))
            DrawLens(ds, glass, origin, body, c, hw, hh, -0.04f * MathF.Min(hw, hh));
    }

    /// <summary>Draws <paramref name="src"/> inside <paramref name="clip"/>, sampling from <paramref name="d"/> px further out at the edge.</summary>
    static void DrawLens(CanvasDrawingSession ds, ICanvasImage src, Vector2 origin, CanvasGeometry clip, Vector2 c, float hw, float hh, float d)
    {
        float mx = hw / MathF.Max(hw + d, 0.5f), my = hh / MathF.Max(hh + d, 0.5f);
        using var lens = new Transform2DEffect
        {
            Source = src,
            TransformMatrix = Matrix3x2.CreateTranslation(origin - c) * Matrix3x2.CreateScale(mx, my) * Matrix3x2.CreateTranslation(c),
            InterpolationMode = CanvasImageInterpolation.Linear,
        };
        using (ds.CreateLayer(1f, clip)) ds.DrawImage(lens);
    }

    static void DrawRim(CanvasDrawingSession ds, CanvasGeometry shape, Rect rect, float r, float s, bool inner)
    {
        using var brush = new CanvasLinearGradientBrush(ds,
        [
            new CanvasGradientStop { Position = 0f, Color = White(235) },
            new CanvasGradientStop { Position = 0.3f, Color = White(50) },
            new CanvasGradientStop { Position = 0.7f, Color = White(30) },
            new CanvasGradientStop { Position = 1f, Color = White(170) },
        ])
        {
            StartPoint = new Vector2((float)rect.Left, (float)rect.Top),
            EndPoint = new Vector2((float)rect.Right, (float)rect.Bottom),
        };
        if (!inner) // melted into the top bar: no line along the screen edge or the shoulders; the rim fades in toward the bottom
        {
            using var fade = new CanvasLinearGradientBrush(ds, White(0), White(150))
            {
                StartPoint = new Vector2((float)rect.Left, (float)rect.Top + 2 * s),
                EndPoint = new Vector2((float)rect.Left, (float)rect.Bottom),
            };
            ds.DrawGeometry(shape, fade, 1.1f * s);
            return;
        }
        ds.DrawGeometry(shape, brush, 1.1f * s);
        using var line = RoundRect(ds, rect, r, 1.5f * s);
        ds.DrawGeometry(line, White(28), 1f * s);
    }

    /// <summary>
    /// The island hanging from the top edge: flat top, rounded bottom, and concave shoulders <paramref name="f"/> wide
    /// that curve out into the screen edge, so it looks poured out of the top bar instead of floating under it.
    /// </summary>
    static CanvasGeometry Attached(ICanvasResourceCreator rc, Rect rect, float r, float f)
    {
        float left = (float)rect.Left, right = (float)rect.Right, top = (float)rect.Top;
        float fv = MathF.Min(f, MathF.Max((float)rect.Height - r, 1)); // same shoulders on the thin bar, the pill and the card
        using var body = CanvasGeometry.CreateRoundedRectangle(rc, left, top - r, right - left, (float)rect.Height + r, r, r); // top corners sit above the screen edge, so it's square there
        using var band = CanvasGeometry.CreateRectangle(rc, left - f, top - 1, right - left + 2 * f, fv + 1);
        using var l = CanvasGeometry.CreateEllipse(rc, left - f, top + fv, f, fv); // carves the concave shoulders
        using var rr = CanvasGeometry.CreateEllipse(rc, right + f, top + fv, f, fv);
        using var a = band.CombineWith(l, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using var b = a.CombineWith(rr, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        return body.CombineWith(b, Matrix3x2.Identity, CanvasGeometryCombine.Union);
    }

    /// <summary>A soft bulge hanging from the bottom middle, like liquid running down while the island opens.</summary>
    static CanvasGeometry Drip(ICanvasResourceCreator rc, CanvasGeometry shape, Rect rect, float sag)
    {
        float rx = (float)rect.Width * 0.38f, ry = MathF.Min(sag * 3, (float)rect.Height); // centred inside the body so it leaves the bottom edge at a shallow angle
        using var drop = CanvasGeometry.CreateEllipse(rc, (float)(rect.X + rect.Width / 2), (float)rect.Bottom + sag - ry, rx, ry);
        return shape.CombineWith(drop, Matrix3x2.Identity, CanvasGeometryCombine.Union);
    }

    static CanvasGeometry RoundRect(ICanvasResourceCreator rc, Rect rect, float r, float inset)
    {
        var inner = new Rect(rect.X + inset, rect.Y + inset, Math.Max(0, rect.Width - 2 * inset), Math.Max(0, rect.Height - 2 * inset));
        float ri = MathF.Max(0, r - inset);
        return CanvasGeometry.CreateRoundedRectangle(rc, inner, ri, ri);
    }
}
