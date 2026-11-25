using System.Diagnostics;
using Microsoft.Win32;

namespace DynamicIsland;

/// <summary>
/// The app was called "Dynamic Island" before it became Delos. On start this moves what the old name left behind:
/// the settings folder (%LOCALAPPDATA%\DynamicIslandData to DelosData), the island's position (it lived in the old install
/// folder), the old "DynamicIsland" Run value, and the old installed copy in %LOCALAPPDATA%\DynamicIsland once it has quit.
/// Each step runs only when there is something to move, so after the first start this costs a few file checks.
/// </summary>
static class Migration
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string OldData = Path.Combine(Local, "DynamicIslandData");
    static readonly string OldInstall = Path.Combine(Local, "DynamicIsland");

    public static void Run()
    {
        try
        {
            if (Directory.Exists(OldData) && !File.Exists(Path.Combine(Settings.Dir, "settings.json")))
            {
                Directory.CreateDirectory(Settings.Dir);
                foreach (var file in Directory.GetFiles(OldData))
                    File.Copy(file, Path.Combine(Settings.Dir, Path.GetFileName(file)), overwrite: false);
            }
            string oldPos = Path.Combine(OldInstall, "position.txt"), pos = Path.Combine(Settings.Dir, "position.txt");
            if (File.Exists(oldPos) && !File.Exists(pos)) { Directory.CreateDirectory(Settings.Dir); File.Copy(oldPos, pos); }
        }
        catch (Exception ex) { Crash.Log("Migrate settings", ex); }

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("DynamicIsland", throwOnMissingValue: false);
        }
        catch (Exception ex) { Crash.Log("Migrate autostart", ex); }

        // The old copy: close it (two islands would fight over the top edge), then remove its folder. Never the folder
        // Delos itself runs from.
        try
        {
            string self = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            if (!Directory.Exists(OldInstall) || string.Equals(self, OldInstall, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var p in Process.GetProcessesByName("DynamicIsland"))
                using (p)
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch (Exception ex) { Debug.WriteLine($"Old island still running: {ex.Message}"); }
                }
            if (File.Exists(Path.Combine(OldInstall, "DynamicIsland.exe"))) Directory.Delete(OldInstall, recursive: true);
        }
        catch (Exception ex) { Crash.Log("Remove old install", ex); }
    }
}
