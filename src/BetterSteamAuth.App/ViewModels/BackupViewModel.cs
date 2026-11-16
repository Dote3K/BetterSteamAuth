using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Back up now: one data-only <c>.bsabackup</c> file.</summary>
public sealed partial class BackupViewModel : ObservableObject
{
    internal const string LastBackupKey = "backup.last_at";

    private static readonly CompositeFormat BackedUpFormat = CompositeFormat.Parse(Strings.BackedUpFormat);
    private static readonly CompositeFormat LastBackupFormat = CompositeFormat.Parse(Strings.LastBackupFormat);

    private readonly ShellViewModel _shell;
    private readonly Vault _vault;
    private readonly Action _back;
    private string? _savedPath;

    public BackupViewModel(ShellViewModel shell, Action back)
    {
        _shell = shell;
        _vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        _back = back;
        HasAppPassword = _vault.IsEncrypted;
        LastBackupText = LastBackup(_vault);
    }

    public bool HasAppPassword { get; }

    /// <summary>No app password: the backup gets its own, unless the user skips it.</summary>
    public bool AsksPassword => !HasAppPassword && !SkipPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AsksPassword), nameof(ShowsPlainWarning))]
    public partial bool SkipPassword { get; private set; }

    public bool ShowsPlainWarning => !HasAppPassword && SkipPassword;

    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string ConfirmPassword { get; set; } = "";

    public string? LastBackupText { get; }

    public bool HasLastBackup => LastBackupText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(BackUpLabel))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public string BackUpLabel => IsBusy ? Strings.BackingUp : Strings.BackUpButton;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; private set; }

    public bool HasResult => Result is not null;

    partial void OnPasswordChanged(string value) => Error = null;

    partial void OnConfirmPasswordChanged(string value) => Error = null;

    /// <summary>"Last backup: 3d", or null if there's never been one.</summary>
    internal static string? LastBackup(Vault vault)
        => long.TryParse(vault.GetSetting(LastBackupKey), NumberStyles.None, CultureInfo.InvariantCulture, out long at)
            ? string.Format(CultureInfo.CurrentCulture, LastBackupFormat, ConfirmationItemViewModel.Age(DateTimeOffset.FromUnixTimeSeconds(at)))
            : null;

    [RelayCommand]
    private void Back() => _back();

    [RelayCommand]
    private void SaveWithoutPassword()
    {
        SkipPassword = true;
        Error = null;
    }

    [RelayCommand]
    private void UsePassword() => SkipPassword = false;

    [RelayCommand]
    private async Task BackUpAsync()
    {
        if (IsBusy)
        {
            return;
        }

        string? newPassword = null;
        if (AsksPassword)
        {
            if (string.IsNullOrEmpty(Password))
            {
                Error = Strings.BackupPasswordNeeded;
                return;
            }

            if (Password != ConfirmPassword)
            {
                Error = Strings.PasswordsDontMatch;
                return;
            }

            newPassword = Password;
        }

        string name = "BetterSteamAuth-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + VaultBackup.Extension;
        string? path = await _shell.Platform.SaveFileAsync(Strings.BackUpTitle, name).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Result = null;
        try
        {
            var manifest = await Task.Run(() => VaultBackup.Create(_vault, path, newPassword, ShellViewModel.AppVersion)).ConfigureAwait(true);
            _vault.SetSetting(LastBackupKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            _savedPath = path;
            Password = "";
            ConfirmPassword = "";
            Result = string.Format(CultureInfo.CurrentCulture, BackedUpFormat, manifest.Accounts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error = Strings.BackupFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (_savedPath is not null)
        {
            _shell.Platform.RevealFile(_savedPath);
        }
    }
}
