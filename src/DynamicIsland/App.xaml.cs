using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DynamicIsland;

public partial class App : Application
{
    IslandWindow? _window;
    static Tray? _tray;
    static SettingsWindow? _settings;
    static Mutex? _single;
    static EventWaitHandle? _showSettings;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { Crash.Log("Unhandled", e.Exception); e.Handled = true; }; // a bad frame shouldn't close the island
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash.Log("Fatal", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Crash.Log("Task", e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Preview.TryRun()) { Exit(); return; } // design previews to PNG, then quit (see Preview.cs)
        _single = new Mutex(true, "DynamicIsland.SingleInstance", out bool first);
        _showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "DynamicIsland.ShowSettings");
        if (!first) // already running: opening the app again opens its Settings
        {
            _showSettings.Set();
            Exit();
            return;
        }

        Settings.Load();
        Settings.ApplyStartup(); // Start with Windows (on by default) = the HKCU Run value
        Native.RegisterApplicationRestart(null, 0); // Windows relaunches it after a crash or hang

        Spring.SelfCheck();
        MediaSource.SelfCheck();
        _window = new IslandWindow();
        _window.Activate();

        var ui = DispatcherQueue.GetForCurrentThread();
        _tray = new Tray(_window.Hwnd, _window.Messages);
        _tray.OpenSettings += OpenSettings;
        _tray.Quit += Quit;
        ThreadPool.RegisterWaitForSingleObject(_showSettings, (_, _) => ui.TryEnqueue(OpenSettings), null, -1, executeOnlyOnce: false);
    }

    /// <summary>Opens the Settings app, or brings it forward if it's already open.</summary>
    public static void OpenSettings()
    {
        try
        {
            if (_settings is null)
            {
                _settings = new SettingsWindow();
                _settings.Closed += (_, _) => _settings = null;
            }
            _settings.Bring();
        }
        catch (Exception ex) { Crash.Log("Settings window", ex); }
    }

    public static void Quit()
    {
        _tray?.Dispose(); // the icon would linger in the tray until hovered
        _tray = null;
        Current.Exit();
    }
}
