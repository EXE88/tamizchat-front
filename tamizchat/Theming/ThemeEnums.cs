namespace TamizChat.Theming;

/// <summary>The colour families the user can pick between.</summary>
public enum ThemeFamily
{
    SaltAndPepper,
    VioletAndLavender,
    CarbonAndLime,
}

/// <summary>Light or dark, or whatever Windows is currently set to.</summary>
public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// The window materials, under the names the user sees. These map onto WinUI's
/// backdrops, whose own names ("Mica Alt") mean nothing to somebody who has not
/// read the WinUI documentation.
/// </summary>
public enum AppBackdrop
{
    /// <summary>Mica.</summary>
    Matte,

    /// <summary>Mica Alt.</summary>
    MatteHigh,

    /// <summary>Desktop Acrylic. The default.</summary>
    Glass,

    /// <summary>Desktop Acrylic, thin.</summary>
    GlassHigh,
}
