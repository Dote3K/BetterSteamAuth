using System.Globalization;
using System.Text;
using Avalonia.Threading;
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
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly CompositeFormat VersionFormat = CompositeFormat.Parse(Strings.VersionFormat);
    private static readonly CompositeFormat UpToDateFormat = CompositeFormat.Parse(Strings.UpdateUpToDateFormat);
    private static readonly CompositeFormat UpdateReadyFormat = CompositeFormat.Parse(Strings.UpdateReadyFormat);
    private static readonly CompositeFormat DownloadingFormat = CompositeFormat.Parse(Strings.UpdateDownloadingFormat);
    private static readonly CompositeFormat MinutesFormat = CompositeFormat.Parse(Strings.MinutesFormat);
    private static readonly CompositeFormat EveryMinutesFormat = CompositeFormat.Parse(Strings.EveryMinutesFormat);
    private static readonly int[] AutoLockMinutes = [5, 15, 30, 60];
    private static readonly int[] KeepCounts = [5, 10, 20, 50];
    private static readonly CompositeFormat KeepLastFormat = CompositeFormat.Parse(Strings.KeepLastFormat);
    private static readonly CompositeFormat LastAutoBackupFormat = CompositeFormat.Parse(Strings.LastAutoBackupFormat);

    private readonly ShellViewModel _shell;

    public SettingsViewModel(ShellViewModel shell, string? notice = null)
    {
        _shell = shell;
        PasswordNotice = notice;
        var current = shell.NetworkOptions;
        SniBypass = SniBypassChoices.Single(c => c.Value == current.SniBypass);
        EncryptedDns = EncryptedDnsChoices.Single(c => c.Value == current.EncryptedDns);

        RefreshChoices = [.. ConfirmationPolicy.RefreshChoices.Select(s => new Choice<int>(s, s switch
        {
            30 => Strings.Every30Seconds,
            60 => Strings.EveryMinute,
            _ => string.Format(CultureInfo.CurrentCulture, EveryMinutesFormat, s / 60),
        }))];
        int refresh = shell.ConfirmationPolicy.RefreshSeconds;
        Refresh = RefreshChoices.FirstOrDefault(c => c.Value == refresh) ?? RefreshChoices.Single(c => c.Value == ConfirmationPolicy.DefaultRefreshSeconds);

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

        // Opening Settings is seeing the details of a rejected update, so the main window's bar goes away.
        shell.Updater.AcknowledgeRejected();
        shell.Updater.StatusChanged += OnUpdaterChanged;
    }

    public void Dispose() => _shell.Updater.StatusChanged -= OnUpdaterChanged;

    // Updates
    public static string VersionText => string.Format(CultureInfo.CurrentCulture, VersionFormat, ShellViewModel.AppVersion ?? "dev");

    /// <summary>One line under the version; empty until there's something to say.</summary>
    public string? UpdateStatusText => StatusText(_shell.Updater);

    internal static string? StatusText(Updater updater)
    {
        return updater.State switch
        {
            UpdateState.NotInstalled => Strings.UpdatesNotInstalled,
            UpdateState.Checking => Strings.UpdateChecking,
            UpdateState.Downloading => string.Format(CultureInfo.CurrentCulture, DownloadingFormat, updater.DownloadProgress),
            UpdateState.Ready => string.Format(CultureInfo.CurrentCulture, UpdateReadyFormat, updater.ReadyVersion),
            UpdateState.Rejected => Strings.UpdateRejected,
            UpdateState.CheckFailed => Strings.UpdateCheckFailed,
            UpdateState.UpToDate when updater.LastCheckedAt is { } at && DateTimeOffset.UtcNow - at >= TimeSpan.FromMinutes(1)
                => string.Format(CultureInfo.CurrentCulture, UpToDateFormat, ConfirmationItemViewModel.Age(at)),
            UpdateState.UpToDate => Strings.UpdateUpToDate,
            _ => null,
        };
    }

    public bool HasUpdateStatus => UpdateStatusText is not null;

    /// <summary>Why the update was rejected or the check failed, under Details.</summary>
    public string? UpdateDetails => _shell.Updater.State is UpdateState.Rejected or UpdateState.CheckFailed
        ? _shell.Updater.FailureDetails
        : null;

    public bool HasUpdateDetails => UpdateDetails is not null;

    public bool IsUpdateReady => _shell.Updater.State == UpdateState.Ready;

    public bool CanCheckForUpdates => !IsUpdateReady
        && _shell.Updater.State is not (UpdateState.Checking or UpdateState.Downloading);

    public bool AutoCheckUpdates
    {
        get => _shell.Updater.AutoCheck;
        set
        {
            if (value != _shell.Updater.AutoCheck)
            {
                _shell.Updater.AutoCheck = value;
                OnPropertyChanged();
                _shell.Updater.CheckIfDue();
            }
        }
    }

    public bool IncludeBetaUpdates
    {
        get => _shell.Updater.IncludeBeta;
        set
        {
            if (value != _shell.Updater.IncludeBeta)
            {
                _shell.Updater.IncludeBeta = value;
                OnPropertyChanged();
                if (value)
                {
                    _ = _shell.Updater.CheckAsync();
                }
            }
        }
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => _shell.Updater.CheckAsync();

    [RelayCommand]
    private void RestartToUpdate() => _shell.RestartToUpdate();

    private void OnUpdaterChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(HasUpdateStatus));
        OnPropertyChanged(nameof(UpdateDetails));
        OnPropertyChanged(nameof(HasUpdateDetails));
        OnPropertyChanged(nameof(IsUpdateReady));
        OnPropertyChanged(nameof(CanCheckForUpdates));
    });

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

    /// <summary>Settings → Advanced → Removed accounts (N); hidden when there are none.</summary>
    public int RemovedCount { get; }

    public bool HasRemoved => RemovedCount > 0;

    public string RemovedLabel => string.Create(CultureInfo.CurrentCulture, $"{Strings.RemovedAccounts} ({RemovedCount})");

    [RelayCommand]
    private void OpenRemoved() => _shell.ShowRemovedAccounts();

    /// <summary>Security settings only apply when the data has a password.</summary>
    public bool HasPassword => _shell.Vault?.IsEncrypted == true;

    public string PasswordBody => HasPassword ? Strings.AppPasswordBody : Strings.NoAppPasswordBody;

    public string PasswordButton => HasPassword ? Strings.ChangePasswordMenu : Strings.SetPasswordMenu;

    /// <summary>"Password changed." after coming back from the password page.</summary>
    public string? PasswordNotice { get; }

    public bool HasPasswordNotice => PasswordNotice is not null;

    [RelayCommand]
    private void OpenPassword() => _shell.ShowPassword();

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

    // Static: the setup settings page shows the same choices.
    public static IReadOnlyList<Choice<SniBypass>> SniBypassChoices { get; } =
    [
        new(Steam.Network.SniBypass.Auto, Strings.Auto),
        new(Steam.Network.SniBypass.On, Strings.On),
        new(Steam.Network.SniBypass.Off, Strings.Off),
    ];

    public static IReadOnlyList<Choice<EncryptedDns>> EncryptedDnsChoices { get; } =
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

    public IReadOnlyList<Choice<int>> RefreshChoices { get; }

    [ObservableProperty]
    public partial Choice<int> Refresh { get; set; }

    partial void OnRefreshChanged(Choice<int> value)
    {
        if (value is not null && value.Value != _shell.ConfirmationPolicy.RefreshSeconds)
        {
            _shell.SetRefreshSeconds(value.Value);
        }
    }

    public bool UndoEnabled
    {
        get => _shell.ConfirmationPolicy.UndoEnabled;
        set
        {
            if (value != _shell.ConfirmationPolicy.UndoEnabled)
            {
                _shell.SetUndoEnabled(value);
                OnPropertyChanged();
            }
        }
    }

    public static IReadOnlyList<Choice<int>> HistoryChoices { get; } =
    [
        .. ShellViewModel.HistoryDayChoices.Select(d => new Choice<int>(d, d == 365
            ? Strings.OneYear
            : string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DaysFormat), d))),
    ];

    /// <summary>How long confirmation history is kept; lowering it deletes older entries at once.</summary>
    public Choice<int> KeepHistory
    {
        get => HistoryChoices.FirstOrDefault(c => c.Value == _shell.HistoryDays) ?? HistoryChoices.Single(c => c.Value == ShellViewModel.DefaultHistoryDays);
        set
        {
            if (value is not null && value.Value != _shell.HistoryDays)
            {
                _shell.HistoryDays = value.Value;
                OnPropertyChanged();
            }
        }
    }

    public bool NotifyEnabled
    {
        get => _shell.NotifyEnabled;
        set
        {
            _shell.NotifyEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool HasTray => _shell.Tray is not null;

    public bool KeepInTray
    {
        get => _shell.KeepInTray;
        set
        {
            _shell.KeepInTray = value;
            OnPropertyChanged();
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
