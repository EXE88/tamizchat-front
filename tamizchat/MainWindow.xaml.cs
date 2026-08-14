using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TamizChat.Services;
using TamizChat.Theming;

namespace TamizChat;

public sealed partial class MainWindow : Window
{
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();

        // The content is drawn all the way up through the title bar, and this
        // strip is what the user can still drag the window by.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle));
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SizeAndCentre(appWindow, handle, 1100, 760);

        ThemeManager.Instance.Initialize(this);

        ThemeBox.SelectedIndex = (int)ThemeManager.Instance.Family;
        ModeBox.SelectedIndex = (int)ThemeManager.Instance.Mode;
        BackdropBox.SelectedIndex = (int)ThemeManager.Instance.Backdrop;
        _loading = false;

        ShowStatus();

        if (Environment.GetEnvironmentVariable("TAMIZCHAT_SELFTEST") == "1")
        {
            RunSelfTest();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Sizes the window in layout units and puts it in the middle of the screen.
    ///
    /// AppWindow.Resize works in physical pixels, so passing a layout size
    /// straight through gives a window that is far too small on a scaled display
    /// — at 250% a "1100x760" window only has 440x304 of usable layout space,
    /// and the page gets clipped.
    /// </summary>
    private static void SizeAndCentre(AppWindow appWindow, IntPtr handle, int width, int height)
    {
        var scale = GetDpiForWindow(handle) / 96.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var size = new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale));
        appWindow.Resize(size);

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        appWindow.Move(new Windows.Graphics.PointInt32(
            area.WorkArea.X + ((area.WorkArea.Width - size.Width) / 2),
            area.WorkArea.Y + ((area.WorkArea.Height - size.Height) / 2)));
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.Instance.SetFamily((ThemeFamily)ThemeBox.SelectedIndex);
        ShowStatus();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.Instance.SetMode((AppThemeMode)ModeBox.SelectedIndex);
        ShowStatus();
    }

    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.Instance.SetBackdrop((AppBackdrop)BackdropBox.SelectedIndex);
        ShowStatus();
    }

    private void ShowStatus()
    {
        var manager = ThemeManager.Instance;
        Status.Text =
            $"{manager.Family} / {(manager.IsDark ? "dark" : "light")} / {manager.Backdrop}\n" +
            SettingsStore.FilePath;
    }

    /// <summary>
    /// Walks every theme, mode and backdrop combination and records whether each
    /// one applied, so the phase can be verified without a person watching the
    /// window. Triggered by TAMIZCHAT_SELFTEST=1.
    /// </summary>
    private void RunSelfTest()
    {
        var log = new StringBuilder();
        log.AppendLine($"packaged   = {IsPackaged()}");
        log.AppendLine($"settings   = {SettingsStore.FilePath}");
        log.AppendLine($"titlebar   = extended");

        var families = Enum.GetValues<ThemeFamily>();
        var modes = Enum.GetValues<AppThemeMode>();
        var backdrops = Enum.GetValues<AppBackdrop>();

        foreach (var family in families)
        {
            foreach (var mode in modes)
            {
                try
                {
                    ThemeManager.Instance.SetFamily(family);
                    ThemeManager.Instance.SetMode(mode);
                    var palette = ThemeManager.Instance.Palette;
                    log.AppendLine(
                        $"OK    {family,-18} {mode,-6} -> tint {Hex(palette.Tint)} " +
                        $"text {Hex(palette.TextPrimary)} accent {Hex(palette.Accent)}");
                }
                catch (Exception ex)
                {
                    log.AppendLine($"FAIL  {family} {mode}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        foreach (var backdrop in backdrops)
        {
            try
            {
                ThemeManager.Instance.SetBackdrop(backdrop);
                log.AppendLine($"OK    backdrop {backdrop} -> {SystemBackdrop?.GetType().Name ?? "set via DevWinUI"}");
            }
            catch (Exception ex)
            {
                log.AppendLine($"FAIL  backdrop {backdrop}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Leave the saved settings as the defaults rather than whatever the
        // sweep ended on.
        ThemeManager.Instance.SetFamily(ThemeFamily.SaltAndPepper);
        ThemeManager.Instance.SetMode(AppThemeMode.System);
        ThemeManager.Instance.SetBackdrop(AppBackdrop.Glass);

        log.AppendLine($"saved      = {File.Exists(SettingsStore.FilePath)}");
        if (File.Exists(SettingsStore.FilePath))
        {
            log.AppendLine(File.ReadAllText(SettingsStore.FilePath));
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), log.ToString());

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Close();
        timer.Start();
    }

    private static string Hex(Windows.UI.Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

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
