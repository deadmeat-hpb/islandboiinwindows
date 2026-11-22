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
        ("clock-timer", new IslandNotification("Clock", NotificationSource.Readable("ms-resource:TimerDoneText"), "3 分鐘", null, "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App", At: DateTime.Now)), // what Clock actually sends
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
        ("claude-done", new IslandNotification("Claude", "Claude finished", "I updated the island to the newest build and pushed it to GitHub.", null, "AnthropicPBC.Claude_fnn82j28hfe8t!Claude", At: DateTime.Now)),
        ("claude-input", new IslandNotification("Claude", "Claude needs your permission", "Allow running dotnet build in islandboiinwindows?", null, "AnthropicPBC.Claude_fnn82j28hfe8t!Claude", At: DateTime.Now)),
        ("outlook", new IslandNotification("Outlook", "Sarah Chen", "Q4 budget review\nHi, could you take a look at the attached numbers before Friday's meeting?", null, "Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows", At: DateTime.Now)),
        ("todo",new IslandNotification("Microsoft To Do", "Reminder: Submit report", "Today 17:00", null, "Microsoft.Todos_8wekyb3d8bbwe!App", ColorHelper.FromArgb(255, 80, 140, 255), DateTime.Now)),
    ];

    /// <summary>A stand-in snip: a window-like gradient with a title bar and a few text lines.</summary>
    static CanvasBitmap FakeSnip(CanvasDevice device, float w, float h)
    {
        var rt = new CanvasRenderTarget(device, w, h, 96);
        using var ds = rt.CreateDrawingSession();
        using var g = new Microsoft.Graphics.Canvas.Brushes.CanvasLinearGradientBrush(ds, Color.FromArgb(255, 40, 90, 160), Color.FromArgb(255, 120, 60, 150)) { StartPoint = new(0, 0), EndPoint = new(w, h) };
        ds.FillRectangle(0, 0, w, h, g);
        ds.FillRectangle(0, 0, w, 28, Color.FromArgb(255, 30, 30, 34));
        for (int i = 0; i < 5; i++) ds.FillRoundedRectangle(24, 52 + i * 26, (w - 48) * (0.9f - i * 0.12f), 12, 6, 6, Color.FromArgb(150, 255, 255, 255));
        return rt;
    }

    static void Render(string dir)
    {
        var device = CanvasDevice.GetSharedDevice();
        const float s = 2;
        foreach (var (name, note) in Samples().Append(("screenshot", new IslandNotification("Snipping Tool", "Screenshot copied to clipboard", "Select to mark up and share.", null, "Microsoft.ScreenSketch_8wekyb3d8bbwe!App", At: DateTime.Now, Thumb: FakeSnip(device, 640, 400))))
                                                   .Append(("screenshot-tall", new IslandNotification("Snipping Tool", "Screenshot copied to clipboard", "", null, "Microsoft.ScreenSketch_8wekyb3d8bbwe!App", At: DateTime.Now, Thumb: FakeSnip(device, 300, 520)))))
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

        HudEvent[] notices =
        [
            new("\uE945", 0.76f, "76%", "Charging", ColorHelper.FromArgb(255, 80, 220, 120), Kind: HudKind.Power),
            new("\uEBA7", 0.68f, "68%", "On battery", ColorHelper.FromArgb(255, 200, 210, 225), Kind: HudKind.Power),
            new("\uEBC0", 0.18f, "18%", "Energy saver on", SystemHud.SaverGreen, Kind: HudKind.Saver),
            new("\uEBA0", 0.09f, "9%", "Battery low", ColorHelper.FromArgb(255, 255, 90, 80), Kind: HudKind.Power),
            new("\uE702", 0.8f, "80%", "Galaxy Buds2 Pro", SystemHud.BluetoothBlue, Kind: HudKind.Bluetooth),
            new("\uE702", -1, "", "MX Master 3S", SystemHud.BluetoothBlue, Kind: HudKind.Bluetooth),
            new("\uE701", 0.8f, "", "HomeWiFi_5G", SystemHud.WifiBlue, Kind: HudKind.Wifi),
            new("\uE720", -1, "", "Discord", SystemHud.MicOrange, Kind: HudKind.Privacy, Detail: "Using your microphone"),
            new("\uE714", -1, "", "Camera", SystemHud.CameraGreen, Kind: HudKind.Privacy, Detail: "Using your camera"),
            new("\uE765", -1, "", "Caps Lock on", ColorHelper.FromArgb(255, 150, 180, 255), Kind: HudKind.Keys, Detail: "TYPING IN CAPITALS"),
            new("\uE8C8", -1, "", "Copied", ColorHelper.FromArgb(255, 150, 180, 255), Kind: HudKind.Clipboard, Detail: "Meet at the station at 6, the north entrance by the bike racks"),
            new("\uE896", -1, "", "Island-Setup-2.4.1.exe", ColorHelper.FromArgb(255, 90, 200, 150), Kind: HudKind.Download, Detail: "Downloaded · click to show"),
            new("\uE708", -1, "", "Focus on", ColorHelper.FromArgb(255, 170, 150, 255), Kind: HudKind.Focus, Detail: "Notifications are silenced"),
            new("\uE709", -1, "", "Airplane mode on", ColorHelper.FromArgb(255, 255, 175, 60), Kind: HudKind.Airplane, Detail: "Wi-Fi and Bluetooth are off"),
        ];
        int k = 0;
        foreach (var e in notices)
        {
            float w = HudView.CardW * s, ch = HudView.CardH(e, 0) * s, ph = 58 * s, gap = 20 * s;
            using var target = new CanvasRenderTarget(device, w + 40 * s, ph + ch + gap + 40 * s, 96);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Color.FromArgb(255, 70, 80, 95));
                var pill = new Rect(20 * s, 20 * s, 330 * s, ph);
                var card = new Rect(20 * s, pill.Bottom + gap, w, ch);
                ds.FillRoundedRectangle(pill, 29 * s, 29 * s, Color.FromArgb(235, 22, 22, 24));
                ds.FillRoundedRectangle(card, 30 * s, 30 * s, Color.FromArgb(235, 22, 22, 24));
                HudView.Draw(ds, pill, s, e, 1);
                if (HudView.HasCard(e.Kind)) HudView.DrawExpanded(ds, card, s, e, 1, null, []); // those have no card
            }
            using var file = File.Create(Path.Combine(dir, $"hud-{k++}-{e.Kind}.png".ToLowerInvariant()));
            target.SaveAsync(file.AsRandomAccessStream(), CanvasBitmapFileFormat.Png).AsTask().Wait();
        }
    }
}
