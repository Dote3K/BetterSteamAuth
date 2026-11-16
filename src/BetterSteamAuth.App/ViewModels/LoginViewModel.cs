using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamKit2;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>
/// Re-login for one account. The password is sent to Steam through the account's own
/// connection and never stored; the resulting tokens go into the maFile.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private static readonly CompositeFormat TitleFormat = CompositeFormat.Parse(Strings.SignInTitleFormat);

    private readonly ShellViewModel _shell;
    private readonly ulong _steamId;
    private readonly Action _done;
    private TaskCompletionSource<string?>? _emailCode;

    public LoginViewModel(ShellViewModel shell, ulong steamId, string accountName, Action done)
    {
        _shell = shell;
        _done = done;
        _steamId = steamId;
        AccountName = accountName;
        Title = string.Format(CultureInfo.CurrentCulture, TitleFormat, accountName);
    }

    public string AccountName { get; }

    public string Title { get; }

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string EmailCode { get; set; } = "";

    [ObservableProperty]
    public partial bool NeedsEmailCode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(SignInLabel))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public string SignInLabel => IsBusy ? Strings.SigningIn : Strings.SignIn;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    partial void OnPasswordChanged(string value) => Error = null;

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy || string.IsNullOrEmpty(Password) || _shell.Vault is not { } vault)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var maFile = vault.Load(_steamId);
            byte[]? sharedSecret = string.IsNullOrEmpty(maFile.SharedSecret) ? null : Convert.FromBase64String(maFile.SharedSecret);
            var connection = _shell.ConnectionFor(_steamId);
            try
            {
                var tokens = await SteamLogin.LoginAsync(connection, AccountName, Password, sharedSecret, AskEmailCodeAsync, vault.GetDeviceProfile(_steamId))
                    .ConfigureAwait(true);
                Save(maFile, tokens);
            }
            finally
            {
                if (sharedSecret is not null)
                {
                    CryptographicOperations.ZeroMemory(sharedSecret);
                }
            }

            Password = "";
            _done();
        }
        catch (LoginFailedException ex)
        {
            Error = ex.Result switch
            {
                EResult.InvalidPassword => Strings.WrongPassword,
                EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle => Strings.LoginRateLimited,
                EResult.TwoFactorCodeMismatch or EResult.InvalidLoginAuthCode => Strings.LoginCodeRejected,
                _ => ex.Message,
            };
        }
        catch (HttpRequestException)
        {
            Error = Strings.LoginNetwork;
        }
        catch (OperationCanceledException)
        {
            // The email code prompt was cancelled.
        }
        finally
        {
            NeedsEmailCode = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SubmitEmailCode()
    {
        if (!string.IsNullOrWhiteSpace(EmailCode))
        {
            _emailCode?.TrySetResult(EmailCode);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _emailCode?.TrySetResult(null);
        _done();
    }

    private Task<string?> AskEmailCodeAsync(GuardChallenge challenge)
    {
        _emailCode = new TaskCompletionSource<string?>();
        EmailCode = "";
        NeedsEmailCode = true;
        IsBusy = false;
        return _emailCode.Task.ContinueWith(
            t =>
            {
                NeedsEmailCode = false;
                IsBusy = true;
                return t.Result;
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Save(MaFile maFile, LoginTokens tokens)
    {
        maFile.Session ??= new MaFileSession();
        maFile.Session.SteamId = tokens.SteamId;
        maFile.Session.AccessToken = tokens.AccessToken;
        maFile.Session.RefreshToken = tokens.RefreshToken;
        maFile.Session.SessionId ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        _shell.Vault!.Save(maFile);
    }
}
