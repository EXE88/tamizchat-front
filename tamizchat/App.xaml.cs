using Microsoft.UI.Xaml;
using TamizChat.Services;

namespace TamizChat;

public partial class App : Application
{
    /// <summary>The app's single main window.</summary>
    public static Window? MainWindow { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Settings have to be read before the window exists, because the window
        // is created with the saved theme and backdrop already applied rather
        // than flashing the defaults first.
        SettingsStore.Load();

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
