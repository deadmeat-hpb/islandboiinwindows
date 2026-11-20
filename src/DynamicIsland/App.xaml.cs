using Microsoft.UI.Xaml;

namespace DynamicIsland;

public partial class App : Application
{
    IslandWindow? _window;
    static Mutex? _single;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { System.Diagnostics.Debug.WriteLine(e.Exception); e.Handled = true; }; // a bad frame shouldn't close the island
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Preview.TryRun()) { Exit(); return; } // design previews to PNG, then quit (see Preview.cs)
        _single = new Mutex(true, "DynamicIsland.SingleInstance", out bool first);
        if (!first) { Exit(); return; } // already running (e.g. started at sign-in)

        // Runs all the time: start at sign-in, and Windows relaunches it after a crash or hang. Quit from Task Manager.
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            run.SetValue("DynamicIsland", $"\"{Environment.ProcessPath}\"");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Autostart unavailable: {ex.Message}"); }
        Native.RegisterApplicationRestart(null, 0);

        Spring.SelfCheck();
        MediaSource.SelfCheck();
        _window = new IslandWindow();
        _window.Activate();
    }
}
