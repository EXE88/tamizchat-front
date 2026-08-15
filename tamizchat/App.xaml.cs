using Microsoft.UI.Xaml;
using TamizChat.Services;

namespace TamizChat;

public partial class App : Application
{
    /// <summary>The app's single main window.</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // Before anything else can fail: a crash during a long upload left
        // nothing behind at all, which is why this is the first thing set up.
        CrashLog.Install(this);
    }

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
