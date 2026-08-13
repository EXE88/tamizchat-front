using System.Text;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TamizChat;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        Subtitle.Text = "unpackaged shell";

        // Setting TAMIZCHAT_SELFTEST=1 runs the environment checks and exits, so the
        // window can be verified from a script without a human looking at it.
        if (Environment.GetEnvironmentVariable("TAMIZCHAT_SELFTEST") == "1")
        {
            RunSelfTest();
        }
    }

    private void RunSelfTest()
    {
        var log = new StringBuilder();

        void Check(string name, Action action)
        {
            try
            {
                action();
                log.AppendLine($"OK    {name}");
            }
            catch (Exception ex)
            {
                log.AppendLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        log.AppendLine($"packaged   = {IsPackaged()}");
        log.AppendLine($"runtime    = {Environment.Version}");
        log.AppendLine($"os         = {Environment.OSVersion.Version}");
        log.AppendLine($"base dir   = {AppContext.BaseDirectory}");

        Check("ExtendsContentIntoTitleBar", () => ExtendsContentIntoTitleBar = true);
        Check("Mica", () => SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base });
        Check("Mica Alt", () => SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt });
        Check("Acrylic", () => SystemBackdrop = new DesktopAcrylicBackdrop());
        Check("Acrylic Thin", () =>
        {
            using var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        });
        Check("AppWindow.TitleBar tall", () =>
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
            AppWindow.GetFromWindowId(id).TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        });
        Check("DevWinUI loaded", () =>
        {
            var asm = typeof(DevWinUI.ThemeService).Assembly;
            log.AppendLine($"      DevWinUI {asm.GetName().Version}");
        });

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "selftest.txt"),
            log.ToString());

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Close();
        timer.Start();
    }

    private static bool IsPackaged()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch
        {
            return false;
        }
    }
}
