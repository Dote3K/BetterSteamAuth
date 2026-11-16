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
/// </summary>
public sealed partial class LinkViewModel : ObservableObject, IDisposable
{
    private static readonly CompositeFormat ProtectedFormat = CompositeFormat.Parse(Strings.LinkProtectedFormat);
    private static readonly CompositeFormat SentToPhoneFormat = CompositeFormat.Parse(Strings.LinkSentToPhoneFormat);

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

    /// <summary>Starts a new link.</summary>
    public LinkViewModel(ShellViewModel shell)
        : this(shell, resumeSteamId: null)
    {
    }

    /// <param name="resumeSteamId">An account whose link isn't finished; null for a new link.</param>
    public LinkViewModel(ShellViewModel shell, ulong? resumeSteamId)
    {
        _shell = shell;
        _vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        _codeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => UpdateCode());

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
    [NotifyPropertyChangedFor(nameof(IsSignIn), nameof(IsEmailCode), nameof(IsChecking), nameof(IsRevocation), nameof(IsActivate), nameof(IsDone), nameof(IsForm))]
    public partial LinkStep Step { get; private set; } = LinkStep.SignIn;

    public bool IsSignIn => Step == LinkStep.SignIn;

    public bool IsEmailCode => Step == LinkStep.EmailCode;

    public bool IsChecking => Step == LinkStep.Checking;

    public bool IsRevocation => Step == LinkStep.Revocation;

    public bool IsActivate => Step == LinkStep.Activate;

    public bool IsDone => Step == LinkStep.Done;

    public bool IsForm => Step is not LinkStep.Done and not LinkStep.Checking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ContinueLabel), nameof(ActivateLabel))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public string ContinueLabel => IsBusy ? Strings.SigningIn : Strings.Continue;

    public string ActivateLabel => IsBusy ? Strings.Activating : Strings.Activate;

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
    [ObservableProperty] public partial string CurrentCode { get; private set; } = "";

    partial void OnPasswordChanged(string value) => Error = null;

    partial void OnAccountNameChanged(string value) => Error = null;

    partial void OnActivationCodeChanged(string value) => Error = null;

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
        try
        {
            // The new account's own connection: its proxy and its generated device.
            _ownConnection?.Dispose();
            _ownConnection = new AccountConnection(0, proxySettings, _shell.Network);
            _connection = _ownConnection;
            await SampleMode.Or(async () => { await TimeSync.SyncAsync(_connection).ConfigureAwait(true); return true; }, () => true).ConfigureAwait(true);

            var tokens = await SampleMode.Or(
                () => SteamLogin.LoginAsync(_connection, AccountName.Trim(), Password, null, AskEmailCodeAsync, _profile),
                () => new LoginTokens(76561198000000777, AccountName.Trim(), "sample-access", "sample-refresh")).ConfigureAwait(true);
            Password = "";

            if (_vault.Contains(tokens.SteamId))
            {
                Error = Strings.LinkAlreadyAdded;
                return;
            }

            var added = await SampleMode.Or(
                () => AuthenticatorLinker.AddAsync(_connection, tokens.AccessToken, tokens.SteamId, _deviceId),
                () => SampleAdd(_deviceId)).ConfigureAwait(true);
            switch (added.Status)
            {
                case AddStatus.AlreadyLinked:
                    Error = Strings.LinkAlreadyLinked;
                    return;
                case AddStatus.PhoneRequired:
                    Error = Strings.LinkPhoneRequired;
                    return;
            }

            var maFile = added.MaFile!;
            maFile.AccountName ??= tokens.AccountName;
            maFile.Session = new MaFileSession
            {
                SteamId = tokens.SteamId,
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                SessionId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)),
            };

            // The only copy of the secrets: save before showing anything else.
            try
            {
                if (proxySettings is not null && proxyId is null)
                {
                    _vault.AddProxies([proxySettings]);
                    proxyId = _vault.ListProxies().First(p => p.Scheme == proxySettings.Scheme && p.Host == proxySettings.Host
                                                             && p.Port == proxySettings.Port && p.Username == proxySettings.Username).Id;
                }

                _vault.Add(maFile, proxyId, _profile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                Error = Strings.LinkSaveFailed;
                return;
            }

            _maFile = maFile;
            _steamId = tokens.SteamId;
            RevocationCode = maFile.RevocationCode ?? "";
            ActivationHint = added.SentToEmail || added.PhoneHint is null
                ? Strings.LinkSentToEmail
                : string.Format(CultureInfo.CurrentCulture, SentToPhoneFormat, added.PhoneHint);
            Step = LinkStep.Revocation;
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
        _vault.Remove(_steamId);
        _shell.ResetConnection(_steamId);
        _shell.ShowLink(accountName: name);
    }

    [RelayCommand]
    private void Open() => _shell.ShowMain();

    /// <summary>Before the account is saved nothing is kept; after, the maFile moves to .trash (recoverable).</summary>
    [RelayCommand]
    private void Cancel()
    {
        _emailCode?.TrySetResult(null);
        if (_maFile is not null && !_maFile.FullyEnrolled && !_resuming)
        {
            _vault.Remove(_steamId);
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
        DoneTitle = string.Format(CultureInfo.CurrentCulture, ProtectedFormat, _maFile.AccountName ?? AccountName);
        DoneNote = note;
        UpdateCode();
        _codeTimer.Start();
        Step = LinkStep.Done;
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

    private Task<string?> AskEmailCodeAsync(GuardChallenge challenge)
    {
        _emailCode = new TaskCompletionSource<string?>();
        EmailCode = "";
        Step = LinkStep.EmailCode;
        IsBusy = false;
        return _emailCode.Task.ContinueWith(
            t =>
            {
                Step = LinkStep.SignIn;
                IsBusy = true;
                return t.Result;
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static AddResult SampleAdd(string deviceId) =>
#if DEBUG
        SampleConfirmations.LinkResult(deviceId);
#else
        throw new NotSupportedException();
#endif
}
