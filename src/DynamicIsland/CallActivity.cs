using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;

namespace DynamicIsland;

/// <summary>
/// A Zoom, Teams, Google Meet or Discord call, seen as that app holding the microphone (the same consent-store record as the
/// taskbar's mic icon). The pill shows the app and how long the call has run; it changes once a second and nothing polls the app.
/// </summary>
sealed record CallActivity(string Key, string App, Color Tint, DateTime Since, string Exe, nint Window)
{
    static readonly (string Exe, string App, Color Tint)[] Apps =
    [
        ("zoom.exe", "Zoom", ColorHelper.FromArgb(255, 70, 150, 255)),
        ("ms-teams.exe", "Teams", ColorHelper.FromArgb(255, 130, 135, 240)),
        ("teams.exe", "Teams", ColorHelper.FromArgb(255, 130, 135, 240)),
        ("discord.exe", "Discord", ColorHelper.FromArgb(255, 110, 125, 250)),
    ];
    static readonly string[] Browsers = ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe"];
    static readonly Color MeetGreen = ColorHelper.FromArgb(255, 60, 190, 120);

    /// <summary>The call among the apps using the microphone (consent-store key → FILETIME it started), or null.</summary>
    public static CallActivity? Find(IReadOnlyDictionary<string, long> mic)
    {
        foreach (var (key, start) in mic.OrderByDescending(p => p.Value))
        {
            string k = key.ToLowerInvariant(), exe = k.Contains('#') ? k[(k.LastIndexOf('#') + 1)..] : "";
            var since = start > 0 ? DateTime.FromFileTime(start) : DateTime.Now;
            if (since > DateTime.Now) since = DateTime.Now;
            if (k.StartsWith("msteams_")) return new(key, "Teams", Apps[1].Tint, since, "ms-teams.exe", 0); // new Teams is a packaged app
            if (k.StartsWith("microsoftteams_")) return new(key, "Teams", Apps[1].Tint, since, "msteams.exe", 0); // Teams (free), packaged too
            foreach (var a in Apps)
                if (exe == a.Exe) return new(key, a.App, a.Tint, since, a.Exe, 0);
            if (Browsers.Contains(exe) && WindowOf(exe, IsMeet) is var meet && meet != 0) return new(key, "Google Meet", MeetGreen, since, exe, meet);
        }
        return null;
    }

    static bool IsMeet(string title) => title.StartsWith("Meet ") || title.StartsWith("Meet –") || title.Contains("Google Meet") || title.Contains("meet.google.com");

    /// <summary>A visible window of <paramref name="exe"/> (whose title passes <paramref name="want"/>, if given), else 0.</summary>
    static nint WindowOf(string exe, Func<string, bool>? want = null)
    {
        var pids = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
        if (pids.Count == 0) return 0;
        nint found = 0;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            var sb = new System.Text.StringBuilder(256);
            Native.GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (want is null || want(title)) { found = h; return false; }
            return true;
        }, 0);
        return found;
    }

    /// <summary>Brings the call window forward (a minimised one is restored).</summary>
    public void Open()
    {
        try
        {
            nint h = Window != 0 && Native.IsWindowVisible(Window) ? Window
                : WindowOf(Exe, t => t.Contains("Meeting") || t.Contains("meeting") || t.Contains("Call") || t.Contains("call")) is var call && call != 0 ? call : WindowOf(Exe);
            if (h == 0) return;
            if (Native.IsIconic(h)) Native.ShowWindow(h, 9 /* SW_RESTORE */);
            Native.SetForegroundWindow(h);
        }
        catch (Exception ex) { Debug.WriteLine($"Couldn't bring the call forward: {ex.Message}"); }
    }

    /// <summary>The pill: the app's call glyph in its colour, its name, and the call's length on the right.</summary>
    public void Draw(CanvasDrawingSession ds, Rect r, float s, float a)
    {
        Color C(Color c, float al) => ColorHelper.FromArgb((byte)Math.Clamp(al * 255 * a, 0, 255), c.R, c.G, c.B);
        float cy = (float)(r.Y + r.Height / 2), x = (float)r.X + 10 * s, rad = 10 * s, cx = x + rad;
        ds.FillCircle(cx, cy, rad, C(Tint, 0.22f));
        var icon = Fonts.Shared(new CanvasTextFormat { FontFamily = "Segoe Fluent Icons", FontSize = 11 * s, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center });
        ds.DrawText("", new Rect(cx - 12 * s, cy - 12 * s, 24 * s, 24 * s), C(Tint, 1), icon);
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
        Fonts.DrawTabular(ds, ClockActivity.Format(DateTime.Now - Since), new Rect(tx, r.Y, right - tx, r.Height), C(Tint, 1), time);
        ds.DrawText(App, new Rect(tx, r.Y, Math.Max(0, right - tx - 70 * s), r.Height), C(Colors.White, 0.7f), label);
    }
}
