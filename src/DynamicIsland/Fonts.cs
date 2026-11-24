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
    static readonly Dictionary<string, CanvasTextFormat> Formats = [];

    /// <summary>
    /// The one shared format equal to <paramref name="format"/> (disposed when an equal one exists); never dispose the result.
    /// Win2D resolves a bundled-font URI through Windows.Storage each time a new format is first used: about 4 ms, and that
    /// blocking call pumps window messages. Formats made per frame paid it every frame, and a mouse event handled inside the
    /// pump, mid-render, made WinUI end the app (the 0xc000027b crashes). A shared format pays it once.
    /// </summary>
    public static CanvasTextFormat Shared(CanvasTextFormat format)
    {
        string key = $"{format.FontFamily}|{format.FontSize}|{format.FontWeight.Weight}|{format.HorizontalAlignment}|{format.VerticalAlignment}|{format.WordWrapping}|{format.TrimmingGranularity}|{format.TrimmingSign}";
        if (Formats.TryGetValue(key, out var shared)) { format.Dispose(); return shared; }
        if (Formats.Count >= 256) Formats.Clear(); // only if sizes keep changing; the dropped ones go to the GC, as a draw may still hold one
        Formats[key] = format;
        return format;
    }

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
