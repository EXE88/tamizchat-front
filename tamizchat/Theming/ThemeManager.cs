using DevWinUI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TamizChat.Services;
using Windows.UI;

namespace TamizChat.Theming;

/// <summary>
/// Owns the app's look: which colour family is active, whether it is showing its
/// light or dark variant, and which window material is behind it.
///
/// Colour changes work by rewriting the Color of the shared brushes declared in
/// Themes/Palette.xaml. Swapping merged ResourceDictionaries at runtime does not
/// reliably re-evaluate StaticResource references in WinUI, but mutating a brush
/// everything already points at always does.
/// </summary>
public sealed class ThemeManager
{
    private static readonly string[] BrushKeys =
    [
        "TcTintBrush", "TcSurfaceBrush", "TcSurfaceHoverBrush", "TcBorderBrush",
        "TcTextPrimaryBrush", "TcTextSecondaryBrush",
        "TcAccentBrush", "TcAccentHoverBrush", "TcOnAccentBrush",
    ];

    private readonly ThemeService _devWinUi = new();
    private Window? _window;
    private bool _devWinUiReady;

    public static ThemeManager Instance { get; } = new();

    /// <summary>Raised after a change has been applied, so open pages can react.</summary>
    public event EventHandler? ThemeChanged;

    public ThemeFamily Family => SettingsStore.Current.Theme;

    public AppThemeMode Mode => SettingsStore.Current.Mode;

    public AppBackdrop Backdrop => SettingsStore.Current.Backdrop;

    /// <summary>The palette currently on screen.</summary>
    public ThemePalette Palette => ThemePalette.Get(Family, IsDark);

    /// <summary>Whether the dark variant of the current family is showing.</summary>
    public bool IsDark => Mode switch
    {
        AppThemeMode.Light => false,
        AppThemeMode.Dark => true,
        _ => (_window?.Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark
             || (_window is null && Application.Current.RequestedTheme == ApplicationTheme.Dark),
    };

    public void Initialize(Window window)
    {
        _window = window;

        try
        {
            // Our own settings file is the source of truth, so DevWinUI must not
            // also try to persist this — in an unpackaged app its storage would
            // have no package identity to write under.
            _devWinUi
                .ConfigureAutoSave(false)
                .ConfigureElementTheme(ToElementTheme(Mode))
                .ConfigureBackdrop(ToBackdropType(Backdrop))
                .Initialize(window);

            _devWinUiReady = true;
        }
        catch (Exception)
        {
            // Fall back to driving the window directly. Everything below already
            // works without DevWinUI; it just saves us the plumbing.
            _devWinUiReady = false;
        }

        ApplyMode();
        ApplyBackdrop();
        ApplyPalette();

        // With "System" selected, the palette has to follow Windows flipping
        // between light and dark while the app is open.
        if (window.Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) =>
            {
                if (Mode == AppThemeMode.System)
                {
                    ApplyPalette();
                }
            };
        }
    }

    public void SetFamily(ThemeFamily family)
    {
        SettingsStore.Current.Theme = family;
        SettingsStore.Save();
        ApplyPalette();
    }

    public void SetMode(AppThemeMode mode)
    {
        SettingsStore.Current.Mode = mode;
        SettingsStore.Save();
        ApplyMode();
        ApplyPalette();
    }

    public void SetBackdrop(AppBackdrop backdrop)
    {
        SettingsStore.Current.Backdrop = backdrop;
        SettingsStore.Save();
        ApplyBackdrop();
    }

    /// <summary>Rewrites every shared brush to the current palette.</summary>
    public void ApplyPalette()
    {
        var palette = Palette;
        var colors = new[]
        {
            palette.Tint, palette.Surface, palette.SurfaceHover, palette.Border,
            palette.TextPrimary, palette.TextSecondary,
            palette.Accent, palette.AccentHover, palette.OnAccent,
        };

        for (var i = 0; i < BrushKeys.Length; i++)
        {
            SetBrush(BrushKeys[i], colors[i]);
        }

        // Standard WinUI controls draw their accent from these, so overriding
        // them keeps built-in controls in step with the chosen family.
        SetBrush("AccentFillColorDefaultBrush", palette.Accent);
        SetBrush("AccentFillColorSecondaryBrush", palette.AccentHover);
        SetBrush("AccentFillColorTertiaryBrush", palette.AccentHover);
        SetBrush("TextOnAccentFillColorPrimaryBrush", palette.OnAccent);

        // The floating bar's own material is acrylic, not a solid brush, so its
        // tint has to be updated separately.
        SetAcrylicTint("TcBarAcrylicBrush", palette.Surface);

        // The board needs the same colour with the transparency taken out.
        SetBrush("TcBoardBrush", Color.FromArgb(0xFF, palette.Surface.R, palette.Surface.G, palette.Surface.B));

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyMode()
    {
        if (_window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = ToElementTheme(Mode);
        }
    }

    private void ApplyBackdrop()
    {
        if (_window is null)
        {
            return;
        }

        if (_devWinUiReady)
        {
            // Fire and forget: this only touches the window's backdrop.
            _ = _devWinUi.SetBackdropTypeWithoutSaveAsync(ToBackdropType(Backdrop));
            return;
        }

        _window.SystemBackdrop = Backdrop switch
        {
            AppBackdrop.Matte => new MicaBackdrop { Kind = MicaKind.Base },
            AppBackdrop.MatteHigh => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            AppBackdrop.GlassHigh => new DesktopAcrylicBackdrop(),
            _ => new DesktopAcrylicBackdrop(),
        };
    }

    /// <summary>
    /// An acrylic brush does its own blending, so it needs an opaque tint rather
    /// than the palette's semi-transparent surface colour.
    /// </summary>
    private static void SetAcrylicTint(string key, Color color)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value)
            && value is AcrylicBrush acrylic)
        {
            var opaque = Color.FromArgb(0xFF, color.R, color.G, color.B);
            acrylic.TintColor = opaque;
            acrylic.FallbackColor = opaque;
        }
    }

    private static void SetBrush(string key, Color color)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value)
            && value is SolidColorBrush brush)
        {
            brush.Color = color;
            return;
        }

        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static ElementTheme ToElementTheme(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => ElementTheme.Light,
        AppThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static BackdropType ToBackdropType(AppBackdrop backdrop) => backdrop switch
    {
        AppBackdrop.Matte => BackdropType.Mica,
        AppBackdrop.MatteHigh => BackdropType.MicaAlt,
        AppBackdrop.GlassHigh => BackdropType.AcrylicThin,
        _ => BackdropType.Acrylic,
    };
}
