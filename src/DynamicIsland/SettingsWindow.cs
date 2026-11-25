using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.Text;

namespace DynamicIsland;

/// <summary>
/// The Settings app, opened from the tray icon (or by launching Delos again). Dark, warm and quiet, in the look of the
/// Apollo EQ app: a gallery-dark backdrop over Mica (the wallpaper tints through like old glass), cards that sit slightly
/// raised from it (neumorphism done with bevelled borders, not shadows), gold hairlines, Palatino for titles and Inter for
/// everything you read. Every brush is static: no blur, no shadows, no animation beyond the controls' own, so the window
/// costs no more to draw than a plain one. Built only while open; closing it frees it.
/// Every control writes through <see cref="Settings.Update"/>, which saves and applies at once.
/// </summary>
public sealed class SettingsWindow : Window
{
    // Palette (Apollo's): warm ivory ink on near-black teal, with an old-gold accent.
    static readonly Color Ink = C(0xFFEFE8D8), Ink2 = C(0xFFA8A293), Ink3 = C(0xFF7C786D);
    static readonly Color Gold = C(0xFFD6B46E), GoldDim = C(0xFFB89A5C), Caution = C(0xFFD9A066);
    static readonly Color Base = C(0xFF0D1112);

    static readonly FontFamily Inter = new("ms-appx:///Fonts/Inter-Regular.ttf#Inter");
    static readonly FontFamily InterMedium = new("ms-appx:///Fonts/Inter-Medium.ttf#Inter");
    static readonly FontFamily InterSemiBold = new("ms-appx:///Fonts/Inter-SemiBold.ttf#Inter");
    static readonly FontFamily Serif = new("Palatino Linotype, Book Antiqua, Georgia"); // Zapf's Renaissance-born roman, shipped with Windows

    readonly NavigationView _nav;
    readonly ScrollViewer _scroll;

    public SettingsWindow()
    {
        Title = "Delos";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Island.ico");
        try { AppWindow.SetIcon(icon); } catch (Exception ex) { Debug.WriteLine($"No window icon: {ex.Message}"); }
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = Ink2;
        bar.ButtonInactiveForegroundColor = Ink3;
        bar.ButtonHoverBackgroundColor = C(0x14FFF4DC);
        bar.ButtonHoverForegroundColor = Ink;
        bar.ButtonPressedBackgroundColor = C(0x0AFFF4DC);
        bar.ButtonPressedForegroundColor = Ink;

        var titleBar = new Grid { Height = 44 }; // empty, draggable strip; the name sits in the sidebar

        _scroll = new ScrollViewer { Padding = new Thickness(40, 0, 40, 40) };
        _nav = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 240,
            Content = _scroll,
            FontFamily = Inter,
            PaneHeader = Brand(),
        };
        foreach (var (tag, glyph, text) in Pages)
            _nav.MenuItems.Add(new NavigationViewItem { Content = text, Tag = tag, FontFamily = Inter, Icon = new FontIcon { Glyph = glyph, FontSize = 15 } });
        _nav.SelectionChanged += (_, e) => { if (e.SelectedItem is NavigationViewItem { Tag: string tag }) Show(tag); };

        var root = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } },
        };
        Restyle(root.Resources);
        // The backdrop: a dark varnish over the Mica, with a faint warm light from the upper left, like a lit canvas.
        var varnish = new Border { Background = Linear(C(0xE6121718), C(0xF2090C0D), new Point(0, 0), new Point(1, 1)) };
        var light = new Border { Background = new RadialGradientBrush
        {
            Center = new Point(0.18, 0.0), GradientOrigin = new Point(0.18, 0.0), RadiusX = 0.75, RadiusY = 0.6,
            GradientStops = { new GradientStop { Color = C(0x16D6B46E), Offset = 0 }, new GradientStop { Color = C(0x00D6B46E), Offset = 1 } },
        } };
        foreach (var layer in new[] { varnish, light }) { Grid.SetRowSpan(layer, 2); root.Children.Add(layer); }
        root.Children.Add(titleBar);
        Grid.SetRow(_nav, 1);
        root.Children.Add(_nav);
        Content = root;
        SetTitleBar(titleBar);

        _nav.SelectedItem = _nav.MenuItems[0];
        Settings.Changed += OnSettingsChanged;
        Closed += (_, _) => Settings.Changed -= OnSettingsChanged;

        // Sized and centred on the screen it opens on.
        float scale = Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96f;
        var size = new SizeInt32((int)(940 * scale), (int)(680 * scale));
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
        _sync.Clear();
        var page = new StackPanel { Spacing = 10, MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(new TextBlock
        {
            Text = Pages.First(p => p.Tag == tag).Text, FontFamily = Serif, FontStyle = FontStyle.Italic, FontSize = 32,
            Foreground = new SolidColorBrush(Ink), Margin = new Thickness(2, 6, 0, 0),
        });
        page.Children.Add(Rule(new Thickness(0, 4, 0, 14)));
        switch (tag)
        {
            case "general":
                page.Children.Add(Card("", "Start with Windows", "On: the island opens when you sign in. Off: it only runs when you open Delos yourself.",
                    Toggle(s => s.StartWithWindows, (s, v) => s.StartWithWindows = v)));
                page.Children.Add(Card("", "Show the island", "Turn the island off without quitting. The icon stays in the hidden icons so you can bring it back.",
                    Toggle(s => s.ShowIsland, (s, v) => s.ShowIsland = v)));
                var quit = PlainButton("Quit");
                quit.Click += (_, _) => App.Quit();
                page.Children.Add(Card("", "Quit Delos", "Closes the island and its icon until you open the app again.", quit));
                var resetAll = PlainButton("Reset");
                resetAll.Click += async (_, _) =>
                {
                    var ask = new ContentDialog
                    {
                        XamlRoot = Content.XamlRoot, RequestedTheme = ElementTheme.Dark, FontFamily = Inter,
                        Title = "Reset all settings?", Content = "Every page goes back to how Delos came. The island's position stays.",
                        PrimaryButtonText = "Reset", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                    };
                    if (await ask.ShowAsync() == ContentDialogResult.Primary) Settings.ResetAll();
                };
                page.Children.Add(Card("", "Reset all settings", "Back to the defaults on every page.", resetAll));
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
                page.Children.Add(Card("", "Microphone and camera", "A dot on the island while an app uses them, and which app when it starts.", Toggle(s => s.Privacy, (s, v) => s.Privacy = v)));
                page.Children.Add(Card("", "Caps Lock", "Caps Lock turning on or off.", Toggle(s => s.CapsLock, (s, v) => s.CapsLock = v)));
                page.Children.Add(Card("", "Copied", "What you copy, in one line. Private copies from password managers are never shown.", Toggle(s => s.Clipboard, (s, v) => s.Clipboard = v)));
                page.Children.Add(Card("", "Downloads", "A file finishing in Downloads; click it to see the file in its folder.", Toggle(s => s.Downloads, (s, v) => s.Downloads = v)));
                page.Children.Add(Card("", "Calls", "Zoom, Teams, Google Meet and Discord calls stay in the island with their length; click to go back to the call.", Toggle(s => s.Calls, (s, v) => s.Calls = v)));
                page.Children.Add(Card("", "Focus and airplane mode", "A Focus session starting or ending, and airplane mode on or off.", Toggle(s => s.FocusAirplane, (s, v) => s.FocusAirplane = v)));
                page.Children.Add(Card("", "Clock timers", "Minimise Clock while a timer or the stopwatch runs and it keeps counting in the island.", Toggle(s => s.ClockTimers, (s, v) => s.ClockTimers = v)));
                page.Children.Add(Section("Behaviour"));
                page.Children.Add(Card("", "Resting island", "What the island shows when nothing is happening.",
                    Choice(["Clock and date", "Slim bar"], s => s.IdleClock ? 0 : 1, (s, i) => s.IdleClock = i == 0)));
                page.Children.Add(Card("", "Notification time", "How long a notification stays before it slides away. Hovering keeps it open.",
                    Choice(["3 seconds", "5 seconds", "8 seconds"], s => s.NotificationSeconds <= 3 ? 0 : s.NotificationSeconds >= 8 ? 2 : 1, (s, i) => s.NotificationSeconds = i switch { 0 => 3, 2 => 8, _ => 5 })));
                page.Children.Add(Card("", "Full-screen apps", "Games, videos and presentations.",
                    Choice(["Show pop-ups over them", "Only the slim bar", "Hide the island"], s => s.HideInFullscreen ? 2 : s.PopUpsInFullscreen ? 0 : 1,
                        (s, i) => { s.HideInFullscreen = i == 2; s.PopUpsInFullscreen = i == 0; })));
                page.Children.Add(Card("", "Drop animation", "Light flows from the music player or Clock into the island when you minimise it.", Toggle(s => s.DropAnimation, (s, v) => s.DropAnimation = v)));
                page.Children.Add(Note(() => Settings.Current.Lively ? null : "Animations are set to Reduced or Off in Power and performance, so the drop doesn't play."));
                break;

            case "appearance":
                page.Children.Add(Card("", "Liquid glass", "The island bends and blurs what's behind it. Off: a solid black island that is lighter on the graphics chip.",
                    Toggle(s => s.LiquidGlass, (s, v) => s.LiquidGlass = v)));
                page.Children.Add(Note(() => { Settings.GlassActive(out var why); return why; }));
                page.Children.Add(Card("", "Size", "Scales the bar, pills and cards.",
                    Choice(["Small", "Default", "Large"], s => (int)s.Size, (s, i) => s.Size = (IslandSize)i)));
                page.Children.Add(Section("Clock"));
                page.Children.Add(Card("", "Time format", null, Choice(["24-hour", "12-hour"], s => s.Clock24h ? 0 : 1, (s, i) => s.Clock24h = i == 0)));
                page.Children.Add(Card("", "Show the date", "Beside the time on the resting island.", Toggle(s => s.ShowDate, (s, v) => s.ShowDate = v)));
                page.Children.Add(Section("Position"));
                var reset = PlainButton("Reset");
                reset.Click += (_, _) => IslandWindow.Current?.ResetPosition();
                page.Children.Add(Card("", "Centre the island", "Moves it back to the middle of the top edge. You can also drag it.", reset));
                break;

            case "power":
                page.Children.Add(Card("", "Mode", "Quality: smoothest glass. Balanced: the usual. Battery saver: a solid island and fewer redraws.",
                    Choice(["Quality", "Balanced", "Battery saver"], s => (int)s.Power, (s, i) => s.Power = (PowerMode)i)));
                page.Children.Add(Card("", "Animations", "Full: wobble, stretch, drip and the drop from the taskbar. Reduced: islands still glide, but no wobble or drip and a still waveform. Off: everything appears in place, the lightest on the graphics chip.",
                    Choice(["Full", "Reduced", "Off"], s => (int)s.Animations, (s, i) => s.Animations = (AnimationLevel)i)));
                page.Children.Add(Card("", "Pause the glass on battery", "Use the solid island while unplugged.", Toggle(s => s.PauseGlassOnBattery, (s, v) => s.PauseGlassOnBattery = v)));
                page.Children.Add(Note(() => Settings.Current.LiquidGlass ? null : "Liquid glass is off in Appearance, so the island is always solid."));
                break;

            case "about":
                var head = new Grid { ColumnSpacing = 20, Margin = new Thickness(0, 0, 0, 8), ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } } };
                var seal = Well(72, new Microsoft.UI.Xaml.Controls.Image { Width = 40, Height = 40, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Island.ico"))) });
                head.Children.Add(seal);
                var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
                name.Children.Add(new TextBlock { Text = "DELOS", FontFamily = Serif, FontSize = 28, CharacterSpacing = 420, Foreground = new SolidColorBrush(Ink) });
                name.Children.Add(new TextBlock { Text = "the floating island of Apollo", FontFamily = Serif, FontStyle = FontStyle.Italic, FontSize = 15, Foreground = new SolidColorBrush(GoldDim) });
                name.Children.Add(new TextBlock { Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}", FontFamily = Inter, FontSize = 12, Foreground = new SolidColorBrush(Ink3), Margin = new Thickness(0, 6, 0, 0) });
                Grid.SetColumn(name, 1);
                head.Children.Add(name);
                page.Children.Add(Panel(head, new Thickness(20, 18, 20, 18)));
                var log = PlainButton("Open folder");
                log.Click += (_, _) => Open(Settings.Dir);
                page.Children.Add(Card("", "Crash log and settings file", "Where the island writes errors and keeps your settings.", log));
                var source = new HyperlinkButton { Content = "GitHub", FontFamily = InterMedium, NavigateUri = new Uri("https://github.com/deadmeat-hpb/delos") };
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

    static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    static LinearGradientBrush Linear(Color from, Color to, Point start, Point end) => new()
    {
        StartPoint = start, EndPoint = end,
        GradientStops = { new GradientStop { Color = from, Offset = 0 }, new GradientStop { Color = to, Offset = 1 } },
    };

    /// <summary>The sidebar's head: the name in spaced Roman capitals over an italic line, like a title page.</summary>
    static FrameworkElement Brand()
    {
        var s = new StackPanel { Margin = new Thickness(22, 2, 16, 18), Spacing = 0 };
        s.Children.Add(new TextBlock { Text = "DELOS", FontFamily = Serif, FontSize = 22, CharacterSpacing = 480, Foreground = new SolidColorBrush(Ink) });
        s.Children.Add(new TextBlock { Text = "island settings", FontFamily = Serif, FontStyle = FontStyle.Italic, FontSize = 14, Foreground = new SolidColorBrush(GoldDim), Margin = new Thickness(1, -2, 0, 0) });
        return s;
    }

    /// <summary>A gold hairline that fades out at both ends.</summary>
    static Border Rule(Thickness margin) => new()
    {
        Height = 1, Margin = margin,
        Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = C(0x00D6B46E), Offset = 0 },
                new GradientStop { Color = C(0x66D6B46E), Offset = 0.3 },
                new GradientStop { Color = C(0x00D6B46E), Offset = 1 },
            },
        },
    };

    static FrameworkElement Section(string text)
    {
        var s = new StackPanel { Margin = new Thickness(2, 22, 0, 2), Spacing = 8 };
        s.Children.Add(new TextBlock { Text = text.ToUpperInvariant(), FontFamily = InterSemiBold, FontWeight = FontWeights.SemiBold, FontSize = 11, CharacterSpacing = 160, Foreground = new SolidColorBrush(GoldDim) });
        return s;
    }

    /// <summary>
    /// A raised panel: a faintly translucent dark glass face, lit along its top-left edge and shaded along its bottom-right,
    /// so it reads as sculpted out of the backdrop. Borders only, no shadow surfaces.
    /// </summary>
    static Border Panel(UIElement child, Thickness padding) => new()
    {
        Child = child,
        Background = Linear(C(0x8C283032), C(0x8C101516), new Point(0.2, 0), new Point(0.8, 1)),
        BorderBrush = Linear(C(0x2EFFF4DC), C(0x80000000), new Point(0, 0), new Point(1, 1)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = padding,
    };

    /// <summary>A round well pressed into the panel (the bevel reversed), holding an icon.</summary>
    static Border Well(double size, UIElement child) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
        Background = Linear(C(0xFF0E1314), C(0xFF1A2122), new Point(0, 0), new Point(1, 1)),
        BorderBrush = Linear(C(0x99000000), C(0x24FFF4DC), new Point(0, 0), new Point(1, 1)),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = child,
    };

    /// <summary>A setting card: icon in a well, title and description, the control on the right.</summary>
    static Border Card(string glyph, string title, string? description, FrameworkElement control)
    {
        var grid = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } },
        };
        var icon = Well(38, new FontIcon { Glyph = glyph, FontSize = 16, Foreground = new SolidColorBrush(Ink2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontFamily = InterMedium, FontWeight = FontWeights.Medium, FontSize = 14, Foreground = new SolidColorBrush(Ink) });
        if (description is not null)
            text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontFamily = Inter, FontSize = 12, LineHeight = 17, Foreground = new SolidColorBrush(Ink2) });
        control.VerticalAlignment = VerticalAlignment.Center;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title); // screen readers say "Liquid glass", not "On"
        control.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(control, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(control);
        var card = Panel(grid, new Thickness(14, 12, 18, 12));
        card.MinHeight = 66;
        return card;
    }

    static Button PlainButton(string text) => new() { Content = text, FontFamily = InterMedium, MinWidth = 96, Padding = new Thickness(16, 6, 16, 7), CornerRadius = new CornerRadius(10) };

    /// <summary>
    /// Lightweight styling: the stock controls keep their behaviour and accessibility, only their colours change.
    /// Gold where Windows would use the accent colour, ivory for text, and soft insets for fields and buttons.
    /// </summary>
    static void Restyle(ResourceDictionary r)
    {
        void Set(Color color, params string[] keys) { var b = new SolidColorBrush(color); foreach (var k in keys) r[k] = b; }

        // Sidebar and content area: let the backdrop through.
        Set(Colors.Transparent, "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground", "NavigationViewTopPaneBackground",
            "NavigationViewContentBackground", "NavigationViewContentGridBorderBrush");
        Set(Gold, "NavigationViewSelectionIndicatorForeground");
        Set(Ink2, "NavigationViewItemForeground");
        Set(Ink, "NavigationViewItemForegroundPointerOver", "NavigationViewItemForegroundSelected", "NavigationViewItemForegroundSelectedPointerOver",
            "NavigationViewItemForegroundPressed", "NavigationViewItemForegroundSelectedPressed");
        Set(C(0x0DFFF4DC), "NavigationViewItemBackgroundPointerOver", "NavigationViewItemBackgroundSelected");
        Set(C(0x14FFF4DC), "NavigationViewItemBackgroundSelectedPointerOver");
        Set(C(0x08FFF4DC), "NavigationViewItemBackgroundPressed", "NavigationViewItemBackgroundSelectedPressed");

        // Toggles: an old-gold track when on, a dark knob; a quiet ivory outline when off.
        Set(GoldDim, "ToggleSwitchFillOn", "ToggleSwitchFillOnPressed");
        Set(Gold, "ToggleSwitchFillOnPointerOver");
        Set(Colors.Transparent, "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver", "ToggleSwitchStrokeOnPressed");
        Set(Base, "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed");
        Set(C(0x330A0E0F), "ToggleSwitchFillOff", "ToggleSwitchFillOffPressed");
        Set(C(0x14FFF4DC), "ToggleSwitchFillOffPointerOver");
        Set(C(0x59EFE8D8), "ToggleSwitchStrokeOff", "ToggleSwitchStrokeOffPressed");
        Set(C(0x8CEFE8D8), "ToggleSwitchStrokeOffPointerOver");
        Set(Ink2, "ToggleSwitchKnobFillOff", "ToggleSwitchKnobFillOffPressed");
        Set(Ink, "ToggleSwitchKnobFillOffPointerOver");
        Set(Ink2, "ToggleSwitchContentForeground", "ToggleSwitchHeaderForeground");

        // Fields and buttons: sunk a little into the card.
        Set(C(0x59080B0C), "ComboBoxBackground", "ButtonBackground");
        Set(C(0x80080B0C), "ComboBoxBackgroundPointerOver", "ButtonBackgroundPointerOver");
        Set(C(0xA6080B0C), "ComboBoxBackgroundPressed", "ButtonBackgroundPressed");
        Set(C(0x1FFFF4DC), "ComboBoxBorderBrush", "ButtonBorderBrush");
        Set(C(0x33FFF4DC), "ComboBoxBorderBrushPointerOver", "ButtonBorderBrushPointerOver");
        Set(C(0x14FFF4DC), "ComboBoxBorderBrushPressed", "ButtonBorderBrushPressed");
        Set(Ink, "ComboBoxForeground", "ComboBoxForegroundPointerOver", "ComboBoxForegroundPressed", "ButtonForeground", "ButtonForegroundPointerOver");
        Set(Ink2, "ButtonForegroundPressed", "ComboBoxDropDownGlyphForeground");
        Set(C(0xF2161C1D), "ComboBoxDropDownBackground");
        Set(C(0x1FFFF4DC), "ComboBoxDropDownBorderBrush");
        Set(Gold, "ComboBoxItemPillFillBrush");

        Set(Gold, "HyperlinkButtonForeground");
        Set(Ink, "HyperlinkButtonForegroundPointerOver");
        Set(GoldDim, "HyperlinkButtonForegroundPressed");

        // Anything else that reaches for the accent (focus, dialogs' primary button) gets the gold too.
        r["AccentFillColorDefaultBrush"] = new SolidColorBrush(GoldDim);
        r["AccentFillColorSecondaryBrush"] = new SolidColorBrush(Gold);
        r["AccentFillColorTertiaryBrush"] = new SolidColorBrush(C(0xFF9E8450));
        r["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(Base);
    }

    // Controls on the open page follow changes made elsewhere (the tray menu's Hide island, Reset): each registers how to
    // re-read its value. _syncing stops a control that is being set from writing the value straight back.
    readonly List<Action> _sync = [];
    bool _syncing;

    void OnSettingsChanged()
    {
        _syncing = true;
        try { foreach (var refresh in _sync) refresh(); }
        finally { _syncing = false; }
    }

    ToggleSwitch Toggle(Func<Settings, bool> get, Action<Settings, bool> set)
    {
        var t = new ToggleSwitch { IsOn = get(Settings.Current), OnContent = "On", OffContent = "Off", MinWidth = 0, FontFamily = Inter };
        t.Toggled += (_, _) => { if (!_syncing && t.IsOn != get(Settings.Current)) Settings.Update(s => set(s, t.IsOn)); };
        _sync.Add(() => { if (t.IsOn != get(Settings.Current)) t.IsOn = get(Settings.Current); });
        return t;
    }

    ComboBox Choice(string[] items, Func<Settings, int> get, Action<Settings, int> set)
    {
        var c = new ComboBox { MinWidth = 180, FontFamily = Inter, CornerRadius = new CornerRadius(10) };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = get(Settings.Current);
        c.SelectionChanged += (_, _) => { if (!_syncing && c.SelectedIndex >= 0 && c.SelectedIndex != get(Settings.Current)) Settings.Update(s => set(s, c.SelectedIndex)); };
        _sync.Add(() => { if (c.SelectedIndex != get(Settings.Current)) c.SelectedIndex = get(Settings.Current); });
        return c;
    }

    /// <summary>A note under a card that shows only while <paramref name="text"/> returns something, e.g. why the glass is solid.</summary>
    TextBlock Note(Func<string?> text)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Inter, FontStyle = FontStyle.Normal, FontSize = 12, Foreground = new SolidColorBrush(Caution), Margin = new Thickness(70, -2, 16, 4) };
        void Refresh() { string? s = text(); t.Text = s ?? ""; t.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible; }
        Refresh();
        _sync.Add(Refresh);
        return t;
    }
}
