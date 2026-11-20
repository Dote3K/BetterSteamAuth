using System.Globalization;
using System.Text.RegularExpressions;

namespace BetterSteamAuth.Steam;

/// <summary>How many items each side of a trade moves.</summary>
public readonly record struct TradeCounts(int Give, int Receive)
{
    /// <summary>You give items and get nothing back.</summary>
    public bool GivesOnly => Give > 0 && Receive == 0;
}

/// <summary>
/// Reads the English summary lines of <c>mobileconf/getlist</c>. Steam's wording isn't documented, so anything not
/// recognized returns null and the row shows Steam's text as it is.
/// </summary>
public static partial class ConfirmationSummary
{
    /// <summary>"You will give 3 items" and "You will receive nothing" (in either order), or null.</summary>
    public static TradeCounts? ReadTrade(IReadOnlyList<string> summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        int? give = null;
        int? receive = null;
        foreach (string line in summary)
        {
            if (line.Contains("give", StringComparison.OrdinalIgnoreCase))
            {
                give ??= Count(line);
            }
            else if (line.Contains("receive", StringComparison.OrdinalIgnoreCase))
            {
                receive ??= Count(line);
            }
        }

        return give is { } g && receive is { } r ? new TradeCounts(g, r) : null;
    }

    /// <summary>The line saying what you get from a market sale ("You receive $10.78"), or null if there is none.</summary>
    public static string? ReadPayout(IReadOnlyList<string> summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.FirstOrDefault(line => line.Contains("receive", StringComparison.OrdinalIgnoreCase) && Number().IsMatch(line));
    }

    private static int? Count(string line)
    {
        if (line.Contains("nothing", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var match = Number().Match(line);
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : null;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();
}
