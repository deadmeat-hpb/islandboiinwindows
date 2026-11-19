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
    const double Duration = 0.75;
    readonly nint _hwnd;
    readonly CanvasControl _canvas;
    readonly Stopwatch _clock = new();
    Vector2 _from, _to;
    Color _color;
    float _s = 1;
    bool _running;

    public event Action? Arrived;
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
        AppWindow.MoveAndResize(monitor);
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

    /// <summary>Upward arc: leaves the taskbar straight up, then bends over to the island.</summary>
    Vector2 At(float t)
    {
        var c = new Vector2(_from.X, _to.Y + (_from.Y - _to.Y) * 0.25f);
        float u = 1 - t;
        return u * u * _from + 2 * u * t * c + t * t * _to;
    }

    static Color Alpha(Color c, float a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    void Draw(CanvasDrawingSession ds)
    {
        ds.Transform = Matrix3x2.CreateScale(1 / _s); // our math is in physical pixels
        float t = (float)Math.Min(_clock.Elapsed.TotalSeconds / Duration, 1);
        static float Ease(float x) => x < 0.5f ? 4 * x * x * x : 1 - MathF.Pow(-2 * x + 2, 3) / 2;
        float head = Ease(t), fade = MathF.Min(1, (1 - t) * 6); // fades as it sinks into the island

        // Liquid tail: tapering, fading segments trailing the head.
        using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        const int n = 16;
        for (int i = n; i > 0; i--)
        {
            float a = MathF.Max(0, head - i * 0.022f), b = MathF.Max(0, head - (i - 1) * 0.022f);
            float k = 1 - i / (float)n;
            ds.DrawLine(At(a), At(b), Alpha(_color, 0.55f * k * k * fade), (3 + 9 * k) * _s, round);
        }

        // Glowing drop at the head.
        var p = At(head);
        float r = 22 * _s;
        using var glow = new CanvasRadialGradientBrush(ds, Alpha(_color, 0.5f * fade), Alpha(_color, 0)) { Center = p, RadiusX = r, RadiusY = r };
        ds.FillCircle(p, r, glow);
        ds.FillCircle(p, 6 * _s, Alpha(Colors.White, 0.9f * fade));
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
