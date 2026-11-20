using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DynamicIsland;

/// <summary>
/// The Settings app, opened from the tray icon (or by launching Dynamic Island again). A normal Windows 11 window:
/// Mica, a sidebar of pages, and setting cards. Built only while open; closing it frees it.
/// Every control writes through <see cref="Settings.Update"/>, which saves and applies at once.
/// </summary>
public sealed class SettingsWindow : Window
{
    readonly NavigationView _nav;
    readonly ScrollViewer _scroll;

    public SettingsWindow()
    {
        Title = "Dynamic Island";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Island.ico");
        try { AppWindow.SetIcon(icon); } catch (Exception ex) { Debug.WriteLine($"No window icon: {ex.Message}"); }

        var titleBar = new StackPanel { Orientation = Orientation.Horizontal, Height = 44, Padding = new Thickness(16, 0, 0, 0), Spacing = 12 };
        titleBar.Children.Add(new Microsoft.UI.Xaml.Controls.Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(icon)) });
        titleBar.Children.Add(new TextBlock { Text = "Dynamic Island", VerticalAlignment = VerticalAlignment.Center, Style = Res<Style>("CaptionTextBlockStyle") });

        _scroll = new ScrollViewer { Padding = new Thickness(36, 8, 36, 36) };
        _nav = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 230,
            Content = _scroll,
        };
        foreach (var (tag, glyph, text) in Pages)
            _nav.MenuItems.Add(new NavigationViewItem { Content = text, Tag = tag, Icon = new FontIcon { Glyph = glyph } });
        _nav.SelectionChanged += (_, e) => { if (e.SelectedItem is NavigationViewItem { Tag: string tag }) Show(tag); };

        var root = new Grid { RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } } };
        root.Children.Add(titleBar);
        Grid.SetRow(_nav, 1);
        root.Children.Add(_nav);
        Content = root;
        SetTitleBar(titleBar);

        _nav.SelectedItem = _nav.MenuItems[0];

        // Sized and centred on the screen it opens on.
        float scale = Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96f;
        var size = new SizeInt32((int)(900 * scale), (int)(660 * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
    }

    static readonly (string Tag, string Glyph, string Text)[] Pages =
    [
        ("general", "", "General"),
        ("behaviour", "", "Island behaviour"),
        ("appearance", "", "Appearance"),
        ("power", "", "Power and performance"),
        ("about", "", "About"),
    ];

    /// <summary>Brings an open window to the front, restoring it if minimised.</summary>
    public void Bring()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    void Show(string tag)
    {
        var page = new StackPanel { Spacing = 4, MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(new TextBlock { Text = Pages.First(p => p.Tag == tag).Text, Style = Res<Style>("TitleTextBlockStyle"), Margin = new Thickness(0, 4, 0, 20) });
        switch (tag)
        {
            case "general":
                page.Children.Add(Card("", "Start with Windows", "On: the island opens when you sign in. Off: it only runs when you open Dynamic Island yourself.",
                    Toggle(s => s.StartWithWindows, (s, v) => s.StartWithWindows = v)));
                page.Children.Add(Card("", "Show the island", "Turn the island off without quitting. The icon stays in the hidden icons so you can bring it back.",
                    Toggle(s => s.ShowIsland, (s, v) => s.ShowIsland = v)));
                var quit = new Button { Content = "Quit" };
                quit.Click += (_, _) => App.Quit();
                page.Children.Add(Card("", "Quit Dynamic Island", "Closes the island and its icon until you open the app again.", quit));
                break;

            case "behaviour":
                page.Children.Add(Section("Islands that appear"));
                page.Children.Add(Card("", "Music", "The playing song as a pill and card, with controls and lyrics.", Toggle(s => s.Music, (s, v) => s.Music = v)));
                page.Children.Add(Card("", "Notifications", "App and Windows notifications drop out of the island.", Toggle(s => s.Notifications, (s, v) => s.Notifications = v)));
                page.Children.Add(Card("", "Screenshots", "A preview of each screenshot you take.", Toggle(s => s.Screenshots, (s, v) => s.Screenshots = v)));
                page.Children.Add(Card("", "Volume and brightness", "A level bar while you change them.", Toggle(s => s.VolumeBrightness, (s, v) => s.VolumeBrightness = v)));
                page.Children.Add(Card("", "Battery and charging", "Plugging in, unplugging, low battery and energy saver.", Toggle(s => s.Battery, (s, v) => s.Battery = v)));
                page.Children.Add(Card("", "Bluetooth", "Devices connecting, with their battery.", Toggle(s => s.Bluetooth, (s, v) => s.Bluetooth = v)));
                page.Children.Add(Card("", "Wi-Fi", "Joining a network.", Toggle(s => s.Wifi, (s, v) => s.Wifi = v)));
                page.Children.Add(Card("", "Clock timers", "Minimise Clock while a timer or the stopwatch runs and it keeps counting in the island.", Toggle(s => s.ClockTimers, (s, v) => s.ClockTimers = v)));
                page.Children.Add(Section("Behaviour"));
                page.Children.Add(Card("", "Resting island", "What the island shows when nothing is happening.",
                    Choice(["Clock and date", "Slim bar"], s => s.IdleClock ? 0 : 1, (s, i) => s.IdleClock = i == 0)));
                page.Children.Add(Card("", "Notification time", "How long a notification stays before it slides away. Hovering keeps it open.",
                    Choice(["3 seconds", "5 seconds", "8 seconds"], s => s.NotificationSeconds <= 3 ? 0 : s.NotificationSeconds >= 8 ? 2 : 1, (s, i) => s.NotificationSeconds = i switch { 0 => 3, 2 => 8, _ => 5 })));
                page.Children.Add(Card("", "Full-screen apps", "Games, videos and presentations.",
                    Choice(["Keep the slim bar", "Hide the island"], s => s.HideInFullscreen ? 1 : 0, (s, i) => s.HideInFullscreen = i == 1)));
                page.Children.Add(Card("", "Drop animation", "Light flows from the music player or Clock into the island when you minimise it.", Toggle(s => s.DropAnimation, (s, v) => s.DropAnimation = v)));
                break;

            case "appearance":
                page.Children.Add(Card("", "Liquid glass", "The island bends and blurs what's behind it. Off: a solid dark island that is lighter on the graphics chip.",
                    Toggle(s => s.LiquidGlass, (s, v) => s.LiquidGlass = v)));
                page.Children.Add(Card("", "Size", "Scales the bar, pills and cards.",
                    Choice(["Small", "Default", "Large"], s => (int)s.Size, (s, i) => s.Size = (IslandSize)i)));
                page.Children.Add(Section("Clock"));
                page.Children.Add(Card("", "Time format", null, Choice(["24-hour", "12-hour"], s => s.Clock24h ? 0 : 1, (s, i) => s.Clock24h = i == 0)));
                page.Children.Add(Card("", "Show the date", "Beside the time on the resting island.", Toggle(s => s.ShowDate, (s, v) => s.ShowDate = v)));
                page.Children.Add(Section("Position"));
                var reset = new Button { Content = "Reset" };
                reset.Click += (_, _) => IslandWindow.Current?.ResetPosition();
                page.Children.Add(Card("", "Centre the island", "Moves it back to the middle of the top edge. You can also drag it.", reset));
                break;

            case "power":
                page.Children.Add(Card("", "Mode", "Quality: smoothest glass. Balanced: the usual. Battery saver: a solid island and fewer redraws.",
                    Choice(["Quality", "Balanced", "Battery saver"], s => (int)s.Power, (s, i) => s.Power = (PowerMode)i)));
                page.Children.Add(Card("", "Pause the glass on battery", "Use the solid island while unplugged.", Toggle(s => s.PauseGlassOnBattery, (s, v) => s.PauseGlassOnBattery = v)));
                break;

            case "about":
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 0, 0, 16) };
                head.Children.Add(new Microsoft.UI.Xaml.Controls.Image { Width = 56, Height = 56, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Island.ico"))) });
                var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                name.Children.Add(new TextBlock { Text = "Dynamic Island", Style = Res<Style>("SubtitleTextBlockStyle") });
                name.Children.Add(new TextBlock { Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}", Foreground = Res<Brush>("TextFillColorSecondaryBrush") });
                head.Children.Add(name);
                page.Children.Add(head);
                var log = new Button { Content = "Open folder" };
                log.Click += (_, _) => Open(Settings.Dir);
                page.Children.Add(Card("", "Crash log and settings file", "Where the island writes errors and keeps your settings.", log));
                var source = new HyperlinkButton { Content = "GitHub", NavigateUri = new Uri("https://github.com/deadmeat-hpb/islandboiinwindows") };
                page.Children.Add(Card("", "Source code", null, source));
                break;
        }
        _scroll.Content = page;
        _scroll.ChangeView(null, 0, null, true);
    }

    static void Open(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Couldn't open {path}: {ex.Message}"); }
    }

    static T Res<T>(string key) => (T)Application.Current.Resources[key];

    static TextBlock Section(string text) => new() { Text = text, Style = Res<Style>("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 20, 0, 6) };

    /// <summary>A Windows 11 settings card: icon, title and description on the left, the control on the right.</summary>
    static Border Card(string glyph, string title, string? description, FrameworkElement control)
    {
        var grid = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
        };
        var icon = new FontIcon { Glyph = glyph, FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description is not null)
            text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Style = Res<Style>("CaptionTextBlockStyle"), Foreground = Res<Brush>("TextFillColorSecondaryBrush") });
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(control, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return new Border
        {
            Child = grid,
            Background = Res<Brush>("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Res<Brush>("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 12),
            MinHeight = 68,
        };
    }

    static ToggleSwitch Toggle(Func<Settings, bool> get, Action<Settings, bool> set)
    {
        var t = new ToggleSwitch { IsOn = get(Settings.Current), OnContent = "On", OffContent = "Off", MinWidth = 0 };
        t.Toggled += (_, _) => Settings.Update(s => set(s, t.IsOn));
        return t;
    }

    static ComboBox Choice(string[] items, Func<Settings, int> get, Action<Settings, int> set)
    {
        var c = new ComboBox { MinWidth = 170 };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = get(Settings.Current);
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) Settings.Update(s => set(s, c.SelectedIndex)); };
        return c;
    }
}
