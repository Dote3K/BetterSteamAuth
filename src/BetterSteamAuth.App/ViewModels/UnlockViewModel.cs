using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Unlock page shown on launch when the data has a password.</summary>
public sealed partial class UnlockViewModel(ShellViewModel shell) : ObservableObject
{
    private static readonly CompositeFormat AccountCountFormat = CompositeFormat.Parse(Strings.AccountCountFormat);

    public string Subtitle { get; } = string.Format(
        CultureInfo.CurrentCulture, AccountCountFormat, shell.Vault?.ListAccounts().Count ?? 0);

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    partial void OnPasswordChanged(string value) => Error = null;

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
