using Avalonia.Controls;
using Avalonia.Input.Platform;
using BetterSteamAuth.App.ViewModels;

namespace BetterSteamAuth.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CopyToClipboard = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        }
    }
}
