using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace TamizChat.Navigation;

/// <summary>
/// How one page gives way to the next.
///
/// Sideways movement between the top-level pages, and a drill for anything that
/// goes a level deeper — joining a server, or opening a feature page from inside
/// one. The two read very differently, which is the point: the user can tell
/// whether they moved across or moved in.
/// </summary>
public enum NavTransition
{
    None,
    SlideFromLeft,
    SlideFromRight,
    DrillIn,
    DrillOut,
}

/// <summary>Drives the shell's content frame.</summary>
public sealed class NavigationService
{
    private Frame? _frame;

    public static NavigationService Instance { get; } = new();

    /// <summary>Raised after each navigation, so the shell can refresh its chrome.</summary>
    public event EventHandler? Navigated;

    public Type? CurrentPageType => _frame?.CurrentSourcePageType;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void Initialize(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += (_, _) => Navigated?.Invoke(this, EventArgs.Empty);
    }

    public void Navigate(Type page, NavTransition transition, object? parameter = null)
    {
        if (_frame is null || _frame.CurrentSourcePageType == page)
        {
            return;
        }

        _frame.Navigate(page, parameter, ToTransitionInfo(transition));
    }

    /// <summary>
    /// Goes back with the reverse of the animation that brought us here. A drill
    /// played backwards is what makes leaving a server feel like leaving rather
    /// than like arriving somewhere new.
    /// </summary>
    public void GoBack(NavTransition transition = NavTransition.DrillOut)
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack(ToTransitionInfo(transition));
        }
    }

    /// <summary>
    /// Picks the slide direction from where the target sits relative to the
    /// current page, so the pages feel laid out left to right rather than
    /// arriving from an arbitrary side.
    /// </summary>
    public static NavTransition SlideTowards(int fromIndex, int toIndex) =>
        toIndex > fromIndex ? NavTransition.SlideFromRight : NavTransition.SlideFromLeft;

    private static NavigationTransitionInfo ToTransitionInfo(NavTransition transition) => transition switch
    {
        NavTransition.SlideFromLeft => new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromLeft,
        },
        NavTransition.SlideFromRight => new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromRight,
        },
        NavTransition.DrillIn or NavTransition.DrillOut => new DrillInNavigationTransitionInfo(),
        _ => new SuppressNavigationTransitionInfo(),
    };
}
