using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Guard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Main window: account list, shared timer, and the selected account's code.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>When fewer seconds are left, Copy copies the next code instead.</summary>
    private const int CopyNextThreshold = 5;

    private static readonly CompositeFormat SecondsLeftFormat = CompositeFormat.Parse(Strings.SecondsLeftFormat);

    private readonly List<AccountItemViewModel> _all = [];
    private readonly DispatcherTimer _timer;

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
