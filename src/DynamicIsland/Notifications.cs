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

sealed record IslandNotification(string App, string Title, string Body, CanvasBitmap? Icon, string AppId, Windows.UI.Color? Accent = null, DateTime At = default, CanvasBitmap? Thumb = null, string? File = null);

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
        try { await StartCore(ui); }
        catch (Exception ex) { Crash.Log("Start", ex); } // async void: an escaping exception would kill the app
    }

        async Task StartCore(DispatcherQueue ui)
    {
        WatchScreenshots(ui); // works without notification access
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
            _timer.Tick += async (_, _) => { try { await Poll(); } catch (Exception ex) { Crash.Log("Poll", ex); } };
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
                if (_seen.Add(n.Id) && await Read(n) is var note && !DuplicateShot(note)) Pending.Enqueue(note);
        }
        catch (Exception ex) { Debug.WriteLine($"Reading notifications failed: {ex}"); }
        finally { _polling = false; }
    }

    async Task<IslandNotification> Read(UserNotification n)
    {
        var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements().Select(t => Readable(t.Text)).Where(t => t.Length > 0).ToList() ?? [];
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
        var note = new IslandNotification(n.AppInfo?.DisplayInfo?.DisplayName ?? "", texts.FirstOrDefault() ?? "", string.Join("\n", texts.Skip(1)), icon, appId, accent, DateTime.Now);
        if (NotificationView.KindOf(note) == NoteKind.Screenshot) note = note with { Thumb = await ClipboardImage() };
        return note;
    }

    /// <summary>
    /// Some apps (Clock) put unresolved resource keys in their toasts, e.g. "ms-resource:TimerDoneText", which only the app
    /// itself can translate. Turn the key into words: "Timer done". Ordinary text passes through.
    /// </summary>
    internal static string Readable(string? text)
    {
        text = text?.Trim() ?? "";
        if (!text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return text;
        string key = text["ms-resource:".Length..].Split('/', '\\').Last();
        key = System.Text.RegularExpressions.Regex.Replace(key, "(Text|String|Label|Title|Message)$", "");
        var words = System.Text.RegularExpressions.Regex.Replace(key, "(?<=[a-z0-9])(?=[A-Z])|_", " ").Trim().ToLowerInvariant();
        return words.Length == 0 ? "" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>The image on the clipboard (Win+Shift+S puts the snip there), scaled down, or null.</summary>
    async Task<CanvasBitmap?> ClipboardImage()
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)) return null;
            using var stream = await (await content.GetBitmapAsync()).OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            double scale = Math.Min(1, 640.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            using var pixels = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale) },
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            return CanvasBitmap.CreateFromSoftwareBitmap(device, pixels);
        }
        catch (Exception ex) { Debug.WriteLine($"Clipboard image unavailable: {ex.Message}"); return null; }
    }
    readonly Dictionary<string, Windows.UI.Color> _accents = [];

    // ---- Screenshots saved without a toast (Win+PrtScn, PrtScn with auto-save) ----

    FileSystemWatcher? _shots; // field: a collected watcher stops raising events
    DateTime _lastShot;

    /// <summary>One island per capture: Snipping Tool both toasts and saves a file, whichever arrives first wins.</summary>
    bool DuplicateShot(IslandNotification n)
    {
        if (NotificationView.KindOf(n) != NoteKind.Screenshot) return false;
        if ((DateTime.Now - _lastShot).TotalSeconds < 4) return true;
        _lastShot = DateTime.Now;
        return false;
    }

    /// <summary>Watches Pictures\Screenshots (the known folder, so OneDrive redirection is followed). Event-driven: nothing runs until a file lands.</summary>
    void WatchScreenshots(DispatcherQueue ui)
    {
        try
        {
            var id = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"); // FOLDERID_Screenshots
            if (Native.SHGetKnownFolderPath(ref id, 0, 0, out nint ptr) != 0) return;
            string dir = System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr) ?? "";
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptr);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _shots = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
            _shots.Created += async (_, e) =>
            {
                if (!Path.GetExtension(e.FullPath).Equals(".png", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(e.FullPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase)) return;
                await Task.Delay(1500); // let a Snipping Tool toast (polled every second) claim it first, and the file finish writing
                ui.TryEnqueue(async () =>
                {
                    try
                    {
                        var note = new IslandNotification("Screenshot", "Screenshot saved", $"Saved to {Path.GetFileName(dir)}", null, "Microsoft.ScreenSketch_8wekyb3d8bbwe!App", null, DateTime.Now, await FileImage(e.FullPath), e.FullPath);
                        if (!DuplicateShot(note)) Pending.Enqueue(note);
                    }
                    catch (Exception ex) { Crash.Log("ScreenshotFile", ex); }
                });
            };
        }
        catch (Exception ex) { Debug.WriteLine($"Screenshot folder watch unavailable: {ex.Message}"); }
    }

    /// <summary>The saved capture, scaled down like the clipboard snip, or null.</summary>
    async Task<CanvasBitmap?> FileImage(string path)
    {
        for (int i = 0; i < 4; i++) // the writer may still hold the file
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                using var mem = new MemoryStream(bytes);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(mem.AsRandomAccessStream());
                double scale = Math.Min(1, 640.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                using var pixels = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale) },
                    Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
                return CanvasBitmap.CreateFromSoftwareBitmap(device, pixels);
            }
            catch (IOException) { await Task.Delay(300); }
            catch (Exception ex) { Debug.WriteLine($"Screenshot file unreadable: {ex.Message}"); return null; }
        return null;
    }

    /// <summary>
    /// Brings the notifying app forward. Windows doesn't let a listener trigger the toast's own click, so: its open window
    /// (matched by app id or app name), else launch it by app id if Windows knows that id, else re-run its running exe
    /// (single-instance apps like G HUB then show their window). Never a bare explorer.exe.
    /// </summary>
    public static void Open(IslandNotification n) => Task.Run(() =>
    {
        try
        {
            if (n.File is { } file && File.Exists(file)) // a saved screenshot: open the image itself
            {
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                return;
            }
            if (FindWindow(n) is var w && w != 0)
            {
                if (Native.IsIconic(w)) Native.ShowWindow(w, 9 /* SW_RESTORE */);
                Native.SetForegroundWindow(w);
                return;
            }
            if (n.AppId.Length > 0 && IsInAppsFolder(n.AppId))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{n.AppId}") { UseShellExecute = true });
                return;
            }
            if (RunningExe(n) is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { Debug.WriteLine($"Couldn't open {n.App}: {ex.Message}"); }
    });

    static bool Mine(Process p, IslandNotification n)
    {
        if (n.AppId.Length > 0 && MediaSource.AppIdOf(p) == n.AppId) return true;
        try
        {
            var info = p.MainModule?.FileVersionInfo;
            string app = n.App.Trim();
            return app.Length > 0 && (string.Equals(info?.FileDescription?.Trim(), app, StringComparison.OrdinalIgnoreCase)
                || string.Equals(info?.ProductName?.Trim(), app, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.ProcessName, app.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; } // elevated or system process
    }

    static nint FindWindow(IslandNotification n)
    {
        nint found = 0;
        var seen = new Dictionary<uint, bool>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindowTextLength(hwnd) == 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!seen.TryGetValue(pid, out bool mine))
            {
                try { using var p = Process.GetProcessById((int)pid); mine = Mine(p, n); } catch { mine = false; }
                seen[pid] = mine;
            }
            if (mine || n.AppId.Length > 0 && Native.WindowAppId(hwnd) == n.AppId) { found = hwnd; return false; } // Store apps live in an ApplicationFrameHost frame
            return true;
        }, 0);
        return found;
    }

    static string? RunningExe(IslandNotification n)
    {
        foreach (var p in Process.GetProcesses())
            using (p)
                try { if (Mine(p, n)) return p.MainModule?.FileName; } catch { }
        return null;
    }

    /// <summary>True when shell:AppsFolder has an entry for this app id (otherwise explorer would just open a folder).</summary>
    static bool IsInAppsFolder(string appId)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            var folder = shell.NameSpace("shell:AppsFolder");
            return folder?.ParseName(appId) != null;
        }
        catch { return false; }
    }
}

