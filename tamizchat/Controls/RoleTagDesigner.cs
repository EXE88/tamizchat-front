using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TamizChat.Localization;

namespace TamizChat.Controls;

/// <summary>
/// Designs a role's tag, with the result shown as you change it.
///
/// The preview is the whole point. Every control here changes something you
/// cannot picture from its name — "Sunset" and "Metal" mean nothing until you
/// see them — so the tag is rebuilt on every change and sits at the top where it
/// cannot be missed.
///
/// Presets exist for the same reason: starting from a finished look and altering
/// it is far easier than assembling one from seven dropdowns, and it means the
/// good combinations are discoverable rather than hidden in the space of all
/// possible ones.
/// </summary>
public sealed class RoleTagDesigner : StackPanel
{
    // The palette lives in Colours, so the bot picker offers the same one.

    /// <summary>Complete looks, so nobody has to start from nothing.</summary>
    private static readonly (string Name, RoleTagStyle Style)[] Presets =
    [
        ("Plain", new() { Background = "#5b6eae", BackgroundTwo = "#5b6eae", Pattern = TagPattern.Solid, Shape = TagShape.Pill }),
        ("Founder", new() { Background = "#f1c40f", BackgroundTwo = "#e67e22", Pattern = TagPattern.Gradient, Shape = TagShape.Pill, Animation = TagAnimation.Shimmer, Icon = "\uE735" }),
        ("Staff", new() { Background = "#e74c3c", BackgroundTwo = "#c0392b", Pattern = TagPattern.Gradient, Shape = TagShape.Rounded, Icon = "\uE7EF" }),
        ("Veteran", new() { Background = "#9b59b6", BackgroundTwo = "#3498db", Pattern = TagPattern.Sunset, Shape = TagShape.Pill, Animation = TagAnimation.Glow }),
        ("Ghost", new() { Background = "#607d8b", BackgroundTwo = "#607d8b", Pattern = TagPattern.Outline, Shape = TagShape.Pill }),
        ("Frost", new() { Background = "#1abc9c", BackgroundTwo = "#3498db", Pattern = TagPattern.Glass, Shape = TagShape.Rounded, Animation = TagAnimation.Float }),
        ("Chrome", new() { Background = "#8e9aa6", BackgroundTwo = "#8e9aa6", Pattern = TagPattern.Metal, Shape = TagShape.Cut }),
        ("Carnival", new() { Background = "#e91e8c", BackgroundTwo = "#f39c12", Pattern = TagPattern.Stripes, Shape = TagShape.Cut, Animation = TagAnimation.Pulse }),
        ("Prism", new() { Background = "#3498db", BackgroundTwo = "#9b59b6", Pattern = TagPattern.Solid, Shape = TagShape.Pill, Animation = TagAnimation.Rainbow }),
    ];

    /// <summary>Glyphs that read clearly at eleven pixels.</summary>
    private static readonly (string Name, string Glyph)[] Icons =
    [
        ("None", ""),
        ("Star", "\uE735"),
        ("Admin", "\uE7EF"),
        ("Bolt", "\uE945"),
        ("Heart", "\uEB51"),
        ("Flag", "\uE7C1"),
        ("Wrench", "\uE90F"),
        ("Globe", "\uE774"),
        ("Pin", "\uE718"),
    ];

    private readonly Grid _previewHost;
    private readonly TextBox _nameSource;

    public RoleTagDesigner(RoleTagStyle style, TextBox nameSource)
    {
        Style = style.Clone();
        _nameSource = nameSource;
        Spacing = 10;

        _previewHost = new Grid
        {
            Height = 54,
            Padding = new Thickness(12),
            Background = (Brush)Application.Current.Resources["TcSurfaceHoverBrush"],
            CornerRadius = (CornerRadius)Application.Current.Resources["TcCardCornerRadius"],
        };

        Children.Add(Label(Loc.Get("Tag.Preview")));
        Children.Add(_previewHost);

        Children.Add(Label(Loc.Get("Tag.Presets")));
        Children.Add(PresetRow());

        Children.Add(Label(Loc.Get("Tag.Colour")));
        Children.Add(ColourRow(first: true));

        Children.Add(Label(Loc.Get("Tag.SecondColour")));
        Children.Add(ColourRow(first: false));

        Children.Add(Choice(Loc.Get("Tag.Pattern"), Enum.GetValues<TagPattern>(), Style.Pattern, v => Style.Pattern = v));
        Children.Add(Choice(Loc.Get("Tag.Shape"), Enum.GetValues<TagShape>(), Style.Shape, v => Style.Shape = v));
        Children.Add(Choice(Loc.Get("Tag.Animation"), Enum.GetValues<TagAnimation>(), Style.Animation, v => Style.Animation = v));
        Children.Add(IconRow());

        var bold = new CheckBox { Content = Loc.Get("Tag.Bold"), IsChecked = Style.Bold };
        bold.Checked += (_, _) => Set(() => Style.Bold = true);
        bold.Unchecked += (_, _) => Set(() => Style.Bold = false);
        Children.Add(bold);

        // The preview shows the name being typed, so it is the real thing rather
        // than a stand-in word.
        nameSource.TextChanged += (_, _) => Refresh();
        Refresh();
    }

    public RoleTagStyle Style { get; }

    private void Set(Action change)
    {
        change();
        Refresh();
    }

    private void Refresh()
    {
        _previewHost.Children.Clear();

        var name = string.IsNullOrWhiteSpace(_nameSource.Text) ? Loc.Get("Tag.SampleName") : _nameSource.Text.Trim();

        _previewHost.Children.Add(new RoleTag(Style, name)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(0, 4, 0, 0),
        Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
    };

    private StackPanel PresetRow()
    {
        var wrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var scroller = new ScrollViewer
        {
            Content = wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
        };

        foreach (var (name, preset) in Presets)
        {
            // The button *is* the tag, so choosing a preset is choosing the look
            // you can already see.
            var button = new Button
            {
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new RoleTag(preset, name),
            };

            button.Click += (_, _) => Set(() =>
            {
                Style.Background = preset.Background;
                Style.BackgroundTwo = preset.BackgroundTwo;
                Style.Pattern = preset.Pattern;
                Style.Shape = preset.Shape;
                Style.Animation = preset.Animation;
                Style.Icon = preset.Icon;
            });

            wrap.Children.Add(button);
        }

        var host = new StackPanel();
        host.Children.Add(scroller);
        return host;
    }

    private StackPanel ColourRow(bool first)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        foreach (var (name, hex, second) in Colours.Palette)
        {
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Colours.Parse(first ? hex : second, Microsoft.UI.Colors.Gray)),
                BorderThickness = new Thickness(1),
            };

            ToolTipService.SetToolTip(swatch, name);

            swatch.Click += (_, _) => Set(() =>
            {
                if (first)
                {
                    Style.Background = hex;

                    // Keep the pair coherent: picking a base colour also sets a
                    // matching second one, which the user can still override.
                    Style.BackgroundTwo = second;
                }
                else
                {
                    Style.BackgroundTwo = second;
                }
            });

            row.Children.Add(swatch);
        }

        var scroller = new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
        };

        var host = new StackPanel();
        host.Children.Add(scroller);
        return host;
    }

    private StackPanel IconRow()
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

        foreach (var (name, glyph) in Icons)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            if (!string.IsNullOrEmpty(glyph))
            {
                content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
            }

            content.Children.Add(new TextBlock { Text = name });
            box.Items.Add(new ComboBoxItem { Content = content, Tag = glyph });
        }

        box.SelectedIndex = Math.Max(0, Array.FindIndex(Icons, i => i.Glyph == Style.Icon));
        box.SelectionChanged += (_, _) => Set(() => Style.Icon = (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "");

        var host = new StackPanel { Spacing = 4 };
        host.Children.Add(Label(Loc.Get("Tag.Icon")));
        host.Children.Add(box);
        return host;
    }

    private StackPanel Choice<T>(string label, T[] values, T current, Action<T> apply)
        where T : struct, Enum
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

        foreach (var value in values)
        {
            box.Items.Add(new ComboBoxItem { Content = Loc.Get($"Tag.{typeof(T).Name}.{value}"), Tag = value });
        }

        box.SelectedIndex = Array.IndexOf(values, current);
        box.SelectionChanged += (_, _) =>
        {
            if ((box.SelectedItem as ComboBoxItem)?.Tag is T chosen)
            {
                Set(() => apply(chosen));
            }
        };

        var host = new StackPanel { Spacing = 4 };
        host.Children.Add(Label(label));
        host.Children.Add(box);
        return host;
    }
}
