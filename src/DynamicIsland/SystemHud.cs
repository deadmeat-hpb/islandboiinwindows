using System.Diagnostics;
using System.Management;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.System.Power;

namespace DynamicIsland;

/// <summary>
/// One island pop-up. With <paramref name="Text"/> it's a ColorOS-style notice: icon, text, and a level ring + value on the right.
/// Without it, a volume/brightness-style level bar. Level &lt; 0 hides the bar/ring.
/// </summary>
sealed record HudEvent(string Glyph, float Level, string Value, string Text = "", Windows.UI.Color? Tint = null, double Seconds = 1.6);

/// <summary>Volume, brightness, power, Bluetooth and Wi-Fi events, re-posted to the UI thread.</summary>
sealed class SystemHud(DispatcherQueue ui)
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // Bluetooth device battery %, where the device reports it
    static readonly Windows.UI.Color Green = ColorHelper.FromArgb(255, 80, 220, 120), Red = ColorHelper.FromArgb(255, 255, 90, 80);

    MMDevice? _speakers; // kept alive so the volume callback keeps firing
    ManagementEventWatcher? _brightness;
    DeviceWatcher? _bluetooth;
    readonly Dictionary<string, bool> _btConnected = [];
    bool _btReady;
    int _lowAnnounced = 101;
    string? _ssid;

    public event Action<HudEvent>? Changed;

    void Raise(HudEvent e) => ui.TryEnqueue(() => Changed?.Invoke(e));

    public void Start()
    {
        // shortcut: follows the default speakers at launch only; re-attach on default-device change if switching headphones matters.
        try
        {
            _speakers = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _speakers.AudioEndpointVolume.OnVolumeNotification += d =>
                Raise(new(d.Muted ? "\uE74F" : "\uE767", d.Muted ? 0 : d.MasterVolume, ""));
        }
        catch (Exception ex) { Debug.WriteLine($"Volume unavailable: {ex}"); }

        try // laptop panels only; external monitors don't report brightness through WMI
        {
            _brightness = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _brightness.EventArrived += (_, e) =>
                Raise(new("\uE706", Convert.ToSingle(e.NewEvent.Properties["Brightness"].Value) / 100f, ""));
            _brightness.Start();
        }
        catch (Exception ex) { Debug.WriteLine($"Brightness unavailable: {ex}"); }

        PowerManager.PowerSupplyStatusChanged += (_, _) =>
        {
            if (PowerManager.PowerSupplyStatus != PowerSupplyStatus.Adequate) return; // only announce plugging in
            int pct = PowerManager.RemainingChargePercent;
            _lowAnnounced = 101;
            Raise(new("\uE945", pct / 100f, $"{pct}%", "Charging", Green, 2.5));
        };
        PowerManager.RemainingChargePercentChanged += (_, _) =>
        {
            int pct = PowerManager.RemainingChargePercent;
            if (PowerManager.BatteryStatus != BatteryStatus.Discharging) return;
            foreach (int level in (int[])[20, 10, 5]) // once per threshold until the next charge
                if (pct <= level && _lowAnnounced > level)
                {
                    _lowAnnounced = level;
                    Raise(new("\uEBA0", pct / 100f, $"{pct}%", "Battery low", Red, 4));
                    break;
                }
        };

        StartBluetooth();
        NetworkInformation.NetworkStatusChanged += _ => ui.TryEnqueue(CheckWifi);
        CheckWifi(); // remember the current network so we only announce changes
    }

    void StartBluetooth()
    {
        try
        {
            // Classic Bluetooth and Bluetooth LE association endpoints.
            const string aqs = "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
            _bluetooth = DeviceInformation.CreateWatcher(aqs, ["System.Devices.Aep.IsConnected", "System.Devices.Aep.ContainerId"], DeviceInformationKind.AssociationEndpoint);
            _bluetooth.Added += (_, d) => ui.TryEnqueue(() => OnBluetooth(d.Id, d.Name, d.Properties));
            _bluetooth.Updated += (_, u) => ui.TryEnqueue(() => OnBluetooth(u.Id, null, u.Properties));
            _bluetooth.EnumerationCompleted += (_, _) => ui.TryEnqueue(() => _btReady = true); // don't announce devices already connected at launch
            _bluetooth.Start();
        }
        catch (Exception ex) { Debug.WriteLine($"Bluetooth watcher unavailable: {ex}"); }
    }

    readonly Dictionary<string, (string Name, Guid Container)> _btInfo = [];

    async void OnBluetooth(string id, string? name, IReadOnlyDictionary<string, object> props)
    {
        if (name is not null) _btInfo[id] = (name, props.TryGetValue("System.Devices.Aep.ContainerId", out var c) && c is Guid g ? g : Guid.Empty);
        if (!props.TryGetValue("System.Devices.Aep.IsConnected", out var v) || v is not bool connected) return;
        bool was = _btConnected.GetValueOrDefault(id);
        _btConnected[id] = connected;
        if (!_btReady || !connected || was || !_btInfo.TryGetValue(id, out var info)) return;

        int? battery = await BatteryOf(info.Container);
        Raise(new("\uE702", battery is int b ? b / 100f : -1, battery is int p ? $"{p}%" : "", info.Name, Seconds: 3));
    }

    /// <summary>Battery % from the device node that shares the endpoint's container, if the device reports one.</summary>
    static async Task<int?> BatteryOf(Guid container)
    {
        if (container == Guid.Empty) return null;
        try
        {
            await Task.Delay(1500); // headsets publish their level a moment after connecting
            var nodes = await PnpObject.FindAllAsync(PnpObjectType.Device, [BatteryKey], $"System.Devices.ContainerId:=\"{{{container}}}\"");
            foreach (var n in nodes)
                if (n.Properties.TryGetValue(BatteryKey, out var v) && v is byte level) return level;
        }
        catch (Exception ex) { Debug.WriteLine($"Bluetooth battery unavailable: {ex.Message}"); }
        return null;
    }

    void CheckWifi()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        string? ssid = profile?.IsWlanConnectionProfile == true ? profile.WlanConnectionProfileDetails.GetConnectedSsid() : null;
        if (ssid == _ssid) return;
        bool announce = _ssid is not null && !string.IsNullOrEmpty(ssid); // skip the very first check and disconnects
        _ssid = ssid ?? "";
        if (announce) Raise(new("\uE701", (profile!.GetSignalBars() ?? 0) / 5f, "", ssid!, Seconds: 2.5));
    }
}

static class HudView
{
    public static void Draw(CanvasDrawingSession ds, Rect r, float s, HudEvent e, float a)
    {
        float x = (float)r.X, cy = (float)(r.Y + r.Height / 2), right = (float)r.Right - 14 * s;
        var white = ColorHelper.FromArgb((byte)(255 * a), 255, 255, 255);
        var tint = e.Tint is { } t ? ColorHelper.FromArgb((byte)(255 * a), t.R, t.G, t.B) : white;
        var track = ColorHelper.FromArgb((byte)(60 * a), 255, 255, 255);

        using var icon = new CanvasTextFormat
        {
            FontFamily = "Segoe Fluent Icons", FontSize = 16 * s,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        ds.DrawText(e.Glyph, new Rect(x + 8 * s, cy - 14 * s, 28 * s, 28 * s), tint, icon);
        using var label = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text", FontSize = 13 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };
        float left = x + 44 * s;

        if (e.Text.Length == 0) // level bar (volume, brightness)
        {
            float barW = right - left - (e.Value.Length > 0 ? 40 * s : 0);
            ds.FillRoundedRectangle(left, cy - 3 * s, barW, 6 * s, 3 * s, 3 * s, track);
            ds.FillRoundedRectangle(left, cy - 3 * s, barW * Math.Clamp(e.Level, 0, 1), 6 * s, 3 * s, 3 * s, tint);
            if (e.Value.Length > 0)
                ds.DrawText(e.Value, new Rect(right - 36 * s, cy - 12 * s, 36 * s, 24 * s), white, label);
            return;
        }

        // Notice: text on the left, value + level ring on the right.
        float ringR = 8 * s, ringX = right - ringR;
        float valueW = e.Value.Length > 0 ? 38 * s : 0, ringW = e.Level >= 0 ? 2 * ringR + 8 * s : 0;
        ds.DrawText(e.Text, new Rect(left, cy - 12 * s, right - left - valueW - ringW, 24 * s), white, label);
        if (e.Value.Length > 0)
            ds.DrawText(e.Value, new Rect(right - ringW - valueW, cy - 12 * s, valueW, 24 * s), white, label);
        if (e.Level < 0) return;
        ds.DrawCircle(ringX, cy, ringR, track, 2.5f * s);
        float sweep = MathF.Tau * Math.Clamp(e.Level, 0, 1);
        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(ringX, cy - ringR);
        builder.AddArc(new System.Numerics.Vector2(ringX, cy), ringR, ringR, -MathF.PI / 2, sweep);
        builder.EndFigure(CanvasFigureLoop.Open);
        using var arc = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(arc, tint, 2.5f * s, new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round });
    }
}
