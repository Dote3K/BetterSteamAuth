#if DEBUG
using BetterSteamAuth.Steam;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Fake confirmations for reviewing the UI in debug builds. Never compiled into release builds.</summary>
internal static class SampleConfirmations
{
    public static IReadOnlyList<MobileConfirmation> Create()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return
        [
            new() { Id = 1, Type = 2, TypeName = "Trade Offer", Headline = "buyer_88", CreationTime = now - 120, Summary = ["You will give 3 items", "You will receive nothing"] },
            new() { Id = 2, Type = 3, TypeName = "Market Listing", Headline = "AK-47 | Redline (Field-Tested)", CreationTime = now - 540, Summary = ["List for $12.40", "You receive $10.78"] },
            new() { Id = 3, Type = 9, TypeName = "Web API Key", Headline = "Create Web API key", CreationTime = now - 20, Summary = ["Domain: steam-trade.example"] },
        ];
    }
}
#endif
