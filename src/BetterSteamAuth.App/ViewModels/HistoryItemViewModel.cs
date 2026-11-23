using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Steam;
using BetterSteamAuth.Storage;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>
/// One accept or reject in an account's history, in two lines: what it was and what happened, then its type and
/// Steam's details (give/receive counts, price, payout).
/// </summary>
/// <param name="withDay">False on the page grouped by day, where only the time is shown.</param>
public sealed class HistoryItemViewModel(HistoryEntry entry, bool withDay = true)
{
    private static readonly CompositeFormat AutoFormat = CompositeFormat.Parse(Strings.HistoryAutoFormat);
    private static readonly CompositeFormat TradeFormat = CompositeFormat.Parse(Strings.HistoryTradeFormat);
    private static readonly CompositeFormat ItemsFormat = CompositeFormat.Parse(Strings.TradeItemsFormat);
    private static readonly CompositeFormat DayTimeFormat = CompositeFormat.Parse(Strings.HistoryDayTimeFormat);

    public long Id => entry.Id;

    public DateTime LocalDate => entry.At.ToLocalTime().Date;

    private ConfirmationKind Kind => new MobileConfirmation { Type = entry.Type }.Kind;

    private bool IsPurchase => entry.Type == MobileConfirmation.MarketPurchaseType;

    public bool IsTrade => Kind == ConfirmationKind.Trade;

    public bool IsMarket => Kind == ConfirmationKind.Market;

    public bool IsDangerous => Kind == ConfirmationKind.Dangerous;

    private string KindLabel => Kind switch
    {
        ConfirmationKind.Trade => Strings.Trade,
        ConfirmationKind.Market => IsPurchase ? Strings.MarketPurchase : Strings.MarketListing,
        _ => Strings.AccountChange,
    };

    public string Headline => entry.Summary ?? KindLabel;

    /// <summary>"Trade · Give 1 item, receive 2 items", "Market listing · List for $12.40 · You receive $10.78".</summary>
    public string DetailLine => Details is { Length: > 0 } details ? KindLabel + " · " + details : KindLabel;

    private string? Details
    {
        get
        {
            var lines = entry.Details?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            if (lines.Length == 0)
            {
                return null;
            }

            return IsTrade && ConfirmationSummary.ReadTrade(lines) is { } counts
                ? string.Format(CultureInfo.CurrentCulture, TradeFormat, Amount(counts.Give), Amount(counts.Receive))
                : string.Join(" · ", lines);
        }
    }

    public bool Failed => !entry.Ok;

    /// <summary>"Accepted", "Rejected automatically", or "Failed".</summary>
    public string Status
    {
        get
        {
            string status = !entry.Ok ? Strings.HistoryFailed : entry.Accepted ? Strings.HistoryAccepted : Strings.HistoryRejected;
            return entry.Automatic ? string.Format(CultureInfo.CurrentCulture, AutoFormat, status) : status;
        }
    }

    public string? StatusTip => Failed ? Strings.HistoryFailedTip : null;

    /// <summary>The time, and outside the page grouped by day, "Yesterday 09:12" or the date before it.</summary>
    public string When
    {
        get
        {
            var local = entry.At.ToLocalTime();
            string time = local.ToString("t", CultureInfo.CurrentCulture);
            if (!withDay || local.Date == DateTime.Today)
            {
                return time;
            }

            string day = local.Date == DateTime.Today.AddDays(-1) ? Strings.Yesterday : local.ToString("d", CultureInfo.CurrentCulture);
            return string.Format(CultureInfo.CurrentCulture, DayTimeFormat, day, time);
        }
    }

    public string WhenFull => entry.At.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

    private static string Amount(int count) => count switch
    {
        0 => Strings.TradeNothing,
        1 => Strings.TradeOneItem,
        _ => string.Format(CultureInfo.CurrentCulture, ItemsFormat, count),
    };
}
