using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland;

sealed record IslandNotification(string App, string Title, string Body, CanvasBitmap? Icon, string AppId, Windows.UI.Color? Accent = null, DateTime At = default);

/// <summary>Picks up new Windows toast notifications. Windows asks the user for access once.</summary>
sealed class NotificationSource(CanvasDevice device)
{
    readonly HashSet<uint> _seen = []; // shortcut: grows by one id per toast this session; fine for days of uptime
    UserNotificationListener? _listener;
    DispatcherQueueTimer? _timer;
    bool _polling;

    public Queue<IslandNotification> Pending { get; } = new();

    public async void Start(DispatcherQueue ui)
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Debug.WriteLine($"Notification access: {access}");
                return;
            }
            foreach (var n in await _listener.GetNotificationsAsync(NotificationKinds.Toast)) _seen.Add(n.Id); // don't replay old ones

            // Polling instead of NotificationChanged: that event needs a packaged app; one cheap call a second works everywhere.
            _timer = ui.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += async (_, _) => await Poll();
            _timer.Start();
        }
        catch (Exception ex) { Debug.WriteLine($"Notification listener unavailable: {ex}"); }
    }

    async Task Poll()
    {
        if (_polling || _listener is null) return;
        _polling = true;
        try
        {
            foreach (var n in await _listener.GetNotificationsAsync(NotificationKinds.Toast))
                if (_seen.Add(n.Id)) Pending.Enqueue(await Read(n));
        }
        catch (Exception ex) { Debug.WriteLine($"Reading notifications failed: {ex}"); }
        finally { _polling = false; }
    }

    async Task<IslandNotification> Read(UserNotification n)
    {
        var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements().Select(t => t.Text).ToList() ?? [];
        CanvasBitmap? icon = null;
        try
        {
            using var stream = await n.AppInfo.DisplayInfo.GetLogo(new Size(64, 64)).OpenReadAsync();
            icon = await CanvasBitmap.LoadAsync(device, stream);
        }
        catch { } // plenty of apps have no readable logo; the card shows a placeholder
        string appId = n.AppInfo?.AppUserModelId ?? "";
        Windows.UI.Color? accent = null;
        if (icon is not null && !_accents.TryGetValue(appId, out var cached)) _accents[appId] = cached = MediaSource.AccentOf(device, icon); // the app's colour, once per app
        if (icon is not null) accent = _accents[appId];
        return new(n.AppInfo?.DisplayInfo?.DisplayName ?? "", texts.FirstOrDefault() ?? "", string.Join("\n", texts.Skip(1)), icon, appId, accent, DateTime.Now);
    }
    readonly Dictionary<string, Windows.UI.Color> _accents = [];

    public static void Open(IslandNotification n)
    {
        if (n.AppId.Length == 0) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{n.AppId}") { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Couldn't open {n.App}: {ex}"); }
    }
}

static class NotificationView
{
    public static void Draw(CanvasDrawingSession ds, Rect r, float s, IslandNotification n, float a)
    {
        float x = (float)r.X, y = (float)r.Y, w = (float)r.Width, h = (float)r.Height;
        float icon = 40 * s;
        MediaView.DrawArt(ds, n.Icon, new Rect(x + 16 * s, y + (h - icon) / 2, icon, icon), 10 * s, a);

        using var title = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.SemiBold), FontSize = 15 * s, FontWeight = FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };
        using var body = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.Normal), FontSize = 13 * s,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };
        float tx = x + 70 * s, tw = w - 86 * s;
        var white = ColorHelper.FromArgb((byte)(255 * a), 255, 255, 255);
        var dim = ColorHelper.FromArgb((byte)(170 * a), 255, 255, 255);
        ds.DrawText(n.Title.Length > 0 ? n.Title : n.App, new Rect(tx, y + 15 * s, tw, 22 * s), white, title);
        ds.DrawText(n.Body.Length > 0 ? n.Body.Replace('\n', ' ') : n.App, new Rect(tx, y + 39 * s, tw, 20 * s), dim, body);
    }

    public const float CardW = 380, CardH = 168;

    /// <summary>Expanded notification: app icon, app name and time, then the full title and message (wrapped, up to ~4 lines).</summary>
    public static void DrawExpanded(CanvasDrawingSession ds, Rect r, float s, IslandNotification n, float a)
    {
        float x = (float)r.X, y = (float)r.Y, w = (float)r.Width;
        var white = ColorHelper.FromArgb((byte)(255 * a), 255, 255, 255);
        var dim = ColorHelper.FromArgb((byte)(175 * a), 255, 255, 255);
        var faint = ColorHelper.FromArgb((byte)(120 * a), 255, 255, 255);
        MediaView.DrawArt(ds, n.Icon, new Rect(x + 16 * s, y + 14 * s, 24 * s, 24 * s), 6 * s, a);
        using var app = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.Medium), FontSize = 12 * s, FontWeight = FontWeights.Medium,
            VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };
        string when = n.At == default ? "now" : (DateTime.Now - n.At).TotalMinutes < 1 ? "now" : n.At.ToString("HH:mm");
        ds.DrawText($"{n.App}  ·  {when}", new Rect(x + 48 * s, y + 14 * s, w - 64 * s, 24 * s), faint, app);

        using var title = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.SemiBold), FontSize = 15 * s, FontWeight = FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.WholeWord, TrimmingGranularity = CanvasTextTrimmingGranularity.Word,
        };
        using var body = new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(FontWeights.Normal), FontSize = 13 * s, LineSpacing = 18 * s,
            WordWrapping = CanvasWordWrapping.WholeWord, TrimmingGranularity = CanvasTextTrimmingGranularity.Word,
        };
        float tw = w - 32 * s;
        string t = n.Title.Length > 0 ? n.Title : n.App;
        using var titleLayout = new CanvasTextLayout(ds, t, title, tw, 42 * s); // up to 2 lines
        float th = MathF.Min((float)titleLayout.LayoutBounds.Height, 42 * s);
        using (ds.CreateLayer(1f, new Rect(x + 16 * s, y + 46 * s, tw, th + 1)))
            ds.DrawTextLayout(titleLayout, x + 16 * s, y + 46 * s, white);
        float by = y + 50 * s + th, bh = (float)r.Bottom - 14 * s - by; // the rest of the card: ~4 lines of message
        if (n.Body.Length > 0 && bh > 10 * s)
            using (ds.CreateLayer(1f, new Rect(x + 16 * s, by, tw, bh)))
                ds.DrawText(n.Body, new Rect(x + 16 * s, by, tw, bh), dim, body);
    }
}
