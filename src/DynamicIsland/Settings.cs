using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland;

enum PowerMode { Quality, Balanced, Saver }
enum IslandSize { Small, Default, Large }
enum AnimationLevel { Full, Reduced, Off }

/// <summary>
/// The user's choices from the Settings app. Saved as JSON in %LOCALAPPDATA%\DynamicIslandData\settings.json and applied
/// live: the island reads <see cref="Current"/> when it needs a value and listens to <see cref="Changed"/> for the few
/// that need work (size, visibility, startup).
/// </summary>
sealed class Settings
{
    // General
    public bool StartWithWindows { get; set; } = true;
    public bool ShowIsland { get; set; } = true;

    // Island behaviour
    public bool Music { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool Screenshots { get; set; } = true;
    public bool VolumeBrightness { get; set; } = true;
    public bool Battery { get; set; } = true;
    public bool Bluetooth { get; set; } = true;
    public bool Wifi { get; set; } = true;
    public bool ClockTimers { get; set; } = true;
    public bool Privacy { get; set; } = true;
    public bool CapsLock { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public bool Downloads { get; set; } = true;
    public bool Calls { get; set; } = true;
    public bool FocusAirplane { get; set; } = true;
    public bool IdleClock { get; set; } = true;
    public int NotificationSeconds { get; set; } = 5;
    public bool HideInFullscreen { get; set; }
    public bool DropAnimation { get; set; } = true;

    // Appearance
    public bool LiquidGlass { get; set; } = true;
    public IslandSize Size { get; set; } = IslandSize.Default;
    public bool Clock24h { get; set; } = true;
    public bool ShowDate { get; set; } = true;

    // Power
    public PowerMode Power { get; set; } = PowerMode.Balanced;
    public bool PauseGlassOnBattery { get; set; }
    public AnimationLevel Animations { get; set; } = AnimationLevel.Full;

    /// <summary>Wobble, stretch, drip, ripple, the moving waveform and the drop from the taskbar: Full only.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Lively => Animations == AnimationLevel.Full;

    [System.Text.Json.Serialization.JsonIgnore] public float SizeScale => Size switch { IslandSize.Small => 0.85f, IslandSize.Large => 1.15f, _ => 1f };

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIslandData");
    static readonly string File_ = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static Settings Current { get; private set; } = new();

    /// <summary>Raised on the UI thread after a value changed and was saved.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try { if (File.Exists(File_)) Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_), Json) ?? new(); }
        catch (Exception ex) { Crash.Log("Settings load", ex); Current = new(); } // a broken file falls back to the defaults
    }

    /// <summary>Glass is drawn right now: switched on, and neither Battery saver nor "pause on battery" is holding it back.</summary>
    public static bool GlassActive(out string? reason)
    {
        var s = Current;
        bool battery = Windows.System.Power.PowerManager.PowerSupplyStatus == Windows.System.Power.PowerSupplyStatus.NotPresent
            && Windows.System.Power.PowerManager.BatteryStatus != Windows.System.Power.BatteryStatus.NotPresent;
        reason = !s.LiquidGlass ? null
            : s.Power == PowerMode.Saver ? "Battery saver mode (Power and performance) is showing the solid island for now."
            : s.PauseGlassOnBattery && battery ? "You're on battery and the glass is paused on battery, so the island is solid for now."
            : null;
        return s.LiquidGlass && reason is null;
    }

    /// <summary>Back to the defaults (Settings > General), saved and applied.</summary>
    public static void ResetAll() => Update(s =>
    {
        var d = new Settings();
        foreach (var p in typeof(Settings).GetProperties().Where(p => p.CanWrite)) p.SetValue(s, p.GetValue(d));
    });

    /// <summary>Changes one value, saves, applies startup, and tells the island.</summary>
    public static void Update(Action<Settings> change)
    {
        change(Current);
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(File_, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception ex) { Crash.Log("Settings save", ex); }
        ApplyStartup();
        Changed?.Invoke();
    }

    /// <summary>Start with Windows = the HKCU Run value; off removes it, so the island only runs when opened.</summary>
    public static void ApplyStartup()
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (Current.StartWithWindows) run.SetValue("DynamicIsland", $"\"{Environment.ProcessPath}\"");
            else run.DeleteValue("DynamicIsland", throwOnMissingValue: false);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Autostart unavailable: {ex.Message}"); }
    }
}
