using System.Collections.ObjectModel;
using BetterSteamAuth.Core.Guard;
using BetterSteamAuth.Steam;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>One account in the list. Holds its shared secret only while the app is unlocked.</summary>
public sealed partial class AccountItemViewModel(ulong steamId, string name, byte[] sharedSecret, SteamClock clock)
    : ObservableObject
{
    private long _period = -1;

    public ulong SteamId { get; } = steamId;

    public string Name { get; } = name;

    public SteamClock Clock { get; } = clock;

    [ObservableProperty]
    public partial string Code { get; private set; } = "";

    public ObservableCollection<ConfirmationItemViewModel> Confirmations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(ShowNothingToConfirm))]
    public partial int PendingCount { get; private set; }

    public bool HasPending => PendingCount > 0;

    /// <summary>Why confirmations can't load (shown as a banner and a warning icon), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(ShowNothingToConfirm))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    /// <summary>Whether the problem is fixed by signing in again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshProblem))]
    public partial bool ProblemNeedsLogin { get; set; }

    /// <summary>Linked in this app but not activated yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshProblem))]
    public partial bool IsLinkPending { get; set; }

    public bool CanRefreshProblem => !ProblemNeedsLogin && !IsLinkPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNothingToConfirm))]
    public partial bool HasLoaded { get; private set; }

    public bool ShowNothingToConfirm => HasLoaded && PendingCount == 0 && !HasProblem;

    /// <summary>Regenerates the code when this account's Steam time enters a new period.</summary>
    public void RefreshIfRotated()
    {
        long now = Clock.Now;
        long period = now / SteamGuardCode.PeriodSeconds;
        if (period != _period)
        {
            _period = period;
            Code = SteamGuardCode.Generate(sharedSecret, now);
        }
    }

    /// <summary>The code of the next period, used when copying in the last seconds.</summary>
    public string NextCode() => SteamGuardCode.Generate(sharedSecret, Clock.Now + SteamGuardCode.PeriodSeconds);

    /// <summary>
    /// Replaces the list with what Steam returned, keeping rows the user is acting on
    /// (their undo window or send is still running).
    /// </summary>
    public void SetConfirmations(IReadOnlyList<MobileConfirmation> latest, Func<MobileConfirmation, ConfirmationItemViewModel> create)
    {
        var existing = Confirmations.ToDictionary(c => c.Id);
        var busy = Confirmations.Where(c => !c.IsIdle).ToList();
        var next = latest.Select(c => existing.Remove(c.Id, out var kept) ? kept : create(c)).ToList();

        // Rows still in an undo window or being sent stay until they finish.
        next.AddRange(busy.Where(b => !next.Contains(b)));
        Confirmations.Clear();
        foreach (var row in next)
        {
            Confirmations.Add(row);
        }

        HasLoaded = true;
        UpdateCount();
    }

    public void Remove(ConfirmationItemViewModel item)
    {
        Confirmations.Remove(item);
        UpdateCount();
    }

    public void ForgetSecret()
    {
        foreach (var confirmation in Confirmations)
        {
            confirmation.Abandon();
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(sharedSecret);
    }

    private void UpdateCount() => PendingCount = Confirmations.Count;
}
