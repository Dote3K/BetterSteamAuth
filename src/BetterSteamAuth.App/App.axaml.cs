using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BetterSteamAuth.App.ViewModels;

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
            var window = new MainWindow();
            var shell = new ShellViewModel(folder, window);
            window.DataContext = shell;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => shell.Dispose();

            // A failed command or timer would otherwise close the app (and an authenticator that vanishes mid-task is
            // worse than one that says something went wrong).
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                if (e.Exception is not (OutOfMemoryException or StackOverflowException))
                {
                    shell.ReportUnexpected(e.Exception);
                    e.Handled = true;
                }
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
