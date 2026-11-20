using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;

namespace DynamicIsland;

/// <summary>
/// `DynamicIsland.exe --preview <folder>` renders every notification design (pill and card) to PNGs and exits.
/// The island hides itself from screen capture, so screenshots can't show it; this is how designs get reviewed.
/// </summary>
static class Preview
{
    public static bool TryRun()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--preview");
        if (i < 0) return false;
        string dir = i + 1 < args.Length ? args[i + 1] : Path.Combine(AppContext.BaseDirectory, "preview");
        Directory.CreateDirectory(dir);
        try { Render(dir); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); }
        return true;
    }

    static IEnumerable<(string Name, IslandNotification Note)> Samples() =>
    [
        ("clock-alarm", new IslandNotification("Clock", "Wake up", "07:30\nAlarm", null, "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App", At: DateTime.Now)),
        ("clock-timer", new IslandNotification("Clock", "Timer", "Pasta\nTime's up", null, "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App", At: DateTime.Now)),
        ("security-ok", new IslandNotification("Windows Security", "Virus & threat protection", "Quick scan finished. No current threats.", null, "Windows.Defender.SecurityCenter", At: DateTime.Now)),
        ("security-threat", new IslandNotification("Windows Security", "Threats found", "Microsoft Defender Antivirus found threats. Get details.", null, "Windows.Defender.SecurityCenter", At: DateTime.Now)),
        ("update", new IslandNotification("Windows Update", "Restart to finish updating", "Your device will restart outside active hours.", null, "Windows.SystemToast.WindowsUpdate.MoNotification2", At: DateTime.Now)),
        ("store", new IslandNotification("Microsoft Store", "Spotify", "was just installed. Check it out.", null, "Microsoft.WindowsStore_8wekyb3d8bbwe!App", At: DateTime.Now)),
        ("gamebar-clip", new IslandNotification("Game Bar", "Game clip recorded", "Valorant · 0:30", null, "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe!App", At: DateTime.Now)),
        ("gamebar-shot", new IslandNotification("Game Bar", "Screenshot saved", "Valorant", null, "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe!App", At: DateTime.Now)),
        ("phonelink", new IslandNotification("Phone Link", "Mum", "Are you coming home for dinner?", null, "Microsoft.YourPhone_8wekyb3d8bbwe!App", ColorHelper.FromArgb(255, 80, 140, 255), DateTime.Now)),
        ("usb", new IslandNotification("AutoPlay", "USB Drive (E:)", "Select to choose what happens with removable drives.", null, "Windows.SystemToast.AutoPlay", At: DateTime.Now)),
        ("lowdisk", new IslandNotification("Low Disk Space", "You're running out of space on Local Disk (C:)", "Free up space to keep your PC running smoothly.", null, "Windows.SystemToast.LowDisk", At: DateTime.Now)),
        ("print", new IslandNotification("Print", "Document printed", "Report.pdf printed on HP LaserJet", null, "Windows.SystemToast.Print.Notification", At: DateTime.Now)),
        ("bluetooth", new IslandNotification("Bluetooth", "New Bluetooth device found", "Galaxy Buds2 Pro · Select to pair", null, "Windows.SystemToast.BthQuickPair", At: DateTime.Now)),
        ("onedrive", new IslandNotification("OneDrive", "OneDrive sync is paused", "Sync will resume in 2 hours.", null, "Microsoft.SkyDrive.Desktop", At: DateTime.Now)),
        ("backup", new IslandNotification("Windows Backup", "Backup complete", "Your files, settings and apps are backed up.", null, "MicrosoftWindows.Client.CBS_cw5n1h2txyewy!WindowsBackup", At: DateTime.Now)),
        ("startup", new IslandNotification("Settings", "New startup app", "TIDAL is set to run when you sign in.", null, "Windows.SystemToast.StartupApp", At: DateTime.Now)),
        ("snip-record", new IslandNotification("Snipping Tool", "Screen recording saved", "Saved to Videos\\Screen Recordings", null, "Microsoft.ScreenSketch_8wekyb3d8bbwe!App", At: DateTime.Now)),
        ("todo", new IslandNotification("Microsoft To Do", "Reminder: Submit report", "Today 17:00", null, "Microsoft.Todos_8wekyb3d8bbwe!App", ColorHelper.FromArgb(255, 80, 140, 255), DateTime.Now)),
    ];

    static void Render(string dir)
    {
        var device = CanvasDevice.GetSharedDevice();
        const float s = 2;
        foreach (var (name, note) in Samples())
        {
            float w = NotificationView.CardW * s, ch = NotificationView.CardH(note) * s, ph = 72 * s, gap = 20 * s;
            using var target = new CanvasRenderTarget(device, w + 40 * s, ph + ch + gap + 40 * s, 96);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(255, 70, 80, 95)); // a mid-tone desktop behind the glass
                var pill = new Rect(20 * s, 20 * s, 360 * s, 72 * s);
                var card = new Rect(20 * s, pill.Bottom + gap, w, ch);
                foreach (var r in new[] { pill, card }) ds.FillRoundedRectangle(r, 30 * s, 30 * s, Color.FromArgb(235, 22, 22, 24));
                NotificationView.Draw(ds, pill, s, note, 1);
                NotificationView.DrawExpanded(ds, card, s, note, 1);
            }
            using var file = File.Create(Path.Combine(dir, name + ".png"));
            target.SaveAsync(file.AsRandomAccessStream(), CanvasBitmapFileFormat.Png).AsTask().Wait();
        }
    }
}
