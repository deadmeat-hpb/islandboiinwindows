using Microsoft.UI.Xaml;

namespace DynamicIsland;

public partial class App : Application
{
    IslandWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Spring.SelfCheck();
        _window = new IslandWindow();
        _window.Activate();
    }
}
