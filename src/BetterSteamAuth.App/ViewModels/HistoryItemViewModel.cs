using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Steam;
using BetterSteamAuth.Storage;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>One accept or reject in an account's history.</summary>
public sealed class HistoryItemViewModel(HistoryEntry entry)
{
    private static readonly CompositeFormat AutoFormat = CompositeFormat.Parse(Strings.HistoryAutoFormat);

    public long Id => entry.Id;

    public DateTime LocalDate => entry.At.ToLocalTime().Date;

    private ConfirmationKind Kind => new MobileConfirmation { Type = entry.Type }.Kind;

    public bool IsTrade => Kind == ConfirmationKind.Trade;

    public bool IsMarket => Kind == ConfirmationKind.Market;

    public bool IsDangerous => Kind == ConfirmationKind.Dangerous;

    public string Headline => entry.Summary ?? Kind switch
    {
        ConfirmationKind.Trade => Strings.Trade,
        ConfirmationKind.Market => entry.Type == MobileConfirmation.MarketPurchaseType ? Strings.Purchase : Strings.Market,
        _ => Strings.Confirmation,
    };

    public bool Failed => !entry.Ok;

    /// <summary>"Accepted", "Rejected" or "Failed", with "· auto" when automation did it.</summary>
    public string Status
    {
        get
        {
            string status = !entry.Ok ? Strings.HistoryFailed : entry.Accepted ? Strings.HistoryAccepted : Strings.HistoryRejected;
            return entry.Automatic ? string.Format(CultureInfo.CurrentCulture, AutoFormat, status) : status;
        }
    }

    public string? StatusTip => Failed ? Strings.HistoryFailedTip : null;

    /// <summary>The time for today's entries, else the date.</summary>
    public string When
    {
        get
        {
            var local = entry.At.ToLocalTime();
            return local.Date == DateTime.Today
                ? local.ToString("t", CultureInfo.CurrentCulture)
                : local.Date == DateTime.Today.AddDays(-1) ? Strings.Yesterday : local.ToString("d", CultureInfo.CurrentCulture);
        }
    }

    public string WhenFull => entry.At.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
}
