using System.Globalization;
using System.Text;
using Avalonia.Threading;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>App-wide confirmation rules the user can change in Settings.</summary>
public sealed class ConfirmationPolicy
{
    public const string BulkTradesKey = "confirmations.bulk_trades";

    /// <summary>Trades without warnings behave like market listings: quick accept and bulk accept.</summary>
    public bool AcceptTradesInBulk { get; set; }
}

public enum ConfirmationState
{
    Idle,

    /// <summary>Trade ✓ pressed: inline "accept this trade?" (needs review).</summary>
    ConfirmingTrade,

    /// <summary>Waiting out the undo window (its own, or a batch's) before sending.</summary>
    Pending,

    Sending,
}

/// <summary>
/// One confirmation row. Quick actions wait <see cref="UndoSeconds"/> before anything is sent;
/// if the app closes in that window nothing is sent.
/// </summary>
public sealed partial class ConfirmationItemViewModel : ObservableObject
{
    public const int UndoSeconds = 4;

    private static readonly CompositeFormat AcceptedFormat = CompositeFormat.Parse(Strings.AcceptedSendingFormat);
    private static readonly CompositeFormat RejectedFormat = CompositeFormat.Parse(Strings.RejectedSendingFormat);

    private readonly Func<ConfirmationItemViewModel, bool, Task> _send;
    private readonly ConfirmationPolicy _policy;
    private DispatcherTimer? _countdown;
    private int _secondsLeft;
    private bool _accepting;

    public ConfirmationItemViewModel(
        MobileConfirmation confirmation, string accountName, ConfirmationPolicy policy, Func<ConfirmationItemViewModel, bool, Task> send)
    {
        Confirmation = confirmation;
        AccountName = accountName;
        _policy = policy;
        _send = send;
    }

    public MobileConfirmation Confirmation { get; }

    public string AccountName { get; }

    public ulong Id => Confirmation.Id;

    public bool IsTrade => Confirmation.Kind == ConfirmationKind.Trade;

    public bool IsMarket => Confirmation.Kind == ConfirmationKind.Market;

    public bool IsDangerous => Confirmation.Kind == ConfirmationKind.Dangerous;

    public string? Warning => Confirmation.Warning;

    public bool HasWarning => Warning is not null;

    /// <summary>Trades with a warning always need review; others only while bulk trades are off.</summary>
    public bool NeedsTradeReview => IsTrade && (HasWarning || !_policy.AcceptTradesInBulk);

    /// <summary>Whether this row may be part of a bulk accept. Dangerous types never are.</summary>
    public bool CanBulkAccept => IsMarket || (IsTrade && !NeedsTradeReview);

    public string TradeConfirmText => HasWarning ? Strings.AcceptFlaggedTradeTitle : Strings.AcceptTradeTitle;

    public string Headline => Confirmation.Headline ?? Confirmation.TypeName ?? Strings.Confirmation;

    public string Summary => string.Join(" · ", Confirmation.Summary.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Meta => $"{TypeLabel} · {Age(Confirmation.CreationTime)}";

    public string MetaWithAccount => $"{AccountName} · {Meta}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsConfirmingTrade), nameof(IsPending), nameof(IsSending))]
    public partial ConfirmationState State { get; private set; }

    public bool IsIdle => State == ConfirmationState.Idle;

    public bool IsConfirmingTrade => State == ConfirmationState.ConfirmingTrade;

    public bool IsPending => State == ConfirmationState.Pending;

    public bool IsSending => State == ConfirmationState.Sending;

    /// <summary>True while a batch (not this row's own timer) holds the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOwnUndo))]
    public partial bool InBatch { get; private set; }

    public bool ShowOwnUndo => IsPending && !InBatch;

    partial void OnStateChanged(ConfirmationState value) => OnPropertyChanged(nameof(ShowOwnUndo));

    [ObservableProperty]
    public partial string PendingText { get; private set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    private string TypeLabel => Confirmation.Kind switch
    {
        ConfirmationKind.Trade => Strings.Trade,
        ConfirmationKind.Market => Strings.Market,
        _ => Strings.Account,
    };

    [RelayCommand]
    private void Accept()
    {
        if (NeedsTradeReview && State == ConfirmationState.Idle)
        {
            State = ConfirmationState.ConfirmingTrade;
            return;
        }

        StartCountdown(accept: true);
    }

    [RelayCommand]
    private void Reject() => StartCountdown(accept: false);

    [RelayCommand]
    private void CancelTrade() => State = ConfirmationState.Idle;

    [RelayCommand]
    private void Undo()
    {
        StopCountdown();
        InBatch = false;
        State = ConfirmationState.Idle;
    }

    /// <summary>Held by a batch's shared undo window.</summary>
    public void JoinBatch(bool accept)
    {
        StopCountdown();
        Error = null;
        InBatch = true;
        PendingText = accept ? Strings.Accepting : Strings.Rejecting;
        State = ConfirmationState.Pending;
    }

    public void LeaveBatch()
    {
        InBatch = false;
        State = ConfirmationState.Idle;
    }

    public void MarkSending() => State = ConfirmationState.Sending;

    /// <summary>Called when the page goes away: pending actions are dropped, not sent.</summary>
    public void Abandon() => StopCountdown();

    private void StartCountdown(bool accept)
    {
        Error = null;
        _accepting = accept;
        _secondsLeft = UndoSeconds;
        InBatch = false;
        State = ConfirmationState.Pending;
        UpdatePendingText();

        StopCountdown();
        _countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, async (_, _) => await TickAsync().ConfigureAwait(true));
        _countdown.Start();
    }

    private async Task TickAsync()
    {
        _secondsLeft--;
        if (_secondsLeft > 0)
        {
            UpdatePendingText();
            return;
        }

        StopCountdown();
        State = ConfirmationState.Sending;
        await _send(this, _accepting).ConfigureAwait(true);
        if (State == ConfirmationState.Sending)
        {
            State = ConfirmationState.Idle;
        }
    }

    private void StopCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
    }

    private void UpdatePendingText()
        => PendingText = string.Format(CultureInfo.CurrentCulture, _accepting ? AcceptedFormat : RejectedFormat, _secondsLeft);

    private static string Age(long unixTime)
    {
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixTime);
        return age.TotalMinutes < 1 ? Strings.Now
            : age.TotalHours < 1 ? string.Create(CultureInfo.CurrentCulture, $"{(int)age.TotalMinutes}m")
            : age.TotalDays < 1 ? string.Create(CultureInfo.CurrentCulture, $"{(int)age.TotalHours}h")
            : string.Create(CultureInfo.CurrentCulture, $"{(int)age.TotalDays}d");
    }
}
