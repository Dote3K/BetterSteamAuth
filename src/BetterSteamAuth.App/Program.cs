using Avalonia;
using Velopack;

namespace BetterSteamAuth.App;

internal static class Program
{
    // Don't use Avalonia, third-party APIs or any SynchronizationContext-reliant code
    // before Main runs: nothing is initialized yet.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack's install, update, and uninstall hooks, which exit right away.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
