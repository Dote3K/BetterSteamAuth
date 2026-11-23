using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Unlock page shown on launch when the data has a password.</summary>
public sealed partial class UnlockViewModel(ShellViewModel shell) : ObservableObject
{
    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnlockLabel))]
    public partial bool IsBusy { get; private set; }

    public string UnlockLabel => IsBusy ? Strings.Unlocking : Strings.Unlock;

    partial void OnPasswordChanged(string value) => Error = null;

    /// <summary>"Forgot password?" was clicked: explain, and offer restoring a backup.</summary>
    [ObservableProperty]
    public partial bool ShowsForgot { get; private set; }

    [RelayCommand]
    private void Forgot() => ShowsForgot = true;

    [RelayCommand]
    private void RestoreFromBackup() => shell.ShowRestoreReplacing(fromFirstRun: false);

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (shell.Vault is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // Argon2id takes a moment; keep the UI responsive.
            string password = Password;
            await Task.Run(() => shell.Vault.Unlock(password)).ConfigureAwait(true);
            shell.ShowMain();
        }
        catch (WrongPasswordException)
        {
            Error = Strings.WrongPassword;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
