namespace BetterSteamAuth.Steam;

/// <summary>Which confirmations an account's automation may accept without asking.</summary>
public static class AutoAccept
{
    /// <summary>
    /// True when the confirmation's type is switched on and nothing about it needs a person. Trades stop for review
    /// when Steam warns about them, when you give items and receive nothing, or when the summary isn't recognized.
    /// Account changes and unknown types are never selected.
    /// </summary>
    public static bool Selects(MobileConfirmation confirmation, bool trades, bool marketListings, bool marketPurchases)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        return confirmation.Kind switch
        {
            ConfirmationKind.Trade => trades
                && confirmation.Warning is null
                && ConfirmationSummary.ReadTrade(confirmation.Summary) is { GivesOnly: false },
            ConfirmationKind.Market => confirmation.IsMarketPurchase ? marketPurchases : marketListings,
            _ => false,
        };
    }
}
