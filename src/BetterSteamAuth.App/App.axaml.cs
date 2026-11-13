using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BetterSteamAuth.App.ViewModels;
using BetterSteamAuth.Storage;

namespace BetterSteamAuth.App;

public partial class App : Application
{
    /// <summary>Default data folder: %AppData%\BetterSteamAuth.</summary>
    public static string DefaultDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterSteamAuth");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string folder = DataFolderFromArgs(desktop.Args) ?? DefaultDataFolder;
            Vault? vault = Vault.Exists(folder) ? Vault.Open(folder) : null;
            var vm = new MainWindowViewModel(vault is { IsUnlocked: true } ? vault : null);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) =>
            {
                vm.Dispose();
                vault?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Reads <c>--data &lt;folder&gt;</c>, which overrides the data folder.</summary>
    private static string? DataFolderFromArgs(string[]? args)
    {
        int index = Array.IndexOf(args ?? [], "--data");
        return index >= 0 && index + 1 < args!.Length ? Path.GetFullPath(args[index + 1]) : null;
    }
}
