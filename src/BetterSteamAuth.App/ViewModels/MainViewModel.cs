using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Guard;
using BetterSteamAuth.Steam;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Main window: account list, shared timer, and the selected account's code.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>When fewer seconds are left, Copy copies the next code instead.</summary>
    private const int CopyNextThreshold = 5;

    private static readonly CompositeFormat SecondsLeftFormat = CompositeFormat.Parse(Strings.SecondsLeftFormat);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly CompositeFormat RemovedFormat = CompositeFormat.Parse(Strings.RemovedFormat);
    private int _removedSecondsLeft = 10 * 4;

    private readonly List<AccountItemViewModel> _all = [];
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _polling = new();

    private readonly ShellViewModel _shell;

    private readonly (RemovedAccount Account, string Name)? _removed;

    public MainViewModel(ShellViewModel shell, ulong? selectSteamId = null, (RemovedAccount Account, string Name)? removed = null, string? notice = null)
    {
        _shell = shell;
        var vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        CanLock = vault.IsEncrypted;

        foreach (var entry in vault.ListAccounts())
        {
            var maFile = vault.Load(entry.SteamId);
            if (string.IsNullOrEmpty(maFile.SharedSecret))
            {
                continue;
            }

            _all.Add(new AccountItemViewModel(
                entry.SteamId,
                maFile.AccountName ?? entry.SteamId.ToString(CultureInfo.InvariantCulture),
                Convert.FromBase64String(maFile.SharedSecret),
                shell.ConnectionFor(entry.SteamId).Clock)
            {
                // Only accounts linked here (they have a device profile) can be unfinished; imported
                // maFiles from other tools may lack fully_enrolled.
                IsLinkPending = !maFile.FullyEnrolled && vault.GetDeviceProfile(entry.SteamId) is not null,
            });
        }

        ApplyFilter();
        SelectedAccount = Accounts.FirstOrDefault(a => a.SteamId == selectSteamId) ?? Accounts.FirstOrDefault();
        _removed = removed;
        RemovedText = removed is { } r ? string.Format(CultureInfo.CurrentCulture, RemovedFormat, r.Name) : notice;
        Tick();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Tick());
        _timer.Start();

        Inbox = new InboxViewModel(_all, RespondBatchAsync, RefreshAllAsync, () => IsInboxOpen = false);

        // Each account polls on its own jittered schedule.
        foreach (var account in _all)
        {
            _ = PollConfirmationsAsync(account, _polling.Token);
        }
    }

    public bool CanLock { get; }

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    public bool HasAccounts => _all.Count > 0;

    public InboxViewModel Inbox { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAccounts))]
    public partial bool IsInboxOpen { get; private set; }

    public bool ShowAccounts => HasAccounts && !IsInboxOpen && Detail is null;

    public bool ShowInbox => IsInboxOpen && Detail is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAccounts), nameof(ShowInbox), nameof(HasDetail))]
    public partial ConfirmationDetailViewModel? Detail { get; private set; }

    public bool HasDetail => Detail is not null;

    partial void OnIsInboxOpenChanged(bool value) => OnPropertyChanged(nameof(ShowInbox));

    private void OpenDetail(AccountItemViewModel account, ConfirmationItemViewModel item)
    {
        Detail?.Dispose();
        Detail = new ConfirmationDetailViewModel(_shell, account, item, CloseDetail);
    }

    private void CloseDetail()
    {
        Detail?.Dispose();
        Detail = null;
    }

    [ObservableProperty]
    public partial AccountItemViewModel? SelectedAccount { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial int SecondsRemaining { get; private set; }

    [ObservableProperty]
    public partial string SecondsLeftText { get; private set; } = "";

    [ObservableProperty]
    public partial string CopyLabel { get; private set; } = Strings.Copy;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task CopyCodeAsync(AccountItemViewModel? account)
    {
        account ??= SelectedAccount;
        if (account is null)
        {
            return;
        }

        string code = SteamGuardCode.SecondsRemaining(account.Clock.Now) <= CopyNextThreshold
            ? account.NextCode()
            : account.Code;
        await _shell.Platform.CopyTextAsync(code).ConfigureAwait(true);

        CopyLabel = Strings.Copied;
        await Task.Delay(1200).ConfigureAwait(true);
        CopyLabel = Strings.Copy;
    }

    [RelayCommand]
    private void ImportFromSda() => _shell.ShowImport(ImportSource.SdaFolder);

    [RelayCommand]
    private void ImportMaFiles() => _shell.ShowImport(ImportSource.MaFiles);

    [RelayCommand]
    private void Lock() => _shell.LockApp();

    [RelayCommand]
    private void OpenSettings() => _shell.ShowSettings();

    [RelayCommand]
    private void OpenProxies() => _shell.ShowProxies();

    [RelayCommand]
    private void ChangeProxy(AccountItemViewModel? account)
    {
        account ??= SelectedAccount;
        if (account is not null)
        {
            _shell.ShowChangeProxy(account.SteamId, account.Name);
        }
    }

    [RelayCommand]
    private void LinkNew() => _shell.ShowLink();

    /// <summary>"Removed {name} · Undo" (or a notice without Undo), shown for a few seconds after removing an account.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoved))]
    public partial string? RemovedText { get; private set; }

    public bool HasRemoved => RemovedText is not null;

    public bool CanUndoRemove => _removed is not null;

    [RelayCommand]
    private void UndoRemove()
    {
        if (_removed is { } r && _shell.Vault is { } vault)
        {
            vault.Restore(r.Account);
            _shell.ShowMain(r.Account.Entry.SteamId);
        }
    }

    [RelayCommand]
    private void OpenDetails(AccountItemViewModel? account)
    {
        if ((account ?? SelectedAccount) is { } target)
        {
            _shell.ShowAccountDetails(target.SteamId);
        }
    }

    [RelayCommand]
    private void FinishLink(AccountItemViewModel? account)
    {
        if ((account ?? SelectedAccount) is { } target)
        {
            _shell.ShowLink(target.SteamId);
        }
    }

    [RelayCommand]
    private void Relogin(AccountItemViewModel? account)
    {
        account ??= SelectedAccount;
        if (account is not null)
        {
            _shell.ShowLogin(account.SteamId, account.Name);
        }
    }

    [RelayCommand]
    private Task RefreshConfirmationsAsync(AccountItemViewModel? account)
        => (account ?? SelectedAccount) is { } target ? LoadConfirmationsAsync(target, _polling.Token) : Task.CompletedTask;

    [RelayCommand]
    private void OpenInbox() => IsInboxOpen = true;

    private Task RefreshAllAsync() => Task.WhenAll(_all.Select(a => LoadConfirmationsAsync(a, _polling.Token)));

    /// <summary>Sends one account's part of a batch in a single request through its own connection.</summary>
    private async Task RespondBatchAsync(AccountItemViewModel account, IReadOnlyList<ConfirmationItemViewModel> items, bool accept)
    {
        if (_shell.Vault is not { IsUnlocked: true } vault)
        {
            return;
        }

#if DEBUG
        if (Environment.GetEnvironmentVariable("BSA_SAMPLE_CONFIRMATIONS") == "1")
        {
            await Task.Delay(500, CancellationToken.None).ConfigureAwait(true);
            foreach (var item in items)
            {
                account.Remove(item);
            }

            return;
        }
#endif

        bool ok = false;
        try
        {
            var maFile = vault.Load(account.SteamId);
            var connection = _shell.ConnectionFor(account.SteamId);
            if (await WebSession.EnsureAsync(connection, maFile.Session).ConfigureAwait(true))
            {
                vault.Save(maFile);
            }

            ok = await Confirmations.RespondAsync(
                connection, maFile.IdentitySecret!, maFile.DeviceId!, items.Select(i => i.Confirmation).ToList(), accept).ConfigureAwait(true);
        }
        catch (SessionExpiredException)
        {
            SetProblem(account, Strings.SignInToLoadConfirmations, needsLogin: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Reported on each row below.
        }

        foreach (var item in items)
        {
            vault.LogConfirmation(account.SteamId, item.Id, item.Confirmation.Type, item.Headline, accept, automatic: false, ok);
            if (ok)
            {
                account.Remove(item);
            }
            else
            {
                item.LeaveBatch();
                item.Error = Strings.ConfirmationFailed;
            }
        }
    }

    public void Dispose()
    {
        Detail?.Dispose();
        Inbox.Dispose();
        _polling.Cancel();
        _polling.Dispose();
        _timer.Stop();
        foreach (var account in _all)
        {
            account.ForgetSecret();
        }
    }

    private async Task PollConfirmationsAsync(AccountItemViewModel account, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Jitter(TimeSpan.FromSeconds(5)), cancellationToken).ConfigureAwait(true);
            while (true)
            {
                // Only while confirmations are on screen.
                if (await _shell.WaitForConfirmationChecksAsync(cancellationToken).ConfigureAwait(true))
                {
                    // Coming back: don't let every account fire at once.
                    await Task.Delay(Jitter(TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(true);
                }

                if (DateTime.UtcNow - account.LastCheckedUtc >= PollInterval)
                {
                    await LoadConfirmationsAsync(account, cancellationToken).ConfigureAwait(true);
                }

                await Task.Delay(PollInterval + Jitter(TimeSpan.FromSeconds(30)), cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Page closed or app locked.
        }
    }

    private async Task LoadConfirmationsAsync(AccountItemViewModel account, CancellationToken cancellationToken, bool mayAutoSignIn = true)
    {
        if (_shell.Vault is not { IsUnlocked: true } vault)
        {
            return;
        }

        account.LastCheckedUtc = DateTime.UtcNow;
        if (account.IsLinkPending)
        {
            // Not active on Steam yet: nothing to load until the link is finished.
            SetProblem(account, Strings.LinkingNotFinished, needsLogin: false);
            return;
        }

#if DEBUG
        // UI review without a real account: BSA_SAMPLE_CONFIRMATIONS=1 shows sample rows. Sending is simulated.
        if (Environment.GetEnvironmentVariable("BSA_SAMPLE_CONFIRMATIONS") == "1")
        {
            account.SetConfirmations(SampleConfirmations.Create(), c => new ConfirmationItemViewModel(
                c, account.Name, _shell.ConfirmationPolicy, async (item, _) =>
                {
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(true);
                    account.Remove(item);
                })
            { Open = i => OpenDetail(account, i) });
            return;
        }
#endif

        try
        {
            var maFile = vault.Load(account.SteamId);
            if (string.IsNullOrEmpty(maFile.IdentitySecret) || string.IsNullOrEmpty(maFile.DeviceId))
            {
                SetProblem(account, Strings.NoConfirmationSecret, needsLogin: false);
                return;
            }

            var connection = _shell.ConnectionFor(account.SteamId);
            if (await WebSession.EnsureAsync(connection, maFile.Session, cancellationToken).ConfigureAwait(true))
            {
                vault.Save(maFile);
            }

            var list = await Confirmations.GetAsync(connection, maFile.IdentitySecret, maFile.DeviceId, cancellationToken)
                .ConfigureAwait(true);
            account.Problem = null;
            account.SetConfirmations(list, c => new ConfirmationItemViewModel(
                c, account.Name, _shell.ConfirmationPolicy, (item, accept) => RespondAsync(account, item, accept))
            { Open = i => OpenDetail(account, i) });
        }
        catch (SessionExpiredException)
        {
            // A saved password renews the session by itself.
            if (mayAutoSignIn && await _shell.TryAutoSignInAsync(account.SteamId, cancellationToken).ConfigureAwait(true))
            {
                await LoadConfirmationsAsync(account, cancellationToken, mayAutoSignIn: false).ConfigureAwait(true);
                return;
            }

            SetProblem(account, Strings.SignInToLoadConfirmations, needsLogin: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Fail-closed: a dead proxy stops the account; say so instead of a generic network error.
            bool proxyDead = vault.GetAccountProxyId(account.SteamId) is { } proxyId
                             && vault.ListProxies().FirstOrDefault(p => p.Id == proxyId)?.Status == Storage.ProxyStatus.Dead;
            SetProblem(account, proxyDead ? Strings.ProxyNotResponding : Strings.CantReachSteam, needsLogin: false);
        }
    }

    private async Task RespondAsync(AccountItemViewModel account, ConfirmationItemViewModel item, bool accept)
    {
        if (_shell.Vault is not { IsUnlocked: true } vault)
        {
            return;
        }

        bool ok = false;
        try
        {
            var maFile = vault.Load(account.SteamId);
            var connection = _shell.ConnectionFor(account.SteamId);
            if (await WebSession.EnsureAsync(connection, maFile.Session).ConfigureAwait(true))
            {
                vault.Save(maFile);
            }

            ok = await Confirmations.RespondAsync(
                connection, maFile.IdentitySecret!, maFile.DeviceId!, [item.Confirmation], accept).ConfigureAwait(true);
        }
        catch (SessionExpiredException)
        {
            SetProblem(account, Strings.SignInToLoadConfirmations, needsLogin: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Reported on the row below.
        }

        vault.LogConfirmation(account.SteamId, item.Id, item.Confirmation.Type, item.Headline, accept, automatic: false, ok);
        if (ok)
        {
            account.Remove(item);
        }
        else
        {
            item.Error = Strings.ConfirmationFailed;
        }
    }

    private static void SetProblem(AccountItemViewModel account, string problem, bool needsLogin)
    {
        account.Problem = problem;
        account.ProblemNeedsLogin = needsLogin;
    }

    private static TimeSpan Jitter(TimeSpan max)
        => TimeSpan.FromMilliseconds(System.Security.Cryptography.RandomNumberGenerator.GetInt32((int)max.TotalMilliseconds));

    private void Tick()
    {
        if (RemovedText is not null && --_removedSecondsLeft <= 0)
        {
            RemovedText = null;
        }

        foreach (var account in _all)
        {
            account.RefreshIfRotated();
        }

        long now = (SelectedAccount ?? _all.FirstOrDefault())?.Clock.Now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SecondsRemaining = SteamGuardCode.SecondsRemaining(now);
        SecondsLeftText = string.Format(CultureInfo.CurrentCulture, SecondsLeftFormat, SecondsRemaining);
    }

    private void ApplyFilter()
    {
        var selected = SelectedAccount;
        Accounts.Clear();
        foreach (var account in _all.Where(a => a.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            Accounts.Add(account);
        }

        SelectedAccount = selected is not null && Accounts.Contains(selected) ? selected : Accounts.FirstOrDefault();
    }
}
