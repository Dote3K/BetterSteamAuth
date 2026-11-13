using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Guard;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Main window: account list, shared timer, and the selected account's code.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>When fewer seconds are left, Copy copies the next code instead.</summary>
    private const int CopyNextThreshold = 5;

    private static readonly CompositeFormat SecondsLeftFormat = CompositeFormat.Parse(Strings.SecondsLeftFormat);

    private readonly List<AccountItemViewModel> _all = [];
    private readonly DispatcherTimer _timer;
    private long _lastPeriod = -1;

    public MainWindowViewModel(Vault? vault)
    {
        foreach (var entry in vault?.ListAccounts() ?? [])
        {
            var maFile = vault!.Load(entry.SteamId);
            if (string.IsNullOrEmpty(maFile.SharedSecret))
            {
                continue;
            }

            _all.Add(new AccountItemViewModel(
                entry.SteamId,
                maFile.AccountName ?? entry.SteamId.ToString(CultureInfo.InvariantCulture),
                Convert.FromBase64String(maFile.SharedSecret),
                new SteamClock()));
        }

        ApplyFilter();
        SelectedAccount = Accounts.FirstOrDefault();
        Tick();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Tick());
        _timer.Start();
    }

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

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
        if (account is null || CopyToClipboard is null)
        {
            return;
        }

        string code = SteamGuardCode.SecondsRemaining(account.Clock.Now) <= CopyNextThreshold
            ? account.NextCode()
            : account.Code;
        await CopyToClipboard(code).ConfigureAwait(true);

        CopyLabel = Strings.Copied;
        await Task.Delay(1200).ConfigureAwait(true);
        CopyLabel = Strings.Copy;
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var account in _all)
        {
            account.ForgetSecret();
        }
    }

    private void Tick()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SecondsRemaining = SteamGuardCode.SecondsRemaining(now);
        SecondsLeftText = string.Format(CultureInfo.CurrentCulture, SecondsLeftFormat, SecondsRemaining);

        long period = now / SteamGuardCode.PeriodSeconds;
        if (period != _lastPeriod)
        {
            _lastPeriod = period;
            foreach (var account in _all)
            {
                account.Refresh();
            }
        }
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
