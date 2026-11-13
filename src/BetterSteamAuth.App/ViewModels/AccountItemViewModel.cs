using BetterSteamAuth.Core.Guard;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>One account in the list. Holds its shared secret only while the app is unlocked.</summary>
public sealed partial class AccountItemViewModel(ulong steamId, string name, byte[] sharedSecret, SteamClock clock)
    : ObservableObject
{
    public ulong SteamId { get; } = steamId;

    public string Name { get; } = name;

    public SteamClock Clock { get; } = clock;

    [ObservableProperty]
    public partial string Code { get; private set; } = "";

    private long _period = -1;

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

    public void ForgetSecret() => System.Security.Cryptography.CryptographicOperations.ZeroMemory(sharedSecret);
}
