using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TamizChat.Core.Protocol;
using TamizChat.Localization;

namespace TamizChat.Controls;

/// <summary>What the room dialog produces, in whichever shape the caller needs.</summary>
public sealed class RoomDialogResult
{
    public required RoomCreate Create { get; init; }

    public required RoomUpdate Update { get; init; }
}

/// <summary>
/// Creating or editing a room: name, password, capacity and the role needed to
/// get in.
///
/// The password field is deliberately **empty when editing**, with its own
/// "change the password" switch. An edit sends only the fields it includes, so
/// showing the existing password back — or sending a blank one because the box
/// happened to be empty — would either leak it or silently remove it.
/// </summary>
public sealed class RoomDialog
{
    private readonly Room? _existing;
    private readonly IReadOnlyList<Role> _roles;

    private readonly TextBox _name;
    private readonly PasswordBox _password;
    private readonly CheckBox _changePassword;
    private readonly NumberBox _capacity;
    private readonly ComboBox _requiredRole;

    public RoomDialog(Room? existing, IReadOnlyList<Role> roles)
    {
        _existing = existing;
        _roles = roles;

        _name = new TextBox { Header = Loc.Get("Admin.RoomName"), Text = existing?.Name ?? "" };

        _password = new PasswordBox
        {
            Header = Loc.Get("Admin.RoomPassword"),
            PlaceholderText = Loc.Get("Admin.NoPassword"),
        };

        _changePassword = new CheckBox
        {
            Content = Loc.Get("Admin.ChangePassword"),
            IsChecked = existing is null,
            Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible,
        };

        _capacity = new NumberBox
        {
            Header = Loc.Get("Admin.RoomCapacity"),
            Value = existing?.Capacity ?? 0,
            Minimum = 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };

        _requiredRole = new ComboBox
        {
            Header = Loc.Get("Admin.RequiredRole"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        _requiredRole.Items.Add(new ComboBoxItem { Content = Loc.Get("Admin.AnyRole"), Tag = "" });

        foreach (var role in roles)
        {
            _requiredRole.Items.Add(new ComboBoxItem { Content = role.Name, Tag = role.Id });
        }

        var index = roles.ToList().FindIndex(r => r.Id == existing?.RequiredRoleId);
        _requiredRole.SelectedIndex = index < 0 ? 0 : index + 1;
    }

    public XamlRoot? XamlRoot { get; init; }

    public async Task<RoomDialogResult?> ShowAsync()
    {
        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(_name);
        panel.Children.Add(_capacity);
        panel.Children.Add(_requiredRole);
        panel.Children.Add(_changePassword);
        panel.Children.Add(_password);

        var dialog = new ContentDialog
        {
            Title = Loc.Get(_existing is null ? "Admin.NewRoom" : "Admin.EditRoom"),
            Content = panel,
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

        var role = (_requiredRole.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var touchesPassword = _changePassword.IsChecked == true;

        return new RoomDialogResult
        {
            Create = new RoomCreate
            {
                Name = _name.Text.Trim(),
                Password = _password.Password,
                Capacity = (int)_capacity.Value,
                RequiredRoleId = role,
            },

            // Null means "leave alone", which is why the password is only
            // included when the switch says so.
            Update = new RoomUpdate
            {
                RoomId = _existing?.Id ?? "",
                Name = _name.Text.Trim(),
                Password = touchesPassword ? _password.Password : null,
                Capacity = (int)_capacity.Value,
                RequiredRoleId = role,
            },
        };
    }
}
