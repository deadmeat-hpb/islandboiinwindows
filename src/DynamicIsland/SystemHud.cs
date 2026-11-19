using System.Diagnostics;
using System.Management;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.System.Power;
using Color = Windows.UI.Color;

namespace DynamicIsland;

/// <summary>
/// One island pop-up. With <paramref name="Text"/> it's a ColorOS-style notice: icon, text, and a level ring + value on the right.
/// Without it, a volume/brightness-style level bar. Level &lt; 0 hides the bar/ring.
/// </summary>
sealed record HudEvent(string Glyph, float Level, string Value, string Text = "", Windows.UI.Color? Tint = null, double Seconds = 1.6, string? Settings = null, HudKind Kind = HudKind.Volume);

enum HudKind { Volume, Brightness, Power, Bluetooth, Wifi }

/// <summary>An audio output the volume card can switch to.</summary>
sealed record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>Volume, brightness, power, Bluetooth and Wi-Fi events, re-posted to the UI thread.</summary>
sealed class SystemHud(DispatcherQueue ui)
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // Bluetooth device battery %, where the device reports it
    static readonly Windows.UI.Color Green = ColorHelper.FromArgb(255, 80, 220, 120), Red = ColorHelper.FromArgb(255, 255, 90, 80);

    MMDevice? _speakers; // kept alive so the volume callback keeps firing
    MMDeviceEnumerator? _devices;
    ManagementEventWatcher? _brightness;
    DeviceWatcher? _bluetooth;
    readonly Dictionary<string, bool> _btConnected = [];
    int _lowAnnounced = 101;
    string? _ssid;

    public event Action<HudEvent>? Changed;

    void Raise(HudEvent e) => ui.TryEnqueue(() => Changed?.Invoke(e));

    public void Start()
    {
        // Follow switches to buds/headphones. WinRT event, not NAudio's IMMNotificationClient: that COM callback crashed the runtime
        // (access violation in coreclr) whenever a Bluetooth headset changed state.
        try { Windows.Media.Devices.MediaDevice.DefaultAudioRenderDeviceChanged += (_, e) => { if (e.Role == Windows.Media.Devices.AudioDeviceRole.Default) ui.TryEnqueue(AttachSpeakers); }; }
        catch (Exception ex) { Debug.WriteLine($"Audio device notifications unavailable: {ex}"); }
        AttachSpeakers();

        try // laptop panels only; external monitors don't report brightness through WMI
        {
            _brightness = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _brightness.EventArrived += (_, e) =>
                Raise(new("\uE706", Convert.ToSingle(e.NewEvent.Properties["Brightness"].Value) / 100f, "", Kind: HudKind.Brightness, Settings: "ms-settings:display"));
            _brightness.Start();
        }
        catch (Exception ex) { Debug.WriteLine($"Brightness unavailable: {ex}"); }

        PowerManager.PowerSupplyStatusChanged += (_, _) =>
        {
            if (PowerManager.PowerSupplyStatus != PowerSupplyStatus.Adequate) return; // only announce plugging in
            int pct = PowerManager.RemainingChargePercent;
            _lowAnnounced = 101;
            Raise(new("\uE945", pct / 100f, $"{pct}%", "Charging", Green, 2.5, "ms-settings:batterysaver", HudKind.Power));
        };
        PowerManager.RemainingChargePercentChanged += (_, _) =>
        {
            int pct = PowerManager.RemainingChargePercent;
            if (PowerManager.BatteryStatus != BatteryStatus.Discharging) return;
            foreach (int level in (int[])[20, 10, 5]) // once per threshold until the next charge
                if (pct <= level && _lowAnnounced > level)
                {
                    _lowAnnounced = level;
                    Raise(new("\uEBA0", pct / 100f, $"{pct}%", "Battery low", Red, 7, "ms-settings:batterysaver", HudKind.Power));
                    break;
                }
        };

        StartBluetooth();
        NetworkInformation.NetworkStatusChanged += _ => ui.TryEnqueue(CheckWifi);
        CheckWifi(); // remember the current network so we only announce changes
    }

    void AttachSpeakers()
    {
        try
        {
            _speakers?.Dispose();
            _speakers = (_devices ??= new MMDeviceEnumerator()).GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _speakers.AudioEndpointVolume.OnVolumeNotification += d =>
                Raise(new(d.Muted ? "\uE74F" : "\uE767", d.Muted ? 0 : d.MasterVolume, "", Settings: "ms-settings:sound"));
        }
        catch (Exception ex) { Debug.WriteLine($"Volume unavailable: {ex}"); }
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
        // Added only records the starting state; a later false→true update is a real connect. (Waiting for EnumerationCompleted
        // instead ignored connects for ~30 s after launch: that is how long the Bluetooth endpoint scan takes.)
        if (name is not null || !connected || was || !_btInfo.TryGetValue(id, out var info)) return;

        int? battery = await BatteryOf(info.Container);
        Raise(new("\uE702", battery is int b ? b / 100f : -1, battery is int p ? $"{p}%" : "", info.Name, Seconds: 5, Settings: "ms-settings:bluetooth", Kind: HudKind.Bluetooth));
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
        if (announce) Raise(new("\uE701", (profile!.GetSignalBars() ?? 0) / 5f, "", ssid!, Seconds: 2.5, Settings: "ms-settings:network-wifi", Kind: HudKind.Wifi));
    }

    // ---- Controls used by the expanded cards ----

    public bool Muted => _speakers?.AudioEndpointVolume.Mute == true;
    public string OutputName { get { try { return _speakers?.FriendlyName ?? ""; } catch { return ""; } } }

    public void SetVolume(float v)
    {
        try
        {
            if (_speakers is null) return;
            _speakers.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1);
            if (v > 0 && _speakers.AudioEndpointVolume.Mute) _speakers.AudioEndpointVolume.Mute = false;
        }
        catch (Exception ex) { Debug.WriteLine($"Set volume failed: {ex.Message}"); }
    }

    public void ToggleMute()
    {
        try { if (_speakers is not null) _speakers.AudioEndpointVolume.Mute = !_speakers.AudioEndpointVolume.Mute; }
        catch (Exception ex) { Debug.WriteLine($"Mute failed: {ex.Message}"); }
    }

    /// <summary>Active playback devices, the default one marked.</summary>
    public List<AudioOutput> Outputs()
    {
        var list = new List<AudioOutput>();
        try
        {
            _devices ??= new MMDeviceEnumerator();
            string def = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            foreach (var d in _devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (d) list.Add(new(d.ID, d.FriendlyName, d.ID == def));
        }
        catch (Exception ex) { Debug.WriteLine($"Listing outputs failed: {ex.Message}"); }
        return list;
    }

    /// <summary>Makes <paramref name="id"/> the default output for all roles (what the Windows sound flyout does).</summary>
    public static void SetDefaultOutput(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role); // console, multimedia, communications
        }
        catch (Exception ex) { Debug.WriteLine($"Switching output failed: {ex.Message}"); }
    }

    /// <summary>Laptop panel brightness through WMI (external monitors don't support it).</summary>
    public static void SetBrightness(float level)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new SelectQuery("WmiMonitorBrightnessMethods"));
            foreach (ManagementObject o in searcher.Get())
                using (o) o.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(level * 100, 0, 100)]);
        }
        catch (Exception ex) { Debug.WriteLine($"Set brightness failed: {ex.Message}"); }
    }

    // Undocumented but long-stable interface the Windows sound UI uses to change the default device.
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(); int GetDeviceFormat(); int ResetDeviceFormat(); int SetDeviceFormat();
        int GetProcessingPeriod(); int SetProcessingPeriod(); int GetShareMode(); int SetShareMode();
        int GetPropertyValue(); int SetPropertyValue();
        [System.Runtime.InteropServices.PreserveSig] int SetDefaultEndpoint([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string id, int role);
        int SetEndpointVisibility();
    }
}

static class HudView
{
    // ---- Expanded cards: volume, brightness, power, Bluetooth, Wi-Fi ----

    public const float CardW = 340;

    /// <summary>Level being dragged on the volume/brightness slider (0..1), or null.</summary>
    public static float? Drag;

    static int Rows(int outputs) => Math.Min(outputs, 4);

    public static float CardH(HudEvent e, int outputs) => e.Kind switch
    {
        HudKind.Volume => 96 + 28 * Rows(outputs) + (outputs > 0 ? 8 : 0),
        HudKind.Brightness => 96,
        HudKind.Power => 128,
        _ => 116,
    };

    static Rect Slider(Rect r, float s) => new(r.X + 20 * s, r.Y + 66 * s, r.Width - 92 * s, 8 * s);
    static Rect Icon(Rect r, float s) => new(r.X + 14 * s, r.Y + 14 * s, 36 * s, 36 * s);
    static Rect Row(Rect r, float s, int i) => new(r.X + 14 * s, r.Y + 92 * s + i * 28 * s, r.Width - 28 * s, 26 * s);
    static Rect Button(Rect r, float s) => new(r.Right - 132 * s, r.Bottom - 42 * s, 118 * s, 28 * s);

    /// <summary>What a click at <paramref name="p"/> on the expanded card hits: "mute", "slider", "out:N", "settings" or null.</summary>
    public static string? Hit(Rect r, float s, HudEvent e, int outputs, System.Numerics.Vector2 p)
    {
        bool In(Rect b, float slack = 0) => p.X >= b.X - slack && p.X <= b.Right + slack && p.Y >= b.Y - slack && p.Y <= b.Bottom + slack;
        switch (e.Kind)
        {
            case HudKind.Volume:
                if (In(Icon(r, s))) return "mute";
                if (In(Slider(r, s), 10 * s)) return "slider";
                for (int i = 0; i < Rows(outputs); i++) if (In(Row(r, s, i))) return $"out:{i}";
                return null;
            case HudKind.Brightness:
                return In(Slider(r, s), 10 * s) ? "slider" : null;
            default:
                return e.Settings is not null && In(Button(r, s)) ? "settings" : null;
        }
    }

    public static float SliderValue(Rect r, float s, float x) { var b = Slider(r, s); return (float)Math.Clamp((x - b.X) / b.Width, 0, 1); }

    public static void DrawExpanded(CanvasDrawingSession ds, Rect r, float s, HudEvent e, float a, SystemHud? hud, IReadOnlyList<AudioOutput> outputs)
    {
        Color W(float al) => ColorHelper.FromArgb((byte)Math.Clamp(al * 255 * a, 0, 255), 255, 255, 255);
        var tintC = e.Tint ?? ColorHelper.FromArgb(255, 150, 180, 255);
        Color T(float al) => ColorHelper.FromArgb((byte)Math.Clamp(al * 255 * a, 0, 255), tintC.R, tintC.G, tintC.B);
        using var glyph = new CanvasTextFormat { FontFamily = "Segoe Fluent Icons", FontSize = 18 * s, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        using var title = new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = 15 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };
        using var sub = new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.Normal), FontSize = 12 * s, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };
        using var value = new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = 13 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center };

        var icon = Icon(r, s);
        bool muted = e.Kind == HudKind.Volume && hud?.Muted == true;
        ds.FillCircle((float)(icon.X + icon.Width / 2), (float)(icon.Y + icon.Height / 2), 18 * s, muted ? W(0.10f) : T(0.22f));
        ds.DrawText(muted ? "" : e.Glyph, icon, muted ? W(0.6f) : W(0.95f), glyph);
        float tx = (float)r.X + 60 * s, tw = (float)r.Width - 74 * s;

        switch (e.Kind)
        {
            case HudKind.Volume:
            case HudKind.Brightness:
            {
                float level = Drag ?? Math.Clamp(e.Level, 0, 1);
                ds.DrawText(e.Kind == HudKind.Volume ? "Volume" : "Brightness", new Rect(tx, r.Y + 14 * s, tw, 20 * s), W(1), title);
                string detail = e.Kind == HudKind.Volume ? (muted ? "Muted · " : "") + (hud?.OutputName ?? "") : "Built-in display";
                ds.DrawText(detail, new Rect(tx, r.Y + 34 * s, tw, 18 * s), W(0.55f), sub);
                var b = Slider(r, s);
                ds.FillRoundedRectangle(b, 4 * s, 4 * s, W(0.18f));
                ds.FillRoundedRectangle(new Rect(b.X, b.Y, b.Width * level, b.Height), 4 * s, 4 * s, T(0.95f));
                ds.FillCircle((float)(b.X + b.Width * level), (float)(b.Y + b.Height / 2), (Drag is null ? 7 : 8) * s, W(1));
                ds.DrawText($"{(int)MathF.Round(level * 100)}%", new Rect(b.Right + 8 * s, b.Y - 10 * s, 44 * s, 28 * s), W(0.9f), value);
                if (e.Kind == HudKind.Volume)
                    for (int i = 0; i < Rows(outputs.Count); i++)
                    {
                        var row = Row(r, s, i); var o = outputs[i];
                        if (o.IsDefault) ds.FillRoundedRectangle(row, 8 * s, 8 * s, T(0.16f));
                        ds.DrawText(o.IsDefault ? "" : "", new Rect(row.X + 4 * s, row.Y, 24 * s, row.Height), W(o.IsDefault ? 0.95f : 0.5f), glyph);
                        using var name = new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.Medium), FontSize = 12.5f * s, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };
                        ds.DrawText(o.Name, new Rect(row.X + 34 * s, row.Y, row.Width - 40 * s, row.Height), W(o.IsDefault ? 1 : 0.7f), name);
                    }
                return;
            }
            case HudKind.Power:
            {
                int pct = PowerManager.RemainingChargePercent;
                bool charging = PowerManager.BatteryStatus == BatteryStatus.Charging || PowerManager.PowerSupplyStatus == PowerSupplyStatus.Adequate;
                ds.DrawText(charging ? "Charging" : e.Text.Length > 0 ? e.Text : "On battery", new Rect(tx, r.Y + 14 * s, tw, 20 * s), W(1), title);
                ds.DrawText(PowerEstimate(charging), new Rect(tx, r.Y + 34 * s, tw, 18 * s), W(0.55f), sub);
                using var big = new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = 30 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = CanvasVerticalAlignment.Center };
                Fonts.DrawTabular(ds, $"{pct}%", new Rect(r.X + 20 * s, r.Y + 62 * s, 120 * s, 40 * s), W(1), big);
                var bar = new Rect(r.X + 20 * s, r.Y + 104 * s, r.Width - 172 * s, 6 * s);
                ds.FillRoundedRectangle(bar, 3 * s, 3 * s, W(0.18f));
                ds.FillRoundedRectangle(new Rect(bar.X, bar.Y, bar.Width * pct / 100.0, bar.Height), 3 * s, 3 * s, T(0.95f));
                DrawButton(ds, Button(r, s), s, "Battery settings", a);
                return;
            }
            case HudKind.Bluetooth:
                ds.DrawText(e.Text, new Rect(tx, r.Y + 14 * s, tw, 20 * s), W(1), title);
                ds.DrawText("Connected", new Rect(tx, r.Y + 34 * s, tw, 18 * s), W(0.55f), sub);
                if (e.Level >= 0)
                {
                    using var big = new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = 24 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = CanvasVerticalAlignment.Center };
                    ds.DrawText("", new Rect(r.X + 18 * s, r.Y + 66 * s, 24 * s, 30 * s), W(0.8f), glyph);
                    Fonts.DrawTabular(ds, e.Value, new Rect(r.X + 46 * s, r.Y + 64 * s, 100 * s, 34 * s), W(1), big);
                }
                DrawButton(ds, Button(r, s), s, "Bluetooth settings", a);
                return;
            case HudKind.Wifi:
            {
                ds.DrawText(e.Text, new Rect(tx, r.Y + 14 * s, tw, 20 * s), W(1), title);
                ds.DrawText("Connected", new Rect(tx, r.Y + 34 * s, tw, 18 * s), W(0.55f), sub);
                int bars = (int)MathF.Round(Math.Clamp(e.Level, 0, 1) * 5);
                for (int i = 0; i < 5; i++)
                {
                    float bh = (8 + i * 5) * s, bx = (float)r.X + 22 * s + i * 9 * s, by = (float)r.Y + 98 * s;
                    ds.FillRoundedRectangle(bx, by - bh, 5 * s, bh, 2 * s, 2 * s, i < bars ? T(0.95f) : W(0.18f));
                }
                DrawButton(ds, Button(r, s), s, "Wi-Fi settings", a);
                return;
            }
        }
    }

    static string PowerEstimate(bool charging)
    {
        try
        {
            if (!charging)
            {
                var left = PowerManager.RemainingDischargeTime;
                return left > TimeSpan.Zero && left < TimeSpan.FromDays(2) ? $"About {Hm(left)} left" : "On battery";
            }
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report.FullChargeCapacityInMilliwattHours is int full && report.RemainingCapacityInMilliwattHours is int now && report.ChargeRateInMilliwatts is int rate && rate > 0)
                return $"About {Hm(TimeSpan.FromHours((full - now) / (double)rate))} to full";
        }
        catch { }
        return charging ? "Plugged in" : "On battery";
    }

    static string Hm(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, t.Minutes)} min";

    public static void DrawButton(CanvasDrawingSession ds, Rect b, float s, string text, float a)
    {
        ds.FillRoundedRectangle(b, (float)b.Height / 2, (float)b.Height / 2, ColorHelper.FromArgb((byte)(34 * a), 255, 255, 255));
        using var f = new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.Medium), FontSize = 12 * s, FontWeight = Microsoft.UI.Text.FontWeights.Medium, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        ds.DrawText(text, b, ColorHelper.FromArgb((byte)(235 * a), 255, 255, 255), f);
    }

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
            FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = 13 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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
