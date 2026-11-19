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
    double _changedAt = double.NegativeInfinity;
    /// <summary>0 → 1 over the song-change slide; 1 when no change is in progress.</summary>
    public float TransitionT => (float)Math.Clamp((Environment.TickCount64 / 1000.0 - _changedAt) / 0.4, 0, 1);

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
            // Bring a running instance forward ourselves: launching a running packaged app via explorer doesn't pass the foreground lock.
            var window = Process.GetProcesses().Where(p => p.MainWindowHandle != 0 && (AppIdOf(p) == id || id.Equals(p.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.MainWindowHandle).FirstOrDefault();
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
                if (p.Thumbnail is not null)
                {
                    using var stream = await p.Thumbnail.OpenReadAsync();
                    art = await CanvasBitmap.LoadAsync(device, stream);
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Couldn't read media properties: {ex}"); } // some apps publish no art or odd formats
        if (session != _session) { art?.Dispose(); return; } // a newer session took over while we awaited

        bool newSong = title != Title || artist != Artist;
        if (newSong && Title.Length > 0)
        {
            PrevArt?.Dispose();
            (PrevTitle, PrevArtist, PrevArt, PrevAccent) = (Title, Artist, Art, Accent);
            _changedAt = Environment.TickCount64 / 1000.0;
        }
        else Art?.Dispose();
        (Title, Artist, Art) = (title, artist, art);
        Accent = art is null ? Microsoft.UI.Colors.White : AccentOf(art);
        Changed?.Invoke();
        if (newSong) await LoadLyrics(session, title, artist);
    }

    /// <summary>A packaged app's AppUserModelId ("Family!App"), or null for plain desktop processes and ones we can't open.</summary>
    static string? AppIdOf(Process p)
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
    Windows.UI.Color AccentOf(CanvasBitmap art)
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
