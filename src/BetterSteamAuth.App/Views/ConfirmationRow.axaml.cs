using Avalonia;
using Avalonia.Controls;

namespace BetterSteamAuth.App.Views;

public partial class ConfirmationRow : UserControl
{
    /// <summary>Show the account name in the meta text (inbox).</summary>
    public static readonly StyledProperty<bool> ShowAccountProperty =
        AvaloniaProperty.Register<ConfirmationRow, bool>(nameof(ShowAccount));

    public ConfirmationRow()
    {
        InitializeComponent();
    }

    public bool ShowAccount
    {
        get => GetValue(ShowAccountProperty);
        set => SetValue(ShowAccountProperty, value);
    }
}
