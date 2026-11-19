using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System.Power;
using WinRT.Interop;

namespace DynamicIsland;

/// <summary>
/// A transparent, always-on-top strip across the top of the primary monitor.
/// The island is drawn inside it; a window region keeps clicks outside the island passing through.
/// </summary>
public sealed class IslandWindow : Window
{
    enum Mode { Idle, Hover, Compact, Hud, Notify, Expanded }

    const float StripDip = 220, CanvasDip = 640; // canvas is island-sized, not full-width: each redraw costs ~1/4 as much
    static readonly string PosFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "position.txt");

    readonly nint _hwnd;
    readonly Grid _root;
    readonly CanvasControl _canvas;
    readonly TranslateTransform _shift = new();
    float _canvasLeftPx;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly MediaSource _media;
    readonly NotificationSource _notes = new(CanvasDevice.GetSharedDevice());
    IslandNotification? _note;
    double _noteUntil, _hudUntil;
    bool _pinned; // pill shown by a click while nothing plays
    bool _hiddenForFullscreen;
    double _lastFullscreenCheck;
    HudEvent? _hud;
    DispatcherQueueTimer _slowTick;
    bool _fast;
    SystemHud? _system; // field so the audio/WMI listeners aren't garbage-collected
    ScreenCapture? _capture;

    nint _monitor;
    int _monLeft, _monTop, _monW, _monH, _stripH;
    FlowWindow? _flow;
    bool _wasPlaying;
    double _pulseAt = -10, _stoppedAt = -10;
    float _s = 1; // physical px per DIP; all island math is in physical px

    Mode _mode = Mode.Idle;
    Spring _x, _w, _h, _top, _stack; // _stack: top edge of the media pill parked under a notification (0 = tucked away)
    double _lastTick, _lastCapture;
    bool _reopen, _dirty = true, _pressed, _dragging, _wasAnimating;
    float _pressX, _pressY, _grabOffset;
    (int, int, int, int) _region = (int.MaxValue, 0, 0, 0);

    public IslandWindow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop();

        _canvas = new CanvasControl { ClearColor = Colors.Transparent, Width = CanvasDip, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _shift };
        // Re-invalidate from Draw while animating: invalidating only from CompositionTarget.Rendering lands every other frame (30 fps on 60 Hz).
        _canvas.Draw += (_, e) => { Draw(e.DrawingSession); if (_wasAnimating) _canvas.Invalidate(); };
        _root = new Grid { Background = new SolidColorBrush(Colors.Transparent), Children = { _canvas } };
        _root.PointerPressed += OnPressed;
        _root.PointerMoved += OnMoved;
        _root.PointerReleased += OnReleased;
        _root.PointerCaptureLost += (_, _) => EndPress(click: false);
        _root.RightTapped += (_, _) => OnRightClick(); // back one stage; quit from Task Manager
        Content = _root;

        SetupWindow();
        PlaceOnPrimaryMonitor();

        _media = new MediaSource(CanvasDevice.GetSharedDevice(), DispatcherQueue);
        _media.Changed += () =>
        {
            if (_mode == Mode.Expanded) SetMode(Mode.Expanded); // card size depends on whether anything is playing
            double now = _clock.Elapsed.TotalSeconds;
            // Only a real start: not music already playing at launch, and not the brief pause apps make when skipping songs.
            if (_media.Playing && !_wasPlaying && now > 3 && now - _stoppedAt > 3 && !_hiddenForFullscreen) FlowIn();
            if (!_media.Playing && _wasPlaying) _stoppedAt = now;
            _wasPlaying = _media.Playing;
            _dirty = true;
        };
        _media.Start();
        _notes.Start(DispatcherQueue);
        var hud = _system = new SystemHud(DispatcherQueue);
        hud.Changed += e =>
        {
            if (_mode == Mode.Notify || _mode == Mode.Expanded && e.Text.Length == 0) return; // level bars don't cover a card; battery/Bluetooth/Wi-Fi notices do
            if (_mode == Mode.Expanded) _reopen = true; // the card stays open now, so come back to it after the notice
            (_hud, _hudUntil) = (e, _clock.Elapsed.TotalSeconds + e.Seconds);
            SetMode(Mode.Hud); // re-run: a notice is wider than a level bar
            _dirty = true;
        };
        hud.Start();

        _x = new Spring(LoadPosition() * _monW, 520, 34); // stiff-ish follow: lags a little behind the cursor while dragging
        _w = new Spring(0, 380, 36);                       // same near-critical damping as height: a bouncing width reads as wobble
        _h = new Spring(0, 380, 36);                       // height barely overshoots: a bouncing bottom edge clips the card's buttons
        _top = new Spring(0, 380, 36);
        _stack = new Spring(0, 380, 36);
        SetMode(Mode.Idle);
        UpdateRegion();

        try { _capture = new ScreenCapture(CanvasDevice.GetSharedDevice(), _monitor, _stripH + 64); } // + room for the lens pad below the island
        catch (Exception ex) { Debug.WriteLine($"Screen capture unavailable, using solid glass: {ex}"); }

        // Battery: a slow 15 Hz tick when nothing moves; per-frame ticks only while animating (see SetFast).
        _slowTick = DispatcherQueue.CreateTimer();
        _slowTick.Interval = TimeSpan.FromMilliseconds(66);
        _slowTick.Tick += (_, _) => OnTick(null, this);
        _slowTick.Start();
        Closed += (_, _) => { _slowTick.Stop(); SetFast(false); _capture?.Dispose(); };
    }

    /// <summary>Media just started: light flows from the app's taskbar button into the island, which glows as it lands.</summary>
    async void FlowIn()
    {
        var (cx, top, _, h) = Shape();
        var to = new System.Numerics.Vector2(_monLeft + cx, _monTop + top + h / 2);
        var from = await Task.Run(() => FlowWindow.TaskbarButton(_media.AppName())) // UI Automation into the taskbar: keep it off the UI thread
            ?? new System.Numerics.Vector2(_monLeft + _monW / 2f, _monTop + _monH - 24 * _s);
        if (_flow is null)
        {
            _flow = new FlowWindow();
            _flow.Arrived += () => { _pulseAt = _clock.Elapsed.TotalSeconds; _w.Velocity += 500 * _s; _dirty = true; }; // a little gulp + glow
        }
        var a = _media.Accent;
        var color = Microsoft.UI.ColorHelper.FromArgb(255, (byte)((255 + a.R) / 2), (byte)((255 + a.G) / 2), (byte)((255 + a.B) / 2));
        _flow.Play(new RectInt32(_monLeft, _monTop, _monW, _monH), _s, from, to, color);
    }

    void SetupWindow()
    {
        var p = OverlappedPresenter.Create();
        p.SetBorderAndTitleBar(false, false);
        p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
        p.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;

        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW);

        int noRound = 1, noBorder = unchecked((int)0xFFFFFFFE);
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, 4);
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref noBorder, 4);
        Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    // shortcut: primary monitor only, fixed at launch; add per-monitor dragging and DPI-change handling later.
    void PlaceOnPrimaryMonitor()
    {
        _monitor = Native.MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(_monitor, ref mi);
        (_monLeft, _monTop, _monW, _monH) = (mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);

        AppWindow.Move(new PointInt32(_monLeft, _monTop)); // move first so we read this monitor's DPI
        _s = Native.GetDpiForWindow(_hwnd) / 96f;
        _stripH = (int)(StripDip * _s);
        AppWindow.MoveAndResize(new RectInt32(_monLeft, _monTop, _monW, _stripH));
    }

    void SetMode(Mode mode)
    {
        _mode = mode;
        (_w.Target, _h.Target, _top.Target) = mode switch
        {
            Mode.Idle => (84 * _s, 8 * _s, 0f),     // idle and hover melt into the screen edge (see Glass.Attached)
            Mode.Hover => (124 * _s, 16 * _s, 0f),
            Mode.Compact => (210 * _s, 32 * _s, 6 * _s), // everything else drops out of the bar as a floating pill with its full glass rim
            Mode.Notify => (360 * _s, 72 * _s, 8 * _s),
            Mode.Hud => ((_hud?.Text.Length > 0 ? 300 : 240) * _s, 32 * _s, 6 * _s), // notices are wider than level bars
            _ when _media.HasSession => (400 * _s, 150 * _s, 10 * _s),
            _ => (340 * _s, 96 * _s, 10 * _s),
        };
        ClampX();
        _dirty = true;
    }

    void ClampX()
    {
        float half = MathF.Max(_w.Target, _w.Value) / 2 + 8 * _s;
        _x.Target = Math.Clamp(_x.Target, half, _monW - half);
    }

    /// <summary>The visible shape, including the liquid stretch from horizontal speed.</summary>
    (float cx, float top, float w, float h) Shape()
    {
        if (_mode is Mode.Expanded or Mode.Notify) return (_x.Value, _top.Value, MathF.Max(_w.Value, 1), MathF.Max(_h.Value, 1)); // cards keep their size while moving
        float speed = MathF.Abs(_x.Velocity);
        float stretch = MathF.Min(speed * 0.045f, 90 * _s);
        float squash = 1 - MathF.Min(speed / 7000f, 0.35f);
        return (_x.Value, _top.Value, MathF.Max(_w.Value + stretch, 1), MathF.Max(_h.Value * squash, 1));
    }

    void OnTick(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(now - _lastTick, 1 / 30.0);
        _lastTick = now;

        if (now - _lastFullscreenCheck > 1) // hide during fullscreen games, videos and presentations; skip all work while hidden
        {
            _lastFullscreenCheck = now;
            bool fullscreen = Native.SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;
            if (fullscreen != _hiddenForFullscreen)
            {
                _hiddenForFullscreen = fullscreen;
                Native.ShowWindow(_hwnd, fullscreen ? 0 /* SW_HIDE */ : 4 /* SW_SHOWNOACTIVATE */);
            }
        }
        if (_hiddenForFullscreen) return;

        if ((Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) == 0) // the shell can drop us out of the topmost band; then clicks hit the window below
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        UpdateHover(now);
        // Stacking: while a notification or notice is up, the playing media drops below it as its own pill, then pushes back up.
        _stack.Target = _media.Playing && _mode is Mode.Notify or Mode.Hud ? _top.Target + _h.Target + 8 * _s : 0;
        bool animating = !_stack.Settled || _pressed || now - _pulseAt < 0.7 || _media.TransitionT < 1 || _media.ArtFadeT < 1 || !(_x.Settled && _w.Settled && _h.Settled && _top.Settled);
        SetFast(animating);

        // Background refraction only refreshes at the slow tick rate when idle, and freezes in battery saver.
        // Each background refresh costs a full redraw; the idle bar is thin, so 4 Hz is enough there.
        bool captureDue = animating || (_mode != Mode.Idle || now - _lastCapture > 0.25) && PowerManager.EnergySaverStatus != EnergySaverStatus.On;
        if (captureDue && _capture?.TryUpdate() == true) { _dirty = true; _lastCapture = now; }
        bool playing = _media.Playing && (_mode is Mode.Compact or Mode.Expanded || _stack.Value > 2 * _s) || _mode == Mode.Expanded && MediaView.TitleScrolls;
        if (playing) _dirty = true; // waveform, progress bar and scrolling title move
        _slowTick.Interval = TimeSpan.FromMilliseconds(playing ? 33 : 66); // waveform needs ~30 fps to look alive

        if (animating)
        {
            _x.Step(dt); _w.Step(dt); _h.Step(dt); _top.Step(dt); _stack.Step(dt);
            _dirty = true;
        }
        else if (_wasAnimating) _dirty = true; // one last pass to shrink the click region
        _wasAnimating = animating;
        if (!_dirty) return;
        _dirty = false;
        FollowIsland();
        UpdateRegion(animating);
        _canvas.Invalidate();
    }

    /// <summary>Moves the canvas only when the island nears its edge, so it rarely moves mid-animation.</summary>
    void FollowIsland()
    {
        var (cx, _, w, _) = Shape();
        float canvasW = CanvasDip * _s, half = w / 2 + 40 * _s;
        if (cx - half >= _canvasLeftPx && cx + half <= _canvasLeftPx + canvasW) return;
        _canvasLeftPx = Math.Clamp(MathF.Round(cx - canvasW / 2), 0, MathF.Max(0, _monW - canvasW));
        _shift.X = _canvasLeftPx / _s;
    }

    void SetFast(bool on)
    {
        if (on == _fast) return;
        _fast = on;
        if (on) CompositionTarget.Rendering += OnTick;
        else CompositionTarget.Rendering -= OnTick;
    }

    void UpdateHover(double now)
    {
        if (_pressed) return;
        Native.GetCursorPos(out var p);
        float px = p.X - _monLeft, py = p.Y - _monTop;
        var (cx, top, w, h) = Shape();
        float m = 12 * _s;
        bool near = px >= cx - w / 2 - m && px <= cx + w / 2 + m && py <= top + h + m && py >= -m;

        if (!near && _mode != Mode.Expanded) _pinned = false;
        if (_mode == Mode.Expanded) { } // stays open until clicked
        else if (_mode == Mode.Notify)
        {
            if (near) _noteUntil = Math.Max(_noteUntil, now + 1); // hovering keeps it open
            else if (now > _noteUntil) ShowNextNote(now, near);
        }
        else if (_notes.Pending.Count > 0) ShowNextNote(now, near);
        else if (_mode == Mode.Hud) { if (now > _hudUntil) { SetMode(_reopen ? Mode.Expanded : RestMode(near)); _reopen = false; } }
        else if (_mode != RestMode(near)) SetMode(RestMode(near));
    }

    /// <summary>Drops the next queued notification down for 5 seconds, or returns to rest when there are none.</summary>
    void ShowNextNote(double now, bool near)
    {
        if (_notes.Pending.TryDequeue(out var next))
        {
            (_note, _noteUntil) = (next, now + 5);
            SetMode(Mode.Notify);
            _w.Velocity += 600 * _s; // arrival bounce
        }
        else
        {
            _note = null;
            SetMode(RestMode(near));
        }
    }

    /// <summary>What the island shows when not expanded: the pill after a click until the mouse leaves, else the slim bar (swollen on hover).</summary>
    Mode RestMode(bool near) => _pinned && _media.HasSession ? Mode.Compact : near ? Mode.Hover : Mode.Idle; // the pill only shows after a click; otherwise the slim bar, even while music plays

    /// <summary>Right click steps back one stage: card → pill → bar. Notices and pop-ups just close.</summary>
    void OnRightClick()
    {
        switch (_mode)
        {
            case Mode.Expanded: _pinned = true; SetMode(RestMode(near: true)); break;
            case Mode.Notify: _noteUntil = 0; break;
            case Mode.Hud: _hudUntil = 0; break;
            default: _pinned = false; SetMode(RestMode(near: true)); break;
        }
    }

    /// <summary>Only the island (plus a small margin) takes clicks; the rest of the strip passes them through.</summary>
    void UpdateRegion(bool animating = false)
    {
        var (cx, top, w, h) = Shape();
        if (animating) (w, h) = (MathF.Max(w, _w.Target * 1.2f), MathF.Max(h, _h.Target * 1.2f)); // cover the target + overshoot up front so the region grows once
        float pad = (_mode is Mode.Expanded or Mode.Notify ? 34 : _mode == Mode.Compact ? 26 : 12) * _s; // compact leaves room for the album glow
        static int Snap4(float v) => (int)MathF.Round(v / 4) * 4; // fewer region updates while animating
        float bottom = MathF.Max(top + h, MathF.Max(_stack.Value, _stack.Target) + PillH); // include the stacked media pill
        var region = (Math.Max(0, Snap4(cx - MathF.Max(w, PillW) / 2 - pad)), 0, Math.Min(_monW, Snap4(cx + MathF.Max(w, PillW) / 2 + pad)), Math.Min(_stripH, Snap4(bottom + pad)));
        if (animating) // only grow while moving: resizing the region every frame makes DWM stutter
            region = (Math.Min(region.Item1, _region.Item1), 0, Math.Max(region.Item3, _region.Item3), Math.Max(region.Item4, _region.Item4));
        if (region == _region) return;
        _region = region;
        Native.SetWindowRgn(_hwnd, Native.CreateRectRgn(region.Item1, region.Item2, region.Item3, region.Item4), true);
    }

    float PointerX(PointerRoutedEventArgs e) => (float)e.GetCurrentPoint(_root).Position.X * _s;
    float PointerY(PointerRoutedEventArgs e) => (float)e.GetCurrentPoint(_root).Position.Y * _s;

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _dragging = false;
        _pressX = PointerX(e);
        _pressY = PointerY(e);
        _grabOffset = _pressX - _x.Value;
        _w.Velocity += 250 * _s; // little squish so the press feels physical
        _root.CapturePointer(e.Pointer);
        SetFast(true);
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        float x = PointerX(e);
        if (!_dragging && MathF.Abs(x - _pressX) > (_mode == Mode.Expanded ? 24 : 5) * _s) // the open card needs a deliberate drag so wobbly clicks still count as clicks
        {
            _dragging = true;
            if (_mode == Mode.Idle) SetMode(Mode.Hover);
        }
        if (!_dragging) return;
        _x.Target = x - _grabOffset;
        ClampX();
        _dirty = true;
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        EndPress(click: true);
        _root.ReleasePointerCapture(e.Pointer);
    }

    void EndPress(bool click)
    {
        if (!_pressed) return;
        _pressed = false;
        if (_dragging)
        {
            _dragging = false;
            _x.Target += _x.Velocity * 0.06f; // carry a bit of fling momentum
            SnapX();
            SavePosition();
        }
        else if (click) OnClick();
    }

    void OnClick()
    {
        if (_stack.Value > 2 * _s && _pressY > _stack.Value) { _media.OpenApp(); return; } // the stacked media pill
        if (_mode == Mode.Notify && _note is not null)
        {
            NotificationSource.Open(_note);
            _noteUntil = 0; // closes on the next tick
            return;
        }
        if (_mode == Mode.Expanded && _media.HasSession)
        {
            var (cx, top, w, h) = Shape();
            switch (MediaView.HitButton(new Windows.Foundation.Rect(cx - w / 2, top, w, h), _s, new System.Numerics.Vector2(_pressX, _pressY)))
            {
                case 0: _media.Previous(); return;
                case 1: _media.TogglePlay(); return;
                case 2: _media.Next(); return;
            }
        }

        if (_mode == Mode.Hud && _hud?.Settings is { } page) // battery / Bluetooth / Wi-Fi notices open their Settings page
        {
            try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine($"Couldn't open {page}: {ex.Message}"); }
            _hudUntil = 0;
            return;
        }

        // Click ladder: bar → pill → card → open the playing app.
        switch (_mode)
        {
            case Mode.Idle or Mode.Hover when _media.HasSession:
                _pinned = true; // show the pill even if paused, until the mouse leaves
                SetMode(Mode.Compact);
                break;
            case Mode.Expanded:
                if (_media.HasSession) _media.OpenApp();
                _pinned = false;
                SetMode(RestMode(near: true));
                break;
            default:
                SetMode(Mode.Expanded);
                break;
        }
    }

    void SnapX()
    {
        float best = _x.Target, bestDist = 140 * _s;
        foreach (float f in (float[])[0.15f, 0.5f, 0.85f])
        {
            float d = MathF.Abs(f * _monW - _x.Target);
            if (d < bestDist) (best, bestDist) = (f * _monW, d);
        }
        _x.Target = best;
        ClampX();
    }

    float PillW => 210 * _s;
    float PillH => 32 * _s;

    void Draw(CanvasDrawingSession ds)
    {
        var (cx, top, w, h) = Shape();
        if (_stack.Value > 2 * _s) // stacked media pill, drawn first so the notification covers it as it tucks back up
        {
            float pillAlpha = Math.Clamp(_stack.Value / (24 * _s), 0, 1);
            Glass.Draw(ds, _capture?.Image, _canvasLeftPx, cx, _stack.Value, PillW, PillH, _s, 0, _media.Accent, _media.Playing ? 1 : 0, 0,
                rect => MediaView.DrawCompact(ds, rect, _s, _media, _clock.Elapsed.TotalSeconds, pillAlpha));
        }
        float expand = Math.Clamp((_h.Value - 14 * _s) / (118 * _s), 0, 1);
        float compactAlpha = _mode == Mode.Compact ? Math.Clamp((_w.Value - 160 * _s) / (50 * _s), 0, 1) : 0;
        float cardAlpha = Math.Clamp((_h.Value / _h.Target - 0.75f) / 0.25f, 0, 1);
        float expandedAlpha = _mode == Mode.Expanded ? cardAlpha : 0;
        var note = _mode == Mode.Notify ? _note : null;
        var hudEvent = _mode == Mode.Hud ? _hud : null;
        float hudAlpha = Math.Clamp((_w.Value - 180 * _s) / (60 * _s), 0, 1);
        // Album glow: breathes slowly while music plays, blends to the new song's colour during the slide.
        float t = _media.TransitionT;
        var a = _media.PrevAccent; var b = _media.Accent;
        var glow = Microsoft.UI.ColorHelper.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        float glowStrength = _media.Playing && _mode is Mode.Compact or Mode.Expanded ? 1 : 0; // steady and faint: a pulse read as gimmicky
        glowStrength = MathF.Max(glowStrength, 2f * MathF.Max(0, 1 - (float)(_clock.Elapsed.TotalSeconds - _pulseAt) / 0.7f)); // brief light as the flow lands
        float sag = Math.Clamp(_h.Velocity * 0.012f, 0, 20 * _s); // melting: the bottom drips down while the island grows
        Glass.Draw(ds, _capture?.Image, _canvasLeftPx, cx, top, w, h, _s, expand, glow, glowStrength, sag, rect =>
        {
            if (compactAlpha > 0) MediaView.DrawCompact(ds, rect, _s, _media, _clock.Elapsed.TotalSeconds, compactAlpha);
            if (expandedAlpha > 0) MediaView.DrawExpanded(ds, rect, _s, _media, _clock.Elapsed.TotalSeconds, expandedAlpha);
            if (note is not null && cardAlpha > 0) NotificationView.Draw(ds, rect, _s, note, cardAlpha);
            if (hudEvent is not null && hudAlpha > 0) HudView.Draw(ds, rect, _s, hudEvent, hudAlpha);
        });
    }

    static float LoadPosition()
    {
        try { return Math.Clamp(float.Parse(File.ReadAllText(PosFile), CultureInfo.InvariantCulture), 0, 1); }
        catch { return 0.5f; }
    }

    void SavePosition()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PosFile)!);
            File.WriteAllText(PosFile, (_x.Target / _monW).ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException) { } // losing the remembered position is harmless
    }
}
