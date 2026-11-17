using System.Diagnostics;
using System.Reflection;
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
    public const string BackgroundCheckKey = "confirmations.background_check";
    public const int DefaultAutoLockMinutes = 15;

    private readonly Dictionary<ulong, (AccountConnection Connection, CancellationTokenSource Stop)> _connections = [];
    private CancellationTokenSource _background = new();
    private bool _proxyChecksRunning;
    private NetworkPolicy _network = new(new NetworkOptions());
    private readonly Avalonia.Threading.DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Avalonia.Threading.DispatcherTimer _autoBackupTimer = new() { Interval = TimeSpan.FromHours(1) };
    private DateTime _lastActivity = DateTime.UtcNow;
    private TaskCompletionSource _checksAllowed = Allowed();
    private bool _minimized;
    private bool _checkInBackground;

    public ShellViewModel(string dataFolder, IPlatformServices platform)
    {
        DataFolder = dataFolder;
        Platform = platform;
        _idleTimer.Tick += (_, _) => LockIfIdle();
        _idleTimer.Start();
        AutoBackup = new AutoBackup(() => Vault);
        _autoBackupTimer.Tick += (_, _) => CheckAutoBackup();
        _autoBackupTimer.Start();

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

    /// <summary>Keep checking confirmations while the window is minimized. Off by default.</summary>
    public bool CheckInBackground
    {
        get => _checkInBackground;
        set
        {
            _checkInBackground = value;
            Vault?.SetSetting(BackgroundCheckKey, value ? "on" : "off");
            UpdateConfirmationChecks();
        }
    }

    /// <summary>Called by the window when it's minimized or restored.</summary>
    public void SetMinimized(bool minimized)
    {
        _minimized = minimized;
        UpdateConfirmationChecks();
    }

    /// <summary>
    /// Waits while confirmation checks are paused (window minimized, unless checking in the background).
    /// Returns true if it had to wait.
    /// </summary>
    public async Task<bool> WaitForConfirmationChecksAsync(CancellationToken cancellationToken)
    {
        var gate = _checksAllowed.Task;
        if (gate.IsCompleted)
        {
            return false;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        return true;
    }

    /// <summary>Called on any keyboard or mouse input; resets the auto-lock countdown.</summary>
    public void NoteActivity() => _lastActivity = DateTime.UtcNow;

    private const string AutoSignInPausedPrefix = "signin.paused.";
    private static readonly TimeSpan AutoSignInInterval = TimeSpan.FromMinutes(15);
    private readonly Dictionary<ulong, DateTime> _lastAutoSignIn = [];

    /// <summary>Automatic sign-in stopped after a wrong password or an email code; a manual sign-in resumes it.</summary>
    public bool IsAutoSignInPaused(ulong steamId)
        => Vault?.GetSetting(AutoSignInPausedPrefix + steamId.ToString(System.Globalization.CultureInfo.InvariantCulture)) == "on";

    public void SetAutoSignInPaused(ulong steamId, bool paused)
        => Vault?.SetSetting(AutoSignInPausedPrefix + steamId.ToString(System.Globalization.CultureInfo.InvariantCulture), paused ? "on" : "off");

    /// <summary>
    /// Signs in again with the account's saved password, through its own connection. At most once per
    /// account every 15 minutes. Returns true when the session was renewed.
    /// </summary>
    public async Task<bool> TryAutoSignInAsync(ulong steamId, CancellationToken cancellationToken)
    {
        if (Vault is not { IsUnlocked: true } vault || !vault.HasSteamPassword(steamId) || IsAutoSignInPaused(steamId)
            || (_lastAutoSignIn.TryGetValue(steamId, out var last) && DateTime.UtcNow - last < AutoSignInInterval))
        {
            return false;
        }

        _lastAutoSignIn[steamId] = DateTime.UtcNow;
        var maFile = vault.Load(steamId);
        if (string.IsNullOrEmpty(maFile.AccountName) || vault.GetSteamPassword(steamId) is not { } password)
        {
            return false;
        }

        byte[]? sharedSecret = string.IsNullOrEmpty(maFile.SharedSecret) ? null : Convert.FromBase64String(maFile.SharedSecret);
        try
        {
            // An email code can't be answered without the user: give up and let them sign in by hand.
            var tokens = await SteamLogin.LoginAsync(
                ConnectionFor(steamId), maFile.AccountName, password, sharedSecret,
                _ => Task.FromResult<string?>(null), vault.GetDeviceProfile(steamId), cancellationToken).ConfigureAwait(true);
            LoginViewModel.ApplyTokens(maFile, tokens);
            vault.Save(maFile);
            return true;
        }
        catch (LoginFailedException ex) when (ex.Result is SteamKit2.EResult.InvalidPassword)
        {
            SetAutoSignInPaused(steamId, paused: true);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetAutoSignInPaused(steamId, paused: true);
            return false;
        }
        catch (Exception ex) when (ex is LoginFailedException or HttpRequestException)
        {
            // Rate limits and network trouble: try again in the next window.
            return false;
        }
        finally
        {
            if (sharedSecret is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(sharedSecret);
            }
        }
    }

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
            var connection = new AccountConnection(steamId, proxy, _network);
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
        CheckAutoBackup();
    }

    /// <summary>Main window with this account selected.</summary>
    public void ShowMain(ulong selectSteamId)
    {
        SwitchTo(new MainViewModel(this, selectSteamId));
        StartProxyChecks();
        CheckAutoBackup();
    }

    /// <summary>Main window right after an account was removed, offering Undo.</summary>
    public void ShowMainAfterRemove(RemovedAccount removed, string name)
    {
        SwitchTo(new MainViewModel(this, removed: (removed, name)));
        StartProxyChecks();
        CheckAutoBackup();
    }

    /// <summary>Main window with a short notice and no Undo (e.g. after removing the authenticator).</summary>
    public void ShowMainWithNotice(string notice)
    {
        SwitchTo(new MainViewModel(this, notice: notice));
        StartProxyChecks();
        CheckAutoBackup();
    }

    public void ShowAccountDetails(ulong steamId) => SwitchTo(new AccountDetailsViewModel(this, steamId));

    public void ShowRemoveAuthenticator(ulong steamId, GuardAfterRemoval after = GuardAfterRemoval.Email, string? typedCode = null)
        => SwitchTo(new RemoveAuthenticatorViewModel(this, steamId, after, typedCode));

    public void ShowProxies() => SwitchTo(new ProxiesViewModel(this));

    public void ShowChangeProxy(ulong steamId, string accountName, Action? done = null)
        => SwitchTo(new ChangeProxyViewModel(this, steamId, accountName, done ?? ShowMain));

    public void ShowUnlock() => SwitchTo(new UnlockViewModel(this));

    /// <summary>Opens the link wizard: a new link, or resuming an unfinished one.</summary>
    public void ShowLink(ulong? resumeSteamId = null, string? accountName = null)
        => SwitchTo(new LinkViewModel(this, resumeSteamId) { AccountName = accountName ?? "" });

    /// <summary>Opens the link wizard in move mode: moves an existing authenticator here.</summary>
    public void ShowMoveFromPhone() => SwitchTo(new LinkViewModel(this, resumeSteamId: null, move: true));

    public void ShowSettings() => SwitchTo(new SettingsViewModel(this));

    public void ShowRemovedAccounts() => SwitchTo(new RemovedAccountsViewModel(this));

    /// <summary>Back up now; <paramref name="fromSettings"/> decides where Back returns.</summary>
    public void ShowBackup(bool fromSettings) => SwitchTo(new BackupViewModel(this, fromSettings ? ShowSettings : ShowMain));

    public void ShowRestore() => SwitchTo(new RestoreViewModel(this, ShowSettings, replace: false));

    /// <summary>Restore that replaces the data: from Unlock ("Forgot password?") or the first run.</summary>
    public void ShowRestoreReplacing(bool fromFirstRun)
        => SwitchTo(new RestoreViewModel(this, fromFirstRun ? ShowFirstRun : ShowUnlock, replace: true));

    public void ShowFirstRun() => SwitchTo(new FirstRunViewModel(this));

    public void ShowExport() => SwitchTo(new ExportViewModel(this));

    /// <summary>
    /// Puts a backup in place of the app's data and opens it, unlocked with the backup's key. The old data
    /// folder is kept aside (returned). If that fails, the old data is opened again (locked) and the error rethrown.
    /// </summary>
    public string? ReplaceDataWithBackup(OpenedBackup backup, string keptFolder)
    {
        ArgumentNullException.ThrowIfNull(backup);
        byte[]? key = backup.Vault.IsEncrypted ? backup.Vault.ExportDataKey() : null;
        try
        {
            CloseConnections();
            Vault?.Dispose();
            Vault = null;
            RememberedKey.Forget(DataFolder);
            string? kept;
            try
            {
                kept = VaultBackup.ReplaceDataFolder(backup, DataFolder, keptFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Vault.Exists(DataFolder))
                {
                    UseVault(Vault.Open(DataFolder));
                }

                throw;
            }

            var restored = Vault.Open(DataFolder);
            if (key is not null)
            {
                restored.TryUnlockWithKey(key);
            }

            UseVault(restored);
            return kept;
        }
        finally
        {
            if (key is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>The app's SemVer version from the git tag (MinVer), without build metadata. Written into backups.</summary>
    public static string? AppVersion { get; } = typeof(ShellViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

    public void ShowLogin(ulong steamId, string accountName, Action? done = null)
        => SwitchTo(new LoginViewModel(this, steamId, accountName, done ?? ShowMain));

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
        _autoBackupTimer.Stop();
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

    private static TaskCompletionSource Allowed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private void UpdateConfirmationChecks()
    {
        if (!_minimized || _checkInBackground)
        {
            _checksAllowed.TrySetResult();
        }
        else if (_checksAllowed.Task.IsCompleted)
        {
            _checksAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void LoadNetworkOptions()
    {
        _checkInBackground = Vault?.GetSetting(BackgroundCheckKey) == "on";
        UpdateConfirmationChecks();
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

    /// <summary>Optional automatic backups.</summary>
    public AutoBackup AutoBackup { get; }

    /// <summary>
    /// Makes an automatic backup if it's on, due, and the data changed. Runs on start (after unlocking), whenever the
    /// main window is shown again (after adding, removing, or restoring accounts), and every hour.
    /// </summary>
    public void CheckAutoBackup() => _ = AutoBackup.RunIfDueAsync(AppVersion);

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
