using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using BetterSteamAuth.App.ViewModels;

namespace BetterSteamAuth.App.Views;

public partial class ConfirmationRow : UserControl
{
    /// <summary>Show the account name in the meta text (inbox).</summary>
    public static readonly StyledProperty<bool> ShowAccountProperty =
        AvaloniaProperty.Register<ConfirmationRow, bool>(nameof(ShowAccount));

    public ConfirmationRow()
    {
        InitializeComponent();
        Tapped += OnTapped;
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        // ✓ / ✕ and other buttons handle their own clicks.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (DataContext is ConfirmationItemViewModel item)
        {
            item.OpenDetailCommand.Execute(null);
        }
    }

    public bool ShowAccount
    {
        get => GetValue(ShowAccountProperty);
        set => SetValue(ShowAccountProperty, value);
    }
}
