using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using TamizChat.Core.Protocol;
using Windows.Foundation;
using Windows.UI;

namespace TamizChat.Controls;

/// <summary>
/// A role drawn as a designed tag rather than plain text.
///
/// Grid rather than Border as the base: Border is sealed in WinUI, and Grid has
/// the same background, corner and padding properties.
///
/// Everything about the look comes from the role's stored style. The one thing
/// decided here is the text colour when the style leaves it empty, because that
/// depends on the background actually chosen and getting it wrong makes half the
/// palette unreadable — which is exactly where people pick loud colours.
/// </summary>
public sealed class RoleTag : Grid
{
    private Storyboard? _story;

    public RoleTag(Role role)
        : this(RoleTagStyle.Parse(role.TagStyle, role.Color), role.Name)
    {
    }

    public RoleTag(RoleTagStyle style, string name)
    {
        var background = Colours.Parse(style.Background, Color.FromArgb(255, 114, 137, 218));
        var second = Colours.Parse(style.BackgroundTwo, background);

        VerticalAlignment = VerticalAlignment.Center;
        Padding = new Thickness(style.Shape == TagShape.Square ? 8 : 10, 2, style.Shape == TagShape.Square ? 8 : 10, 3);
        CornerRadius = style.Shape switch
        {
            TagShape.Pill => new CornerRadius(20),
            TagShape.Rounded => new CornerRadius(6),
            TagShape.Square => new CornerRadius(0),

            // One square corner and one round, which reads as a cut edge.
            TagShape.Cut => new CornerRadius(10, 2, 10, 2),
            _ => new CornerRadius(20),
        };

        ApplyFill(style, background, second);

        var foreground = string.IsNullOrWhiteSpace(style.Foreground)
            ? Colours.Readable(style.Pattern is TagPattern.Outline or TagPattern.Glass ? background : Blend(background, second))
            : Colours.Parse(style.Foreground, Colors.White);

        // Outline and Glass show what is behind them, so their text takes the
        // role's own colour instead of a contrast pick against a fill that is
        // not really there.
        if (style.Pattern == TagPattern.Outline && string.IsNullOrWhiteSpace(style.Foreground))
        {
            foreground = background;
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };

        if (!string.IsNullOrWhiteSpace(style.Icon))
        {
            content.Children.Add(new FontIcon
            {
                Glyph = style.Icon,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(foreground),
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 11,
            FontWeight = style.Bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(foreground),
        });

        Children.Add(content);

        Loaded += (_, _) => StartAnimation(style, background, second);
        Unloaded += (_, _) => _story?.Stop();
    }

    private void ApplyFill(RoleTagStyle style, Color background, Color second)
    {
        switch (style.Pattern)
        {
            case TagPattern.Solid:
                Background = new SolidColorBrush(background);
                break;

            case TagPattern.Gradient:
                Background = Linear(background, second, new Point(0, 0), new Point(1, 0));
                break;

            case TagPattern.Sunset:
                Background = Linear(background, second, new Point(0, 0), new Point(1, 1));
                break;

            case TagPattern.Stripes:
                // Hard stops in a gradient make bands: each colour holds its
                // value right up to the next stop instead of fading into it.
                Background = Stripes(background, second);
                break;

            case TagPattern.Glass:
                Background = new SolidColorBrush(Colours.WithAlpha(background, 90));
                BorderThickness = new Thickness(1);
                BorderBrush = new SolidColorBrush(Colours.WithAlpha(background, 160));
                break;

            case TagPattern.Outline:
                Background = new SolidColorBrush(Colors.Transparent);
                BorderThickness = new Thickness(1.5);
                BorderBrush = new SolidColorBrush(background);
                break;

            case TagPattern.Metal:
                // A light band across the middle is what makes a flat fill look
                // like a brushed surface.
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Colours.Lighten(background, 0.25), Offset = 0 },
                        new GradientStop { Color = background, Offset = 0.45 },
                        new GradientStop { Color = Colours.Darken(background, 0.25), Offset = 0.55 },
                        new GradientStop { Color = background, Offset = 1 },
                    },
                };
                break;
        }
    }

    private static LinearGradientBrush Linear(Color from, Color to, Point start, Point end) => new()
    {
        StartPoint = start,
        EndPoint = end,
        GradientStops =
        {
            new GradientStop { Color = from, Offset = 0 },
            new GradientStop { Color = to, Offset = 1 },
        },
    };

    private static LinearGradientBrush Stripes(Color a, Color b)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.25, 1) };

        for (var i = 0; i < 4; i++)
        {
            var start = i / 4.0;
            var colour = i % 2 == 0 ? a : b;

            brush.GradientStops.Add(new GradientStop { Color = colour, Offset = start });
            brush.GradientStops.Add(new GradientStop { Color = colour, Offset = start + 0.2499 });
        }

        return brush;
    }

    /// <summary>
    /// Starts whatever the style asks for.
    ///
    /// All of these animate a property that does not force a layout pass —
    /// opacity, a transform, a brush colour — so a room full of animated tags
    /// costs nothing to lay out repeatedly.
    /// </summary>
    private void StartAnimation(RoleTagStyle style, Color background, Color second)
    {
        _story?.Stop();

        if (style.Animation == TagAnimation.None)
        {
            return;
        }

        var story = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        switch (style.Animation)
        {
            case TagAnimation.Pulse:
                story.Children.Add(Fade(this, 1, 0.55, 1100, autoReverse: true));
                break;

            case TagAnimation.Glow:
                // A soft ring behind the tag, breathing. Cheaper and steadier
                // than animating a real shadow.
                var glow = new Rectangle
                {
                    RadiusX = 12,
                    RadiusY = 12,
                    Margin = new Thickness(-3),
                    Fill = new SolidColorBrush(Colours.WithAlpha(background, 110)),
                    Opacity = 0,
                };

                Children.Insert(0, glow);
                story.Children.Add(Fade(glow, 0.15, 0.85, 1300, autoReverse: true));
                break;

            case TagAnimation.Float:
                var move = new TranslateTransform();
                RenderTransform = move;

                var up = new DoubleAnimation
                {
                    From = 0,
                    To = -2.5,
                    Duration = TimeSpan.FromMilliseconds(1200),
                    AutoReverse = true,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    EnableDependentAnimation = true,
                };

                Storyboard.SetTarget(up, move);
                Storyboard.SetTargetProperty(up, "Y");
                story.Children.Add(up);
                break;

            case TagAnimation.Shimmer:
                // A pale highlight sliding along the tag. Clipped to the tag's
                // own bounds so it never spills past the corners.
                var shine = new Rectangle
                {
                    Width = 18,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Fill = Linear(Colours.WithAlpha(Colors.White, 0), Colours.WithAlpha(Colors.White, 130), new Point(0, 0), new Point(1, 0)),
                    RenderTransform = new TranslateTransform(),
                };

                Children.Insert(0, shine);
                SizeChanged += (_, _) => Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };

                var slide = new DoubleAnimation
                {
                    From = -30,
                    To = 140,
                    Duration = TimeSpan.FromMilliseconds(1800),
                    EnableDependentAnimation = true,
                };

                Storyboard.SetTarget(slide, shine.RenderTransform);
                Storyboard.SetTargetProperty(slide, "X");
                story.Children.Add(slide);
                break;

            case TagAnimation.Rainbow:
                // Steps around the hue circle rather than between two colours,
                // so it travels the long way and actually looks like a rainbow.
                var brush = new SolidColorBrush(background);
                Background = brush;

                var hue = new ColorAnimationUsingKeyFrames { EnableDependentAnimation = true };
                for (var i = 0; i <= 6; i++)
                {
                    hue.KeyFrames.Add(new LinearColorKeyFrame
                    {
                        Value = Colours.FromHue(i * 60),
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 700)),
                    });
                }

                Storyboard.SetTarget(hue, brush);
                Storyboard.SetTargetProperty(hue, "Color");
                story.Children.Add(hue);
                break;
        }

        _story = story;
        story.Begin();
    }

    private static DoubleAnimation Fade(DependencyObject target, double from, double to, int ms, bool autoReverse)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            AutoReverse = autoReverse,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        return animation;
    }

    private static Color Blend(Color a, Color b) => Color.FromArgb(
        255,
        (byte)((a.R + b.R) / 2),
        (byte)((a.G + b.G) / 2),
        (byte)((a.B + b.B) / 2));
}
