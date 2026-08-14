using System.Globalization;
using System.Resources;
using Microsoft.UI.Xaml;

namespace TamizChat.Localization;

/// <summary>
/// Every user-visible string in the app.
///
/// This is a plain .NET <see cref="ResourceManager"/> over embedded .resx files
/// rather than WinUI's `x:Uid` and .resw. Two reasons: most of this app's UI is
/// built in C# rather than XAML, so `x:Uid` would only reach a minority of the
/// strings; and .resw resolves through PRI, which is the part of the resource
/// stack that is least happy in an unpackaged app. A ResourceManager behaves the
/// same packaged or not.
///
/// Look-ups never throw. A key with no translation falls back to English, and a
/// key that does not exist at all comes back as the key itself — a missing
/// string should look wrong in the UI, not crash the page showing it.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Resources =
        new("TamizChat.Localization.Strings", typeof(Loc).Assembly);

    /// <summary>The language currently in use: <c>en</c> or <c>fa</c>.</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>Persian is written right to left; English is not.</summary>
    public static bool IsRightToLeft => Language == "fa";

    public static FlowDirection FlowDirection =>
        IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Raised after the language changes, so open pages can redraw.</summary>
    public static event EventHandler? LanguageChanged;

    public static void Initialize(string language) => Apply(language);

    public static void SetLanguage(string language)
    {
        if (language == Language)
        {
            return;
        }

        Apply(language);
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The translation for a key, or the key itself if there is none.</summary>
    public static string Get(string key)
    {
        try
        {
            return Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    /// <summary>A translation with <c>{0}</c>-style placeholders filled in.</summary>
    public static string Get(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), arguments);

    private static void Apply(string language)
    {
        Language = language == "fa" ? "fa" : "en";

        var culture = new CultureInfo(Language == "fa" ? "fa-IR" : "en-US");

        // UICulture picks the .resx; Culture would also switch number and date
        // formatting to Persian digits, which is deliberately not done — a room
        // count in Eastern Arabic numerals next to a latin server address reads
        // worse than plain digits, and the app is full of such mixtures.
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
