using System.Diagnostics;
using System.Numerics;
using FlaUI.UIA3;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace DynamicIsland;

/// <summary>
/// When media starts, a drop of light flows from the app's taskbar button up into the island.
/// A full-screen, click-through overlay that is hidden (and does no work) except during the 0.75 s flight.
/// </summary>
sealed class FlowWindow : Window
{
    const double Duration = 0.75; // 0.11 s gathering at the taskbar + 0.64 s flight
    readonly nint _hwnd;
    readonly CanvasControl _canvas;
    readonly Stopwatch _clock = new();
    Vector2 _from, _to;
    Color _color;
    float _s = 1;
    bool _running;

    public event Action? Arrived;
    public bool IsPlaying => _running;
    /// <summary>Just before the drop touches the island, so it can reach out to meet it.</summary>
    public event Action? Nearing;
    bool _neared;

    public FlowWindow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop();
        _canvas = new CanvasControl { ClearColor = Colors.Transparent };
        _canvas.Draw += (_, e) => Draw(e.DrawingSession);
        Content = _canvas;

        var p = OverlappedPresenter.Create();
        p.SetBorderAndTitleBar(false, false);
        p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
        p.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;
        var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, // layered + transparent = every click goes straight through
            (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT) & ~Native.WS_EX_APPWINDOW);
        Native.SetLayeredWindowAttributes(_hwnd, 0, 255, Native.LWA_ALPHA);
        int noRound = 1, noBorder = unchecked((int)0xFFFFFFFE);
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, 4);
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref noBorder, 4);
        Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE); // keep it out of the island's glass

        // Same as the island: the presenter keeps a 3 px dialog frame that DWM paints white around the whole overlay. All client area.
        _messages = new WinUIEx.Messaging.WindowMessageMonitor(_hwnd);
        _messages.WindowMessageReceived += (_, e) =>
        {
            if (e.Message.MessageId == Native.WM_NCCALCSIZE && e.Message.WParam != 0) { e.Result = 0; e.Handled = true; }
        };
        Native.SetWindowPos(_hwnd, 0, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }
    readonly WinUIEx.Messaging.WindowMessageMonitor _messages; // field so the subclass isn't garbage-collected

    /// <summary>Flies from <paramref name="from"/> to <paramref name="to"/> (screen pixels) across the given monitor.</summary>
    public void Play(RectInt32 monitor, float s, Vector2 from, Vector2 to, Color color)
    {
        var origin = new Vector2(monitor.X, monitor.Y);
        (_from, _to, _color, _s) = (from - origin, to - origin, color, s);
        _bead?.Dispose(); _halo?.Dispose(); (_bead, _halo) = (null, null); // rebuilt in the new colour on the first frame
        // 1 px short of the monitor: a click-through overlay that exactly covers it makes Windows report a fullscreen app
        // (QUNS_BUSY), which the island treats as "stay the idle bar".
        AppWindow.MoveAndResize(new RectInt32(monitor.X, monitor.Y, monitor.Width, monitor.Height - 1));
        AppWindow.Show(false); // don't take focus from the app
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _clock.Restart();
        _neared = false;
        if (!_running) CompositionTarget.Rendering += Tick;
        _running = true;
    }

    void Tick(object? sender, object e)
    {
        if (!_neared && _clock.Elapsed.TotalSeconds > Duration - 0.18) { _neared = true; Nearing?.Invoke(); }
        if (_clock.Elapsed.TotalSeconds < Duration) { _canvas.Invalidate(); return; }
        _neared = false;
        CompositionTarget.Rendering -= Tick;
        _running = false;
        AppWindow.Hide();
        Arrived?.Invoke();
    }

    /// <summary>
    /// Cubic arc from the taskbar button to the island: rises off the taskbar, bows a little to the side, and drops into
    /// the island from straight below.
    /// </summary>
    Vector2 At(float t)
    {
        float dy = _from.Y - _to.Y, dx = _to.X - _from.X;
        float bow = (MathF.Abs(dx) * 0.10f + 40 * _s) * (dx >= 0 ? -1 : 1); // bow away from the direction of travel
        var p1 = new Vector2(_from.X + bow, _from.Y - dy * 0.45f);
        var p2 = new Vector2(_to.X, _to.Y + dy * 0.40f);
        float u = 1 - t;
        return u * u * u * _from + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * _to;
    }

    static Color Alpha(Color c, float a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    const float Gather = 0.11f; // seconds the bead swells at the taskbar button before it leaves
    CanvasRadialGradientBrush? _bead, _halo; // created once per device; only centre/radius/opacity change per frame

    void Draw(CanvasDrawingSession ds)
    {
        ds.Transform = Matrix3x2.CreateScale(1 / _s); // our math is in physical pixels
        float time = (float)_clock.Elapsed.TotalSeconds;
        if (_bead is null || _bead.Device != ds.Device)
        {
            _bead?.Dispose(); _halo?.Dispose();
            // Near-white core, album colour around it, fading out: reads as light, not paint.
            _bead = new CanvasRadialGradientBrush(ds,
            [
                new() { Position = 0f, Color = Color.FromArgb(255, 255, 255, 255) },
                new() { Position = 0.35f, Color = Alpha(_color, 0.85f) },
                new() { Position = 1f, Color = Alpha(_color, 0) },
            ]);
            _halo = new CanvasRadialGradientBrush(ds, Alpha(_color, 0.22f), Alpha(_color, 0));
        }

        // Departure: a soft bead gathers at the taskbar button, which brightens faintly, then dims as the light leaves.
        float gather = Math.Clamp(time / Gather, 0, 1);
        float haloA = time < Gather ? gather : MathF.Max(0, 1 - (time - Gather) / 0.25f);
        if (haloA > 0)
        {
            _halo!.Center = _from; _halo.RadiusX = _halo.RadiusY = 30 * _s; _halo.Opacity = haloA;
            ds.FillCircle(_from, 30 * _s, _halo);
        }

        float flight = (float)Duration - Gather;
        float u = Math.Clamp((time - Gather) / flight, 0, 1);
        if (time < Gather) { Blob(ds, _from, Vector2.UnitY, 0, 7 * _s * gather, 1); return; }

        // Ease in-out that keeps accelerating into the island.
        static float Ease(float x) => 0.5f * (x * x * (3 - 2 * x)) + 0.5f * MathF.Pow(x, 2.2f);
        float e = Ease(u);
        var p = At(e);
        var dir = At(MathF.Min(1, e + 0.01f)) - p;
        dir = dir.LengthSquared() > 0 ? Vector2.Normalize(dir) : -Vector2.UnitY;
        float speed = (Ease(MathF.Min(1, u + 0.02f)) - e) / 0.02f; // ~0..2: how fast it moves along the path right now

        // Last 12 %: the drop merges into the bar, flattening into it instead of overlapping.
        float merge = Math.Clamp((u - 0.88f) / 0.12f, 0, 1);
        float r = 7 * _s * (1 - 0.6f * merge);

        // Tapered tail: a few decreasing soft circles behind the head (no blur).
        for (int i = 7; i >= 1; i--)
        {
            float k = i / 8f;
            var q = At(MathF.Max(0, e - k * 0.09f));
            float a = (1 - k) * (1 - k) * 0.55f * (1 - merge);
            if (a <= 0.01f) continue;
            Blob(ds, q, dir, 0, r * (1 - 0.75f * k), a);
        }

        // Tiny sparks shed along the way: drift outward and fade within a quarter second.
        for (int i = 0; i < 6; i++)
        {
            float born = 0.15f + i * 0.12f, age = (u - born) * flight;
            if (age <= 0 || age > 0.25f) continue;
            var at = At(Ease(born));
            float side = (i % 2 == 0 ? 1 : -1) * (6 + 3 * i) * _s;
            var drift = new Vector2(-dir.Y, dir.X) * side * (age / 0.25f) + new Vector2(0, 10 * _s * age / 0.25f);
            ds.FillCircle(at + drift, 1.3f * _s, Alpha(i % 3 == 0 ? Colors.White : _color, 0.6f * (1 - age / 0.25f)));
        }

        // The droplet: stretched along its velocity, squashed sideways when it lands.
        float stretch = Math.Clamp(speed * 0.6f, 0, 1.3f) * (1 - merge);
        Blob(ds, p, merge > 0 ? Vector2.UnitX : dir, stretch - 0.5f * merge, r, 1);
    }

    /// <summary>A soft light droplet at <paramref name="c"/>, stretched by <paramref name="stretch"/> along <paramref name="dir"/>.</summary>
    void Blob(CanvasDrawingSession ds, Vector2 c, Vector2 dir, float stretch, float r, float opacity)
    {
        if (r <= 0.1f || opacity <= 0) return;
        float halo = r * 2.6f; // the bright core is ~1/3 of the soft disc
        var old = ds.Transform;
        float angle = MathF.Atan2(dir.Y, dir.X);
        ds.Transform = Matrix3x2.CreateScale(1 + stretch, 1 / (1 + 0.5f * stretch)) * Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(c) * old;
        _bead!.Center = Vector2.Zero; _bead.RadiusX = _bead.RadiusY = halo; _bead.Opacity = opacity;
        ds.FillCircle(Vector2.Zero, halo, _bead);
        ds.Transform = old;
    }

    /// <summary>Centre of the app's taskbar button (Windows 11 names them "App name - 1 running window"), or null.</summary>
    public static Vector2? TaskbarButton(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return null;
        try
        {
            using var uia = new UIA3Automation();
            var tray = uia.FromHandle(Native.FindWindow("Shell_TrayWnd", null));
            foreach (var b in tray.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)))
            {
                if (!(b.Properties.Name.ValueOrDefault ?? "").StartsWith(appName, StringComparison.OrdinalIgnoreCase)) continue;
                var r = b.BoundingRectangle;
                return new Vector2(r.X + r.Width / 2f, r.Y + r.Height / 2f);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Taskbar lookup failed: {ex.Message}"); }
        return null;
    }
}
