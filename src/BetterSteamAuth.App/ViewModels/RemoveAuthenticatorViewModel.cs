using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Steam;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>
/// Turns off the mobile authenticator on the Steam account with the revocation code, then takes the
/// account out of the app.
/// </summary>
public sealed partial class RemoveAuthenticatorViewModel : ObservableObject
{
    private static readonly CompositeFormat WrongCodeFormat = CompositeFormat.Parse(Strings.RemoveAuthWrongCodeFormat);
    private static readonly CompositeFormat RemovedFormat = CompositeFormat.Parse(Strings.GuardRemovedFormat);
    private static readonly Uri SupportUri = new("https://help.steampowered.com");

    private readonly ShellViewModel _shell;
    private readonly Vault _vault;
    private readonly ulong _steamId;
    private readonly string? _storedCode;

    /// <param name="after">The choice to start with (kept when coming back from sign-in).</param>
    /// <param name="typedCode">A code the user already typed, which also means the code field is shown.</param>
    public RemoveAuthenticatorViewModel(ShellViewModel shell, ulong steamId, GuardAfterRemoval after = GuardAfterRemoval.Email, string? typedCode = null)
    {
        _shell = shell;
        _vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        _steamId = steamId;
        var maFile = _vault.Load(steamId);
        Name = maFile.AccountName ?? steamId.ToString(CultureInfo.InvariantCulture);
        _storedCode = string.IsNullOrWhiteSpace(maFile.RevocationCode) ? null : maFile.RevocationCode.Trim();
        MaskedCode = _storedCode is { Length: > 0 } c ? c[0] + new string('•', c.Length - 1) : "";
        ShowCodeField = _storedCode is null || typedCode is not null;
        RevocationCode = typedCode ?? "";
        TurnOffCompletely = after == GuardAfterRemoval.None;
        NeedsPassword = _vault.IsEncrypted;
    }

    public string Name { get; }

    public string MaskedCode { get; }

    public bool NeedsPassword { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepEmailCodes))]
    public partial bool TurnOffCompletely { get; set; }

    public bool KeepEmailCodes
    {
        get => !TurnOffCompletely;
        set => TurnOffCompletely = !value;
    }

    /// <summary>The code is typed by the user: the maFile has none, or Steam rejected the stored one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStoredCode))]
    public partial bool ShowCodeField { get; private set; }

    public bool ShowStoredCode => !ShowCodeField;

    [ObservableProperty] public partial string RevocationCode { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial bool Understood { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>The session can't be renewed and there's no saved password: the user signs in first.</summary>
    [ObservableProperty] public partial bool NeedsSignIn { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool NoAttemptsLeft { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanRemove), nameof(RemoveLabel))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public bool CanRemove => !IsBusy && !NoAttemptsLeft;

    public string RemoveLabel => IsBusy ? Strings.RemovingAuth : Strings.RemoveAuthButton;

    private GuardAfterRemoval After => TurnOffCompletely ? GuardAfterRemoval.None : GuardAfterRemoval.Email;

    partial void OnRevocationCodeChanged(string value) => ClearError();

    partial void OnPasswordChanged(string value) => ClearError();

    partial void OnUnderstoodChanged(bool value) => ClearError();

    [RelayCommand]
    private void Cancel() => _shell.ShowAccountDetails(_steamId);

    [RelayCommand]
    private void SignIn()
        => _shell.ShowLogin(_steamId, Name, () => _shell.ShowRemoveAuthenticator(_steamId, After, ShowCodeField ? RevocationCode : null));

    [RelayCommand]
    private Task OpenSupportAsync() => _shell.Platform.OpenUriAsync(SupportUri);

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (!CanRemove)
        {
            return;
        }

        string code = (ShowCodeField ? RevocationCode : _storedCode ?? "").Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(code))
        {
            Error = Strings.RevocationCodeInvalid;
            return;
        }

        if (NeedsPassword && string.IsNullOrEmpty(Password))
        {
            Error = Strings.WrongPassword;
            return;
        }

        if (!Understood)
        {
            Error = Strings.RemoveNeedsCheck;
            return;
        }

        IsBusy = true;
        Error = null;
        NeedsSignIn = false;
        try
        {
            string password = Password;
            if (NeedsPassword && !await Task.Run(() => _vault.VerifyPassword(password)).ConfigureAwait(true))
            {
                Error = Strings.WrongPassword;
                return;
            }

            if (SampleMode.IsOn || await EnsureSessionAsync().ConfigureAwait(true))
            {
                await SendAsync(code).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Error = Strings.RemoveAuthNetwork;
        }
        catch (LinkFailedException)
        {
            Error = Strings.RemoveAuthFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Renews the access token, signing in with the saved password if needed. False: the user must sign in.</summary>
    private async Task<bool> EnsureSessionAsync()
    {
        var maFile = _vault.Load(_steamId);
        try
        {
            if (await WebSession.EnsureAsync(_shell.ConnectionFor(_steamId), maFile.Session).ConfigureAwait(true))
            {
                _vault.Save(maFile);
            }

            return true;
        }
        catch (SessionExpiredException)
        {
            if (await _shell.TryAutoSignInAsync(_steamId, CancellationToken.None).ConfigureAwait(true))
            {
                return true;
            }

            NeedsSignIn = true;
            return false;
        }
    }

    private async Task SendAsync(string code)
    {
        // Never retried: every wrong code uses up one of the account's attempts.
        var maFile = _vault.Load(_steamId);
        var result = await SampleMode.Or(
            () => AuthenticatorLinker.RemoveAsync(_shell.ConnectionFor(_steamId), maFile.Session!.AccessToken!, _steamId, code, After),
            () => string.Equals(code, _storedCode, StringComparison.OrdinalIgnoreCase) ? new RemoveResult(true, null) : new RemoveResult(false, 4))
            .ConfigureAwait(true);

        if (result.Removed)
        {
            _vault.Remove(_steamId);
            _shell.ResetConnection(_steamId);
            _shell.ShowMainWithNotice(string.Format(CultureInfo.CurrentCulture, RemovedFormat, Name));
            return;
        }

        switch (result.AttemptsRemaining)
        {
            case 0:
                NoAttemptsLeft = true;
                Error = Strings.RemoveAuthNoAttempts;
                break;
            case { } left:
                ShowCodeField = true;
                Error = string.Format(CultureInfo.CurrentCulture, WrongCodeFormat, left);
                break;
            default:
                Error = Strings.RemoveAuthFailed;
                break;
        }
    }

    private void ClearError()
    {
        if (!NoAttemptsLeft)
        {
            Error = null;
        }
    }

    [GeneratedRegex(@"^R\d{5}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
