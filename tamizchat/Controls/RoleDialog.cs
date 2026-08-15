using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TamizChat.Core.Protocol;
using TamizChat.Localization;

namespace TamizChat.Controls;

/// <summary>
/// Creating or editing a role: its name, colour, priority and permissions.
///
/// The permission list is the protocol's own set of text keys. They are shown as
/// they are rather than prettified, because they are what the server documents
/// and what an operator reads about — inventing friendlier names here would mean
/// two vocabularies for the same thing.
/// </summary>
public sealed class RoleDialog
{
    /// <summary>The permissions the protocol defines, in the order it lists them.</summary>
    private static readonly string[] AllPermissions =
    [
        "send_messages", "upload_files", "moderate_chat", "manage_rooms",
        "join_locked_rooms", "bypass_room_password", "kick", "ban", "mute",
        "move_users", "manage_roles", "control_bots",
    ];

    private readonly Role? _existing;
    private readonly TextBox _name;
    private readonly NumberBox _priority;
    private readonly Dictionary<string, CheckBox> _permissions = [];

    public RoleDialog(Role? existing)
    {
        _existing = existing;

        _name = new TextBox
        {
            Header = Loc.Get("Admin.RoleName"),
            Text = existing?.Name ?? "",
        };


        _priority = new NumberBox
        {
            Header = Loc.Get("Admin.RolePriority"),
            Value = existing?.Priority ?? 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
    }

    public XamlRoot? XamlRoot { get; init; }

    /// <summary>Returns what to send, or null if the dialog was cancelled.</summary>
    public async Task<RoleSpec?> ShowAsync()
    {
        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(_name);
        panel.Children.Add(_priority);

        // The designer watches the name box, so the preview shows the real name
        // as it is typed rather than a placeholder.
        var designer = new RoleTagDesigner(
            RoleTagStyle.Parse(_existing?.TagStyle, _existing?.Color ?? ""),
            _name);

        panel.Children.Add(designer);

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("Admin.Permissions"),
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = (Brush)Application.Current.Resources["TcTextSecondaryBrush"],
        });

        foreach (var permission in AllPermissions)
        {
            var box = new CheckBox
            {
                Content = permission,
                IsChecked = _existing?.Permissions.Contains(permission) == true,
                MinHeight = 30,
            };

            _permissions[permission] = box;
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            Title = Loc.Get(_existing is null ? "Admin.NewRole" : "Admin.EditRole"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = Loc.Get("Admin.Save"),
            CloseButtonText = Loc.Get("Admin.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["TcAccentButtonStyle"],
        };

        dialog.Themed(XamlRoot);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return new RoleSpec
        {
            RoleId = _existing?.Id ?? "",
            Name = _name.Text.Trim(),

            // `color` stays in step with the tag's main colour so anything that
            // only understands the old field still shows the right hue.
            Color = designer.Style.Background,
            TagStyle = designer.Style.ToJson(),
            Priority = (int)_priority.Value,
            Permissions = [.. _permissions.Where(p => p.Value.IsChecked == true).Select(p => p.Key)],
        };
    }
}
