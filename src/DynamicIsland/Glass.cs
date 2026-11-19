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

    public static void Draw(CanvasDrawingSession ds, ICanvasImage? background, float cx, float top, float w, float h, float s, float expand, Action<Rect>? content)
    {
        ds.Transform = Matrix3x2.CreateScale(1f / s); // draw in physical pixels
        var rect = new Rect(cx - w / 2, top, w, h);
        float r = MathF.Min(h / 2, 30 * s);
        using var shape = RoundRect(ds, rect, r, 0);

        if (expand > 0) DrawShadow(ds, shape, s, expand);

        if (background is not null) DrawRefraction(ds, background, rect, r, s, expand);
        else ds.FillGeometry(shape, Gray(200, 22));

        ds.FillGeometry(shape, Gray((int)(16 + 70 * expand), 12)); // light tint, darker when expanded for legible text
        if (content is not null)
            using (ds.CreateLayer(1f, shape)) content(rect);
        DrawRim(ds, shape, rect, r, s);
    }

    static void DrawShadow(CanvasDrawingSession ds, CanvasGeometry shape, float s, float expand)
    {
        using var list = new CanvasCommandList(ds);
        using (var c = list.CreateDrawingSession()) c.FillGeometry(shape, Colors.Black);
        using var shadow = new ShadowEffect { Source = list, BlurAmount = 12 * s, ShadowColor = Gray((int)(110 * expand), 0) };
        ds.DrawImage(shadow, 0, 6 * s);
    }

    static void DrawRefraction(CanvasDrawingSession ds, ICanvasImage background, Rect rect, float r, float s, float expand)
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

        // Body: a touch of magnification, like thick glass.
        using (var body = RoundRect(ds, rect, r, 0))
            DrawLens(ds, glass, origin, body, c, hw, hh, -0.04f * MathF.Min(hw, hh));

        // Rim: rings that bend more strongly toward the edge.
        float band = MathF.Min(12 * s, MathF.Min(hw, hh) * 0.7f);
        const int rings = 5;
        for (int i = 0; i < rings; i++)
        {
            float t = 1 - (i + 0.5f) / rings; // 1 at the edge, 0 inside
            using var outer = RoundRect(ds, rect, r, band * i / rings);
            using var inner = RoundRect(ds, rect, r, band * (i + 1) / rings);
            using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            DrawLens(ds, glass, origin, ring, c, hw, hh, band * 1.6f * t * t);
        }
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

    static void DrawRim(CanvasDrawingSession ds, CanvasGeometry shape, Rect rect, float r, float s)
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
        ds.DrawGeometry(shape, brush, 1.1f * s);
        using var inner = RoundRect(ds, rect, r, 1.5f * s);
        ds.DrawGeometry(inner, White(28), 1f * s);
    }

    static CanvasGeometry RoundRect(ICanvasResourceCreator rc, Rect rect, float r, float inset)
    {
        var inner = new Rect(rect.X + inset, rect.Y + inset, Math.Max(0, rect.Width - 2 * inset), Math.Max(0, rect.Height - 2 * inset));
        float ri = MathF.Max(0, r - inset);
        return CanvasGeometry.CreateRoundedRectangle(rc, inner, ri, ri);
    }
}
