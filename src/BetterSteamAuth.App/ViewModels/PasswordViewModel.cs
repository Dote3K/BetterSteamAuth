using BetterSteamAuth.App.Resources;
using BetterSteamAuth.App.Services;
using BetterSteamAuth.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>
/// Settings → App password. Changes the password of encrypted data, or sets one on plain data (which encrypts it).
/// </summary>
public sealed partial class PasswordViewModel(ShellViewModel shell) : ObservableObject
{
    private const int MinPasswordLength = 8;
    private static readonly System.Text.CompositeFormat OldDataNotDeletedFormat = System.Text.CompositeFormat.Parse(Strings.OldDataNotDeletedFormat);

    /// <summary>True when the data already has a password; otherwise this page sets one.</summary>
    public bool IsChange { get; } = shell.Vault?.IsEncrypted == true;

    public string Title => IsChange ? Strings.ChangePasswordTitle : Strings.SetPasswordTitle;

    public string Body => IsChange ? Strings.ChangePasswordBody : Strings.SetPasswordLaterBody;

    public bool CanRemember => !IsChange && RememberedKey.IsSupported;

    [ObservableProperty]
    public partial string CurrentPassword { get; set; } = "";

    [ObservableProperty]
    public partial string NewPassword { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = "";

    [ObservableProperty]
    public partial bool RememberOnDevice { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel), nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public string SaveLabel => IsBusy ? Strings.Saving : IsChange ? Strings.ChangePassword : Strings.SetPassword;

    partial void OnCurrentPasswordChanged(string value) => Error = null;

    partial void OnNewPasswordChanged(string value) => Error = null;

    partial void OnConfirmPasswordChanged(string value) => Error = null;

    [RelayCommand]
    private void Back() => shell.ShowSettings();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (NewPassword.Length < MinPasswordLength)
        {
            Error = Strings.PasswordTooShort;
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            Error = Strings.PasswordsDontMatch;
            return;
        }

        IsBusy = true;
        try
        {
            if (IsChange)
            {
                var vault = shell.Vault ?? throw new InvalidOperationException("No data is open.");
                string current = CurrentPassword, next = NewPassword;
                await Task.Run(() => vault.ChangePassword(current, next)).ConfigureAwait(true);
                shell.ShowSettings(Strings.PasswordChanged);
            }
            else
            {
                string? leftover = await shell.SetAppPasswordAsync(NewPassword, RememberOnDevice).ConfigureAwait(true);
                shell.ShowSettings(leftover is null
                    ? Strings.PasswordSetDone
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, OldDataNotDeletedFormat, leftover));
            }
        }
        catch (WrongPasswordException)
        {
            Error = Strings.WrongPassword;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            Error = Strings.PasswordNotSaved;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
