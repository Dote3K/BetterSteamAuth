using System.Globalization;
using System.Text;
using Avalonia.Threading;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public enum ConfirmationState
{
    Idle,

    /// <summary>Trade ✓ pressed: inline "accept this trade?" while trade checks don't exist yet.</summary>
    ConfirmingTrade,

    /// <summary>Waiting out the undo window before sending.</summary>
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
    private DispatcherTimer? _countdown;
    private int _secondsLeft;

    public ConfirmationItemViewModel(MobileConfirmation confirmation, Func<ConfirmationItemViewModel, bool, Task> send)
    {
        Confirmation = confirmation;
        _send = send;
    }

    public MobileConfirmation Confirmation { get; }

    public ulong Id => Confirmation.Id;

    public bool IsTrade => Confirmation.Kind == ConfirmationKind.Trade;

    public bool IsMarket => Confirmation.Kind == ConfirmationKind.Market;

    public bool IsDangerous => Confirmation.Kind == ConfirmationKind.Dangerous;

    /// <summary>Dangerous types never get a quick accept.</summary>
    public bool CanQuickAccept => !IsDangerous;

    public string Headline => Confirmation.Headline ?? Confirmation.TypeName ?? Strings.Confirmation;

    public string Summary => string.Join(" · ", Confirmation.Summary.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Meta => $"{TypeLabel} · {Age(Confirmation.CreationTime)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsConfirmingTrade), nameof(IsPending), nameof(IsSending))]
    public partial ConfirmationState State { get; private set; }

    public bool IsIdle => State == ConfirmationState.Idle;

    public bool IsConfirmingTrade => State == ConfirmationState.ConfirmingTrade;

    public bool IsPending => State == ConfirmationState.Pending;

    public bool IsSending => State == ConfirmationState.Sending;

    [ObservableProperty]
    public partial string PendingText { get; private set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    private bool _accepting;

    private string TypeLabel => Confirmation.Kind switch
    {
        ConfirmationKind.Trade => Strings.Trade,
        ConfirmationKind.Market => Strings.Market,
        _ => Strings.Account,
    };

    [RelayCommand]
    private void Accept()
    {
        if (IsDangerous)
        {
            return;
        }

        if (IsTrade && State == ConfirmationState.Idle)
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
        State = ConfirmationState.Idle;
    }

    /// <summary>Called when the account's page goes away: pending actions are dropped, not sent.</summary>
    public void Abandon() => StopCountdown();

    private void StartCountdown(bool accept)
    {
        Error = null;
        _accepting = accept;
        _secondsLeft = UndoSeconds;
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
