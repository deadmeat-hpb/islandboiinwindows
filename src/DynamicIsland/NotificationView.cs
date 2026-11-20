using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;
using FontWeight = Windows.UI.Text.FontWeight;

namespace DynamicIsland;

/// <summary>What kind of notification it is, which picks its pill and card design.</summary>
enum NoteKind { Generic, Message, Mail, Calendar, Call, Web, Screenshot, Dpi, Alarm, Security, Update, Store, Capture, System }

/// <summary>
/// Notification pills and cards. Windows only gives a notification listener the toast's text and the app's icon (no
/// images inside the toast, no way to press its buttons or reply), so each design is built from those, plus the
/// clipboard image for screenshots.
/// </summary>
static partial class NotificationView
{
    static readonly string[] Messengers = ["whatsapp", "telegram", "discord", "line", "wechat", "weixin", "messenger", "teams", "signal", "slack", "skype", "instagram", "yourphone", "phone link"];
    static readonly string[] Mailers = ["outlook", "mail", "gmail", "thunderbird", "spark"];
    static readonly string[] Browsers = ["chrome", "edge", "firefox", "brave", "opera", "vivaldi", "arc"];

    [GeneratedRegex(@"\b(incoming (video |voice |audio )?call|is calling|calling you|來電|通話)", RegexOptions.IgnoreCase)] private static partial Regex CallText();
    [GeneratedRegex(@"\b(reminder|starts? (in|at)|meeting|event|提醒|會議)\b", RegexOptions.IgnoreCase)] private static partial Regex CalendarText();
    [GeneratedRegex(@"^(?:https?://)?([\w-]+\.)+[a-z]{2,}(/\S*)?$", RegexOptions.IgnoreCase)] private static partial Regex Domain();
    [GeneratedRegex(@"\b(\d{1,2}):(\d{2})\s*(am|pm|上午|下午)?", RegexOptions.IgnoreCase)] private static partial Regex Clock();
    [GeneratedRegex(@"\d{2,5}")] private static partial Regex Number();
    [GeneratedRegex(@"record|video|\bclips?\b|錄製|錄影|剪輯", RegexOptions.IgnoreCase)] private static partial Regex RecordText();

    static bool Has(string s, string[] keys) => keys.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static NoteKind KindOf(IslandNotification n)
    {
        string app = $"{n.App} {n.AppId}", text = $"{n.Title}\n{n.Body}";
        // Built-in Windows apps, by app id (display names are localized).
        if (app.Contains("WindowsAlarms", StringComparison.OrdinalIgnoreCase)) return NoteKind.Alarm;
        if (app.Contains("XboxGamingOverlay", StringComparison.OrdinalIgnoreCase)) return NoteKind.Capture;
        if (Has(app, ["SecHealthUI", "Defender", "SecurityCenter"])) return NoteKind.Security;
        if (app.Contains("WindowsUpdate", StringComparison.OrdinalIgnoreCase)) return NoteKind.Update;
        if (Has(app, ["WindowsStore", "AppInitiatedDownload"])) return NoteKind.Store;
        if (SystemBadge(app, text) is not null) return NoteKind.System;
        if (app.Contains("ScreenSketch", StringComparison.OrdinalIgnoreCase) && RecordText().IsMatch(text)) return NoteKind.Capture; // Snipping Tool screen recording
        if (app.Contains("G HUB", StringComparison.OrdinalIgnoreCase) || app.Contains("logitech", StringComparison.OrdinalIgnoreCase))
            return text.Contains("DPI", StringComparison.OrdinalIgnoreCase) || Number().IsMatch(text) ? NoteKind.Dpi : NoteKind.Generic;
        if (app.Contains("snip", StringComparison.OrdinalIgnoreCase) || app.Contains("ScreenSketch", StringComparison.OrdinalIgnoreCase) || text.Contains("screenshot", StringComparison.OrdinalIgnoreCase))
            return NoteKind.Screenshot;
        if (CallText().IsMatch(text)) return NoteKind.Call;
        if (Has(app, Messengers)) return NoteKind.Message;
        if (app.Contains("calendar", StringComparison.OrdinalIgnoreCase) || app.Contains("Todos", StringComparison.OrdinalIgnoreCase) || app.Contains("outlook", StringComparison.OrdinalIgnoreCase) && CalendarText().IsMatch(text)) return NoteKind.Calendar;
        if (Has(app, Mailers)) return NoteKind.Mail;
        if (Has(app, Browsers)) return NoteKind.Web;
        if (CalendarText().IsMatch(n.Title)) return NoteKind.Calendar;
        return NoteKind.Generic;
    }

    public const float CardW = 380;

    public static float CardH(IslandNotification n) => KindOf(n) switch
    {
        NoteKind.Dpi => 120,
        NoteKind.Call => 132,
        NoteKind.Screenshot => n.Thumb is { } t ? 46 + 48 + ShotSize(t).H + 18 : 112,
        NoteKind.Calendar => 140,
        NoteKind.Alarm => 132,
        NoteKind.Security or NoteKind.Update or NoteKind.Store or NoteKind.Capture or NoteKind.System => BadgeCardH(n),
        _ => 168,
    };

    /// <summary>Badge cards fit their text: header, title, up to two body lines, the chip, then the bottom margin.</summary>
    static float BadgeCardH(IslandNotification n)
    {
        float h = 46 + 24;
        if (n.Body.Length > 0)
        {
            using var f = Format(13, FontWeights.Normal, wrap: true);
            using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), n.Body, f, CardW - 32 - 62, 40);
            h += MathF.Min((float)layout.LayoutBounds.Height, 40);
        }
        if (Badge(n) is { } b && b.Chip.Length > 0 && !n.Title.Contains(b.Chip, StringComparison.OrdinalIgnoreCase)) h += 10 + 24;
        return MathF.Max(h + 18, 112); // the 48 px badge needs about 112
    }

    static Color White(float a) => ColorHelper.FromArgb((byte)Math.Clamp(a * 255, 0, 255), 255, 255, 255);
    static Color Tint(Color c, float a) => ColorHelper.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);
    static readonly Color Cool = ColorHelper.FromArgb(255, 150, 180, 255);

    static CanvasTextFormat Format(float size, FontWeight weight, bool wrap = false, CanvasHorizontalAlignment h = CanvasHorizontalAlignment.Left, CanvasVerticalAlignment v = CanvasVerticalAlignment.Top) => new()
    {
        FontFamily = Fonts.Inter(weight), FontSize = size, FontWeight = weight,
        WordWrapping = wrap ? CanvasWordWrapping.WholeWord : CanvasWordWrapping.NoWrap,
        TrimmingGranularity = wrap ? CanvasTextTrimmingGranularity.Word : CanvasTextTrimmingGranularity.Character,
        HorizontalAlignment = h, VerticalAlignment = v,
        TrimmingSign = CanvasTrimmingSign.Ellipsis,
    };

    static void Icon(CanvasDrawingSession ds, IslandNotification n, Rect r, float a, bool round)
    {
        if (round && n.Icon is not null)
        {
            using var clip = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateEllipse(ds, (float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2), (float)r.Width / 2, (float)r.Height / 2);
            using (ds.CreateLayer(a, clip)) ds.DrawImage(n.Icon, r, n.Icon.Bounds);
            return;
        }
        MediaView.DrawArt(ds, n.Icon, r, (float)r.Width * (round ? 0.5f : 0.25f), a);
    }

    /// <summary>Clipped text block (wrapped, word-trimmed) so long messages never spill out of the card.</summary>
    static void Block(CanvasDrawingSession ds, string text, Rect box, Color c, CanvasTextFormat f)
    {
        if (text.Length == 0 || box.Height <= 0 || box.Width <= 0) return;
        using (ds.CreateLayer(1f, box)) ds.DrawText(text, box, c, f);
    }

    static string When(IslandNotification n) => n.At == default || (DateTime.Now - n.At).TotalMinutes < 1 ? "now" : n.At.ToString("HH:mm");

    /// <summary>A browser toast's site ("youtube.com"), taken from a text line that is just a domain.</summary>
    static (string Site, string Body) SplitSite(IslandNotification n)
    {
        var lines = n.Body.Split('\n').ToList();
        int i = lines.FindLastIndex(l => Domain().IsMatch(l.Trim()));
        if (i < 0) return ("", n.Body);
        string site = Domain().Match(lines[i].Trim()).Value.Replace("https://", "").Replace("http://", "").Split('/')[0];
        lines.RemoveAt(i);
        return (site, string.Join("\n", lines));
    }

    static string DpiValue(IslandNotification n) => Number().Matches($"{n.Title} {n.Body}").Select(m => m.Value).LastOrDefault() ?? "";

    // ---------------------------------------------------------------- pill

    public static void Draw(CanvasDrawingSession ds, Rect r, float s, IslandNotification n, float a)
    {
        float x = (float)r.X, y = (float)r.Y, w = (float)r.Width, h = (float)r.Height;
        var kind = KindOf(n);
        float icon = 40 * s;
        var iconRect = new Rect(x + 16 * s, y + (h - icon) / 2, icon, icon);
        if (kind == NoteKind.Screenshot && n.Thumb is { } snip) Cover(ds, snip, iconRect, 10 * s, s, a); // the snip itself
        else if (Badge(n) is { } badge) DrawBadge(ds, iconRect, s, badge, a, kind == NoteKind.Alarm);
        else Icon(ds, n, iconRect, a, kind is NoteKind.Message or NoteKind.Call);
        float tx = x + 70 * s, tw = w - 86 * s;
        using var title = Format(15 * s, FontWeights.SemiBold);
        using var body = Format(13 * s, FontWeights.Normal);

        if (kind == NoteKind.Dpi)
        {
            using var big = Format(22 * s, FontWeights.SemiBold, v: CanvasVerticalAlignment.Center);
            ds.DrawText("DPI", new Rect(tx, y, 50 * s, h), White(0.55f * a), Format(13 * s, FontWeights.SemiBold, v: CanvasVerticalAlignment.Center));
            Fonts.DrawTabular(ds, DpiValue(n), new Rect(tx + 38 * s, y, tw - 38 * s, h), White(a), big);
            return;
        }
        string head = n.Title.Length > 0 ? n.Title : n.App;
        string line = kind == NoteKind.Web ? SplitSite(n).Body : n.Body;
        if (kind == NoteKind.Call) line = "Incoming call";
        ds.DrawText(head, new Rect(tx, y + 15 * s, tw, 22 * s), White(a), title);
        ds.DrawText(line.Length > 0 ? line.Replace('\n', ' ') : n.App, new Rect(tx, y + 39 * s, tw, 20 * s), White(0.67f * a), body);
    }

    // ---------------------------------------------------------------- card

    public static void DrawExpanded(CanvasDrawingSession ds, Rect r, float s, IslandNotification n, float a)
    {
        var kind = KindOf(n);
        float x = (float)r.X, y = (float)r.Y, w = (float)r.Width, pad = 16 * s, tw = w - 2 * pad;
        var accent = n.Accent ?? Cool;

        // Header shared by every design: small icon, app name, time.
        if (kind is not (NoteKind.Call or NoteKind.Dpi))
        {
            bool badge = Badge(n) is not null; // built-in apps: the big glyph badge below stands in for the icon
            if (!badge) Icon(ds, n, new Rect(x + pad, y + 14 * s, 22 * s, 22 * s), a, false);
            float hx = badge ? 4 * s : 30 * s;
            using var app = Format(12 * s, FontWeights.Medium, v: CanvasVerticalAlignment.Center);
            string site = kind == NoteKind.Web ? SplitSite(n).Site : "";
            ds.DrawText($"{n.App}{(site.Length > 0 ? "  ·  " + site : "")}  ·  {When(n)}", new Rect(x + pad + hx, y + 13 * s, tw - hx, 24 * s), White(0.47f * a), app);
        }
        float top = y + 46 * s, bottom = (float)r.Bottom - 14 * s;

        switch (kind)
        {
            case NoteKind.Message: // sender, then the message in a soft bubble
            {
                using var name = Format(15 * s, FontWeights.SemiBold);
                ds.DrawText(n.Title.Length > 0 ? n.Title : n.App, new Rect(x + pad, top, tw, 22 * s), White(a), name);
                using var msg = Format(13.5f * s, FontWeights.Normal, wrap: true);
                string text = n.Body.Length > 0 ? n.Body : "New message";
                using var layout = new CanvasTextLayout(ds, text, msg, tw - 24 * s, bottom - top - 44 * s);
                float bh = MathF.Min((float)layout.LayoutBounds.Height, bottom - top - 44 * s) + 16 * s;
                var bubble = new Rect(x + pad, top + 28 * s, MathF.Min((float)layout.LayoutBounds.Width + 24 * s, tw), bh);
                ds.FillRoundedRectangle(bubble, 14 * s, 14 * s, Tint(accent, 0.20f * a));
                using (ds.CreateLayer(1f, bubble)) ds.DrawTextLayout(layout, (float)bubble.X + 12 * s, (float)bubble.Y + 8 * s, White(0.92f * a));
                break;
            }
            case NoteKind.Mail: // sender, bold subject, preview
            {
                var lines = n.Body.Split('\n', 2);
                using var from = Format(13 * s, FontWeights.Medium);
                using var subject = Format(15 * s, FontWeights.SemiBold);
                using var preview = Format(13 * s, FontWeights.Normal, wrap: true);
                ds.DrawText(n.Title.Length > 0 ? n.Title : n.App, new Rect(x + pad, top, tw, 20 * s), White(0.75f * a), from);
                ds.DrawText(lines[0], new Rect(x + pad, top + 22 * s, tw, 22 * s), White(a), subject);
                Block(ds, lines.Length > 1 ? lines[1] : "", new Rect(x + pad, top + 48 * s, tw, bottom - top - 48 * s), White(0.62f * a), preview);
                break;
            }
            case NoteKind.Calendar: // accent bar, title, time + countdown, location
            {
                ds.FillRoundedRectangle(x + pad, top + 2 * s, 4 * s, bottom - top - 4 * s, 2 * s, 2 * s, Tint(accent, 0.9f * a));
                float cx = x + pad + 14 * s, cw = tw - 14 * s;
                using var title = Format(16 * s, FontWeights.SemiBold);
                using var detail = Format(13 * s, FontWeights.Normal);
                ds.DrawText(n.Title.Length > 0 ? n.Title : n.App, new Rect(cx, top, cw, 24 * s), White(a), title);
                var lines = n.Body.Split('\n');
                string time = lines.FirstOrDefault(l => Clock().IsMatch(l)) ?? (lines.Length > 0 ? lines[0] : "");
                string countdown = Countdown(time);
                ds.DrawText(time + (countdown.Length > 0 ? "  ·  " + countdown : ""), new Rect(cx, top + 28 * s, cw, 20 * s), Tint(accent, a), detail);
                string place = string.Join("  ·  ", lines.Where(l => l != time && l.Trim().Length > 0));
                ds.DrawText(place, new Rect(cx, top + 50 * s, cw, 20 * s), White(0.6f * a), detail);
                break;
            }
            case NoteKind.Call: // caller centred, a slow ring around the avatar
            {
                float av = 56 * s, ax = x + pad, ay = y + ((float)r.Height - av) / 2;
                for (int k = 1; k <= 2; k++) // still halo rings around the caller (static: no redraws while it rings)
                    ds.DrawCircle(ax + av / 2, ay + av / 2, av / 2 + k * 5 * s, Tint(accent, (0.5f - k * 0.17f) * a), 1.5f * s);
                Icon(ds, n, new Rect(ax, ay, av, av), a, true);
                using var who = Format(18 * s, FontWeights.SemiBold);
                using var what = Format(13 * s, FontWeights.Normal);
                ds.DrawText(n.Title.Length > 0 ? n.Title : n.App, new Rect(ax + av + 16 * s, ay + 6 * s, tw - av - 16 * s, 26 * s), White(a), who);
                ds.DrawText($"{n.App} · {(n.Body.Length > 0 ? n.Body.Split('\n')[0] : "Incoming call")}", new Rect(ax + av + 16 * s, ay + 32 * s, tw - av - 16 * s, 20 * s), White(0.6f * a), what);
                break;
            }
            case NoteKind.Screenshot: // badge, title + status, then the snip itself
            {
                var b = Badge(n)!.Value;
                float bs = 40 * s;
                DrawBadge(ds, new Rect(x + pad, top, bs, bs), s, b, a, false);
                float tx = x + pad + bs + 12 * s, cw = tw - bs - 12 * s;
                using var title = Format(15 * s, FontWeights.SemiBold);
                using var sub = Format(12.5f * s, FontWeights.Normal);
                ds.DrawText(n.Title.Length > 0 ? n.Title : "Screenshot", new Rect(tx, top - 1 * s, cw, 22 * s), White(a), title);
                string status = n.Body.Length > 0 ? n.Body.Replace('\n', ' ')
                    : n.Thumb is not null && !n.Title.Contains("clipboard", StringComparison.OrdinalIgnoreCase) ? "Copied to clipboard" : "Click to mark up";
                ds.DrawText(status, new Rect(tx, top + 20 * s, cw, 20 * s), Tint(b.Color, a), sub);
                if (n.Thumb is { } img)
                {
                    var (sw, sh) = ShotSize(img);
                    var dest = new Rect(x + (float)r.Width / 2 - sw * s / 2, top + 48 * s, sw * s, sh * s);
                    ds.FillRoundedRectangle(new Rect(x + pad, dest.Y, tw, dest.Height), 12 * s, 12 * s, White(0.05f * a)); // a soft tray behind narrow snips
                    MediaView.DrawArt(ds, img, dest, 12 * s, a);
                    ds.DrawRoundedRectangle(dest, 12 * s, 12 * s, White(0.18f * a), 1 * s);
                }
                break;
            }
            case NoteKind.Alarm: // Clock: ringing badge, the time large, the alarm or timer name
            {
                var b = Badge(n)!.Value;
                float bs = 56 * s, by = top + 2 * s;
                DrawBadge(ds, new Rect(x + pad, by, bs, bs), s, b, a, ringing: true);
                float tx = x + pad + bs + 20 * s, cw = tw - bs - 20 * s;
                bool timer = Says(n, "timer|計時");
                var lines = $"{n.Title}\n{n.Body}".Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => !Regex.IsMatch(l, "time's up|time is up|timer done|時間到", RegexOptions.IgnoreCase)).ToArray(); // the big line already says it
                string time = lines.Select(l => Clock().Match(l)).FirstOrDefault(m => m.Success)?.Value ?? (timer ? "Time's up" : DateTime.Now.ToString("HH:mm"));
                string label = lines.FirstOrDefault(l => !Clock().IsMatch(l)) ?? n.App;
                string detail = string.Join("  ·  ", lines.Where(l => l != label && !Clock().IsMatch(l)));
                using var big = Format(30 * s, FontWeights.SemiBold);
                using var name = Format(14 * s, FontWeights.SemiBold);
                using var sub = Format(12.5f * s, FontWeights.Normal);
                Fonts.DrawTabular(ds, time, new Rect(tx, by - 8 * s, cw, 40 * s), White(a), big);
                ds.DrawText(label, new Rect(tx, by + 32 * s, cw, 20 * s), Tint(b.Color, a), name);
                ds.DrawText(detail.Length > 0 ? detail : "Click to open Clock", new Rect(tx, by + 52 * s, cw, 18 * s), White(0.55f * a), sub);
                break;
            }
            case NoteKind.Security or NoteKind.Update or NoteKind.Store or NoteKind.Capture or NoteKind.System: // system apps: glyph badge, title, text, a status chip
            {
                var b = Badge(n)!.Value;
                float bs = 48 * s;
                DrawBadge(ds, new Rect(x + pad, top + 2 * s, bs, bs), s, b, a, false);
                float tx = x + pad + bs + 14 * s, cw = tw - bs - 14 * s;
                using var title = Format(15 * s, FontWeights.SemiBold);
                using var text = Format(13 * s, FontWeights.Normal, wrap: true);
                ds.DrawText(n.Title.Length > 0 ? n.Title : n.App, new Rect(tx, top, cw, 22 * s), White(a), title);
                float chipH = b.Chip.Length > 0 && !n.Title.Contains(b.Chip, StringComparison.OrdinalIgnoreCase) ? 24 * s : 0; // no chip that just repeats the title
                float bodyMax = bottom - top - 24 * s - (chipH > 0 ? chipH + 10 * s : 0);
                using var bodyLayout = new CanvasTextLayout(ds, n.Body, text, cw, bodyMax);
                float bodyH = n.Body.Length > 0 ? MathF.Min((float)bodyLayout.LayoutBounds.Height, bodyMax) : 0;
                Block(ds, n.Body, new Rect(tx, top + 24 * s, cw, bodyMax), White(0.65f * a), text);
                if (chipH > 0)
                {
                    using var chip = Format(12 * s, FontWeights.SemiBold, h: CanvasHorizontalAlignment.Center, v: CanvasVerticalAlignment.Center);
                    using var tl = new CanvasTextLayout(ds, b.Chip, chip, cw, chipH);
                    var cr = new Rect(tx, top + 24 * s + bodyH + 10 * s, (float)tl.LayoutBounds.Width + 22 * s, chipH);
                    ds.FillRoundedRectangle(cr, chipH / 2, chipH / 2, Tint(b.Color, 0.22f * a));
                    ds.DrawText(b.Chip, cr, Tint(b.Color, a), chip);
                }
                break;
            }
            case NoteKind.Dpi: // G HUB: mouse glyph, big DPI value
            {
                var logi = ColorHelper.FromArgb(255, 0, 184, 252);
                float cy = y + (float)r.Height / 2;
                ds.FillCircle(x + pad + 26 * s, cy, 26 * s, Tint(logi, 0.18f * a));
                using var glyph = new CanvasTextFormat { FontFamily = "Segoe Fluent Icons", FontSize = 24 * s, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
                ds.DrawText("", new Rect(x + pad, cy - 26 * s, 52 * s, 52 * s), White(0.95f * a), glyph);
                using var label = Format(12 * s, FontWeights.Medium);
                using var big = Format(34 * s, FontWeights.SemiBold, v: CanvasVerticalAlignment.Center);
                float tx = x + pad + 70 * s;
                ds.DrawText($"{n.App} · DPI", new Rect(tx, cy - 32 * s, tw - 70 * s, 18 * s), White(0.5f * a), label);
                Fonts.DrawTabular(ds, DpiValue(n), new Rect(tx, cy - 16 * s, tw - 70 * s, 44 * s), White(a), big);
                break;
            }
            default: // generic and web push (the site is in the header)
            {
                using var title = Format(15 * s, FontWeights.SemiBold, wrap: true);
                using var text = Format(13 * s, FontWeights.Normal, wrap: true);
                string head = n.Title.Length > 0 ? n.Title : n.App;
                string bodyText = kind == NoteKind.Web ? SplitSite(n).Body : n.Body;
                using var tl = new CanvasTextLayout(ds, head, title, tw, 42 * s);
                float th = MathF.Min((float)tl.LayoutBounds.Height, 42 * s);
                Block(ds, head, new Rect(x + pad, top, tw, th + 2 * s), White(a), title);
                Block(ds, bodyText, new Rect(x + pad, top + th + 6 * s, tw, bottom - top - th - 6 * s), White(0.65f * a), text);
                break;
            }
        }
    }

    // ---------------------------------------------------------------- built-in Windows apps

    /// <summary>A Segoe Fluent Icons glyph in the app's colour, and an optional status chip for the card.</summary>
    readonly record struct BadgeInfo(string Glyph, Color Color, string Chip);

    static bool Says(IslandNotification n, string pattern) => Regex.IsMatch($"{n.Title}\n{n.Body}", pattern, RegexOptions.IgnoreCase);

    static readonly Color Amber = ColorHelper.FromArgb(255, 255, 170, 60);

    /// <summary>The glyph badge that stands in for the app icon on built-in Windows apps (system toasts often have no logo).</summary>
    static BadgeInfo? Badge(IslandNotification n) => KindOf(n) switch
    {
        NoteKind.Alarm => Says(n, "timer|計時") ? new("\uE916", Amber, "")
                        : Says(n, "focus|專注") ? new("\uE823", ColorHelper.FromArgb(255, 170, 140, 255), "")
                        : new("\uEA8F", Amber, ""),
        NoteKind.Security => Says(n, "threat|found|quarantin|blocked|action|attention|威脅|封鎖|需要")
                        ? new("\uEA18", ColorHelper.FromArgb(255, 255, 95, 85), "Action needed")
                        : new("\uEA18", ColorHelper.FromArgb(255, 80, 210, 130), "Protected"),
        NoteKind.Update => new("\uE895", ColorHelper.FromArgb(255, 90, 160, 255), Says(n, "restart|reboot|重新啟動|重新開機") ? "Restart required" : ""),
        NoteKind.Store => new("\uE719", ColorHelper.FromArgb(255, 130, 150, 255), Says(n, "install|安裝") ? "Installed" : Says(n, "update|更新") ? "Updated" : ""),
        NoteKind.Capture => !RecordText().IsMatch($"{n.Title}\n{n.Body}") ? new("\uE722", ColorHelper.FromArgb(255, 90, 200, 110), "Screenshot saved")
                        : new("\uE714", ColorHelper.FromArgb(255, 255, 85, 85), n.AppId.Contains("ScreenSketch", StringComparison.OrdinalIgnoreCase) ? "Recording saved" : "Clip saved"),
        NoteKind.Screenshot => new("\uE722", ColorHelper.FromArgb(255, 90, 200, 110), ""),
        NoteKind.System => SystemBadge($"{n.App} {n.AppId}", $"{n.Title}\n{n.Body}"),
        _ => null,
    };

    /// <summary>The snip's size on the card in DIPs: card width, at most 130 tall (the card must fit the island window).</summary>
    static (float W, float H) ShotSize(CanvasBitmap img)
    {
        float maxW = CardW - 32, maxH = 130, k = MathF.Min(maxW / (float)img.Size.Width, maxH / (float)img.Size.Height);
        return ((float)img.Size.Width * k, (float)img.Size.Height * k);
    }

    /// <summary>The image cropped to fill <paramref name="dest"/> (centre crop), with rounded corners and a thin rim.</summary>
    static void Cover(CanvasDrawingSession ds, CanvasBitmap img, Rect dest, float radius, float s, float a)
    {
        double k = Math.Max(dest.Width / img.Size.Width, dest.Height / img.Size.Height);
        double sw = dest.Width / k, sh = dest.Height / k;
        var src = new Rect((img.Size.Width - sw) / 2, (img.Size.Height - sh) / 2, sw, sh);
        using var clip = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateRoundedRectangle(ds, dest, radius, radius);
        using (ds.CreateLayer(a, clip)) ds.DrawImage(img, dest, src);
        ds.DrawRoundedRectangle(dest, radius, radius, White(0.22f * a), 1 * s);
    }

    static bool Says(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);

    /// <summary>Windows' own system toasts (Windows.SystemToast.*, OneDrive, Backup), matched by app id.</summary>
    static BadgeInfo? SystemBadge(string app, string text)
    {
        bool Is(params string[] ids) => ids.Any(i => app.Contains(i, StringComparison.OrdinalIgnoreCase));
        if (Is("SystemToast.AutoPlay")) return new("\uE88E", ColorHelper.FromArgb(255, 80, 205, 220), "Drive connected");
        if (Is("SystemToast.LowDisk")) return new("\uEDA2", ColorHelper.FromArgb(255, 255, 120, 70), "Low space");
        if (Is("SystemToast.Print"))
            return Says(text, "error|fail|jam|out of|錯誤|失敗|卡紙") ? new("\uE749", ColorHelper.FromArgb(255, 255, 95, 85), "Needs attention")
                 : new("\uE749", ColorHelper.FromArgb(255, 160, 180, 210), Says(text, "printed|complete|done|列印完成|完成") ? "Printed" : "");
        if (Is("SystemToast.Bthprops", "SystemToast.BthQuickPair")) return new("\uE702", ColorHelper.FromArgb(255, 70, 140, 255), Says(text, "pair|配對") ? "Ready to pair" : "");
        if (Is("SkyDrive", "OneDrive"))
            return Says(text, "paused|暫停") ? new("\uE753", Amber, "Sync paused")
                 : Says(text, "full|storage|空間") ? new("\uE753", ColorHelper.FromArgb(255, 255, 120, 70), "Storage full")
                 : new("\uE753", ColorHelper.FromArgb(255, 60, 150, 255), "");
        if (Is("WindowsBackup")) return new("\uE898", ColorHelper.FromArgb(255, 80, 210, 130), Says(text, "complete|backed up|完成|已備份") ? "Backed up" : "");
        if (Is("SystemToast.StartupApp")) return new("\uE7E8", Amber, "New startup app");
        if (Is("SystemToast.DefaultAudioEndpoint", "SystemToast.AudioTroubleshooter")) return new("\uE767", ColorHelper.FromArgb(255, 150, 180, 255), "");
        if (Is("SystemToast.WiFiNetworkManager")) return new("\uE701", ColorHelper.FromArgb(255, 90, 160, 255), "");
        return null;
    }

    /// <summary>Tinted circle with the glyph; a ringing badge gets two still halo rings (static: nothing redraws while it waits).</summary>
    static void DrawBadge(CanvasDrawingSession ds, Rect r, float s, BadgeInfo b, float a, bool ringing)
    {
        float cx = (float)(r.X + r.Width / 2), cy = (float)(r.Y + r.Height / 2), rad = (float)r.Width / 2;
        if (ringing)
            for (int i = 1; i <= 2; i++)
                ds.DrawCircle(cx, cy, rad + i * 5 * s, Tint(b.Color, (0.5f - i * 0.17f) * a), 1.5f * s);
        ds.FillCircle(cx, cy, rad, Tint(b.Color, 0.22f * a));
        ds.DrawCircle(cx, cy, rad - 0.5f * s, Tint(b.Color, 0.35f * a), 1 * s);
        using var glyph = new CanvasTextFormat { FontFamily = "Segoe Fluent Icons", FontSize = rad * 0.95f, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        ds.DrawText(b.Glyph, r, Tint(b.Color, a), glyph);
    }

    /// <summary>"in 10 min" for a time later today found in the text, else "".</summary>
    static string Countdown(string text)
    {
        var m = Clock().Match(text);
        if (!m.Success) return "";
        int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        string ampm = m.Groups[3].Value.ToLowerInvariant();
        if ((ampm is "pm" or "下午") && h < 12) h += 12;
        if ((ampm is "am" or "上午") && h == 12) h = 0;
        if (h > 23 || min > 59) return "";
        var at = DateTime.Today.AddHours(h).AddMinutes(min);
        var left = at - DateTime.Now;
        if (left <= TimeSpan.Zero || left > TimeSpan.FromHours(12)) return left <= TimeSpan.Zero && left > TimeSpan.FromMinutes(-30) ? "now" : "";
        return left.TotalMinutes < 60 ? $"in {Math.Max(1, (int)left.TotalMinutes)} min" : $"in {(int)left.TotalHours} h {left.Minutes} min";
    }
}
