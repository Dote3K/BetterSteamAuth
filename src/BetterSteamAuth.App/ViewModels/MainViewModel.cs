using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Guard;
using BetterSteamAuth.Steam;
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

    private readonly List<AccountItemViewModel> _all = [];
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _polling = new();

    private readonly ShellViewModel _shell;

    public MainViewModel(ShellViewModel shell)
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
                shell.ConnectionFor(entry.SteamId).Clock));
        }

        ApplyFilter();
        SelectedAccount = Accounts.FirstOrDefault();
        Tick();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Tick());
        _timer.Start();

        // Each account polls on its own jittered schedule.
        foreach (var account in _all)
        {
            _ = PollConfirmationsAsync(account, _polling.Token);
        }
    }

    public bool CanLock { get; }

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    public bool HasAccounts => _all.Count > 0;

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

    public void Dispose()
    {
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
                await LoadConfirmationsAsync(account, cancellationToken).ConfigureAwait(true);
                await Task.Delay(PollInterval + Jitter(TimeSpan.FromSeconds(30)), cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Page closed or app locked.
        }
    }

    private async Task LoadConfirmationsAsync(AccountItemViewModel account, CancellationToken cancellationToken)
    {
        if (_shell.Vault is not { IsUnlocked: true } vault)
        {
            return;
        }

#if DEBUG
        // UI review without a real account: BSA_SAMPLE_CONFIRMATIONS=1 shows sample rows. Sending is simulated.
        if (Environment.GetEnvironmentVariable("BSA_SAMPLE_CONFIRMATIONS") == "1")
        {
            account.SetConfirmations(SampleConfirmations.Create(), async (item, _) =>
            {
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(true);
                account.Remove(item);
            });
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
            account.SetConfirmations(list, (item, accept) => RespondAsync(account, item, accept));
        }
        catch (SessionExpiredException)
        {
            SetProblem(account, Strings.SignInToLoadConfirmations, needsLogin: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            SetProblem(account, Strings.CantReachSteam, needsLogin: false);
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
