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

    /// <summary>Regenerates the code for the account's current Steam time.</summary>
    public void Refresh() => Code = SteamGuardCode.Generate(sharedSecret, Clock.Now);

    /// <summary>The code of the next period, used when copying in the last seconds.</summary>
    public string NextCode() => SteamGuardCode.Generate(sharedSecret, Clock.Now + SteamGuardCode.PeriodSeconds);

    public void ForgetSecret() => System.Security.Cryptography.CryptographicOperations.ZeroMemory(sharedSecret);
}
