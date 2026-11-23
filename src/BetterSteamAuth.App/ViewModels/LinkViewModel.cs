using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Devices;
using BetterSteamAuth.Core.Guard;
using BetterSteamAuth.Steam;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamKit2;

namespace BetterSteamAuth.App.ViewModels;

public enum LinkStep
{
    SignIn,
    EmailCode,
    Checking,
    MoveSend,
    MoveCode,
    Revocation,
    Activate,
    Done,
}

/// <summary>A proxy the new account can use: one from the pool, or a new pasted one (Id = null).</summary>
public sealed record ProxyChoice(long? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Links a new authenticator: sign in → save the revocation code → activate → done. The maFile is
/// saved before the revocation code is shown and is never deleted automatically; an unfinished link
/// can be resumed.
/// In move mode it moves an existing authenticator here instead: sign in → send a text → enter the code
/// → save the revocation code → done.
/// </summary>
public sealed partial class LinkViewModel : ObservableObject, IDisposable
{
    private static readonly CompositeFormat ProtectedFormat = CompositeFormat.Parse(Strings.LinkProtectedFormat);
    private static readonly CompositeFormat SentToPhoneFormat = CompositeFormat.Parse(Strings.LinkSentToPhoneFormat);
    private static readonly CompositeFormat MovedFormat = CompositeFormat.Parse(Strings.MovedFormat);
    private static readonly CompositeFormat MoveSendBodyFormat = CompositeFormat.Parse(Strings.MoveSendBodyFormat);
    private static readonly CompositeFormat MoveCodeSubtitleFormat = CompositeFormat.Parse(Strings.MoveCodeSubtitleFormat);
    private static readonly CompositeFormat SendAgainInFormat = CompositeFormat.Parse(Strings.SendAgainInFormat);
    private static readonly Uri TradeHoldsUri = new("https://help.steampowered.com/en/faqs/view/34A1-EA3F-83ED-54AB");
    private const int ResendSeconds = 30;

    private readonly ShellViewModel _shell;
    private readonly Vault _vault;
    private readonly DispatcherTimer _codeTimer;
    private readonly bool _resuming;
    private DeviceProfile _profile;
    private string _deviceId;
    private AccountConnection? _ownConnection;
    private AccountConnection? _connection;
    private TaskCompletionSource<string?>? _emailCode;
    private MaFile? _maFile;
    private ulong _steamId;
    private LoginTokens? _tokens;
    private ProxySettings? _proxySettings;
    private long? _proxyId;
    private int _resendLeft;

    /// <summary>Starts a new link.</summary>
    public LinkViewModel(ShellViewModel shell)
        : this(shell, resumeSteamId: null)
    {
    }

    /// <param name="resumeSteamId">An account whose link isn't finished; null for a new link.</param>
    /// <param name="move">Move an existing authenticator here instead of linking a new one.</param>
    public LinkViewModel(ShellViewModel shell, ulong? resumeSteamId, bool move = false)
    {
        _shell = shell;
        IsMove = move;
        _vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        _codeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick());

        // The device identity exists before anything is sent, so sign-in and linking share it.
        _profile = DeviceCatalog.Load(new ShippedDeviceCatalogSource()).Pick();
        _deviceId = DeviceProfile.NewDeviceId();

        foreach (var proxy in _vault.ListProxies().Where(p => p.AccountIds.Count == 0 && p.Status != ProxyStatus.Dead))
        {
            ProxyChoices.Add(new ProxyChoice(proxy.Id, proxy.Address));
        }

        ProxyChoices.Add(new ProxyChoice(null, Strings.PasteNewProxy));
        SelectedProxy = ProxyChoices[0];

        if (resumeSteamId is { } steamId)
        {
            _resuming = true;
            _ = ResumeAsync(steamId);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignIn), nameof(IsEmailCode), nameof(IsChecking), nameof(IsMoveSend), nameof(IsMoveCode),
                              nameof(IsRevocation), nameof(IsActivate), nameof(IsDone), nameof(IsForm))]
    public partial LinkStep Step { get; private set; } = LinkStep.SignIn;

    public bool IsSignIn => Step == LinkStep.SignIn;

    public bool IsEmailCode => Step == LinkStep.EmailCode;

    public bool IsChecking => Step == LinkStep.Checking;

    public bool IsMoveSend => Step == LinkStep.MoveSend;

    public bool IsMoveCode => Step == LinkStep.MoveCode;

    public bool IsRevocation => Step == LinkStep.Revocation;

    public bool IsActivate => Step == LinkStep.Activate;

    public bool IsDone => Step == LinkStep.Done;

    public bool IsForm => Step is not LinkStep.Done and not LinkStep.Checking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ContinueLabel), nameof(ActivateLabel), nameof(SendTextLabel), nameof(MoveLabel), nameof(CanSendAgain))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public string ContinueLabel => IsBusy ? Strings.SigningIn : Strings.Continue;

    public string ActivateLabel => IsBusy ? Strings.Activating : Strings.Activate;

    // Move from your phone
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(SignInSubtitle), nameof(RevocationSubtitle))]
    public partial bool IsMove { get; private set; }

    public string Title => IsMove ? Strings.MoveFromPhone : Strings.LinkNewAuthenticator;

    public string SignInSubtitle => IsMove ? Strings.MoveSignInSubtitle : Strings.LinkSignInSubtitle;

    public string RevocationSubtitle => IsMove ? Strings.MoveRevocationBody : Strings.RevocationBody;

    /// <summary>Steam asks for the phone's code (no secret here yet), not an emailed one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuardTitle), nameof(GuardPrompt), nameof(GuardPlaceholder))]
    public partial bool GuardIsPhone { get; private set; }

    public string GuardTitle => GuardIsPhone ? Strings.PhoneCodeTitle : Title;

    public string GuardPrompt => GuardIsPhone ? Strings.PhoneCodePrompt : Strings.EmailCodePrompt;

    public string GuardPlaceholder => GuardIsPhone ? Strings.SteamGuardCode : Strings.EmailCode;

    /// <summary>Link found an authenticator elsewhere: offer to move it here.</summary>
    [ObservableProperty] public partial bool CanMoveHere { get; private set; }

    /// <summary>Move found no authenticator: offer to link a new one.</summary>
    [ObservableProperty] public partial bool CanLinkInstead { get; private set; }

    [ObservableProperty] public partial string MoveSendBody { get; private set; } = "";
    [ObservableProperty] public partial string MoveCodeSubtitle { get; private set; } = "";
    [ObservableProperty] public partial string SmsCode { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendAgain))]
    public partial string SendAgainLabel { get; private set; } = Strings.SendAgain;

    public bool CanSendAgain => !IsBusy && _resendLeft <= 0;

    public string SendTextLabel => IsBusy ? Strings.SendingText : Strings.SendText;

    public string MoveLabel => IsBusy ? Strings.Moving : Strings.MoveButton;

    /// <summary>The move went through but the maFile couldn't be written: the secrets exist only in memory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    public partial bool SaveFailed { get; private set; }

    public bool CanCancel => !SaveFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    // Sign in
    [ObservableProperty] public partial string AccountName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial bool UseProxy { get; set; }

    public ObservableCollection<ProxyChoice> ProxyChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewProxy))]
    public partial ProxyChoice? SelectedProxy { get; set; }

    public bool IsNewProxy => SelectedProxy?.Id is null;

    [ObservableProperty] public partial string NewProxy { get; set; } = "";
    [ObservableProperty] public partial string? NewProxyPreview { get; private set; }
    [ObservableProperty] public partial bool NewProxyIsError { get; private set; }

    // Email code (sign-in)
    [ObservableProperty] public partial string EmailCode { get; set; } = "";

    // Revocation
    [ObservableProperty] public partial string RevocationCode { get; private set; } = "";
    [ObservableProperty] public partial bool SavedRevocationCode { get; set; }

    // Activate
    [ObservableProperty] public partial string ActivationHint { get; private set; } = Strings.LinkSentToEmail;
    [ObservableProperty] public partial string ActivationCode { get; set; } = "";

    /// <summary>Resuming an unfinished link: offer to start over when the emailed code expired.</summary>
    public bool CanStartOver => _resuming;

    // Done
    [ObservableProperty] public partial string DoneTitle { get; private set; } = "";
    [ObservableProperty] public partial string? DoneNote { get; private set; }
    [ObservableProperty] public partial string DoneBody { get; private set; } = Strings.LinkDoneBody;
    [ObservableProperty] public partial string CurrentCode { get; private set; } = "";

    partial void OnPasswordChanged(string value) => Error = null;

    partial void OnAccountNameChanged(string value) => Error = null;

    partial void OnActivationCodeChanged(string value) => Error = null;

    partial void OnSmsCodeChanged(string value) => Error = null;

    partial void OnSavedRevocationCodeChanged(bool value) => Error = null;

    partial void OnNewProxyChanged(string value)
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            NewProxyPreview = null;
            return;
        }

        bool ok = ProxyParser.TryParse(value, out var proxy) && proxy is not null;
        NewProxyIsError = !ok;
        NewProxyPreview = ok ? proxy + (proxy!.Username is null ? "" : " · " + Strings.WithLogin) : Strings.ProxyUnreadable;
    }

    public void Dispose()
    {
        _codeTimer.Stop();
        _emailCode?.TrySetResult(null);
        _ownConnection?.Dispose();
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(AccountName) || string.IsNullOrEmpty(Password))
        {
            Error = Strings.LinkEnterAccount;
            return;
        }

        if (!TryGetProxy(out var proxySettings, out long? proxyId))
        {
            Error = Strings.ProxyUnreadable;
            return;
        }

        IsBusy = true;
        Error = null;
        CanMoveHere = false;
        CanLinkInstead = false;
        try
        {
            // The new account's own connection: its proxy and its generated device.
            _ownConnection?.Dispose();
            _ownConnection = new AccountConnection(0, proxySettings, _shell.Network);
            _connection = _ownConnection;
            _proxySettings = proxySettings;
            _proxyId = proxyId;
            await SampleMode.Or(async () => { await TimeSync.SyncAsync(_connection).ConfigureAwait(true); return true; }, () => true).ConfigureAwait(true);

            var tokens = await SampleMode.Or(
                () => SteamLogin.LoginAsync(_connection, AccountName.Trim(), Password, null, AskEmailCodeAsync, _profile),
                async () =>
                {
                    // An account name starting with "email" asks for a code, from a background thread like Steam.
                    if (AccountName.Trim().StartsWith("email", StringComparison.OrdinalIgnoreCase)
                        && await Task.Run(() => AskEmailCodeAsync(GuardChallenge.EmailCode)).ConfigureAwait(true) is null)
                    {
                        throw new OperationCanceledException();
                    }

                    return new LoginTokens(76561198000000777, AccountName.Trim(), "sample-access", "sample-refresh");
                }).ConfigureAwait(true);
            Password = "";

            // Approving on the phone finishes sign-in without the code prompt being answered.
            _emailCode = null;
            if (Step == LinkStep.EmailCode)
            {
                Step = LinkStep.SignIn;
            }

            if (_vault.Contains(tokens.SteamId))
            {
                Error = Strings.LinkAlreadyAdded;
                return;
            }

            _tokens = tokens;
            if (IsMove)
            {
                await CheckBeforeMoveAsync().ConfigureAwait(true);
            }
            else
            {
                await AddAsync().ConfigureAwait(true);
            }
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
        catch (LinkFailedException ex)
        {
            Error = ex.Message;
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
            if (Step == LinkStep.EmailCode)
            {
                Step = LinkStep.SignIn;
            }

            IsBusy = false;
        }
    }

    /// <summary>Asks Steam for new secrets and saves them before the revocation code is shown.</summary>
    private async Task AddAsync()
    {
        var tokens = _tokens!;
        var added = await SampleMode.Or(
            () => AuthenticatorLinker.AddAsync(_connection!, tokens.AccessToken, tokens.SteamId, _deviceId),
            () => SampleAdd(_deviceId, tokens.AccountName)).ConfigureAwait(true);
        switch (added.Status)
        {
            case AddStatus.AlreadyLinked:
                Error = Strings.LinkAlreadyLinked;
                CanMoveHere = true;
                return;
            case AddStatus.PhoneRequired:
                Error = Strings.LinkPhoneRequired;
                return;
        }

        if (!SaveNew(added.MaFile!))
        {
            Error = Strings.LinkSaveFailed;
            return;
        }

        ActivationHint = added.SentToEmail || added.PhoneHint is null
            ? Strings.LinkSentToEmail
            : string.Format(CultureInfo.CurrentCulture, SentToPhoneFormat, added.PhoneHint);
        Step = LinkStep.Revocation;
    }

    /// <summary>Only an account with a mobile authenticator can move it here.</summary>
    private async Task CheckBeforeMoveAsync()
    {
        var tokens = _tokens!;
        var status = await SampleMode.Or(
            () => AuthenticatorLinker.QueryStatusAsync(_connection!, tokens.AccessToken, tokens.SteamId),
            () => new AuthenticatorStatus(true, null)).ConfigureAwait(true);
        if (!status.Active)
        {
            Error = Strings.MoveNoAuthenticator;
            CanLinkInstead = true;
            return;
        }

        ShowMoveSend();
    }

    private void ShowMoveSend()
    {
        string name = _tokens?.AccountName is { Length: > 0 } n ? n : AccountName.Trim();
        MoveSendBody = string.Format(CultureInfo.CurrentCulture, MoveSendBodyFormat, name);
        MoveCodeSubtitle = string.Format(CultureInfo.CurrentCulture, MoveCodeSubtitleFormat, name);
        Error = null;
        Step = LinkStep.MoveSend;
    }

    /// <summary>Link found an authenticator on another device: continue as a move, keeping the sign-in.</summary>
    [RelayCommand]
    private void MoveHere()
    {
        if (_tokens is null)
        {
            return;
        }

        IsMove = true;
        CanMoveHere = false;
        ShowMoveSend();
    }

    /// <summary>Move found no authenticator: link a new one instead, keeping the sign-in.</summary>
    [RelayCommand]
    private async Task LinkInsteadAsync()
    {
        if (_tokens is null || IsBusy)
        {
            return;
        }

        IsMove = false;
        CanLinkInstead = false;
        IsBusy = true;
        Error = null;
        try
        {
            await AddAsync().ConfigureAwait(true);
        }
        catch (LinkFailedException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Error = Strings.LoginNetwork;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenTradeHoldsAsync() => _shell.Platform.OpenUriAsync(TradeHoldsUri);

    [RelayCommand]
    private async Task SendTextAsync()
    {
        if (IsBusy || _tokens is null || _connection is null)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var tokens = _tokens;
            var connection = _connection;
            bool sent = await SampleMode.Or(
                () => AuthenticatorLinker.MoveStartAsync(connection, tokens.AccessToken),
                () => true).ConfigureAwait(true);
            if (!sent)
            {
                Error = Strings.MoveNoPhone;
                return;
            }

            SmsCode = "";
            _resendLeft = ResendSeconds;
            UpdateResend();
            _codeTimer.Start();
            Step = LinkStep.MoveCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LinkFailedException)
        {
            Error = Strings.RemoveAuthNetwork;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendAgainAsync()
    {
        if (CanSendAgain)
        {
            await SendTextAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task MoveAsync()
    {
        if (IsBusy || _tokens is null || _connection is null || SaveFailed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SmsCode))
        {
            Error = Strings.LinkEnterActivationCode;
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var tokens = _tokens;
            var connection = _connection;
            string code = SmsCode;
            var maFile = await SampleMode.Or(
                () => AuthenticatorLinker.MoveFinishAsync(connection, tokens.AccessToken, code, _deviceId),
                () => SampleMove(_deviceId, tokens.AccountName)).ConfigureAwait(true);
            if (maFile is null)
            {
                Error = Strings.LinkBadActivationCode;
                return;
            }

            // The old authenticator is dead from here on: the secrets exist only in memory until saved.
            _maFile = maFile;
            if (!SaveNew(maFile))
            {
                SaveFailed = true;
                Error = Strings.MoveSaveFailed;
                return;
            }

            Step = LinkStep.Revocation;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LinkFailedException)
        {
            Error = Strings.MoveNetwork;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RetrySave()
    {
        if (_maFile is null || !SaveFailed)
        {
            return;
        }

        if (SaveNew(_maFile))
        {
            SaveFailed = false;
            Error = null;
            Step = LinkStep.Revocation;
        }
    }

    /// <summary>Writes the moved maFile anywhere the user picks, as a last resort when the app folder can't be written.</summary>
    [RelayCommand]
    private async Task SaveAsFileAsync()
    {
        if (_maFile is null)
        {
            return;
        }

        string name = (_tokens?.SteamId ?? 0).ToString(CultureInfo.InvariantCulture) + ".maFile";
        string? path = await _shell.Platform.SaveFileAsync(Strings.SaveAsFile, name).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, _maFile.ToJson()).ConfigureAwait(true);
            SaveFailed = false;
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Strings.MoveSaveFailed;
        }
    }

    /// <summary>Saves a new account (maFile, proxy, device profile) in one go. False if the write failed.</summary>
    private bool SaveNew(MaFile maFile)
    {
        var tokens = _tokens!;
        maFile.AccountName ??= tokens.AccountName;
        maFile.Session = new MaFileSession
        {
            SteamId = tokens.SteamId,
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            SessionId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)),
        };

        try
        {
            if (_proxySettings is { } proxy && _proxyId is null)
            {
                _vault.AddProxies([proxy]);
                _proxyId = _vault.ListProxies().First(p => p.Scheme == proxy.Scheme && p.Host == proxy.Host
                                                          && p.Port == proxy.Port && p.Username == proxy.Username).Id;
            }

            _vault.Add(maFile, _proxyId, _profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }

        _maFile = maFile;
        _steamId = tokens.SteamId;
        RevocationCode = maFile.RevocationCode ?? "";
        return true;
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
    private Task CopyRevocationCodeAsync() => _shell.Platform.CopyTextAsync(RevocationCode);

    [RelayCommand]
    private void ContinueToActivate()
    {
        if (!SavedRevocationCode)
        {
            Error = Strings.LinkSaveCodeFirst;
            return;
        }

        Error = null;
        if (IsMove)
        {
            Finish(note: null);
            return;
        }

        Step = LinkStep.Activate;
    }

    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (IsBusy || _maFile is null || _connection is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ActivationCode))
        {
            Error = Strings.LinkEnterActivationCode;
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            byte[] secret = Convert.FromBase64String(_maFile.SharedSecret!);
            var result = await SampleMode.Or(
                () => AuthenticatorLinker.FinalizeAsync(_connection, _maFile.Session!.AccessToken!, _steamId, secret, ActivationCode),
                () => FinalizeStatus.Done).ConfigureAwait(true);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);

            switch (result)
            {
                case FinalizeStatus.Done:
                    Finish(note: null);
                    break;
                case FinalizeStatus.BadActivationCode:
                    Error = Strings.LinkBadActivationCode;
                    break;
                default:
                    Error = Strings.LinkCodesRejected;
                    break;
            }
        }
        catch (LinkFailedException ex)
        {
            Error = ex.Message + " " + Strings.LinkProgressSaved;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Error = Strings.LinkNetwork;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The emailed code expired: keep the old maFile in .trash and link again from sign-in.</summary>
    [RelayCommand]
    private void StartOver()
    {
        string name = _maFile?.AccountName ?? "";
        _vault.Remove(_steamId, RemovalReason.LinkCancelled);
        _shell.ResetConnection(_steamId);
        _shell.ShowLink(accountName: name);
    }

    [RelayCommand]
    private void Open()
    {
        if (_steamId != 0)
        {
            _shell.ShowMain(_steamId);
        }
        else
        {
            _shell.ShowMain();
        }
    }

    /// <summary>Before the account is saved nothing is kept; after, the maFile moves to .trash (recoverable).</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (SaveFailed)
        {
            // The moved secrets aren't saved anywhere yet.
            return;
        }

        _emailCode?.TrySetResult(null);
        if (_maFile is not null && !_maFile.FullyEnrolled && !_resuming)
        {
            _vault.Remove(_steamId, RemovalReason.LinkCancelled);
        }

        if (_maFile is { FullyEnrolled: true } && _steamId != 0)
        {
            _shell.ShowMain(_steamId);
            return;
        }

        _shell.ShowMain();
    }

    private async Task ResumeAsync(ulong steamId)
    {
        Step = LinkStep.Checking;
        _steamId = steamId;
        _maFile = _vault.Load(steamId);
        _profile = _vault.GetDeviceProfile(steamId) ?? _profile;
        _deviceId = _maFile.DeviceId ?? _deviceId;
        AccountName = _maFile.AccountName ?? "";
        RevocationCode = _maFile.RevocationCode ?? "";
        _connection = _shell.ConnectionFor(steamId);
        try
        {
            bool refreshed = await SampleMode.Or(
                async () =>
                {
                    await TimeSync.SyncAsync(_connection).ConfigureAwait(true);
                    return await WebSession.EnsureAsync(_connection, _maFile.Session).ConfigureAwait(true);
                },
                () => false).ConfigureAwait(true);
            if (refreshed)
            {
                _vault.Save(_maFile);
            }

            // Activation may have gone through with the answer lost.
            var status = await SampleMode.Or(
                () => AuthenticatorLinker.QueryStatusAsync(_connection, _maFile.Session!.AccessToken!, steamId),
                () => new AuthenticatorStatus(false, null)).ConfigureAwait(true);
            if (status.Active && string.Equals(status.DeviceId, _maFile.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                Finish(Strings.LinkAlreadyActive);
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SessionExpiredException or LinkFailedException)
        {
            Error = Strings.LinkStatusUnknown;
        }

        Step = LinkStep.Activate;
    }

    private void Finish(string? note)
    {
        _maFile!.FullyEnrolled = true;
        _vault.Save(_maFile);
        DoneTitle = string.Format(CultureInfo.CurrentCulture, IsMove ? MovedFormat : ProtectedFormat, _maFile.AccountName ?? AccountName);
        DoneBody = IsMove ? Strings.MoveDoneBody : Strings.LinkDoneBody;
        DoneNote = note;
        UpdateCode();
        _codeTimer.Start();
        Step = LinkStep.Done;
    }

    private void Tick()
    {
        UpdateCode();
        if (_resendLeft > 0)
        {
            _resendLeft--;
            UpdateResend();
        }
    }

    private void UpdateResend()
    {
        SendAgainLabel = _resendLeft > 0
            ? string.Format(CultureInfo.CurrentCulture, SendAgainInFormat, _resendLeft)
            : Strings.SendAgain;
    }

    private void UpdateCode()
    {
        if (_maFile?.SharedSecret is { } secret && _connection is not null)
        {
            CurrentCode = SteamGuardCode.Generate(secret, _connection.Clock.Now);
        }
    }

    private bool TryGetProxy(out ProxySettings? settings, out long? proxyId)
    {
        settings = null;
        proxyId = null;
        if (!UseProxy || SelectedProxy is null)
        {
            return true;
        }

        if (SelectedProxy.Id is { } id)
        {
            proxyId = id;
            settings = _vault.GetProxySettings(id);
            return settings is not null;
        }

        return ProxyParser.TryParse(NewProxy, out settings) && settings is not null;
    }

    // Steam calls this from a background thread, so the prompt runs on the UI thread.
    private Task<string?> AskEmailCodeAsync(GuardChallenge challenge) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            GuardIsPhone = challenge == GuardChallenge.PhoneCode;
            _emailCode = new TaskCompletionSource<string?>();
            EmailCode = "";
            Step = LinkStep.EmailCode;
            IsBusy = false;
            string? code = await _emailCode.Task.ConfigureAwait(true);
            Step = LinkStep.SignIn;
            IsBusy = true;
            return code;
        });

    // Sample mode: an account name starting with "phone" already has an authenticator elsewhere.
    private static AddResult SampleAdd(string deviceId, string accountName) =>
#if DEBUG
        accountName.StartsWith("phone", StringComparison.OrdinalIgnoreCase)
            ? new AddResult(AddStatus.AlreadyLinked, null, false, null)
            : SampleConfirmations.LinkResult(deviceId);
#else
        throw new NotSupportedException();
#endif

    private static MaFile? SampleMove(string deviceId, string accountName)
    {
#if DEBUG
        var maFile = SampleConfirmations.LinkResult(deviceId).MaFile!;
        maFile.AccountName = accountName;
        maFile.FullyEnrolled = true;
        return maFile;
#else
        throw new NotSupportedException();
#endif
    }
}
