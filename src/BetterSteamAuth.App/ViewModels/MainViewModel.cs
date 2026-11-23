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

    private static readonly CompositeFormat RemovedFormat = CompositeFormat.Parse(Strings.RemovedFormat);
    private static readonly CompositeFormat RestartToUpdateFormat = CompositeFormat.Parse(Strings.RestartToUpdateFormat);
    private static readonly CompositeFormat UpdateBarDownloadingFormat = CompositeFormat.Parse(Strings.UpdateBarDownloadingFormat);
    private static readonly CompositeFormat UpdateBarReadyFormat = CompositeFormat.Parse(Strings.UpdateReadyFormat);
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

        Inbox = new InboxViewModel(_all, RespondBatchAsync, RefreshAllAsync, () => IsInboxOpen = false, shell.ConfirmationPolicy);

        // The shell's monitor checks every account on its own schedule; this page shows the results.
        shell.Monitor.Checked += OnChecked;
        foreach (var account in _all)
        {
            if (shell.Monitor.Latest(account.SteamId) is { } latest)
            {
                Apply(account, latest);
            }
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

    partial void OnSelectedAccountChanged(AccountItemViewModel? value)
    {
        // Kept for the page's lifetime, so a batch's undo window keeps running when another account is selected.
        if (value is { Selection: null })
        {
            value.Selection = new InboxViewModel(
                [value], RespondBatchAsync, () => _shell.Monitor.CheckNowAsync(value.SteamId), () => { }, _shell.ConfirmationPolicy);
        }
    }

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

    [RelayCommand]
    private void MoveFromPhone() => _shell.ShowMoveFromPhone();

    [RelayCommand]
    private void BackUp() => _shell.ShowBackup(fromSettings: false);

    /// <summary>"Removed {name} · Undo" (or a notice without Undo), shown for a few seconds after removing an account.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoved))]
    public partial string? RemovedText { get; private set; }

    public bool HasRemoved => RemovedText is not null;

    /// <summary>The last automatic backup failed: one quiet warning until the next one works.</summary>
    [ObservableProperty]
    public partial bool AutoBackupFailed { get; private set; }

    public bool CanUndoRemove => _removed is not null;

    /// <summary>"Restart to update (1.4.0)" in the ⋯ menu, with a dot on the button, once an update is downloaded and verified.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateReady), nameof(MoreName))]
    public partial string? UpdateReadyText { get; private set; }

    public bool HasUpdateReady => UpdateReadyText is not null;

    /// <summary>"Downloading the update… 42%" as a disabled item at the top of the ⋯ menu while it downloads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadingUpdate))]
    public partial string? DownloadingText { get; private set; }

    public bool IsDownloadingUpdate => DownloadingText is not null;

    public string MoreName => HasUpdateReady ? Strings.MoreUpdateReady : Strings.More;

    /// <summary>An update didn't verify: one quiet bar until Settings has shown the details.</summary>
    [ObservableProperty]
    public partial bool UpdateRejected { get; private set; }

    [RelayCommand]
    private void RestartToUpdate() => _shell.RestartToUpdate();

    /// <summary>
    /// A quiet bar at the bottom while an update downloads ("Downloading 1.2.1… 42%") and once it's ready
    /// ("1.2.1 is ready…" with Restart now / Later). Later hides it for this session.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateBar))]
    public partial string? UpdateBarText { get; private set; }

    public bool HasUpdateBar => UpdateBarText is not null;

    /// <summary>Restart now and Later only once the update is ready.</summary>
    [ObservableProperty]
    public partial bool UpdateBarIsReady { get; private set; }

    [RelayCommand]
    private void DismissUpdateBar()
    {
        _shell.Updater.DismissReadyNotice();
        UpdateBarText = null;
    }

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
        => (account ?? SelectedAccount) is { } target ? _shell.Monitor.CheckNowAsync(target.SteamId) : Task.CompletedTask;

    [RelayCommand]
    private void OpenInbox() => IsInboxOpen = true;

    private Task RefreshAllAsync() => _shell.Monitor.CheckAllNowAsync();

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

            _shell.Monitor.Forget(account.SteamId, [.. items.Select(i => i.Id)]);
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

        if (ok)
        {
            _shell.Monitor.Forget(account.SteamId, [.. items.Select(i => i.Id)]);
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
        foreach (var account in _all)
        {
            account.Selection?.Dispose();
        }

        _shell.Monitor.Checked -= OnChecked;
        _polling.Cancel();
        _polling.Dispose();
        _timer.Stop();
        foreach (var account in _all)
        {
            account.ForgetSecret();
        }
    }

    private void OnChecked(object? sender, Services.AccountCheck check)
    {
        if (_all.FirstOrDefault(a => a.SteamId == check.SteamId) is { } account)
        {
            Apply(account, check);
        }
    }

    /// <summary>Shows an account's latest check: its confirmations, or why they couldn't be loaded.</summary>
    private void Apply(AccountItemViewModel account, Services.AccountCheck check)
    {
        account.LastCheckedUtc = check.CheckedUtc;
        if (check.Problem is not null)
        {
            SetProblem(account, check.Problem, check.ProblemNeedsLogin);
            return;
        }

        account.Problem = null;
#if DEBUG
        if (SampleMode.IsOn)
        {
            // Sending is simulated in sample mode.
            account.SetConfirmations(check.Confirmations, c => new ConfirmationItemViewModel(
                c, account.Name, _shell.ConfirmationPolicy, async (item, _) =>
                {
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(true);
                    account.Remove(item);
                    _shell.Monitor.Forget(account.SteamId, [item.Id]);
                })
            { Open = i => OpenDetail(account, i), Picture = SampleConfirmations.Picture(c) });
            return;
        }
#endif

        account.SetConfirmations(check.Confirmations, c => new ConfirmationItemViewModel(
            c, account.Name, _shell.ConfirmationPolicy, (item, accept) => RespondAsync(account, item, accept))
        { Open = i => OpenDetail(account, i) });
        _ = LoadPicturesAsync(account, _shell.ConnectionFor(account.SteamId), _polling.Token);
    }

    /// <summary>Avatars and item images for new rows, one at a time through the account's connection.</summary>
    private static async Task LoadPicturesAsync(AccountItemViewModel account, AccountConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var item in account.Confirmations.Where(c => c.WantsPicture && !c.PictureRequested).ToList())
            {
                item.PictureRequested = true;
                item.Picture = await Services.SteamImages.LoadAsync(connection, item.Confirmation.Icon, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Page closed.
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
            _shell.Monitor.Forget(account.SteamId, [item.Id]);
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

    private void Tick()
    {
        AutoBackupFailed = _shell.AutoBackup.LastFailed;
        var updater = _shell.Updater;
        DownloadingText = updater.State == Services.UpdateState.Downloading
            ? SettingsViewModel.StatusText(updater)
            : null;
        UpdateReadyText = updater.State == Services.UpdateState.Ready
            ? string.Format(CultureInfo.CurrentCulture, RestartToUpdateFormat, updater.ReadyVersion)
            : null;
        UpdateRejected = updater.RejectedNoticePending;
        (UpdateBarText, UpdateBarIsReady) = updater.State switch
        {
            Services.UpdateState.Downloading => (string.Format(
                CultureInfo.CurrentCulture, UpdateBarDownloadingFormat, updater.DownloadingVersion, updater.DownloadProgress), false),
            Services.UpdateState.Ready when !updater.ReadyNoticeDismissed => (string.Format(
                CultureInfo.CurrentCulture, UpdateBarReadyFormat, updater.ReadyVersion), true),
            _ => ((string?)null, false),
        };
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
