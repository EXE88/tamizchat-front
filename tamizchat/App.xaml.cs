using Microsoft.UI.Xaml;

namespace TamizChat;

public partial class App : Application
{
    /// <summary>The app's single main window.</summary>
    public static Window? MainWindow { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
