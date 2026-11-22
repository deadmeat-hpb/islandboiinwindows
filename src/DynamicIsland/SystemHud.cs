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

enum HudKind { Volume, Brightness, Power, Bluetooth, Wifi, Saver }

/// <summary>An audio output the volume card can switch to.</summary>
sealed record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>Volume, brightness, power, Bluetooth and Wi-Fi events, re-posted to the UI thread.</summary>
sealed class SystemHud(DispatcherQueue ui)
{
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // Bluetooth device battery %, where the device reports it
    static readonly Windows.UI.Color Green = ColorHelper.FromArgb(255, 80, 220, 120), Red = ColorHelper.FromArgb(255, 255, 90, 80);
    internal static readonly Windows.UI.Color BluetoothBlue = ColorHelper.FromArgb(255, 70, 140, 255), WifiBlue = ColorHelper.FromArgb(255, 90, 170, 255);

    MMDevice? _speakers; // kept alive so the volume callback keeps firing
    MMDeviceEnumerator? _devices;
    ManagementEventWatcher? _brightness;
    DeviceWatcher? _bluetooth;
    readonly Dictionary<string, bool> _btConnected = [];
    int _lowAnnounced = 101;
    PowerSupplyStatus _supply;
    EnergySaverStatus _saver;
    DateTime _powerChangedAt;
    internal static readonly Windows.UI.Color SaverGreen = ColorHelper.FromArgb(255, 130, 215, 90);

    /// <summary>The battery glyph for a charge level (Segoe Fluent Battery0..Battery10).</summary>
    static string BatteryGlyph(int pct) => ((char)(0xEBA0 + Math.Clamp((pct + 5) / 10, 0, 10))).ToString();
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
            {
                if ((DateTime.Now - _powerChangedAt).TotalSeconds < 4) return; // Windows changes brightness on AC/battery switches: that's not the user
                Raise(new("\uE706", Convert.ToSingle(e.NewEvent.Properties["Brightness"].Value) / 100f, "", Kind: HudKind.Brightness, Settings: "ms-settings:display"));
            };
            _brightness.Start();
        }
        catch (Exception ex) { Debug.WriteLine($"Brightness unavailable: {ex}"); }

        // Plugged in / unplugged: compare with the last state (a slow charger reports Inadequate, which used to be ignored).
        _supply = PowerManager.PowerSupplyStatus;
        PowerManager.PowerSupplyStatusChanged += (_, _) =>
        {
            var now = PowerManager.PowerSupplyStatus;
            bool plugged = now != PowerSupplyStatus.NotPresent, was = _supply != PowerSupplyStatus.NotPresent;
            _supply = now;
            if (plugged == was || PowerManager.BatteryStatus == BatteryStatus.NotPresent) return; // desktops: no battery, nothing to say
            _powerChangedAt = DateTime.Now;
            int pct = PowerManager.RemainingChargePercent;
            if (plugged)
            {
                _lowAnnounced = 101;
                Raise(new("\uE945", pct / 100f, $"{pct}%", "Charging", Green, 2.5, "ms-settings:batterysaver", HudKind.Power));
            }
            else Raise(new(BatteryGlyph(pct), pct / 100f, $"{pct}%", "On battery", ColorHelper.FromArgb(255, 200, 210, 225), 2.5, "ms-settings:batterysaver", HudKind.Power));
        };

        // Energy saver (battery saver) switching on or off, by the user or at the threshold.
        _saver = PowerManager.EnergySaverStatus;
        PowerManager.EnergySaverStatusChanged += (_, _) =>
        {
            var now = PowerManager.EnergySaverStatus;
            bool on = now == EnergySaverStatus.On, wasOn = _saver == EnergySaverStatus.On;
            _saver = now;
            if (on == wasOn) return;
            int pct = PowerManager.RemainingChargePercent;
            Raise(new(on ? "\uEBC0" : BatteryGlyph(pct), pct / 100f, $"{pct}%", on ? "Energy saver on" : "Energy saver off", on ? SaverGreen : ColorHelper.FromArgb(255, 200, 210, 225), 3, "ms-settings:batterysaver", HudKind.Saver));
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
    readonly Dictionary<Guid, DateTime> _btAnnounced = [];

    async void OnBluetooth(string id, string? name, IReadOnlyDictionary<string, object> props)
    {
        try { await OnBluetoothCore(id, name, props); }
        catch (Exception ex) { Crash.Log("OnBluetooth", ex); } // async void: an escaping exception would kill the app
    }

        async Task OnBluetoothCore(string id, string? name, IReadOnlyDictionary<string, object> props)
    {
        if (name is not null) _btInfo[id] = (string.IsNullOrWhiteSpace(name) ? "Bluetooth device" : name, props.TryGetValue("System.Devices.Aep.ContainerId", out var c) && c is Guid g ? g : Guid.Empty);
        if (!props.TryGetValue("System.Devices.Aep.IsConnected", out var v) || v is not bool connected) return;
        bool was = _btConnected.GetValueOrDefault(id);
        _btConnected[id] = connected;
        // Added only records the starting state; a later false→true update is a real connect. (Waiting for EnumerationCompleted
        // instead ignored connects for ~30 s after launch: that is how long the Bluetooth endpoint scan takes.)
        if (name is not null || !connected || was || !_btInfo.TryGetValue(id, out var info)) return;
        // A headset has a classic and an LE endpoint in one container; both report connecting. Announce it once.
        if (info.Container != Guid.Empty && _btAnnounced.TryGetValue(info.Container, out var at) && DateTime.Now - at < TimeSpan.FromSeconds(10)) return;
        _btAnnounced[info.Container] = DateTime.Now;

        int? battery = await BatteryOf(info.Container);
        Raise(new("\uE702", battery is int b ? b / 100f : -1, battery is int p ? $"{p}%" : "", info.Name, BluetoothBlue, 5, "ms-settings:bluetooth", HudKind.Bluetooth));
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
        if (announce) Raise(new("\uE701", (profile!.GetSignalBars() ?? 0) / 5f, "", ssid!, WifiBlue, 2.5, "ms-settings:network-wifi", HudKind.Wifi));
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
        _ => 150, // power, Bluetooth, Wi-Fi: badge header, a large value with its gauge, the settings button
    };

    static Rect Slider(Rect r, float s) => new(r.X + 20 * s, r.Y + 66 * s, r.Width - 92 * s, 8 * s);
    static Rect Icon(Rect r, float s) => new(r.X + 14 * s, r.Y + 14 * s, 36 * s, 36 * s);
    static Rect Row(Rect r, float s, int i) => new(r.X + 14 * s, r.Y + 92 * s + i * 28 * s, r.Width - 28 * s, 26 * s);
    static Rect Button(Rect r, float s) => new(r.Right - 148 * s, r.Bottom - 42 * s, 134 * s, 28 * s);

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
        var title = Fonts.Shared(new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = 15 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character });
        var sub = Fonts.Shared(new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.Normal), FontSize = 12 * s, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character });
        var value = Fonts.Shared(new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = 13 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center });

        if (e.Kind is HudKind.Power or HudKind.Saver or HudKind.Bluetooth or HudKind.Wifi) { DrawNoticeCard(ds, r, s, e, a); return; }
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
                        var name = Fonts.Shared(new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.Medium), FontSize = 12.5f * s, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character });
                        ds.DrawText(o.Name, new Rect(row.X + 34 * s, row.Y, row.Width - 40 * s, row.Height), W(o.IsDefault ? 1 : 0.7f), name);
                    }
                return;
            }
            case HudKind.Power:
            case HudKind.Bluetooth:
            case HudKind.Wifi:
                DrawNoticeCard(ds, r, s, e, a);
                return;
        }
    }

    /// <summary>
    /// Battery, Bluetooth and Wi-Fi cards, in the same style as the built-in app notifications: a tinted glyph badge with a
    /// title and status, then the value large beside its gauge (battery capsule, or signal bars), and the settings button.
    /// </summary>
    static void DrawNoticeCard(CanvasDrawingSession ds, Rect r, float s, HudEvent e, float a)
    {
        Color W(float al) => ColorHelper.FromArgb((byte)Math.Clamp(al * 255 * a, 0, 255), 255, 255, 255);
        var c = e.Tint ?? ColorHelper.FromArgb(255, 150, 180, 255);
        float x = (float)r.X, y = (float)r.Y, pad = 18 * s, bs = 44 * s;
        bool charging = e.Kind == HudKind.Power && (PowerManager.BatteryStatus == BatteryStatus.Charging || PowerManager.PowerSupplyStatus == PowerSupplyStatus.Adequate);
        Badge(ds, new Rect(x + pad, y + 16 * s, bs, bs), s, e.Glyph, c, a, pulse: charging);

        string title = e.Kind switch
        {
            HudKind.Power => charging ? "Charging" : e.Text.Length > 0 ? e.Text : "On battery",
            HudKind.Saver => PowerManager.EnergySaverStatus == EnergySaverStatus.On ? "Energy saver on" : "Energy saver off",
            _ => e.Text,
        };
        string status = e.Kind switch
        {
            HudKind.Power => PowerEstimate(charging),
            HudKind.Saver => PowerManager.EnergySaverStatus == EnergySaverStatus.On ? "Lower brightness, less background activity" : PowerEstimate(charging),
            HudKind.Bluetooth => "Connected",
            _ => "Connected",
        };
        float tx = x + pad + bs + 14 * s, tw = (float)r.Right - pad - tx;
        var titleF = Text(15 * s, Microsoft.UI.Text.FontWeights.SemiBold);
        var subF = Text(12.5f * s, Microsoft.UI.Text.FontWeights.Normal);
        ds.DrawText(title, new Rect(tx, y + 17 * s, tw, 22 * s), W(1), titleF);
        ds.DrawText(status, new Rect(tx, y + 39 * s, tw, 20 * s), W(0.55f), subF);

        // Value row: big number + gauge, left-aligned under the badge; the settings button sits at the right.
        float rowY = y + 82 * s, rowH = 44 * s;
        var big = Text(28 * s, Microsoft.UI.Text.FontWeights.SemiBold, CanvasVerticalAlignment.Center);
        switch (e.Kind)
        {
            case HudKind.Power:
            case HudKind.Saver:
            {
                int pct = PowerManager.RemainingChargePercent;
                DrawCapsule(ds, new Rect(x + pad, rowY + 12 * s, 52 * s, 22 * s), s, pct / 100f, c, charging, a);
                Fonts.DrawTabular(ds, $"{pct}%", new Rect(x + pad + 64 * s, rowY, 100 * s, rowH), W(1), big);
                break;
            }
            case HudKind.Bluetooth when e.Level >= 0:
                DrawCapsule(ds, new Rect(x + pad, rowY + 12 * s, 52 * s, 22 * s), s, e.Level, c, false, a);
                Fonts.DrawTabular(ds, e.Value, new Rect(x + pad + 64 * s, rowY, 100 * s, rowH), W(1), big);
                break;
            case HudKind.Bluetooth: // the device doesn't report its battery
                ds.DrawText("Battery level not reported", new Rect(x + pad, rowY, 170 * s, rowH), W(0.4f), Text(12 * s, Microsoft.UI.Text.FontWeights.Normal, CanvasVerticalAlignment.Center));
                break;
            case HudKind.Wifi:
            {
                int bars = (int)MathF.Round(Math.Clamp(e.Level, 0, 1) * 5);
                for (int i = 0; i < 5; i++)
                {
                    float bh = (8 + i * 6) * s, bx = x + pad + i * 11 * s, by = rowY + 38 * s;
                    ds.FillRoundedRectangle(bx, by - bh, 7 * s, bh, 2.5f * s, 2.5f * s, i < bars ? Tint(c, a) : W(0.16f));
                }
                ds.DrawText(Signal(e.Level), new Rect(x + pad + 64 * s, rowY, 130 * s, rowH), W(1), Text(22 * s, Microsoft.UI.Text.FontWeights.SemiBold, CanvasVerticalAlignment.Center));
                break;
            }
        }
        DrawButton(ds, Button(r, s), s, e.Kind switch { HudKind.Power or HudKind.Saver => "Battery settings", HudKind.Bluetooth => "Bluetooth settings", _ => "Wi-Fi settings" }, a);
    }

    static string Signal(float level) => level switch { >= 0.8f => "Excellent", >= 0.6f => "Strong", >= 0.4f => "Good", _ => "Weak" };

    static Color Tint(Color c, float a) => ColorHelper.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    static CanvasTextFormat Text(float size, Windows.UI.Text.FontWeight weight, CanvasVerticalAlignment v = CanvasVerticalAlignment.Top) => Fonts.Shared(new()
    {
        FontFamily = Fonts.Inter(weight), FontSize = size, FontWeight = weight, VerticalAlignment = v,
        WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
    });

    /// <summary>Tinted circle with the glyph (as on the built-in app notifications); <paramref name="pulse"/> adds a still halo ring while charging.</summary>
    static void Badge(CanvasDrawingSession ds, Rect r, float s, string glyph, Color c, float a, bool pulse)
    {
        float cx = (float)(r.X + r.Width / 2), cy = (float)(r.Y + r.Height / 2), rad = (float)r.Width / 2;
        if (pulse)
        {
            ds.DrawCircle(cx, cy, rad + 5 * s, Tint(c, 0.3f * a), 1.5f * s);
        }
        ds.FillCircle(cx, cy, rad, Tint(c, 0.22f * a));
        ds.DrawCircle(cx, cy, rad - 0.5f * s, Tint(c, 0.35f * a), 1 * s);
        using var f = new CanvasTextFormat { FontFamily = "Segoe Fluent Icons", FontSize = rad * 0.9f, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        ds.DrawText(glyph, r, Tint(c, a), f);
    }

    /// <summary>A battery capsule: outline, terminal nub, fill to <paramref name="level"/>; a bolt over it while charging.</summary>
    static void DrawCapsule(CanvasDrawingSession ds, Rect r, float s, float level, Color c, bool charging, float a)
    {
        level = Math.Clamp(level, 0, 1);
        float rad = (float)r.Height * 0.3f, inset = 2.5f * s;
        var fill = level <= 0.2f && !charging ? ColorHelper.FromArgb(255, 255, 90, 80) : c;
        ds.DrawRoundedRectangle(r, rad, rad, ColorHelper.FromArgb((byte)(110 * a), 255, 255, 255), 1.5f * s);
        ds.FillRoundedRectangle((float)r.Right + 2 * s, (float)(r.Y + r.Height * 0.32), 2.5f * s, (float)r.Height * 0.36f, 1 * s, 1 * s, ColorHelper.FromArgb((byte)(110 * a), 255, 255, 255));
        var inner = new Rect(r.X + inset, r.Y + inset, Math.Max(0, (r.Width - 2 * inset) * level), r.Height - 2 * inset);
        if (inner.Width > 0) ds.FillRoundedRectangle(inner, rad * 0.6f, rad * 0.6f, Tint(fill, a));
        if (!charging) return;
        using var bolt = new CanvasTextFormat { FontFamily = "Segoe Fluent Icons", FontSize = (float)r.Height * 0.75f, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        ds.DrawText("\uE945", r, ColorHelper.FromArgb((byte)(255 * a), 255, 255, 255), bolt);
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
        var f = Fonts.Shared(new CanvasTextFormat { FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.Medium), FontSize = 12 * s, FontWeight = Microsoft.UI.Text.FontWeights.Medium, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap });
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
        if (e.Text.Length == 0) ds.DrawText(e.Glyph, new Rect(x + 8 * s, cy - 14 * s, 28 * s, 28 * s), tint, icon); // notices draw a badge instead
        var label = Fonts.Shared(new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = 13 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        });
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

        // Notice (battery, Bluetooth, Wi-Fi): glyph badge, name + status, a small gauge on the right.
        var c = e.Tint ?? ColorHelper.FromArgb(255, 150, 180, 255);
        bool charging = e.Kind == HudKind.Power && e.Text == "Charging";
        float bs = 36 * s;
        Badge(ds, new Rect(x + 12 * s, cy - bs / 2, bs, bs), s, e.Glyph, c, a, pulse: charging);
        float tx = x + 60 * s, gaugeW = e.Kind == HudKind.Wifi ? 30 * s : e.Level >= 0 ? 74 * s : 0;
        var name = Text(13.5f * s, Microsoft.UI.Text.FontWeights.SemiBold);
        var status = Text(11.5f * s, Microsoft.UI.Text.FontWeights.Normal);
        string line = e.Kind switch { HudKind.Power => charging ? "Plugged in" : e.Text == "On battery" ? "Unplugged" : "Plug in soon", HudKind.Saver => e.Text.EndsWith("on") ? "Battery lasts longer" : "Full performance", HudKind.Bluetooth => "Connected", _ => $"{Signal(e.Level)} signal" };
        ds.DrawText(e.Text, new Rect(tx, cy - 18 * s, right - tx - gaugeW - 8 * s, 20 * s), white, name);
        ds.DrawText(line, new Rect(tx, cy + 1 * s, right - tx - gaugeW - 8 * s, 18 * s), ColorHelper.FromArgb((byte)(150 * a), 255, 255, 255), status);
        if (e.Kind == HudKind.Wifi)
        {
            int bars = (int)MathF.Round(Math.Clamp(e.Level, 0, 1) * 5);
            for (int i = 0; i < 5; i++)
            {
                float bh = (5 + i * 3.5f) * s, bx = right - 30 * s + i * 6 * s;
                ds.FillRoundedRectangle(bx, cy + 9 * s - bh, 4 * s, bh, 1.5f * s, 1.5f * s, i < bars ? Tint(c, a) : track);
            }
            return;
        }
        if (e.Level < 0) return;
        DrawCapsule(ds, new Rect(right - 30 * s, cy - 7 * s, 26 * s, 14 * s), s, e.Level, c, charging, a);
        var pct = Fonts.Shared(new CanvasTextFormat
        {
            FontFamily = Fonts.Inter(Microsoft.UI.Text.FontWeights.SemiBold), FontSize = 13 * s, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
        });
        Fonts.DrawTabular(ds, e.Value, new Rect(right - 78 * s, cy - 12 * s, 42 * s, 24 * s), white, pct);
    }
}
