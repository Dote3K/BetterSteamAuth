using System.Diagnostics;
using BetterSteamAuth.App.Services;
using BetterSteamAuth.Steam;
using BetterSteamAuth.Steam.Network;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Owns the vault and decides which page fills the window: first run, unlock, import or main.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TimeSyncInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ProxyCheckInterval = TimeSpan.FromMinutes(15);

    public const string AutoLockKey = "security.auto_lock_minutes";
    public const int DefaultAutoLockMinutes = 15;

    private readonly Dictionary<ulong, (AccountConnection Connection, CancellationTokenSource Stop)> _connections = [];
    private CancellationTokenSource _background = new();
    private bool _proxyChecksRunning;
    private NetworkPolicy _network = new(new NetworkOptions());
    private readonly Avalonia.Threading.DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTime _lastActivity = DateTime.UtcNow;

    public ShellViewModel(string dataFolder, IPlatformServices platform)
    {
        DataFolder = dataFolder;
        Platform = platform;
        _idleTimer.Tick += (_, _) => LockIfIdle();
        _idleTimer.Start();

        if (!Vault.Exists(dataFolder))
        {
            CurrentPage = new FirstRunViewModel(this);
            return;
        }

        Vault = Vault.Open(dataFolder);
        LoadNetworkOptions();
        if (Vault.IsUnlocked || UnlockWithRememberedKey())
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

    public NetworkOptions NetworkOptions => _network.Options;

    public ConfirmationPolicy ConfirmationPolicy { get; } = new();

    public void SetAcceptTradesInBulk(bool value)
    {
        ConfirmationPolicy.AcceptTradesInBulk = value;
        Vault?.SetSetting(ConfirmationPolicy.BulkTradesKey, value ? "on" : "off");
    }

    public NetworkPolicy Network => _network;

    /// <summary>Raised on the UI thread after proxy health was rechecked.</summary>
    public event EventHandler? ProxyHealthChanged;

    public void UseVault(Vault vault)
    {
        Vault = vault;
        LoadNetworkOptions();
        _lastActivity = DateTime.UtcNow;
    }

    /// <summary>Minutes without input before the app locks itself; 0 means never.</summary>
    public int AutoLockMinutes
    {
        get => int.TryParse(Vault?.GetSetting(AutoLockKey), out int minutes) ? minutes : DefaultAutoLockMinutes;
        set => Vault?.SetSetting(AutoLockKey, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public bool IsRemembered => RememberedKey.Exists(DataFolder);

    /// <summary>Saves or forgets the data key on this device (DPAPI). Needs the app unlocked to save.</summary>
    public void SetRemembered(bool remember)
    {
        if (!remember)
        {
            RememberedKey.Forget(DataFolder);
            return;
        }

        if (Vault is not { IsEncrypted: true, IsUnlocked: true } vault)
        {
            return;
        }

        byte[] key = vault.ExportDataKey();
        try
        {
            RememberedKey.Save(DataFolder, key);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Called on any keyboard or mouse input; resets the auto-lock countdown.</summary>
    public void NoteActivity() => _lastActivity = DateTime.UtcNow;

    /// <summary>Saves new network settings and rebuilds every account connection with them.</summary>
    public void ApplyNetworkOptions(NetworkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Vault?.SetSetting(NetworkOptions.SniBypassKey, options.SniBypass.ToString().ToLowerInvariant());
        Vault?.SetSetting(NetworkOptions.EncryptedDnsKey, options.EncryptedDns.ToString().ToLowerInvariant());
        CloseConnections();
        _network.Dispose();
        _network = new NetworkPolicy(options);
    }

    /// <summary>
    /// The account's own connection with its assigned proxy, created on first use; starts its clock sync.
    /// </summary>
    public AccountConnection ConnectionFor(ulong steamId)
    {
        if (!_connections.TryGetValue(steamId, out var entry))
        {
            var proxy = Vault?.GetAccountProxyId(steamId) is { } proxyId ? Vault.GetProxySettings(proxyId) : null;
            // Accounts linked here present their own device; imported ones use the default profile.
            string userAgent = Vault?.GetDeviceProfile(steamId)?.UserAgent ?? MobileClient.DefaultUserAgent;
            var connection = new AccountConnection(steamId, proxy, userAgent, _network);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_background.Token);
            entry = (connection, stop);
            _connections[steamId] = entry;
            _ = TimeSync.RunAsync(
                connection,
                TimeSyncInterval,
                ex => Trace.WriteLine($"Time sync failed for {steamId}: {ex.Message}"),
                stop.Token);
        }

        return entry.Connection;
    }

    /// <summary>Drops one account's connection so the next request uses its current proxy.</summary>
    public void ResetConnection(ulong steamId)
    {
        if (_connections.Remove(steamId, out var entry))
        {
            entry.Stop.Cancel();
            entry.Stop.Dispose();
            entry.Connection.Dispose();
        }
    }

    /// <summary>Checks every proxy and records the results.</summary>
    public async Task CheckProxiesAsync(IEnumerable<long>? proxyIds = null, CancellationToken cancellationToken = default)
    {
        if (Vault is not { IsUnlocked: true } vault)
        {
            return;
        }

        var ids = proxyIds?.ToHashSet();
        var checks = vault.ListProxies()
            .Where(p => ids is null || ids.Contains(p.Id))
            .Select(async p =>
            {
                if (vault.GetProxySettings(p.Id) is not { } settings)
                {
                    return;
                }

                var result = await ProxyHealth.CheckAsync(settings, _network, cancellationToken).ConfigureAwait(true);
                vault.RecordProxyHealth(p.Id, result.Ok, result.LatencyMs, result.Error);
            });
        await Task.WhenAll(checks).ConfigureAwait(true);
        ProxyHealthChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowMain()
    {
        SwitchTo(new MainViewModel(this));
        StartProxyChecks();
    }

    public void ShowProxies() => SwitchTo(new ProxiesViewModel(this));

    public void ShowChangeProxy(ulong steamId, string accountName) => SwitchTo(new ChangeProxyViewModel(this, steamId, accountName));

    public void ShowUnlock() => SwitchTo(new UnlockViewModel(this));

    /// <summary>Opens the link wizard: a new link, or resuming an unfinished one.</summary>
    public void ShowLink(ulong? resumeSteamId = null, string? accountName = null)
        => SwitchTo(new LinkViewModel(this, resumeSteamId) { AccountName = accountName ?? "" });

    public void ShowSettings() => SwitchTo(new SettingsViewModel(this));

    public void ShowLogin(ulong steamId, string accountName) => SwitchTo(new LoginViewModel(this, steamId, accountName));

    public void ShowImport(ImportSource source) => SwitchTo(new ImportViewModel(this, source, onDone: ShowMain));

    public void LockApp()
    {
        if (Vault is not { IsEncrypted: true, IsUnlocked: true })
        {
            return;
        }

        Vault.Lock();
        CloseConnections();
        ShowUnlock();
    }

    public void Dispose()
    {
        _idleTimer.Stop();
        (CurrentPage as IDisposable)?.Dispose();
        CloseConnections();
        _background.Dispose();
        _network.Dispose();
        Vault?.Dispose();
    }

    private bool UnlockWithRememberedKey()
    {
        if (Vault is null || RememberedKey.Load(DataFolder) is not { } key)
        {
            return false;
        }

        try
        {
            if (Vault.TryUnlockWithKey(key))
            {
                return true;
            }

            // Saved for another vault that used to live in this folder.
            RememberedKey.Forget(DataFolder);
            return false;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    private void LockIfIdle()
    {
        int minutes = Vault is { IsEncrypted: true, IsUnlocked: true } ? AutoLockMinutes : 0;
        if (minutes > 0 && DateTime.UtcNow - _lastActivity >= TimeSpan.FromMinutes(minutes))
        {
            LockApp();
        }
    }

    private void LoadNetworkOptions()
    {
        ConfirmationPolicy.AcceptTradesInBulk = Vault?.GetSetting(ConfirmationPolicy.BulkTradesKey) == "on";

        var options = NetworkOptions.FromSettings(
            Vault?.GetSetting(NetworkOptions.SniBypassKey),
            Vault?.GetSetting(NetworkOptions.EncryptedDnsKey));
        _network.Dispose();
        _network = new NetworkPolicy(options);
    }

    private void CloseConnections()
    {
        _background.Cancel();
        _background.Dispose();
        _background = new CancellationTokenSource();
        foreach (var entry in _connections.Values)
        {
            entry.Stop.Dispose();
            entry.Connection.Dispose();
        }

        _connections.Clear();
        _proxyChecksRunning = false;
    }

    /// <summary>Background proxy checks about every 15 minutes, with jitter, while unlocked.</summary>
    private void StartProxyChecks()
    {
        if (_proxyChecksRunning)
        {
            return;
        }

        _proxyChecksRunning = true;
        var token = _background.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(10, 40)), token).ConfigureAwait(false);
                while (true)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => CheckProxiesAsync(cancellationToken: token));
                    await Task.Delay(ProxyCheckInterval + TimeSpan.FromSeconds(Random.Shared.Next(0, 120)), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Locked or closed.
            }
        }, token);
    }

    private void SwitchTo(ObservableObject page)
    {
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = page;
    }
}
