using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.App.Services;
using BetterSteamAuth.Steam.Network;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings page: security, confirmations, data, Advanced → Network.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly CompositeFormat MinutesFormat = CompositeFormat.Parse(Strings.MinutesFormat);
    private static readonly int[] AutoLockMinutes = [5, 15, 30, 60];
    private static readonly int[] KeepCounts = [5, 10, 20, 50];
    private static readonly CompositeFormat KeepLastFormat = CompositeFormat.Parse(Strings.KeepLastFormat);
    private static readonly CompositeFormat LastAutoBackupFormat = CompositeFormat.Parse(Strings.LastAutoBackupFormat);

    private readonly ShellViewModel _shell;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        var current = shell.NetworkOptions;
        SniBypass = SniBypassChoices.Single(c => c.Value == current.SniBypass);
        EncryptedDns = EncryptedDnsChoices.Single(c => c.Value == current.EncryptedDns);

        AutoLockChoices = [new(0, Strings.Never), .. AutoLockMinutes.Select(m => new Choice<int>(m, string.Format(CultureInfo.CurrentCulture, MinutesFormat, m)))];
        int minutes = shell.AutoLockMinutes;
        AutoLock = AutoLockChoices.FirstOrDefault(c => c.Value == minutes) ?? AutoLockChoices.Single(c => c.Value == ShellViewModel.DefaultAutoLockMinutes);

        // Names come from the files in encrypted vaults; the count alone is enough here.
        RemovedCount = shell.Vault?.ListRemoved().Count ?? 0;
        LastBackupText = shell.Vault is { } vault ? BackupViewModel.LastBackup(vault) : null;

        var auto = shell.AutoBackup;
        KeepChoices = [.. KeepCounts.Select(n => new Choice<int>(n, string.Format(CultureInfo.CurrentCulture, KeepLastFormat, n))), new(AutoBackup.KeepAll, Strings.KeepAll)];
        AutoBackupFrequency = FrequencyChoices.Single(c => c.Value == auto.Frequency);
        AutoBackupKeep = KeepChoices.FirstOrDefault(c => c.Value == auto.Keep) ?? KeepChoices[1];
        AutoBackupFolder = auto.Folder;
        LastAutoBackupText = auto.LastAt is { } at
            ? string.Format(CultureInfo.CurrentCulture, LastAutoBackupFormat, ConfirmationItemViewModel.Age(at))
            : null;
    }

    // Automatic backups
    public bool AutoBackupAvailable => _shell.AutoBackup.IsAvailable;

    public bool AutoBackupUnavailable => !AutoBackupAvailable;

    public bool AutoBackupEnabled
    {
        get => _shell.AutoBackup.Enabled;
        set
        {
            if (value != _shell.AutoBackup.Enabled)
            {
                _shell.AutoBackup.Enabled = value;
                OnPropertyChanged();
                if (value)
                {
                    _shell.CheckAutoBackup();
                }
            }
        }
    }

    public IReadOnlyList<Choice<AutoBackupFrequency>> FrequencyChoices { get; } =
    [
        new(Services.AutoBackupFrequency.AsSoonAsPossible, Strings.AutoBackupAsap),
        new(Services.AutoBackupFrequency.Daily, Strings.AutoBackupDaily),
        new(Services.AutoBackupFrequency.Weekly, Strings.AutoBackupWeekly),
    ];

    [ObservableProperty]
    public partial Choice<AutoBackupFrequency> AutoBackupFrequency { get; set; }

    partial void OnAutoBackupFrequencyChanged(Choice<AutoBackupFrequency> value)
    {
        if (value is not null && value.Value != _shell.AutoBackup.Frequency)
        {
            _shell.AutoBackup.Frequency = value.Value;
        }
    }

    public IReadOnlyList<Choice<int>> KeepChoices { get; }

    [ObservableProperty]
    public partial Choice<int> AutoBackupKeep { get; set; }

    partial void OnAutoBackupKeepChanged(Choice<int> value)
    {
        if (value is not null && value.Value != _shell.AutoBackup.Keep)
        {
            _shell.AutoBackup.Keep = value.Value;
        }
    }

    [ObservableProperty]
    public partial string AutoBackupFolder { get; private set; }

    public string? LastAutoBackupText { get; }

    public bool HasLastAutoBackup => LastAutoBackupText is not null;

    [RelayCommand]
    private async Task ChangeAutoBackupFolderAsync()
    {
        if (await _shell.Platform.PickFolderAsync(Strings.ChooseBackupFolder).ConfigureAwait(true) is { } folder)
        {
            _shell.AutoBackup.Folder = folder;
            AutoBackupFolder = folder;
        }
    }

    /// <summary>"Last backup: 3d" under Back up, or the generic description if there's never been one.</summary>
    public string? LastBackupText { get; }

    public string BackupDetail => LastBackupText ?? Strings.BackUpBody;

    [RelayCommand]
    private void OpenBackup() => _shell.ShowBackup(fromSettings: true);

    [RelayCommand]
    private void OpenRestore() => _shell.ShowRestore();

    [RelayCommand]
    private void OpenExport() => _shell.ShowExport();

    /// <summary>Settings → Data → Removed accounts (N); hidden when there are none.</summary>
    public int RemovedCount { get; }

    public bool HasRemoved => RemovedCount > 0;

    public string RemovedLabel => string.Create(CultureInfo.CurrentCulture, $"{Strings.RemovedAccounts} ({RemovedCount})");

    [RelayCommand]
    private void OpenRemoved() => _shell.ShowRemovedAccounts();

    /// <summary>Security settings only apply when the data has a password.</summary>
    public bool HasPassword => _shell.Vault?.IsEncrypted == true;

    public bool CanRemember => HasPassword && RememberedKey.IsSupported;

    public bool RememberOnDevice
    {
        get => _shell.IsRemembered;
        set
        {
            if (value != _shell.IsRemembered)
            {
                _shell.SetRemembered(value);
                OnPropertyChanged();
            }
        }
    }

    public IReadOnlyList<Choice<int>> AutoLockChoices { get; }

    [ObservableProperty]
    public partial Choice<int> AutoLock { get; set; }

    partial void OnAutoLockChanged(Choice<int> value)
    {
        if (value is not null && value.Value != _shell.AutoLockMinutes)
        {
            _shell.AutoLockMinutes = value.Value;
        }
    }

    public IReadOnlyList<Choice<SniBypass>> SniBypassChoices { get; } =
    [
        new(Steam.Network.SniBypass.Auto, Strings.Auto),
        new(Steam.Network.SniBypass.On, Strings.On),
        new(Steam.Network.SniBypass.Off, Strings.Off),
    ];

    public IReadOnlyList<Choice<EncryptedDns>> EncryptedDnsChoices { get; } =
    [
        new(Steam.Network.EncryptedDns.Off, Strings.Off),
        new(Steam.Network.EncryptedDns.Cloudflare, "Cloudflare"),
        new(Steam.Network.EncryptedDns.Google, "Google"),
    ];

    [ObservableProperty]
    public partial Choice<SniBypass> SniBypass { get; set; }

    [ObservableProperty]
    public partial Choice<EncryptedDns> EncryptedDns { get; set; }

    partial void OnSniBypassChanged(Choice<SniBypass> value) => Apply();

    partial void OnEncryptedDnsChanged(Choice<EncryptedDns> value) => Apply();

    public bool AcceptTradesInBulk
    {
        get => _shell.ConfirmationPolicy.AcceptTradesInBulk;
        set
        {
            if (value != _shell.ConfirmationPolicy.AcceptTradesInBulk)
            {
                _shell.SetAcceptTradesInBulk(value);
                OnPropertyChanged();
            }
        }
    }

    public bool CheckInBackground
    {
        get => _shell.CheckInBackground;
        set
        {
            if (value != _shell.CheckInBackground)
            {
                _shell.CheckInBackground = value;
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void Back() => _shell.ShowMain();

    private void Apply()
    {
        // Both are set in the constructor; ignore the first change notifications.
        if (SniBypass is null || EncryptedDns is null)
        {
            return;
        }

        var options = new NetworkOptions(SniBypass.Value, EncryptedDns.Value);
        if (options != _shell.NetworkOptions)
        {
            _shell.ApplyNetworkOptions(options);
        }
    }
}
