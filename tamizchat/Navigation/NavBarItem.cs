namespace TamizChat.Navigation;

/// <summary>What happens when an item in the bottom bar is pressed.</summary>
public enum NavItemKind
{
    /// <summary>Goes to a page.</summary>
    Navigate,

    /// <summary>Flips between two states, like the microphone.</summary>
    Toggle,

    /// <summary>Opens a floating menu of choices.</summary>
    Menu,

    /// <summary>Runs an action, like disconnecting.</summary>
    Command,
}

/// <summary>
/// One entry in the floating bottom bar.
///
/// <see cref="Key"/> is the item's identity everywhere — selection, state and
/// event handling all go through it. Deriving identity from <see cref="Page"/>
/// instead breaks as soon as two entries share a page type.
/// </summary>
public sealed class NavBarItem
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public required string Glyph { get; init; }

    public NavItemKind Kind { get; init; } = NavItemKind.Navigate;

    /// <summary>Only meaningful for <see cref="NavItemKind.Navigate"/>.</summary>
    public Type? Page { get; init; }

    // --- toggles ---

    /// <summary>Glyph shown in the off state; falls back to <see cref="Glyph"/>.</summary>
    public string? OffGlyph { get; init; }

    /// <summary>Label shown in the off state; falls back to <see cref="Label"/>.</summary>
    public string? OffLabel { get; init; }

    public bool StartsOn { get; init; }

    /// <summary>
    /// True when "off" is the state worth flagging — a muted microphone or a
    /// deafened speaker. Those draw in the danger colour rather than the accent,
    /// because being live is the normal state and should not shout.
    /// </summary>
    public bool WarnWhenOff { get; init; }

    // --- menus ---

    public IReadOnlyList<string> MenuOptions { get; init; } = [];

    // --- commands ---

    /// <summary>Draws in the danger colour. Used for Disconnect.</summary>
    public bool IsDangerous { get; init; }
}

/// <summary>Reports a toggle flip or a menu choice from the bar.</summary>
public sealed class NavBarStateEventArgs(NavBarItem item, bool isOn, string? option) : EventArgs
{
    public NavBarItem Item { get; } = item;

    /// <summary>The new state, for toggles.</summary>
    public bool IsOn { get; } = isOn;

    /// <summary>The chosen entry, for menus.</summary>
    public string? Option { get; } = option;
}
