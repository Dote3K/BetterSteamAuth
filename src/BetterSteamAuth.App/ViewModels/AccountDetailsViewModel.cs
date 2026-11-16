using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Guard;
using BetterSteamAuth.Steam;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Everything about one account that the main window leaves out.</summary>
public sealed partial class AccountDetailsViewModel : ObservableObject, IDisposable
{
    private const int RevealSeconds = 30;
    private const int CopyNextThreshold = 5;

    private static readonly CompositeFormat SecondsLeftFormat = CompositeFormat.Parse(Strings.SecondsLeftFormat);
    private static readonly CompositeFormat ValidUntilFormat = CompositeFormat.Parse(Strings.SessionValidUntilFormat);
    private static readonly CompositeFormat LatencyFormat = CompositeFormat.Parse(Strings.LatencyFormat);
    private static readonly CompositeFormat HidesInFormat = CompositeFormat.Parse(Strings.HidesInFormat);
    private static readonly CompositeFormat RemoveTitleFormat = CompositeFormat.Parse(Strings.RemoveAccountTitleFormat);
    private static readonly CompositeFormat DeviceFormat = CompositeFormat.Parse(Strings.DeviceFormat);

    private readonly ShellViewModel _shell;
    private readonly Vault _vault;
    private readonly ulong _steamId;
    private readonly MaFile _maFile;
    private readonly AccountConnection _connection;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private long? _proxyId;
    private int _revealLeft;
    private int _saveCountdown = -1;
    private bool _loading = true;

    public AccountDetailsViewModel(ShellViewModel shell, ulong steamId)
    {
        _shell = shell;
        _vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        _steamId = steamId;
        _maFile = _vault.Load(steamId);
        _connection = shell.ConnectionFor(steamId);
        var entry = _vault.ListAccounts().Single(a => a.SteamId == steamId);

        Name = _maFile.AccountName ?? steamId.ToString(CultureInfo.InvariantCulture);
        SteamIdText = steamId.ToString(CultureInfo.InvariantCulture);
        DeviceId = _maFile.DeviceId ?? "";
        FileName = entry.FileName;
        HasRevocationCode = !string.IsNullOrEmpty(_maFile.RevocationCode);
        Groups = _vault.ListGroups();
        GroupName = _vault.GetGroupName(entry.GroupId) ?? "";
        Note = entry.Note ?? "";
        DeviceText = _vault.GetDeviceProfile(steamId) is { } profile
            ? string.Format(CultureInfo.CurrentCulture, DeviceFormat, profile.FriendlyName, profile.AndroidVersion)
            : Strings.FromImportedMaFile;

        UpdateSession();
        UpdateConnection();
        _loading = false;

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick());
        Tick();
        _timer.Start();
        shell.ProxyHealthChanged += OnProxyHealthChanged;
        _ = LoadAvatarAsync();
    }

    // Header
    public string Name { get; }

    [ObservableProperty] public partial Bitmap? Avatar { get; private set; }
    [ObservableProperty] public partial string Code { get; private set; } = "";
    [ObservableProperty] public partial string SecondsLeftText { get; private set; } = "";
    [ObservableProperty] public partial string CopyLabel { get; private set; } = Strings.Copy;

    public bool HasGroupLine => !string.IsNullOrWhiteSpace(GroupName);

    // Sign-in
    [ObservableProperty] public partial string SessionStatus { get; private set; } = "";
    [ObservableProperty] public partial bool SessionProblem { get; private set; }
    [ObservableProperty] public partial string? SessionValidUntil { get; private set; }

    // Connection
    [ObservableProperty] public partial string ProxyText { get; private set; } = "";
    [ObservableProperty] public partial bool HasProxy { get; private set; }
    [ObservableProperty] public partial string? ProxyHealth { get; private set; }
    [ObservableProperty] public partial bool ProxyDead { get; private set; }
    [ObservableProperty] public partial bool IsTesting { get; private set; }

    // Steam Guard
    public bool HasRevocationCode { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRevocationHidden))]
    public partial string? RevealedCode { get; private set; }

    public bool IsRevocationHidden => RevealedCode is null && !IsAskingPassword;

    public string MaskedRevocationCode => HasRevocationCode ? "R•••••" : Strings.NotInMaFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRevocationHidden))]
    public partial bool IsAskingPassword { get; private set; }

    [ObservableProperty] public partial string RevealPassword { get; set; } = "";
    [ObservableProperty] public partial string? RevealError { get; private set; }
    [ObservableProperty] public partial bool IsVerifying { get; private set; }
    [ObservableProperty] public partial string HidesInText { get; private set; } = "";

    // Organize
    public IReadOnlyList<string> Groups { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupLine))]
    public partial string GroupName { get; set; }

    [ObservableProperty] public partial string Note { get; set; }

    // Advanced
    public string SteamIdText { get; }

    public string DeviceText { get; }

    public string DeviceId { get; }

    public string DeviceIdShort => DeviceId.Length > 20 ? DeviceId[..20] + "…" : DeviceId;

    public string FileName { get; }

    // Remove
    [ObservableProperty] public partial bool IsConfirmingRemove { get; private set; }
    [ObservableProperty] public partial bool SavedForRemove { get; set; }
    [ObservableProperty] public partial string? RemoveError { get; private set; }

    public string RemoveTitle => string.Format(CultureInfo.CurrentCulture, RemoveTitleFormat, Name);

    [ObservableProperty] public partial string? ExportMessage { get; private set; }

    partial void OnRevealPasswordChanged(string value) => RevealError = null;

    partial void OnSavedForRemoveChanged(bool value) => RemoveError = null;

    // Group and note save shortly after typing stops.
    partial void OnGroupNameChanged(string value) => ScheduleSave();

    partial void OnNoteChanged(string value) => ScheduleSave();

    public void Dispose()
    {
        SaveOrganize();
        _timer.Stop();
        _shell.ProxyHealthChanged -= OnProxyHealthChanged;
        _cts.Cancel();
        _cts.Dispose();
    }

    [RelayCommand]
    private void Back() => _shell.ShowMain(_steamId);

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        string code = SteamGuardCode.SecondsRemaining(_connection.Clock.Now) <= CopyNextThreshold
            ? SteamGuardCode.Generate(_maFile.SharedSecret!, _connection.Clock.Now + SteamGuardCode.PeriodSeconds)
            : Code;
        await _shell.Platform.CopyTextAsync(code).ConfigureAwait(true);
        CopyLabel = Strings.Copied;
        await Task.Delay(1200).ConfigureAwait(true);
        CopyLabel = Strings.Copy;
    }

    [RelayCommand]
    private void Relogin() => _shell.ShowLogin(_steamId, Name, () => _shell.ShowAccountDetails(_steamId));

    [RelayCommand]
    private void ChangeProxy() => _shell.ShowChangeProxy(_steamId, Name, () => _shell.ShowAccountDetails(_steamId));

    [RelayCommand]
    private async Task TestProxyAsync()
    {
        if (_proxyId is not { } id)
        {
            return;
        }

        IsTesting = true;
        try
        {
            await _shell.CheckProxiesAsync([id]).ConfigureAwait(true);
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void Reveal()
    {
        if (!HasRevocationCode)
        {
            return;
        }

        if (_vault.IsEncrypted)
        {
            RevealPassword = "";
            IsAskingPassword = true;
            return;
        }

        ShowRevocationCode();
    }

    [RelayCommand]
    private async Task ConfirmRevealAsync()
    {
        if (IsVerifying || string.IsNullOrEmpty(RevealPassword))
        {
            return;
        }

        IsVerifying = true;
        string password = RevealPassword;
        bool ok = await Task.Run(() => _vault.VerifyPassword(password)).ConfigureAwait(true);
        IsVerifying = false;
        if (!ok)
        {
            RevealError = Strings.WrongPassword;
            return;
        }

        RevealPassword = "";
        IsAskingPassword = false;
        ShowRevocationCode();
    }

    [RelayCommand]
    private void CancelReveal()
    {
        RevealPassword = "";
        IsAskingPassword = false;
    }

    [RelayCommand]
    private void HideRevocationCode()
    {
        RevealedCode = null;
        _revealLeft = 0;
    }

    [RelayCommand]
    private Task CopyRevocationCodeAsync()
        => RevealedCode is { } code ? _shell.Platform.CopyTextAsync(code) : Task.CompletedTask;

    [RelayCommand]
    private Task CopySteamIdAsync() => _shell.Platform.CopyTextAsync(SteamIdText);

    [RelayCommand]
    private Task CopyDeviceIdAsync() => _shell.Platform.CopyTextAsync(DeviceId);

    [RelayCommand]
    private void ShowInFolder() => _shell.Platform.RevealFile(Path.Combine(_vault.Folder, FileName));

    /// <summary>Saves a plain (unencrypted) maFile that works in SDA and ASF.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        string? path = await _shell.Platform.SaveFileAsync(Strings.ExportMaFile, FileName).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        await File.WriteAllTextAsync(path, _vault.Load(_steamId).ToJson()).ConfigureAwait(true);
        ExportMessage = Strings.ExportedPlain;
    }

    [RelayCommand]
    private void AskRemove()
    {
        SavedForRemove = false;
        RemoveError = null;
        IsConfirmingRemove = true;
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private void ConfirmRemove()
    {
        if (!SavedForRemove)
        {
            RemoveError = Strings.RemoveNeedsCheck;
            return;
        }

        _saveCountdown = -1;
        var removed = _vault.Remove(_steamId);
        _shell.ResetConnection(_steamId);
        _shell.ShowMainAfterRemove(removed, Name);
    }

    private void Tick()
    {
        long now = _connection.Clock.Now;
        if (_maFile.SharedSecret is { } secret)
        {
            Code = SteamGuardCode.Generate(secret, now);
        }

        SecondsLeftText = string.Format(CultureInfo.CurrentCulture, SecondsLeftFormat, SteamGuardCode.SecondsRemaining(now));

        if (RevealedCode is not null && --_revealLeft <= 0)
        {
            RevealedCode = null;
        }
        else if (RevealedCode is not null)
        {
            HidesInText = string.Format(CultureInfo.CurrentCulture, HidesInFormat, _revealLeft);
        }

        if (_saveCountdown > 0 && --_saveCountdown == 0)
        {
            SaveOrganize();
        }
    }

    private void ShowRevocationCode()
    {
        _revealLeft = RevealSeconds;
        RevealedCode = _maFile.RevocationCode;
        HidesInText = string.Format(CultureInfo.CurrentCulture, HidesInFormat, _revealLeft);
    }

    private void ScheduleSave()
    {
        if (!_loading)
        {
            _saveCountdown = 2;
        }
    }

    private void SaveOrganize()
    {
        if (_saveCountdown < 0 || !_vault.Contains(_steamId))
        {
            return;
        }

        _saveCountdown = -1;
        _vault.SetAccountGroup(_steamId, GroupName);
        _vault.SetAccountNote(_steamId, Note);
    }

    private void UpdateSession()
    {
        var expiry = WebSession.GetExpiry(_maFile.Session?.RefreshToken);
        if (expiry is null)
        {
            SessionStatus = Strings.NotSignedIn;
            SessionProblem = true;
            SessionValidUntil = null;
            return;
        }

        bool expired = expiry <= DateTimeOffset.UtcNow;
        SessionStatus = expired ? Strings.SessionExpired : Strings.SignedInRenews;
        SessionProblem = expired;
        int days = Math.Max(0, (int)(expiry.Value - DateTimeOffset.UtcNow).TotalDays);
        SessionValidUntil = expired
            ? null
            : string.Format(CultureInfo.CurrentCulture, ValidUntilFormat, expiry.Value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture), days);
    }

    private void UpdateConnection()
    {
        _proxyId = _vault.GetAccountProxyId(_steamId);
        var proxy = _proxyId is { } id ? _vault.ListProxies().FirstOrDefault(p => p.Id == id) : null;
        HasProxy = proxy is not null;
        ProxyText = proxy?.Address ?? Strings.DirectConnection;
        ProxyDead = proxy?.Status == ProxyStatus.Dead;
        ProxyHealth = proxy switch
        {
            null => null,
            { Status: ProxyStatus.Dead } => proxy.LastError is { } error ? $"{Strings.NotResponding} · {error}" : Strings.NotResponding,
            { LatencyMs: { } ms } => string.Format(CultureInfo.CurrentCulture, LatencyFormat, ms),
            _ => Strings.NotCheckedYet,
        };
    }

    private void OnProxyHealthChanged(object? sender, EventArgs e) => UpdateConnection();

    private async Task LoadAvatarAsync()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("BSA_SAMPLE_CONFIRMATIONS") == "1")
        {
            return;
        }
#endif
        try
        {
            var profile = await TradeOffers.GetProfileAsync(_connection, _steamId, _cts.Token).ConfigureAwait(true);
            byte[]? bytes = await TradeOffers.GetImageAsync(_connection, profile.AvatarUrl, _cts.Token).ConfigureAwait(true);
            if (bytes is not null)
            {
                using var stream = new MemoryStream(bytes);
                Avatar = new Bitmap(stream);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException
                                       or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // The placeholder stays.
        }
    }
}
