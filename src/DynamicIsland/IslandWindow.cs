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

    const float StripDip = 300, CanvasDip = 640; // 300: room for the tallest card (screenshot) plus the media pill stacked under it. The canvas is island-sized, not full-width: each redraw costs ~1/4 as much
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
    bool _fullscreen;
    double _lastFullscreenCheck;
    HudEvent? _hud;
    DispatcherQueueTimer _slowTick;
    bool _fast;
    SystemHud? _system; // field so the audio/WMI listeners aren't garbage-collected
    ScreenCapture? _capture;

    nint _monitor;
    int _monLeft, _monTop, _monW, _monH, _stripH;
    FlowWindow? _flow;
    double _pulseAt = -10, _reachAt = -10, _rippleAt = -10, _artInAt = -10, _holdUntil; // minimize-flow arrival animation
    Native.WinEventProc? _minimizeProc; // field so the hook callback isn't garbage-collected
    float _s = 1; // island size scale in physical px (DPI, but at least screen width / 1920); all island math is in physical px
    float _dpi = 1; // physical px per DIP, for converting to and from XAML coordinates

    Mode _mode = Mode.Idle;
    Spring _x, _w, _h, _top, _stack; // _stack: top edge of the media pill parked under a notification (0 = tucked away)
    double _lastTick, _lastCapture;
    bool _reopen, _dirty = true, _pressed, _dragging, _wasAnimating;
    float _pressX, _pressY, _grabOffset;
    (int, int, int, int) _region = (int.MaxValue, 0, 0, 0);

    public static IslandWindow? Current { get; private set; }
    public nint Hwnd => _hwnd;
    public WinUIEx.Messaging.WindowMessageMonitor Messages => _messages!;

    // Settings-aware reads: a switched-off music island behaves as if nothing plays; the glass falls back to the solid fill.
    bool HasMedia => Settings.Current.Music && _media.HasSession;
    bool MediaPlaying => Settings.Current.Music && _media.Playing;
    bool _useGlass = true, _visible = true;
    Microsoft.Graphics.Canvas.ICanvasImage? GlassImage => _useGlass ? _capture?.Image : null;

    public IslandWindow()
    {
        Current = this;
        _hwnd = WindowNative.GetWindowHandle(this);
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop();

        _canvas = new CanvasControl { ClearColor = Colors.Transparent, Width = CanvasDip, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _shift };
        // Re-invalidate from Draw while animating: invalidating only from CompositionTarget.Rendering lands every other frame (30 fps on 60 Hz).
        _canvas.Draw += (_, e) =>
        {
            try { Draw(e.DrawingSession); }
            catch (Exception ex) { Crash.Log("Draw", ex); } // a bad frame is skipped, never fatal
            if (_wasAnimating) _canvas.Invalidate();
        };
        _root = new Grid { Background = new SolidColorBrush(Colors.Transparent), Children = { _canvas } };
        _root.PointerPressed += OnPressed;
        _root.PointerMoved += OnMoved;
        _root.PointerReleased += OnReleased;
        _root.PointerCaptureLost += (_, _) => { if (MediaView.Scrub is not null || HudView.Drag is not null) { MediaView.Scrub = null; HudView.Drag = null; _dirty = true; } EndPress(click: false); };
        _root.RightTapped += (_, e) => OnRightClick((float)e.GetPosition(_root).Y * _dpi); // back one stage / dismiss; quit from Task Manager
        Content = _root;

        SetupWindow();
        PlaceOnPrimaryMonitor();

        _media = new MediaSource(CanvasDevice.GetSharedDevice(), DispatcherQueue);
        _media.Changed += () =>
        {
            if (_mode is Mode.Expanded or Mode.Idle or Mode.Hover) SetMode(_mode); // card size depends on whether anything is playing; the bar shows the clock when nothing is
            double now = _clock.Elapsed.TotalSeconds;
            _dirty = true;
        };
        _media.Start();

        // The light flow plays when the window that's playing gets minimized: the music "drops" into the island.
        // Out-of-context WinEvent hook: Windows calls us only on minimize, no polling.
        _minimizeProc = async (_, _, hwnd, idObject, _, _, _) =>
        {
            try
            {
                if (idObject != 0 || !MediaPlaying || _fullscreen || !_visible || !Settings.Current.DropAnimation) return; // OBJID_WINDOW only
                if (_mode is not (Mode.Idle or Mode.Hover)) return; // already showing the pill or card: nothing to announce
                string title = _media.Title;
                if (await Task.Run(() => _media.Window(title)) == hwnd) FlowIn(); // same window the card's art click brings forward
            }
            catch (Exception ex) { Crash.Log("Minimize", ex); }
        };
        Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZESTART, 0, _minimizeProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);        _notes.Start(DispatcherQueue);
        var hud = _system = new SystemHud(DispatcherQueue);
        hud.Changed += e =>
        {
            if (_fullscreen || _mode == Mode.Notify || !_visible || !Allowed(e.Kind)) return;
            if (_mode == Mode.Expanded) // the open card stacks under the notice (as a card if it's the media card) and comes back after it
            {
                if (HasMedia) _stackOpen = true; else _reopen = true;
            }
            bool sameCard = _mode == Mode.Hud && _detail && _hud?.Kind == e.Kind; // the open card just shows the new value
            if (_mode == Mode.Hud) _leaving = false; // a new value while it was sliding away: it stays
            _hud = e;
            if (!sameCard) { _detail = false; _hudUntil = _clock.Elapsed.TotalSeconds + e.Seconds; }
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
        Settings.Changed += OnSettingsChanged;
    }

    static bool Allowed(HudKind kind) => kind switch
    {
        HudKind.Volume or HudKind.Brightness => Settings.Current.VolumeBrightness,
        HudKind.Power or HudKind.Saver => Settings.Current.Battery,
        HudKind.Bluetooth => Settings.Current.Bluetooth,
        HudKind.Wifi => Settings.Current.Wifi,
        _ => true,
    };

    static bool Allowed(IslandNotification n) =>
        NotificationView.KindOf(n) == NoteKind.Screenshot ? Settings.Current.Screenshots : Settings.Current.Notifications;

    /// <summary>A value changed in the Settings app: resize if the size changed, then redraw with the new choices.</summary>
    void OnSettingsChanged()
    {
        try
        {
            if (MathF.Abs(_dpi * Settings.Current.SizeScale - _s) > 0.001f)
            {
                PlaceOnPrimaryMonitor();
                _capture?.Dispose();
                _capture = null;
                try { _capture = new ScreenCapture(CanvasDevice.GetSharedDevice(), _monitor, _stripH + 64); }
                catch (Exception ex) { Debug.WriteLine($"Screen capture unavailable, using solid glass: {ex}"); }
            }
            _lastCapture = 0;
            _clockMinute = -1;
            _region = (int.MaxValue, 0, 0, 0);
            SetMode(_mode);
            UpdateRegion();
            _dirty = true;
        }
        catch (Exception ex) { Crash.Log("Settings apply", ex); }
    }

    /// <summary>Back to the middle of the top edge (Settings > Appearance).</summary>
    public void ResetPosition()
    {
        _x.Target = _monW / 2f;
        ClampX();
        SavePosition();
        _dirty = true;
    }

    /// <summary>Media just started: light flows from the app's taskbar button into the island, which glows as it lands.</summary>
    async void FlowIn()
    {
        try { await FlowInCore(); }
        catch (Exception ex) { Crash.Log("FlowIn", ex); } // async void: an escaping exception would kill the app
    }

        async Task FlowInCore()
    {        var (cx, top, _, h) = Shape();
        var to = new System.Numerics.Vector2(_monLeft + cx, _monTop + top + h / 2);
        var from = await Task.Run(() => FlowWindow.TaskbarButton(_media.AppName())) // UI Automation into the taskbar: keep it off the UI thread
            ?? new System.Numerics.Vector2(_monLeft + _monW / 2f, _monTop + _monH - 24 * _s);
        if (_flow is null)
        {
            _flow = new FlowWindow();
            _flow.Nearing += () => { _reachAt = _clock.Elapsed.TotalSeconds; _dirty = true; SetFast(true); }; // the underside reaches for the drop
            _flow.Arrived += () =>
            {
                double now = _clock.Elapsed.TotalSeconds;                if (_mode is Mode.Idle or Mode.Hover && HasMedia) // swallow the drop and swell into the pill (fullscreen was checked when it started)
                {
                    (_pulseAt, _rippleAt) = (now, now); // only for the bar: if it opened in the meantime, no ripple
                    _pinned = true;
                    _holdUntil = now + 3; // the mouse is down at the taskbar: keep the pill up a moment before the idle rule takes it back
                    SetMode(Mode.Compact);
                    _w.Velocity += 400 * _s; // a little liquid wobble in width only: it lands as the pill, never swelling past it
                    _artInAt = now;
                }
                _dirty = true;
            };
        }
        var a = _media.Accent;
        var color = a; // album colour (already softened), same as the pill's inner light; the drop's core is near-white
        _flow.Play(new RectInt32(_monLeft, _monTop, _monW, _monH), _dpi, from, to, color);
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

        // The presenter keeps a 3 px dialog frame (WS_DLGFRAME, and WinUI puts the style back if it's removed) that DWM paints
        // as a white bar above the island and that pushes the canvas down. Claim the whole window as client area instead.
        _messages = new WinUIEx.Messaging.WindowMessageMonitor(_hwnd);
        _messages.WindowMessageReceived += (_, e) =>
        {
            if (e.Message.MessageId == Native.WM_NCCALCSIZE && e.Message.WParam != 0) { e.Result = 0; e.Handled = true; }
        };
        Native.SetWindowPos(_hwnd, 0, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }
    WinUIEx.Messaging.WindowMessageMonitor? _messages; // field so the subclass isn't garbage-collected

    // shortcut: primary monitor only, fixed at launch; add per-monitor dragging and DPI-change handling later.
    void PlaceOnPrimaryMonitor()
    {
        _monitor = Native.MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(_monitor, ref mi);
        (_monLeft, _monTop, _monW, _monH) = (mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);

        AppWindow.Move(new PointInt32(_monLeft, _monTop)); // move first so we read this monitor's DPI
        _dpi = Native.GetDpiForWindow(_hwnd) / 96f;
        _s = _dpi * Settings.Current.SizeScale; // island sizes follow the display scaling (screen-width scaling was too big on a 2560 px monitor), times the Size setting
        Glass.Dpi = _dpi;
        _canvas.Width = CanvasDip * _s / _dpi;
        _stripH = (int)(StripDip * _s);
        AppWindow.MoveAndResize(new RectInt32(_monLeft, _monTop, _monW, _stripH));
    }

    void SetMode(Mode mode)
    {
        if (_mode is Mode.Idle or Mode.Hover && mode is not (Mode.Idle or Mode.Hover)) _clockOutAt = _clock.Elapsed.TotalSeconds;
        _mode = mode;
        (_w.Target, _h.Target, _top.Target) = mode switch
        {
            Mode.Idle when ShowClock => (150 * _s, 18 * _s, 0f), // a little wider and taller so the time and date fit
            Mode.Hover when ShowClock => (170 * _s, 22 * _s, 0f),
            Mode.Idle => (84 * _s, 8 * _s, 0f),     // idle and hover melt into the screen edge (see Glass.Attached)
            Mode.Hover => (124 * _s, 16 * _s, 0f),
            Mode.Compact => (210 * _s, 32 * _s, 6 * _s), // everything else drops out of the bar as a floating pill with its full glass rim
            Mode.Notify when _detail && _note is not null => (NotificationView.CardW * _s, NotificationView.CardH(_note) * _s, 8 * _s),
            Mode.Hud when _detail && _hud is not null => (HudView.CardW * _s, HudView.CardH(_hud, _outputs.Count) * _s, 8 * _s),
            Mode.Notify => (360 * _s, 72 * _s, 8 * _s),
            Mode.Hud => _hud?.Text.Length > 0 ? (330 * _s, 58 * _s, 8 * _s) : (240 * _s, 32 * _s, 6 * _s), // notices (battery, Bluetooth, Wi-Fi) are pills with a badge; level bars stay slim
            _ when HasMedia => (400 * _s, 150 * _s, 10 * _s),
            _ => (340 * _s, 96 * _s, 10 * _s),
        };
        ClampX();
        _dirty = true;
    }

    // Stacked islands: each one expands in place. _detail = the top notice/notification is open as its card;
    // _stackOpen = the media pill stacked under it is open as the media card. Only one at a time.
    bool _detail, _stackOpen;
    Spring _stackW = new(0, 380, 36), _stackH = new(0, 380, 36);
    List<AudioOutput> _outputs = [];
    float MediaCardW => 400 * _s;
    float MediaCardH => 150 * _s;

    /// <summary>Back from a notice's/notification's card to its pill, which then times out as usual.</summary>
    void CollapseDetail()
    {
        if (!_detail) return;
        _detail = false;
        double now = _clock.Elapsed.TotalSeconds;
        if (_mode == Mode.Notify) _noteUntil = now + 3;
        if (_mode == Mode.Hud) _hudUntil = now + 2;
        SetMode(_mode);
    }

    /// <summary>Where the media card is drawn right now: stacked under a notice, or the island itself.</summary>
    Windows.Foundation.Rect? MediaRect()
    {
        if (_stackOpen && _stack.Value > 2 * _s) return new(_x.Value - _stackW.Value / 2, _stack.Value, _stackW.Value, _stackH.Value);
        return _mode == Mode.Expanded && HasMedia ? IslandRect() : null;
    }

    /// <summary>The resting bar always shows the time and date (music shows as the bar's album-coloured light).</summary>
    bool ShowClock => Settings.Current.IdleClock;
    double _clockOutAt = -10; // when the bar last opened into a pill: the clock hands over to the pill's content
    int _clockMinute = -1;

    void ClampX()
    {
        float half = MathF.Max(_w.Target, _w.Value) / 2 + 8 * _s;
        _x.Target = Math.Clamp(_x.Target, half, _monW - half);
    }

    /// <summary>The visible shape, including the liquid stretch from horizontal speed.</summary>
    (float cx, float top, float w, float h) Shape()
    {
        if (_mode is Mode.Expanded or Mode.Notify || _detail) return (_x.Value, _top.Value, MathF.Max(_w.Value, 1), MathF.Max(_h.Value, 1)); // cards keep their size while moving
        float speed = MathF.Abs(_x.Velocity);
        float stretch = MathF.Min(speed * 0.045f, 90 * _s);
        float squash = 1 - MathF.Min(speed / 7000f, 0.35f);
        return (_x.Value, _top.Value, MathF.Max(_w.Value + stretch, 1), MathF.Max(_h.Value * squash, 1));
    }

    void OnTick(object? sender, object e)
    {
        try { Tick(); }
        catch (Exception ex) { Crash.Log("Tick", ex); }
    }

    void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(now - _lastTick, 1 / 30.0);
        _lastTick = now;

        if (now - _lastFullscreenCheck > 1 && _flow?.IsPlaying != true) // fullscreen games, videos and presentations: never hide, just stay the idle bar (our own flow overlay doesn't count)
        {
            _lastFullscreenCheck = now;
            // D3D exclusive full screen and presentations always count; "busy" (a window covering the screen) only when it's an app.
            _fullscreen = Native.SHQueryUserNotificationState(out int state) == 0 && (state is 3 or 4 || state == 2 && AppFillsScreen());
        }
        if (_fullscreen && _mode != Mode.Idle) { _pinned = false; SetMode(Mode.Idle); }

        // Hidden (Settings, tray menu, or full screen with "Hide the island"): the window goes away and only this check runs, twice a second.
        bool show = Settings.Current.ShowIsland && !(_fullscreen && Settings.Current.HideInFullscreen);
        if (show != _visible)
        {
            _visible = show;
            Native.ShowWindow(_hwnd, show ? 4 /* SW_SHOWNOACTIVATE */ : 0 /* SW_HIDE */);
            _slowTick.Interval = TimeSpan.FromMilliseconds(show ? 66 : 500);
            if (show) { _lastCapture = 0; _dirty = true; }
        }
        if (!_visible)
        {
            _notes.Pending.Clear(); // nothing piles up to burst out when it comes back
            SetFast(false);
            return;
        }

        if ((Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) == 0) // the shell can drop us out of the topmost band; then clicks hit the window below
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (!_fullscreen) UpdateHover(now);
        // Stacking: while a notification or notice is up, the playing media drops below it as its own pill, then pushes back up.
        bool stacked = (MediaPlaying || _stackOpen) && _mode is Mode.Notify or Mode.Hud;
        _stack.Target = !stacked ? 0 : _leaving ? 6 * _s : _top.Target + _h.Target + 8 * _s; // glides up into the leaving island's place
        (_stackW.Target, _stackH.Target) = _stackOpen ? (MediaCardW, MediaCardH) : (PillW, PillH);
        bool animating = _leaving || !_stack.Settled || !_stackW.Settled || !_stackH.Settled || HudView.Drag is not null || _pressed || now - _pulseAt < 0.9 || now - _clockOutAt < ClockOutTime || now - _reachAt < 0.3 || _media.TransitionT < 1 || _media.ArtFadeT < 1 || !(_x.Settled && _w.Settled && _h.Settled && _top.Settled);
        SetFast(animating);

        // Background refraction: each refresh copies a screen band and forces a full redraw, so it's rationed.
        // 30 Hz while the island moves, 4 Hz for an open pill or card, 1 Hz for the thin smoked idle bar; half that on battery,
        // frozen in energy saver.
        bool battery = PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent && PowerManager.BatteryStatus != BatteryStatus.NotPresent;
        var set = Settings.Current;
        bool glass = set.LiquidGlass && set.Power != PowerMode.Saver && !(battery && set.PauseGlassOnBattery);
        if (glass != _useGlass) { _useGlass = glass; _lastCapture = 0; _dirty = true; }
        double rate = set.Power == PowerMode.Quality ? 0.5 : 1; // Quality refreshes the glass twice as often
        double captureEvery = (animating ? 1 / 30.0 : _mode is Mode.Idle or Mode.Hover ? 1.0 : 0.25) * (battery ? 2 : 1) * rate;
        bool captureDue = _useGlass && now - _lastCapture >= captureEvery - 0.004 && PowerManager.EnergySaverStatus != EnergySaverStatus.On;
        if (captureDue && _capture?.TryUpdate() == true) { _dirty = true; _lastCapture = now; }
        bool playing = MediaPlaying && (_mode is Mode.Compact or Mode.Expanded || _stack.Value > 2 * _s) || _mode == Mode.Expanded && (MediaView.TitleScrolls || MediaView.LyricsAnimating(_clock.Elapsed.TotalSeconds));
        if (playing) _dirty = true; // waveform, progress bar and scrolling title move
        if (ShowClock && DateTime.Now.Minute != _clockMinute) { _clockMinute = DateTime.Now.Minute; _dirty = true; } // clock: one redraw per minute change
        _slowTick.Interval = TimeSpan.FromMilliseconds(playing && set.Power != PowerMode.Saver ? 33 : 66); // waveform needs ~30 fps to look alive; Battery saver accepts 15

        if (animating)
        {
            _x.Step(dt); _w.Step(dt); _h.Step(dt); _top.Step(dt); _stack.Step(dt); _stackW.Step(dt); _stackH.Step(dt);
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
    /// <summary>
    /// True when the foreground window is an ordinary app window covering this monitor (a game, a full-screen video).
    /// Always-on-top and tool windows are overlays, not apps: the snipping overlay, Alt+Tab, Start, Game Bar.
    /// </summary>
    bool AppFillsScreen()
    {
        nint fg = Native.GetForegroundWindow();
        if (fg == 0 || fg == _hwnd) return false;
        if ((Native.GetWindowLongPtr(fg, Native.GWL_EXSTYLE) & (Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW)) != 0) return false;
        return Native.GetWindowRect(fg, out var r) && r.Left <= _monLeft && r.Top <= _monTop && r.Right >= _monLeft + _monW && r.Bottom >= _monTop + _monH;
    }

    void FollowIsland()
    {
        var (cx, _, w, _) = Shape();
        float canvasW = CanvasDip * _s, half = w / 2 + 40 * _s;
        if (cx - half >= _canvasLeftPx && cx + half <= _canvasLeftPx + canvasW) return;
        _canvasLeftPx = Math.Clamp(MathF.Round(cx - canvasW / 2), 0, MathF.Max(0, _monW - canvasW));
        _shift.X = _canvasLeftPx / _dpi;
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

        if (!near && _mode != Mode.Expanded && now > _holdUntil) _pinned = false;
        if (_mode == Mode.Expanded) { } // stays open until clicked
        else if (_mode == Mode.Notify)
        {
            if (near && !_leaving && _noteUntil > 0) _noteUntil = Math.Max(_noteUntil, now + 1); // hovering keeps it open (not once dismissed)
            if ((!near || _noteUntil == 0 || _leaving) && now > _noteUntil && Leave(now)) ShowNextNote(now, near);
        }
        else if (_notes.Pending.Count > 0) ShowNextNote(now, near);
        else if (_mode == Mode.Hud)
        {
            if (now > _hudUntil && Leave(now)) // the notice slides away; an open media card (stacked or from before) takes the island
            {
                _detail = false;
                SetMode(_reopen || _stackOpen ? Mode.Expanded : StackedRest(now, near));
                (_reopen, _stackOpen) = (false, false);
            }
        }
        else if (_mode != RestMode(near)) SetMode(RestMode(near));
    }

    // Exit: a notification or notice slides up into the screen edge (ease-in, a little squash, fading), then the next stage takes over.
    bool _leaving;
    double _leaveAt;
    const double LeaveTime = 0.25;

    /// <summary>Starts the slide-up exit and returns false until it has finished; then true (once).</summary>
    bool Leave(double now)
    {
        if (!_leaving)
        {
            (_leaving, _leaveAt) = (true, now);
            SetFast(true);
            return false;
        }
        if (now - _leaveAt < LeaveTime) return false;
        _leaving = false;
        // Continue from what's on screen: the stacked media that glided up becomes the island, or the island comes back
        // down out of the edge it just tucked into.
        if (_stack.Value > 2 * _s)
        {
            (_w.Value, _h.Value, _top.Value) = (_stackW.Value, _stackH.Value, _stack.Value);
            (_stack.Value, _stack.Velocity) = (0, 0);
        }
        else _top.Value = -(_h.Value + 12 * _s);
        (_w.Velocity, _h.Velocity, _top.Velocity) = (0, 0, 0);
        return true;
    }

    /// <summary>After a notice leaves: the stacked media pill that glided up becomes the island's pill for a moment, then the idle rule applies.</summary>
    Mode StackedRest(double now, bool near)
    {
        if (_stack.Value <= 2 * _s || !HasMedia) return RestMode(near);
        (_pinned, _holdUntil) = (true, now + 2);
        return Mode.Compact;
    }

    /// <summary>0..1 progress of the slide-up exit (eased in), 0 when not leaving.</summary>
    float LeaveT => _leaving ? (float)Math.Pow(Math.Clamp((_clock.Elapsed.TotalSeconds - _leaveAt) / LeaveTime, 0, 1), 2) : 0;

    /// <summary>Drops the next queued notification down for 5 seconds, or returns to rest when there are none.</summary>
    void ShowNextNote(double now, bool near)
    {
        _detail = false;
        IslandNotification? next = null;
        while (_notes.Pending.TryDequeue(out var n)) if (Allowed(n)) { next = n; break; } // switched-off kinds are dropped
        if (next is not null)
        {
            _lastFullscreenCheck = 0; // re-check now: the full-screen state may be a second old (e.g. the snip overlay just closed)
            (_note, _noteUntil) = (next, now + (NotificationView.KindOf(next) == NoteKind.Alarm ? 30 : Settings.Current.NotificationSeconds)); // a ringing alarm stays until clicked, dismissed or 30 s
            SetMode(Mode.Notify);
            _w.Velocity += 600 * _s; // arrival bounce
        }
        else
        {
            _note = null;
            SetMode(_stackOpen ? Mode.Expanded : StackedRest(now, near)); // an open stacked media card takes the island
            _stackOpen = false;
        }
    }

    /// <summary>What the island shows when not expanded: the pill after a click until the mouse leaves, else the slim bar (swollen on hover).</summary>
    Mode RestMode(bool near) => _pinned ? Mode.Compact : near ? Mode.Hover : Mode.Idle; // the pill only shows after a click; otherwise the slim bar, even while music plays

    /// <summary>Right click steps back one stage: card → pill → bar. Notices and pop-ups just close.</summary>
    void OnRightClick(float y)
    {
        if (_stack.Value > 2 * _s && y > _stack.Value) // the stacked media: card → pill; the pill itself can't be dismissed
        {
            if (_stackOpen) { _stackOpen = false; _dirty = true; }
            return;
        }
        switch (_mode)
        {
            case Mode.Expanded: _pinned = true; SetMode(RestMode(near: true)); break;
            case Mode.Notify: _noteUntil = 0; break; // notifications and notices, pill or card: slide away (see Leave)
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
        if (_detail) pad = 34 * _s;
        float sw = MathF.Max(PillW, MathF.Max(_stackW.Value, _stackW.Target)), sh = MathF.Max(PillH, MathF.Max(_stackH.Value, _stackH.Target));
        float bottom = MathF.Max(top + h, MathF.Max(_stack.Value, _stack.Target) + sh); // include the stacked pill or card
        var region = (Math.Max(0, Snap4(cx - MathF.Max(w, sw) / 2 - pad)), 0, Math.Min(_monW, Snap4(cx + MathF.Max(w, sw) / 2 + pad)), Math.Min(_stripH, Snap4(bottom + pad)));
        if (animating) // only grow while moving: resizing the region every frame makes DWM stutter
            region = (Math.Min(region.Item1, _region.Item1), 0, Math.Max(region.Item3, _region.Item3), Math.Max(region.Item4, _region.Item4));
        if (region == _region) return;
        _region = region;
        Native.SetWindowRgn(_hwnd, Native.CreateRectRgn(region.Item1, region.Item2, region.Item3, region.Item4), true);
    }

    float PointerX(PointerRoutedEventArgs e) => (float)e.GetCurrentPoint(_root).Position.X * _dpi;
    float PointerY(PointerRoutedEventArgs e) => (float)e.GetCurrentPoint(_root).Position.Y * _dpi;

    Windows.Foundation.Rect IslandRect() { var (cx, top, w, h) = Shape(); return new(cx - w / 2, top, w, h); }

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
        var p = new System.Numerics.Vector2(PointerX(e), PointerY(e));
        if (MediaRect() is { } media && _media.CanSeek && MediaView.HitProgress(media, _s, p) is float f) // grab the progress bar: scrub, seek on release
        {
            MediaView.Scrub = f;
            _root.CapturePointer(e.Pointer);
            _dirty = true;
            return;
        }
        if (_mode == Mode.Hud && _detail && _hud is { Kind: HudKind.Volume or HudKind.Brightness } hud
            && HudView.Hit(IslandRect(), _s, hud, _outputs.Count, p) == "slider") // grab the level slider
        {
            HudView.Drag = HudView.SliderValue(IslandRect(), _s, p.X);
            if (hud.Kind == HudKind.Volume) _system?.SetVolume(HudView.Drag.Value);
            _root.CapturePointer(e.Pointer);
            _dirty = true;
            return;
        }
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
        if (HudView.Drag is not null)
        {
            HudView.Drag = HudView.SliderValue(IslandRect(), _s, PointerX(e));
            if (_hud?.Kind == HudKind.Volume) _system?.SetVolume(HudView.Drag.Value); // live; brightness is set on release (WMI is slow)
            _dirty = true;
            return;
        }
        if (MediaView.Scrub is not null && MediaRect() is { } media)
        {
            var bar = MediaView.ProgressBar(media, _s);
            MediaView.Scrub = (float)Math.Clamp((PointerX(e) - bar.X) / bar.Width, 0, 1);
            _dirty = true;
            return;
        }
        if (!_pressed) return;
        float x = PointerX(e);
        if (!_dragging && MathF.Abs(x - _pressX) > (_mode is Mode.Expanded or Mode.Notify ? 24 : 5) * _s) // cards need a deliberate drag so wobbly clicks still count as clicks
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
        if (MediaView.Scrub is float f) { MediaView.Scrub = null; _media.Seek(f); _dirty = true; }
        if (HudView.Drag is float level)
        {
            HudView.Drag = null;
            if (_hud?.Kind == HudKind.Brightness) _ = Task.Run(() => SystemHud.SetBrightness(level));
            _dirty = true;
            _root.ReleasePointerCapture(e.Pointer);
            return;
        }
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
    {        if (_fullscreen) return; // idle bar only while something is fullscreen
        var p = new System.Numerics.Vector2(_pressX, _pressY);
        bool onStack = _stack.Value > 2 * _s && _pressY > _stack.Value;
        if (onStack && !_stackOpen) // the stacked media pill opens into the media card in place; the notice above stays a pill
        {
            CollapseDetail();
            _stackOpen = true;
            _dirty = true;
            return;
        }
        if (MediaRect() is { } media && (onStack || _mode == Mode.Expanded)) // media card: controls, and the album art brings the app forward
        {
            switch (MediaView.HitButton(media, _s, p))
            {
                case 0: _media.Previous(); return;
                case 1: _media.TogglePlay(); return;
                case 2: _media.Next(); return;
            }
            if (onStack) { if (MediaView.ArtRect(media, _s).Contains(new Windows.Foundation.Point(p.X, p.Y))) _media.OpenApp(); return; }
        }
        if (_mode == Mode.Notify && _note is not null)
        {
            if (!_detail) // pill → card; it stays until closed
            {
                (_detail, _stackOpen, _noteUntil) = (true, false, double.MaxValue);
                SetMode(Mode.Notify);
                return;
            }
            NotificationSource.Open(_note); // anywhere on the card opens the app (right click dismisses)
            _noteUntil = 0; // slides away on the next tick
            return;
        }
        if (_mode == Mode.Hud && _hud is { } hud)
        {
            if (!_detail) // notice pill → its card; it stays until closed
            {
                if (hud.Kind == HudKind.Volume) _outputs = _system?.Outputs() ?? [];
                (_detail, _stackOpen, _hudUntil) = (true, false, double.MaxValue);
                SetMode(Mode.Hud);
                return;
            }
            string? hit = HudView.Hit(IslandRect(), _s, hud, _outputs.Count, p);
            if (hit == "mute") _system?.ToggleMute();
            else if (hit?.StartsWith("out:") == true && int.TryParse(hit[4..], out int i) && i < _outputs.Count)
            {
                SystemHud.SetDefaultOutput(_outputs[i].Id);
                _outputs = _system?.Outputs() ?? _outputs; // the volume listener follows the new default device on its own
                SetMode(Mode.Hud);
            }
            else if (hit == "settings" && hud.Settings is { } page)
            {
                try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); }
                catch (Exception ex) { Debug.WriteLine($"Couldn't open {page}: {ex.Message}"); }
                _hudUntil = 0;
            }
            _dirty = true;
            return;
        }

        // Click ladder: bar → pill → card → open the playing app.
        switch (_mode)
        {
            case Mode.Idle or Mode.Hover:
                _pinned = true; // show the pill (media, or the clock when nothing plays) until the mouse leaves
                SetMode(Mode.Compact);
                break;
            case Mode.Expanded:
                if (HasMedia) // only the album art opens the playing app; the rest of the card ignores left clicks
                {
                    if (!MediaView.ArtRect(IslandRect(), _s).Contains(new Windows.Foundation.Point(_pressX, _pressY))) break;
                    _media.OpenApp();
                }
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
        if (_stack.Value > 2 * _s) // stacked media pill (or its card), drawn first so the notification covers it as it tucks back up
        {
            float pillAlpha = Math.Clamp(_stack.Value / (24 * _s), 0, 1);
            float sw = MathF.Max(_stackW.Value, 1), sh = MathF.Max(_stackH.Value, 1);
            float open = Math.Clamp((sh - PillH) / (MediaCardH - PillH), 0, 1); // 0 = pill, 1 = card
            Glass.Draw(ds, GlassImage, _canvasLeftPx, cx, _stack.Value, sw, sh, _s, open, _media.Accent, 0, 0, rect =>
            {
                if (open < 0.5f) MediaView.DrawCompact(ds, rect, _s, _media, _clock.Elapsed.TotalSeconds, pillAlpha * (1 - 2 * open));
                if (open > 0.6f) MediaView.DrawExpanded(ds, rect, _s, _media, _clock.Elapsed.TotalSeconds, pillAlpha * (open - 0.6f) / 0.4f);
            }, _media.Accent, pillAlpha);
        }
        float leave = _mode is Mode.Notify or Mode.Hud ? LeaveT : 0;
        if (leave > 0) // sliding up into the screen edge: rises by its own height, squashes a little
        {
            top -= leave * (top + h + 12 * _s);
            h *= 1 - 0.15f * leave;
        }
        float expand = Math.Clamp((_h.Value - 14 * _s) / (118 * _s), 0, 1);
        float compactAlpha = _mode == Mode.Compact ? Math.Clamp((_w.Value - 160 * _s) / (50 * _s), 0, 1) : 0;
        float cardAlpha = Math.Clamp((_h.Value / _h.Target - 0.75f) / 0.25f, 0, 1);
        float expandedAlpha = _mode == Mode.Expanded ? cardAlpha : 0;
        var note = _mode == Mode.Notify ? _note : null;
        var hudEvent = _mode == Mode.Hud ? _hud : null;
        float hudAlpha = Math.Clamp((_w.Value - 180 * _s) / (60 * _s), 0, 1);
        float clockAlpha = _mode is Mode.Idle or Mode.Hover && ShowClock ? Math.Clamp((_w.Value - 110 * _s) / (40 * _s), 0, 1) : 0; // fades in as the bar widens
        // Bar → pill hand-over: the time drops into the opening pill, growing and fading, while the pill's content rises in behind it.
        float clockOut = (float)Math.Clamp((_clock.Elapsed.TotalSeconds - _clockOutAt) / ClockOutTime, 0, 1);
        float handOver = _mode is Mode.Idle or Mode.Hover ? 1 : clockOut;
        float popIn = Math.Clamp(handOver * 1.5f - 0.3f, 0, 1); // content starts once the clock is a third gone
        compactAlpha *= popIn; hudAlpha *= popIn;
        // Album glow: breathes slowly while music plays, blends to the new song's colour during the slide.
        float t = _media.TransitionT;
        var a = _media.PrevAccent; var b = _media.Accent;
        var glow = Microsoft.UI.ColorHelper.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        float glowStrength = 0; // the outer halo is only the brief flow landing; the steady colour lives inside the glass (wash)
        double clock = _clock.Elapsed.TotalSeconds;
        // Illumination: soft light rising inside every pill and notice, in its source's colour.
        var cool = Microsoft.UI.ColorHelper.FromArgb(255, 150, 180, 255);
        var wash = _mode switch
        {
            Mode.Notify => _note?.Accent ?? cool,
            Mode.Hud => _hud?.Tint ?? cool,
            _ => HasMedia ? glow : cool, // album colour, blended during the song slide
        };
        float washStrength = Math.Clamp((_h.Value - 16 * _s) / (14 * _s), 0, 1); // grows in with the pill
        if (_mode is Mode.Idle or Mode.Hover) // the bar: a soft light from its lower edge so it reads on dark screens too
        {
            wash = MediaPlaying ? glow : Microsoft.UI.ColorHelper.FromArgb(255, 210, 220, 240);
            washStrength = 1.6f;
        }
        float landed = MathF.Max(0, 1 - (float)(clock - _pulseAt) / 0.6f); // 1 at the flow's impact, 0 after 0.6 s
        washStrength *= 1 + 0.5f * landed; // the inner light brightens a touch at the flow's impact, then settles
        glowStrength = MathF.Max(glowStrength, 0.25f * landed); // barely-there light spilling as the flow lands
        // Melting: the bottom drips down while the card grows. Not for the pill: there the drop read as a second pill popping out underneath.
        float sag = _mode is Mode.Expanded or Mode.Notify || _detail ? Math.Clamp(_h.Velocity * 0.012f, 0, 20 * _s) : 0;
        float reach = (float)Math.Clamp((clock - _reachAt) / 0.25, 0, 1);
        if (reach is > 0 and < 1) sag = MathF.Max(sag, 8 * _s * MathF.Sin(MathF.PI * reach)); // the underside bulges toward the incoming drop
        float ripple = (float)((clock - _rippleAt) / 0.7);
        MediaView.ArtIn = (float)Math.Clamp((clock - _artInAt - 0.15) / 0.35, 0, 1); // the art arrives last
        using var fading = leave > 0 ? ds.CreateLayer(1 - 0.9f * leave) : null; // fades as it tucks under the edge
        Glass.Draw(ds, GlassImage, _canvasLeftPx, cx, top, w, h, _s, expand, glow, glowStrength, sag, rect =>
        {
            if (compactAlpha > 0)
            {
                if (HasMedia) MediaView.DrawCompact(ds, rect, _s, _media, _clock.Elapsed.TotalSeconds, compactAlpha);
                else MediaView.DrawClock(ds, rect, _s, compactAlpha, 20);
            }
            if (expandedAlpha > 0) MediaView.DrawExpanded(ds, rect, _s, _media, _clock.Elapsed.TotalSeconds, expandedAlpha);
            if (note is not null && cardAlpha > 0)
            {
                if (_detail) NotificationView.DrawExpanded(ds, rect, _s, note, cardAlpha);
                else NotificationView.Draw(ds, rect, _s, note, cardAlpha * popIn);
            }
            if (hudEvent is not null)
            {
                if (_detail) { if (cardAlpha > 0) HudView.DrawExpanded(ds, rect, _s, hudEvent, cardAlpha, _system, _outputs); }
                else if (hudAlpha > 0 && _h.Value < (hudEvent.Text.Length > 0 ? 66 : 40) * _s) HudView.Draw(ds, rect, _s, hudEvent, hudAlpha); // notice pills are 58 tall: a 40 cap left Bluetooth, Wi-Fi and battery pills empty
            }
            if (clockAlpha > 0) DrawClock(ds, rect, clockAlpha);
            else if (handOver < 1) // the clock leaving into the pill
            {
                float e = 1 - (1 - handOver) * (1 - handOver) * (1 - handOver);
                var c = new System.Numerics.Vector2((float)(rect.X + rect.Width / 2), (float)rect.Y + 9 * _s);
                var old = ds.Transform;
                ds.Transform = System.Numerics.Matrix3x2.CreateScale(1 + 0.45f * e, c) * System.Numerics.Matrix3x2.CreateTranslation(0, 10 * _s * e) * old;
                DrawClock(ds, new Windows.Foundation.Rect(rect.X, rect.Y, rect.Width, 18 * _s), 1 - e);
                ds.Transform = old;
            }
            if (ripple is > 0 and < 1) // soft ring running out along the glass from where the drop hit
            {
                float e = 1 - (1 - ripple) * (1 - ripple), rx = (float)rect.Width * 0.65f * e;
                ds.DrawEllipse((float)(rect.X + rect.Width / 2), (float)rect.Bottom, rx, rx * 0.4f,
                    Microsoft.UI.ColorHelper.FromArgb((byte)(60 * (1 - ripple)), 255, 255, 255), 1.5f * _s);
            }
        }, wash, washStrength);
    }

    const double ClockOutTime = 0.32;

    void DrawClock(CanvasDrawingSession ds, Windows.Foundation.Rect rect, float alpha)
    {
        var now = DateTime.Now;
        using var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
        {
            FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = (_mode == Mode.Hover ? 12 : 11) * _s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center, VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Center,
            WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
        };
        var set = Settings.Current;
        string time = set.Clock24h ? now.ToString("HH:mm", CultureInfo.InvariantCulture) : now.ToString("h:mm tt", CultureInfo.InvariantCulture);
        string text = set.ShowDate ? $"{time}  ·  {now.ToString("ddd M/d", CultureInfo.CurrentCulture)}" : time;
        Fonts.DrawTabular(ds, text, rect, Microsoft.UI.ColorHelper.FromArgb((byte)(210 * alpha), 255, 255, 255), format);
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
