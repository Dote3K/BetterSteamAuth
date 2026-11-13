using System.Diagnostics;
using BetterSteamAuth.App.Services;
using BetterSteamAuth.Steam;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Owns the vault and decides which page fills the window: first run, unlock, import or main.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TimeSyncInterval = TimeSpan.FromHours(1);

    private readonly Dictionary<ulong, AccountConnection> _connections = [];
    private CancellationTokenSource _background = new();

    public ShellViewModel(string dataFolder, IPlatformServices platform)
    {
        DataFolder = dataFolder;
        Platform = platform;

        if (!Vault.Exists(dataFolder))
        {
            CurrentPage = new FirstRunViewModel(this);
            return;
        }

        Vault = Vault.Open(dataFolder);
        if (Vault.IsUnlocked)
        {
            ShowMain();
        }
        else
        {
            ShowUnlock();
        }
    }

    public string DataFolder { get; }

    public IPlatformServices Platform { get; }

    public Vault? Vault { get; private set; }

    [ObservableProperty]
    public partial ObservableObject? CurrentPage { get; private set; }

    public void UseVault(Vault vault) => Vault = vault;

    /// <summary>The account's own connection, created on first use; starts its clock sync.</summary>
    public AccountConnection ConnectionFor(ulong steamId)
    {
        if (!_connections.TryGetValue(steamId, out var connection))
        {
            // Proxies aren't assigned yet (Phase 2), so every account connects directly.
            connection = new AccountConnection(steamId, proxy: null, MobileClient.DefaultUserAgent);
            _connections[steamId] = connection;
            _ = TimeSync.RunAsync(
                connection,
                TimeSyncInterval,
                ex => Trace.WriteLine($"Time sync failed for {steamId}: {ex.Message}"),
                _background.Token);
        }

        return connection;
    }

    public void ShowMain() => SwitchTo(new MainViewModel(this));

    public void ShowUnlock() => SwitchTo(new UnlockViewModel(this));

    public void ShowImport(ImportSource source) => SwitchTo(new ImportViewModel(this, source, onDone: ShowMain));

    public void LockApp()
    {
        if (Vault is not { IsEncrypted: true })
        {
            return;
        }

        Vault.Lock();
        CloseConnections();
        ShowUnlock();
    }

    public void Dispose()
    {
        (CurrentPage as IDisposable)?.Dispose();
        CloseConnections();
        _background.Dispose();
        Vault?.Dispose();
    }

    private void CloseConnections()
    {
        _background.Cancel();
        _background.Dispose();
        _background = new CancellationTokenSource();
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        _connections.Clear();
    }

    private void SwitchTo(ObservableObject page)
    {
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = page;
    }
}
