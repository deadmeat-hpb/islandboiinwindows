using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace DynamicIsland;

/// <summary>
/// The island's typeface: Inter (bundled in Fonts\, SIL Open Font License), one static file per weight.
/// Characters Inter lacks (Chinese titles, weekdays) fall back through DirectWrite's system fallback for the user's locale
/// (Microsoft JhengHei UI on zh-TW, Microsoft YaHei UI on zh-CN).
/// </summary>
static class Fonts
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fonts");

    /// <summary>A CanvasTextFormat family string for Inter at <paramref name="weight"/>; Segoe UI if the files are missing.</summary>
    public static string Inter(FontWeight weight)
    {
        string file = weight.Weight switch { < 450 => "Regular", < 550 => "Medium", < 650 => "SemiBold", _ => "Bold" };
        string path = Path.Combine(Dir, $"Inter-{file}.ttf");
        return File.Exists(path) ? $"{new Uri(path).AbsoluteUri}#Inter" : "Segoe UI Variable Text";
    }

    /// <summary>Draws text with tabular (fixed-width) digits, so a ticking clock doesn't jitter.</summary>
    public static void DrawTabular(CanvasDrawingSession ds, string text, Rect box, Color color, CanvasTextFormat format)
    {
        using var layout = new CanvasTextLayout(ds, text, format, (float)box.Width, (float)box.Height);
        using var typography = new CanvasTypography();
        typography.AddFeature(CanvasTypographyFeatureName.TabularFigures, 1);
        layout.SetTypography(0, text.Length, typography);
        ds.DrawTextLayout(layout, (float)box.X, (float)box.Y, color);
    }
}
