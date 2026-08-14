using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TamizChat.Localization;
using TamizChat.Navigation;
using Windows.Foundation;

namespace TamizChat.Controls;

/// <summary>
/// The floating bottom navigation bar: an icon above a label per item, rounded,
/// glass, and carrying whichever item set the shell is currently in.
///
/// Two things are deliberate here. Buttons are built once per item set and then
/// only restyled — rebuilding them on every selection change destroyed the button
/// being pressed, so fast clicking landed on whatever replaced it. And selection
/// is drawn by a single pill that slides between items, rather than a background
/// switching on and off per button.
/// </summary>
public sealed partial class FloatingNavBar : UserControl
{
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(280);

    /// <summary>How many buttons are shown before the rest go to the overflow menu.</summary>
    public int MaxPrimaryItems { get; set; } = 10;

    private readonly Dictionary<string, Button> _buttons = [];
    private readonly Dictionary<string, bool> _toggles = [];
    private IReadOnlyList<NavBarItem> _items = [];
    private string? _selectedKey;
    private bool _pillPlaced;

    public FloatingNavBar()
    {
        InitializeComponent();

        // ThemeShadow only draws for an element that has been pushed forward in z.
        BarRoot.Translation = new Vector3(0, 0, 24);

        // Buttons have no measured width until the first layout pass, so the pill
        // cannot be positioned from Build().
        ItemsHost.LayoutUpdated += (_, _) =>
        {
            if (!_pillPlaced)
            {
                MovePill(animate: false);
            }
        };
    }

    /// <summary>Raised for navigate and command items.</summary>
    public event EventHandler<NavBarItem>? ItemInvoked;

    /// <summary>Raised when a toggle flips or a menu entry is chosen.</summary>
    public event EventHandler<NavBarStateEventArgs>? StateChanged;

    public string? SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (_selectedKey == value)
            {
                return;
            }

            _selectedKey = value;
            RefreshVisuals();
            MovePill(animate: true);
        }
    }

    public bool IsOn(string key) => _toggles.TryGetValue(key, out var on) && on;

    /// <summary>
    /// Forces a toggle back without raising <see cref="StateChanged"/>.
    ///
    /// For the case where the action behind a toggle did not happen — a share
    /// the user cancelled, a camera that would not open — so the button has to
    /// stop claiming it did.
    /// </summary>
    public void SetToggle(string key, bool on)
    {
        if (!_toggles.ContainsKey(key))
        {
            return;
        }

        _toggles[key] = on;
        RefreshVisuals();
    }

    /// <summary>
    /// Rebuilds the buttons even though the item set has not changed.
    ///
    /// Needed after a language change: the items are the same objects, so
    /// <see cref="SetItems"/> would short-circuit and the bar would keep the old
    /// language's labels. Toggle state survives, because it lives in _toggles
    /// rather than on the buttons.
    /// </summary>
    public void Retranslate()
    {
        _pillPlaced = false;
        Build();
    }

    /// <summary>
    /// Temporarily replaces what an item looks like and does.
    ///
    /// Used for the soundboard: while a clip is playing, Effects becomes Stop.
    /// The flyout is detached rather than left in place, because a button that
    /// says Stop but opens a menu of clips is worse than either behaviour on its
    /// own — and a Flyout opens on click whatever the Click handler does.
    /// </summary>
    public void SetOverride(string key, string? glyph, string? label)
    {
        if (!_buttons.TryGetValue(key, out var button) || button.Tag is not NavBarItem item)
        {
            return;
        }

        if (glyph is null)
        {
            _overrides.Remove(key);

            if (item.Kind == NavItemKind.Menu)
            {
                button.Flyout = BuildMenu(item);
            }
        }
        else
        {
            _overrides[key] = (glyph, label ?? "");
            button.Flyout = null;
        }

        RefreshVisuals();
    }

    private readonly Dictionary<string, (string Glyph, string Label)> _overrides = [];

    public void SetItems(IReadOnlyList<NavBarItem> items, string? selectedKey)
    {
        // Only tear the buttons down when the set itself changed. Selection is a
        // restyle plus a slide, not a rebuild.
        if (ReferenceEquals(_items, items))
        {
            SelectedKey = selectedKey;
            return;
        }

        _items = items;
        _selectedKey = selectedKey;
        _pillPlaced = false;
        Build();
    }

    private void Build()
    {
        ItemsHost.Children.Clear();
        _buttons.Clear();
        SelectionPill.Opacity = 0;

        if (_items.Count == 0)
        {
            return;
        }

        foreach (var item in _items)
        {
            if (item.Kind == NavItemKind.Toggle && !_toggles.ContainsKey(item.Key))
            {
                _toggles[item.Key] = item.StartsOn;
            }
        }

        var primaryCount = _items.Count <= MaxPrimaryItems ? _items.Count : MaxPrimaryItems - 1;

        for (var i = 0; i < primaryCount; i++)
        {
            var button = CreateButton(_items[i]);
            _buttons[_items[i].Key] = button;
            ItemsHost.Children.Add(button);
        }

        if (primaryCount < _items.Count)
        {
            ItemsHost.Children.Add(CreateOverflowButton(_items.Skip(primaryCount).ToList()));
        }

        RefreshVisuals();
    }

    private Button CreateButton(NavBarItem item)
    {
        var button = new Button
        {
            Tag = item,
            Style = (Style)Resources["NavItemStyle"],
            Padding = new Thickness(10, 7, 10, 6),
            MinWidth = 68,
            Content = BuildContent(),
        };

        if (item.Kind == NavItemKind.Menu)
        {
            button.Flyout = BuildMenu(item);

            // Also handled directly, for when an override has taken the flyout
            // away and the button is standing in for something else.
            button.Click += (_, _) =>
            {
                if (_overrides.ContainsKey(item.Key))
                {
                    StateChanged?.Invoke(this, new NavBarStateEventArgs(item, true, null));
                }
            };
        }
        else
        {
            button.Click += (_, _) => OnPressed(item);
        }

        ToolTipService.SetToolTip(button, item.Label);
        return button;
    }

    /// <summary>
    /// Builds a menu item's flyout from its current options.
    ///
    /// Rebuilt on every open rather than cached, because the soundboard's list
    /// is edited in Settings and a cached menu would show yesterday's clips.
    /// </summary>
    private MenuFlyout BuildMenu(NavBarItem item)
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };

        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            foreach (var option in item.MenuOptions)
            {
                var entry = new MenuFlyoutItem { Text = option };
                entry.Click += (_, _) => StateChanged?.Invoke(this, new NavBarStateEventArgs(item, true, option));
                flyout.Items.Add(entry);
            }
        };

        return flyout;
    }

    private void OnPressed(NavBarItem item)
    {
        if (item.Kind == NavItemKind.Toggle)
        {
            var next = !IsOn(item.Key);
            _toggles[item.Key] = next;
            RefreshVisuals();
            StateChanged?.Invoke(this, new NavBarStateEventArgs(item, next, null));
            return;
        }

        ItemInvoked?.Invoke(this, item);
    }

    private Button CreateOverflowButton(IReadOnlyList<NavBarItem> overflow)
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        foreach (var item in overflow)
        {
            var entry = new MenuFlyoutItem
            {
                Text = item.Label,
                Icon = new FontIcon { Glyph = item.Glyph },
            };
            entry.Click += (_, _) => OnPressed(item);
            flyout.Items.Add(entry);
        }

        var button = new Button
        {
            Style = (Style)Resources["NavItemStyle"],
            Padding = new Thickness(10, 7, 10, 6),
            MinWidth = 68,
            Content = BuildContent(),
            Flyout = flyout,
        };

        // Written as an escape, not pasted: glyphs live in Unicode's private use
        // area and get silently stripped in transit, which is exactly what had
        // happened here — the More button was rendering with no icon at all.
        var more = Loc.Get("Nav.More");
        Paint(button, "", more, Brush("TcTextSecondaryBrush"));
        ToolTipService.SetToolTip(button, more);
        return button;
    }

    /// <summary>
    /// Recolours every button for the current selection and toggle states.
    ///
    /// Only navigation takes the pill. A toggle that is on shows it in the accent
    /// colour instead, because two toggles can be on at once and one sliding pill
    /// cannot be in two places.
    /// </summary>
    private void RefreshVisuals()
    {
        foreach (var item in _items)
        {
            if (!_buttons.TryGetValue(item.Key, out var button))
            {
                continue;
            }

            var glyph = item.Glyph;
            var label = item.Label;
            var foreground = Brush("TcTextSecondaryBrush");

            // An override wins over everything below: while it is in place the
            // item is standing in for something else entirely.
            if (_overrides.TryGetValue(item.Key, out var replacement))
            {
                Paint(button, replacement.Glyph, replacement.Label, Brush("TcAccentBrush"));
                continue;
            }

            switch (item.Kind)
            {
                case NavItemKind.Navigate:
                    foreground = item.Key == _selectedKey
                        ? Brush("TcOnAccentBrush")
                        : Brush("TcTextSecondaryBrush");
                    break;

                case NavItemKind.Toggle:
                    var on = IsOn(item.Key);
                    if (!on)
                    {
                        glyph = item.OffGlyph ?? item.Glyph;
                        label = item.OffLabel ?? item.Label;
                    }

                    foreground = item.WarnWhenOff
                        // Being live is the normal state, so only muted is called out.
                        ? (on ? Brush("TcTextSecondaryBrush") : Brush("TcDangerBrush"))
                        // Broadcasting is the notable state.
                        : (on ? Brush("TcAccentBrush") : Brush("TcTextSecondaryBrush"));
                    break;

                case NavItemKind.Command:
                    foreground = item.IsDangerous ? Brush("TcDangerBrush") : Brush("TcTextSecondaryBrush");
                    break;
            }

            Paint(button, glyph, label, foreground);
        }
    }

    /// <summary>Slides the selection pill onto the selected item.</summary>
    private void MovePill(bool animate)
    {
        if (_selectedKey is null
            || !_buttons.TryGetValue(_selectedKey, out var target)
            || target.ActualWidth <= 0)
        {
            SelectionPill.Opacity = 0;
            return;
        }

        var x = target.TransformToVisual(ItemsHost).TransformPoint(new Point(0, 0)).X;
        var width = target.ActualWidth;
        _pillPlaced = true;

        if (!animate || SelectionPill.Opacity == 0)
        {
            // Nothing to slide from on the first placement, or after the item set
            // changed — appearing in place reads better than flying in from zero.
            PillOffset.X = x;
            SelectionPill.Width = width;
            FadeTo(SelectionPill, 1);
            return;
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(PillOffset, "X", x));
        storyboard.Children.Add(Animate(SelectionPill, "Width", width, dependent: true));
        storyboard.Begin();
    }

    private static void FadeTo(UIElement element, double opacity)
    {
        var storyboard = new Storyboard();
        storyboard.Children.Add(Animate(element, "Opacity", opacity, duration: TimeSpan.FromMilliseconds(160)));
        storyboard.Begin();
    }

    private static DoubleAnimation Animate(
        DependencyObject target,
        string property,
        double to,
        bool dependent = false,
        TimeSpan? duration = null)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration ?? SlideDuration),
            // Fast at first and easing out is what makes the movement feel
            // like it settles rather than stops.
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },

            // Width and Opacity cannot run on the composition thread.
            EnableDependentAnimation = dependent || property == "Opacity",
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static StackPanel BuildContent() => new()
    {
        Spacing = 2,
        HorizontalAlignment = HorizontalAlignment.Center,
        Children =
        {
            new FontIcon { FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
        },
    };

    private static void Paint(Button button, string glyph, string label, Brush foreground)
    {
        var panel = (StackPanel)button.Content;
        var icon = (FontIcon)panel.Children[0];
        var text = (TextBlock)panel.Children[1];

        icon.Glyph = glyph;
        icon.Foreground = foreground;
        text.Text = label;
        text.Foreground = foreground;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
