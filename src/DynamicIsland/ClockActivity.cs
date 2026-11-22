using System.Diagnostics;
using System.Text.RegularExpressions;
using FlaUI.UIA3;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;

namespace DynamicIsland;

/// <summary>
/// A running timer or stopwatch from the Windows Clock app, taken over by the island when Clock is minimised.
/// Clock has no API for this, so its window is read once through UI Automation at the moment it minimises
/// (while its page is still live) and the island counts on by itself from there: no polling of the app.
/// </summary>
sealed partial record ClockActivity(bool Stopwatch, string Name, TimeSpan Value, TimeSpan Total, DateTime ReadAt)
{
    public const string AppId = "Microsoft.WindowsAlarms";
    public static readonly Color Amber = ColorHelper.FromArgb(255, 255, 170, 60), Teal = ColorHelper.FromArgb(255, 90, 210, 200);
    public Color Tint => Stopwatch ? Teal : Amber;

    /// <summary>Time left (timer) or elapsed (stopwatch) right now.</summary>
    public TimeSpan Now()
    {
        var gone = DateTime.Now - ReadAt;
        return Stopwatch ? Value + gone : Value - gone;
    }

    public bool Finished => !Stopwatch && Now() <= TimeSpan.Zero;

    /// <summary>
    /// The running timer with the least time left, else a running stopwatch, from the Clock window <paramref name="hwnd"/>.
    /// Only the page Clock is showing is in its tree, so a timer runs into the island from the Timer page and the
    /// stopwatch from the Stopwatch page. Call off the UI thread.
    /// </summary>
    public static ClockActivity? Read(nint hwnd)
    {
        try
        {
            using var uia = new UIA3Automation();
            var window = uia.FromHandle(hwnd);
            ClockActivity? best = null;
            foreach (var e in window.FindAllDescendants())
            {
                string id = e.Properties.AutomationId.ValueOrDefault ?? "", name = e.Properties.Name.ValueOrDefault ?? "";
                // "Edit timer, 3 分鐘, Running, 3 minutes 26 seconds, Remaining Of, 5 minutes 0 seconds, 15:15"
                if (id == "TimerViewGrid" && name.Contains(", Running,") && !name.Contains('−'))
                {
                    var parts = name.Split(", ");
                    int state = Array.IndexOf(parts, "Running");
                    if (state < 0 || state + 1 >= parts.Length) continue;
                    var left = Duration(parts[state + 1]);
                    int of = Array.IndexOf(parts, "Remaining Of");
                    var total = of >= 0 && of + 1 < parts.Length ? Duration(parts[of + 1]) : left;
                    if (left is not { } l || l <= TimeSpan.Zero || l > TimeSpan.FromDays(2)) continue; // overdue timers read as huge or negative
                    if (best is null || l < best.Value)
                        best = new(false, state >= 1 ? parts[state - 1] : "Timer", l, total ?? l, DateTime.Now);
                }
                // "Stopwatch, Running, 0 hours 1 minutes 5 seconds 20 centiseconds"
                else if (best is null && name.StartsWith("Stopwatch, Running,") && Duration(name["Stopwatch, Running,".Length..]) is { } elapsed)
                    best = new(true, "Stopwatch", elapsed, TimeSpan.Zero, DateTime.Now);
            }
            return best;
        }
        catch (Exception ex) { Debug.WriteLine($"Clock read failed: {ex.Message}"); return null; }
    }

    [GeneratedRegex(@"(\d+)\s*(hour|minute|second|centisecond)", RegexOptions.IgnoreCase)] private static partial Regex Part();

    /// <summary>"1 hour 15 minutes 0 seconds" (UI Automation names are English even on a Chinese system).</summary>
    static TimeSpan? Duration(string text)
    {
        var ms = Part().Matches(text);
        if (ms.Count == 0) return null;
        double sec = 0;
        foreach (Match m in ms)
        {
            double n = double.Parse(m.Groups[1].Value);
            sec += m.Groups[2].Value.ToLowerInvariant() switch { "hour" => n * 3600, "minute" => n * 60, "second" => n, _ => n / 100 };
        }
        return TimeSpan.FromSeconds(sec);
    }

    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>
    /// The pill: tinted glyph on the left, the name, and the time large on the right; a timer's ring shows how much is left.
    /// Static apart from the digits, which change once a second.
    /// </summary>
    public void Draw(CanvasDrawingSession ds, Rect r, float s, float a)
    {
        Color C(Color c, float al) => ColorHelper.FromArgb((byte)Math.Clamp(al * 255 * a, 0, 255), c.R, c.G, c.B);
        float cy = (float)(r.Y + r.Height / 2), x = (float)r.X + 10 * s, rad = 9 * s;
        var cx = x + rad;
        if (!Stopwatch && Total > TimeSpan.Zero) // ring: the share of the timer still left
        {
            float left = (float)Math.Clamp(Now().TotalSeconds / Total.TotalSeconds, 0, 1);
            ds.DrawCircle(cx, cy, rad, C(Colors.White, 0.18f), 2.2f * s);
            if (left > 0)
            {
                using var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ds);
                float start = -MathF.PI / 2, sweep = 2 * MathF.PI * left;
                pb.BeginFigure(cx + rad * MathF.Cos(start), cy + rad * MathF.Sin(start));
                pb.AddArc(new System.Numerics.Vector2(cx, cy), rad, rad, start, sweep);
                pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
                using var arc = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(arc, C(Tint, 1), 2.2f * s, new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle { StartCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round, EndCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round });
            }
        }
        else
        {
            using var icon = new CanvasTextFormat { FontFamily = "Segoe Fluent Icons", FontSize = 14 * s, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
            ds.DrawText("", new Rect(cx - 12 * s, cy - 12 * s, 24 * s, 24 * s), C(Tint, 1), icon);
        }
        var time = Fonts.Shared(new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.SemiBold), FontSize = 15 * s, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap,
        });
        var label = Fonts.Shared(new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.Medium), FontSize = 12 * s, FontWeight = FontWeights.Medium,
            VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        });
        float right = (float)r.Right - 14 * s, tx = x + 2 * rad + 10 * s;
        Fonts.DrawTabular(ds, Format(Now()), new Rect(tx, r.Y, right - tx, r.Height), C(Tint, 1), time);
        ds.DrawText(Name, new Rect(tx, r.Y, Math.Max(0, right - tx - 70 * s), r.Height), C(Colors.White, 0.7f), label);
    }
}
