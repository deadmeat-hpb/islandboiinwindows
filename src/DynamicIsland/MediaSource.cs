using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Windows.Media.Control;

namespace DynamicIsland;

/// <summary>
/// Now-playing info from whatever app Windows' media controls show (Spotify, browsers, Media Player...).
/// Windows raises its events on background threads; everything here is re-posted to the UI thread.
/// </summary>
sealed class MediaSource(CanvasDevice device, DispatcherQueue ui)
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    TimeSpan _position, _end;
    DateTimeOffset _positionAt;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public CanvasBitmap? Art { get; private set; }

    // The song before the current one, kept briefly so the views can slide it out.
    public string PrevTitle { get; private set; } = "";
    public string PrevArtist { get; private set; } = "";
    public CanvasBitmap? PrevArt { get; private set; }
    double _changedAt = double.NegativeInfinity, _artAt = double.NegativeInfinity;
    static double Now => Environment.TickCount64 / 1000.0;
    /// <summary>0 → 1 while late-arriving art for the same song fades in.</summary>
    public float ArtFadeT => (float)Math.Clamp((Now - _artAt) / 0.25, 0, 1);
    /// <summary>0 → 1 over the song-change slide; 1 when no change is in progress.</summary>
    public float TransitionT => (float)Math.Clamp((Now - _changedAt) / 0.4, 0, 1);

    /// <summary>Glow colour picked from the album art; previous song's for blending during the slide.</summary>
    public Windows.UI.Color Accent { get; private set; } = Microsoft.UI.Colors.White;
    public Windows.UI.Color PrevAccent { get; private set; } = Microsoft.UI.Colors.White;
    public bool Playing { get; private set; }
    public bool HasSession => _session is not null;
    public event Action? Changed;

    List<(TimeSpan At, string Text)> _lyrics = [];
    static readonly HttpClient Http = new() { DefaultRequestHeaders = { { "User-Agent", "DynamicIsland/0.1" } }, Timeout = TimeSpan.FromSeconds(8) };
    sealed record LrcResult([property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);

    /// <summary>The synced lyric line for the current position, or "" when there are none.</summary>
    public string CurrentLyric
    {
        get
        {
            var pos = Position;
            string line = "";
            foreach (var (at, text) in _lyrics) { if (at > pos) break; line = text; }
            return line;
        }
    }

    /// <summary>The synced lyric lines of the current song (empty when there are none).</summary>
    public IReadOnlyList<(TimeSpan At, string Text)> Lyrics => _lyrics;

    /// <summary>Index into <see cref="Lyrics"/> of the line for the current position, -1 before the first.</summary>
    public int LyricIndex
    {
        get
        {
            var pos = Position;
            int i = -1;
            for (int k = 0; k < _lyrics.Count && _lyrics[k].At <= pos; k++) i = k;
            return i;
        }
    }

    public double Progress
    {
        get
        {
            if (_end <= TimeSpan.Zero) return 0;
            return Math.Clamp(Position / _end, 0, 1);
        }
    }

    TimeSpan Position => _position + (Playing ? DateTimeOffset.Now - _positionAt : TimeSpan.Zero);

    public async void Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Post(Attach);
            Attach();
        }
        catch (Exception ex) { Debug.WriteLine($"Media controls unavailable: {ex}"); }
    }

    public void TogglePlay() => _ = _session?.TryTogglePlayPauseAsync();
    public void Next() => _ = _session?.TrySkipNextAsync();
    public void Previous() => _ = _session?.TrySkipPreviousAsync();

    /// <summary>Brings the playing app forward: its window if it's a running desktop app, else launches it by app id.</summary>
    public void OpenApp()
    {
        string? id = _session?.SourceAppUserModelId;
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            // Bring the existing window forward instead of launching a new one. Browsers have many windows, so prefer the one
            // whose title shows the song/video (a YouTube tab puts the video title in the window title).
            var window = FindWindow(id, Title);
            if (window != 0)
            {
                if (Native.IsIconic(window)) Native.ShowWindow(window, 9 /* SW_RESTORE */); // restoring a non-minimized window would un-maximize it
                Native.SetForegroundWindow(window);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{id}") { UseShellExecute = true });
        }
        catch (Exception ex) { Debug.WriteLine($"Couldn't open {id}: {ex.Message}"); }
    }

    /// <summary>The playing app's window (the one <see cref="OpenApp"/> brings forward), or 0. Enumerates windows: call off the UI thread.</summary>
    public nint Window(string title) => _session?.SourceAppUserModelId is { Length: > 0 } id ? FindWindow(id, title) : 0;

    /// <summary>True when the playing app accepts seeking.</summary>
    public bool CanSeek => _end > TimeSpan.Zero && _session?.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled == true;

    /// <summary>Jumps to <paramref name="fraction"/> of the track.</summary>
    public void Seek(double fraction)
    {
        if (!CanSeek) return;
        var target = TimeSpan.FromTicks((long)(_end.Ticks * Math.Clamp(fraction, 0, 1)));
        _ = _session!.TryChangePlaybackPositionAsync(target.Ticks);
        (_position, _positionAt) = (target, DateTimeOffset.Now); // show it right away; the app's timeline update follows
    }

    void Post(Action a) => ui.TryEnqueue(() => a());

    void Attach()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnProps;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        _session = _manager?.GetCurrentSession();
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnProps;
            _session.PlaybackInfoChanged += OnPlayback;
            _session.TimelinePropertiesChanged += OnTimeline;
        }
        RefreshPlayback();
        RefreshTimeline();
        RefreshProps();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Post(RefreshProps);
    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Post(RefreshPlayback);
    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => Post(RefreshTimeline);

    void RefreshPlayback()
    {
        Playing = _session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        Changed?.Invoke();
    }

    void RefreshTimeline()
    {
        var t = _session?.GetTimelineProperties();
        (_position, _end, _positionAt) = t is null ? (TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.Now) : (t.Position, t.EndTime, t.LastUpdatedTime);
        Changed?.Invoke();
    }

    async void RefreshProps()
    {
        var session = _session;
        string title = "", artist = "";
        CanvasBitmap? art = null;
        try
        {
            if (session is not null)
            {
                var p = await session.TryGetMediaPropertiesAsync();
                (title, artist) = (p.Title, p.Artist);
                if (p.Thumbnail is not null) art = await LoadArt(p.Thumbnail);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Couldn't read media properties: {ex}"); } // some apps publish no art or odd formats
        if (session != _session) { art?.Dispose(); return; } // a newer session took over while we awaited

        bool newSong = title != Title || artist != Artist;
        if (!newSong && Art is null && art is not null) _artAt = Now; // art arrived after the title: fade it in instead of popping
        if (newSong && Title.Length > 0)
        {
            PrevArt?.Dispose();
            (PrevTitle, PrevArtist, PrevArt, PrevAccent) = (Title, Artist, Art, Accent);
            _changedAt = Now;
        }
        else Art?.Dispose();
        (Title, Artist, Art) = (title, artist, art);
        Accent = art is null ? Microsoft.UI.Colors.White : AccentOf(art);
        Changed?.Invoke();
        if (newSong) await LoadLyrics(session, title, artist);
    }

    /// <summary>
    /// Decodes cover art on the CPU (Windows Imaging, off the UI thread), scaled to at most 256 px, then uploads it.
    /// TIDAL sends a second, large cover per track; decoding that through the shared Win2D device stalled the slide for up to 1.3 s.
    /// </summary>
    async Task<CanvasBitmap> LoadArt(Windows.Storage.Streams.IRandomAccessStreamReference thumbnail)
    {
        using var stream = await thumbnail.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        double scale = Math.Min(1, 256.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
        };
        using var pixels = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, transform,
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        return CanvasBitmap.CreateFromSoftwareBitmap(device, pixels);
    }

    /// <summary>A packaged app's AppUserModelId ("Family!App"), or null for plain desktop processes and ones we can't open.</summary>
    /// <summary>The playing app's display name ("Google Chrome", "TIDAL"), as its taskbar button is labelled. Slow-ish: call off the UI thread.</summary>
    public string? AppName()
    {
        string? id = _session?.SourceAppUserModelId;
        if (string.IsNullOrEmpty(id)) return null;
        var window = FindWindow(id, Title);
        if (window == 0) return null;
        Native.GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? name = p.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(name) ? p.ProcessName : name;
        }
        catch { return null; }
    }

    /// <summary>The playing app's visible window: one titled with the media first, then any window of that app.</summary>
    static nint FindWindow(string id, string title)
    {
        string name = Path.GetFileNameWithoutExtension(id.Split('!')[0]); // "chrome.exe" → "chrome"; "Chrome", "MSEdge", "Spotify.exe" work too
        var appIds = new Dictionary<uint, bool>();
        nint titled = 0, titledAny = 0, any = 0;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindowTextLength(hwnd) == 0) return true;
            var sb = new System.Text.StringBuilder(512);
            Native.GetWindowText(hwnd, sb, sb.Capacity);
            bool hasTitle = title.Length > 2 && sb.ToString().Contains(title, StringComparison.OrdinalIgnoreCase);
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!appIds.TryGetValue(pid, out bool mine))
            {
                try { using var p = Process.GetProcessById((int)pid); mine = p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) || AppIdOf(p) == id; }
                catch { mine = false; }
                appIds[pid] = mine;
            }
            if (!mine) mine = Native.WindowAppId(hwnd) == id; // Store apps (Media Player, ...): the ApplicationFrameHost frame names its app
            if (mine && hasTitle) { titled = hwnd; return false; }
            if (hasTitle && titledAny == 0) titledAny = hwnd;
            if (mine && any == 0) any = hwnd; // windows enumerate front to back, so this is the app's most recent window
            return true;
        }, 0);
        return titled != 0 ? titled : any != 0 ? any : titledAny;
    }

    internal static string? AppIdOf(Process p)
    {
        try
        {
            uint len = 256;
            var sb = new System.Text.StringBuilder((int)len);
            return Native.GetApplicationUserModelId(p.Handle, ref len, sb) == 0 ? sb.ToString() : null;
        }
        catch { return null; } // access denied (elevated or system processes)
    }

    /// <summary>The most vivid colour in the art (scaled to 8×8), brightened, so the glow reads on dark and light screens.</summary>
    Windows.UI.Color AccentOf(CanvasBitmap art) => AccentOf(device, art);

    /// <summary>The image's most vibrant colour, softened toward grey (album glow, notification app tint).</summary>
    internal static Windows.UI.Color AccentOf(CanvasDevice device, CanvasBitmap art)
    {
        using var small = new CanvasRenderTarget(device, 8, 8, 96);
        using (var ds = small.CreateDrawingSession()) ds.DrawImage(art, new Windows.Foundation.Rect(0, 0, 8, 8), art.Bounds);
        var best = Microsoft.UI.Colors.White;
        float bestScore = -1;
        foreach (var c in small.GetPixelColors())
        {
            float max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255f, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255f;
            float score = max <= 0 ? 0 : (max - min) / max * max; // saturated and not too dark
            if (score > bestScore) (best, bestScore) = (c, score);
        }
        // Mix 40% toward light grey so the glow stays a tint, not a neon.
        static byte Soft(byte v) => (byte)(v * 0.6f + 200 * 0.4f);
        return Microsoft.UI.ColorHelper.FromArgb(255, Soft(best.R), Soft(best.G), Soft(best.B));
    }

    /// <summary>Time-synced lyrics from LRCLIB (free, no key). Sends only the title and artist.</summary>
    async Task LoadLyrics(GlobalSystemMediaTransportControlsSession? session, string title, string artist)
    {
        _lyrics = [];
        if (title.Length == 0) return;
        try
        {
            var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            var results = await Http.GetFromJsonAsync<LrcResult[]>(url) ?? [];
            var lrc = results.FirstOrDefault(r => !string.IsNullOrEmpty(r.SyncedLyrics))?.SyncedLyrics;
            if (lrc is null || session != _session || title != Title) return; // no synced lyrics, or the song changed meanwhile
            _lyrics = ParseLrc(lrc);
        }
        catch (Exception ex) { Debug.WriteLine($"Lyrics unavailable: {ex.Message}"); } // offline or service down: just no lyrics
    }

    /// <summary>"[01:23.45] line" → (1:23.45, "line"), sorted by time.</summary>
    internal static List<(TimeSpan, string)> ParseLrc(string lrc) =>
        lrc.Split('\n')
           .Select(l => Regex.Match(l.Trim(), @"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$"))
           .Where(m => m.Success)
           .Select(m => (TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) + TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)), m.Groups[3].Value.Trim()))
           .OrderBy(x => x.Item1)
           .ToList();

    [Conditional("DEBUG")]
    public static void SelfCheck()
    {
        var l = ParseLrc("[ti:x]\n[00:01.50] one\n[01:02.00]two\n");
        Debug.Assert(l.Count == 2 && l[0].Item1 == TimeSpan.FromSeconds(1.5) && l[1].Item2 == "two", "LRC parsing");
    }
}
