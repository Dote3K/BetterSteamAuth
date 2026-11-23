using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>
/// One account's automation: when it's checked, whether it notifies, and which confirmation types it accepts without
/// asking. Each change is saved at once and the monitor picks it up.
/// </summary>
public sealed partial class AutomationViewModel : ObservableObject
{
    private static readonly CompositeFormat TitleFormat = CompositeFormat.Parse(Strings.AutomationTitleFormat);
    private static readonly CompositeFormat AutoLockFormat = CompositeFormat.Parse(Strings.AutoAcceptAutoLockFormat);

    private readonly ShellViewModel _shell;
    private readonly ulong _steamId;
    private bool _loading = true;
    private bool _tradesConfirmed;

    public AutomationViewModel(ShellViewModel shell, ulong steamId, string accountName)
    {
        _shell = shell;
        _steamId = steamId;
        Title = string.Format(CultureInfo.CurrentCulture, TitleFormat, accountName);
        var current = shell.Vault?.GetAutomation(steamId) ?? AccountAutomation.Default;
        Checks = CheckChoices.Single(c => c.Value == current.Checks);
        Notify = current.Notify;
        Trades = current.Trades;
        Market = current.Market;
        Purchases = current.Purchases;
        LockNote = shell.Vault?.IsEncrypted != true ? null
            : shell.AutoLockMinutes > 0
                ? Strings.AutoAcceptStopsWhenLocked + " " + string.Format(CultureInfo.CurrentCulture, AutoLockFormat, shell.AutoLockMinutes)
                : Strings.AutoAcceptStopsWhenLocked;
        _loading = false;
    }

    public string Title { get; }

    public static IReadOnlyList<Choice<ConfirmationChecks>> CheckChoices { get; } =
    [
        new(ConfirmationChecks.LikeSettings, Strings.LikeSettings),
        new(ConfirmationChecks.Always, Strings.Always),
        new(ConfirmationChecks.Never, Strings.Never),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAutoAccept), nameof(ShowNeedsBackground))]
    public partial Choice<ConfirmationChecks> Checks { get; set; }

    [ObservableProperty]
    public partial bool Notify { get; set; }

    [ObservableProperty]
    public partial bool Trades { get; set; }

    [ObservableProperty]
    public partial bool Market { get; set; }

    [ObservableProperty]
    public partial bool Purchases { get; set; }

    /// <summary>Turning Trades on waits for this confirmation.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingTrades { get; private set; }

    /// <summary>Auto-accept needs the account checked in the background.</summary>
    public bool CanAutoAccept => _shell.Monitor.ChecksInBackground(Current with { Trades = false, Market = false, Purchases = false });

    public bool ShowNeedsBackground => !CanAutoAccept;

    /// <summary>Only with an app password: locking stops automation, and auto-lock may lock by itself.</summary>
    public string? LockNote { get; }

    public bool HasLockNote => LockNote is not null;

    private AccountAutomation Current => new(Checks?.Value ?? ConfirmationChecks.LikeSettings, Notify, Trades, Market, Purchases);

    partial void OnChecksChanged(Choice<ConfirmationChecks> value) => Save();

    partial void OnNotifyChanged(bool value) => Save();

    partial void OnMarketChanged(bool value) => Save();

    partial void OnPurchasesChanged(bool value) => Save();

    partial void OnTradesChanged(bool value)
    {
        if (value && !_loading && !_tradesConfirmed)
        {
            // Asked first: switched back off until confirmed.
            IsConfirmingTrades = true;
            Trades = false;
            return;
        }

        _tradesConfirmed = false;
        Save();
    }

    [RelayCommand]
    private void ConfirmTrades()
    {
        IsConfirmingTrades = false;
        _tradesConfirmed = true;
        Trades = true;
    }

    [RelayCommand]
    private void CancelTrades() => IsConfirmingTrades = false;

    [RelayCommand]
    private void Back() => _shell.ShowAccountDetails(_steamId);

    private void Save()
    {
        if (_loading || IsConfirmingTrades || _shell.Vault is not { } vault)
        {
            return;
        }

        vault.SetAutomation(_steamId, Current);
        _shell.Monitor.Sync();
    }

    /// <summary>"Off", "Trades · Market listings", or "Paused: not checked in the background".</summary>
    public static string Summary(AccountAutomation automation, bool checksInBackground)
    {
        ArgumentNullException.ThrowIfNull(automation);
        if (!automation.AcceptsAny)
        {
            return Strings.AutomationOff;
        }

        if (!checksInBackground)
        {
            return Strings.AutomationPaused;
        }

        return string.Join(" · ", new[]
        {
            automation.Trades ? Strings.AutoTrades : null,
            automation.Market ? Strings.AutoMarketListings : null,
            automation.Purchases ? Strings.AutoMarketPurchases : null,
        }.OfType<string>());
    }
}
