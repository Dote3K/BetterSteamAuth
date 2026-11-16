using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using BetterSteamAuth.App.ViewModels;

namespace BetterSteamAuth.App.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    /// <summary>Double-click an account to open its details.</summary>
    private void OnAccountDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is AccountItemViewModel account
            && DataContext is MainViewModel main)
        {
            main.OpenDetailsCommand.Execute(account);
        }
    }
}
