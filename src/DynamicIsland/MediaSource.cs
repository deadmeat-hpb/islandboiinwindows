using System.Diagnostics;
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

    public double Progress
    {
        get
        {
            if (_end <= TimeSpan.Zero) return 0;
            var pos = _position + (Playing ? DateTimeOffset.Now - _positionAt : TimeSpan.Zero);
            return Math.Clamp(pos / _end, 0, 1);
        }
    }

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
        (Title, Artist, Art) = (title, artist, art);
        Changed?.Invoke();
    }
}
