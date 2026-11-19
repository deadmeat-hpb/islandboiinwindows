using System.Diagnostics;
using System.Management;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using Windows.Foundation;
using Windows.System.Power;

namespace DynamicIsland;

sealed record HudEvent(string Glyph, float Level, string Label);

/// <summary>Volume, brightness and charger events, re-posted to the UI thread.</summary>
sealed class SystemHud(DispatcherQueue ui)
{
    MMDevice? _speakers; // kept alive so the volume callback keeps firing
    ManagementEventWatcher? _brightness;

    public event Action<HudEvent>? Changed;

    void Raise(HudEvent e) => ui.TryEnqueue(() => Changed?.Invoke(e));

    public void Start()
    {
        // shortcut: follows the default speakers at launch only; re-attach on default-device change if switching headphones matters.
        try
        {
            _speakers = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _speakers.AudioEndpointVolume.OnVolumeNotification += d =>
                Raise(new(d.Muted ? "" : "", d.Muted ? 0 : d.MasterVolume, ""));
        }
        catch (Exception ex) { Debug.WriteLine($"Volume unavailable: {ex}"); }

        try // laptop panels only; external monitors don't report brightness through WMI
        {
            _brightness = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _brightness.EventArrived += (_, e) =>
                Raise(new("", Convert.ToSingle(e.NewEvent.Properties["Brightness"].Value) / 100f, ""));
            _brightness.Start();
        }
        catch (Exception ex) { Debug.WriteLine($"Brightness unavailable: {ex}"); }

        PowerManager.PowerSupplyStatusChanged += (_, _) =>
        {
            if (PowerManager.PowerSupplyStatus != PowerSupplyStatus.Adequate) return; // only announce plugging in
            int pct = PowerManager.RemainingChargePercent;
            Raise(new("", pct / 100f, $"{pct}%"));
        };
    }
}

static class HudView
{
    public static void Draw(CanvasDrawingSession ds, Rect r, float s, HudEvent e, float a)
    {
        float x = (float)r.X, cy = (float)(r.Y + r.Height / 2), right = (float)r.Right;
        var white = ColorHelper.FromArgb((byte)(255 * a), 255, 255, 255);

        using var icon = new CanvasTextFormat
        {
            FontFamily = "Segoe Fluent Icons", FontSize = 16 * s,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        ds.DrawText(e.Glyph, new Rect(x + 8 * s, cy - 14 * s, 28 * s, 28 * s), white, icon);

        float barX = x + 44 * s, barW = right - barX - (e.Label.Length > 0 ? 54 * s : 18 * s);
        ds.FillRoundedRectangle(barX, cy - 3 * s, barW, 6 * s, 3 * s, 3 * s, ColorHelper.FromArgb((byte)(60 * a), 255, 255, 255));
        ds.FillRoundedRectangle(barX, cy - 3 * s, barW * Math.Clamp(e.Level, 0, 1), 6 * s, 3 * s, 3 * s, white);

        if (e.Label.Length == 0) return;
        using var label = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text", FontSize = 13 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        ds.DrawText(e.Label, new Rect(right - 54 * s, cy - 12 * s, 40 * s, 24 * s), white, label);
    }
}
