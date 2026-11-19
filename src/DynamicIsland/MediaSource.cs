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

        Art?.Dispose();
        bool newSong = title != Title || artist != Artist;
        (Title, Artist, Art) = (title, artist, art);
        Changed?.Invoke();
        if (newSong) await LoadLyrics(session, title, artist);
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
