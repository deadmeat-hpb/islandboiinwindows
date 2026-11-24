using System.Diagnostics;

namespace DynamicIsland;

/// <summary>
/// Writes errors with their stack to %LOCALAPPDATA%\DynamicIsland\crash.log (outside the install folder, which installs
/// replace). One bad frame or callback must never take the island down: callers catch, log here, and carry on.
/// </summary>
static class Crash
{
    static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIslandData", "crash.log");
    static readonly object Gate = new();
    static string _last = "";
    static int _repeats;

    public static void Log(string where, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            string text = $"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})";
            lock (Gate)
            {
                if (text == _last && ++_repeats > 3) return; // a frame that fails every tick: log it a few times, not 60 a second
                if (text != _last) (_last, _repeats) = (text, 0);
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 512 * 1024) File.Delete(LogFile); // keep it small
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {text}\n{ex.StackTrace}\n{(ex.InnerException is { } inner ? "inner: " + inner + "\n" : "")}\n");
            }
        }
        catch { }
        Debug.WriteLine($"[{where}] {ex}");
    }
}
