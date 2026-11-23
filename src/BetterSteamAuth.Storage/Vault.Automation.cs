using Dapper;

namespace BetterSteamAuth.Storage;

/// <summary>When an account is checked for new confirmations.</summary>
public enum ConfirmationChecks
{
    /// <summary>Follows the app-wide "check in the background" setting.</summary>
    LikeSettings = 0,
    Always = 1,
    Never = 2,
}

/// <summary>One account's automation. The defaults (no row) do nothing on their own.</summary>
public sealed record AccountAutomation(
    ConfirmationChecks Checks = ConfirmationChecks.LikeSettings,
    bool Notify = true,
    bool Trades = false,
    bool Market = false,
    bool Purchases = false)
{
    public static AccountAutomation Default { get; } = new();

    public bool AcceptsAny => Trades || Market || Purchases;
}

public sealed partial class Vault
{
    public AccountAutomation GetAutomation(ulong steamId)
    {
        using var connection = _db.Open();
        return connection.QuerySingleOrDefault<AutomationRow>(
                   "SELECT checks AS Checks, notify AS Notify, trades AS Trades, market AS Market, purchases AS Purchases " +
                   "FROM account_automation WHERE steam_id = @id", new { id = (long)steamId })?.ToAutomation()
               ?? AccountAutomation.Default;
    }

    /// <summary>Every account's automation; accounts without a row get the defaults.</summary>
    public IReadOnlyDictionary<ulong, AccountAutomation> ListAutomation()
    {
        using var connection = _db.Open();
        var rows = connection.Query<AutomationRow>(
                "SELECT steam_id AS SteamId, checks AS Checks, notify AS Notify, trades AS Trades, market AS Market, " +
                "purchases AS Purchases FROM account_automation")
            .ToDictionary(r => (ulong)r.SteamId, r => r.ToAutomation());
        return ListAccounts().ToDictionary(a => a.SteamId, a => rows.GetValueOrDefault(a.SteamId) ?? AccountAutomation.Default);
    }

    public void SetAutomation(ulong steamId, AccountAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        using var connection = _db.Open();
        connection.Execute(
            "INSERT INTO account_automation(steam_id, checks, notify, trades, market, purchases) " +
            "VALUES (@id, @checks, @notify, @trades, @market, @purchases) " +
            "ON CONFLICT(steam_id) DO UPDATE SET checks = excluded.checks, notify = excluded.notify, " +
            "trades = excluded.trades, market = excluded.market, purchases = excluded.purchases",
            new
            {
                id = (long)steamId,
                checks = (int)automation.Checks,
                notify = automation.Notify ? 1 : 0,
                trades = automation.Trades ? 1 : 0,
                market = automation.Market ? 1 : 0,
                purchases = automation.Purchases ? 1 : 0,
            });
    }

    private sealed class AutomationRow
    {
        public long SteamId { get; init; }

        public long Checks { get; init; }

        public long Notify { get; init; }

        public long Trades { get; init; }

        public long Market { get; init; }

        public long Purchases { get; init; }

        public AccountAutomation ToAutomation() => new(
            Enum.IsDefined((ConfirmationChecks)Checks) ? (ConfirmationChecks)Checks : ConfirmationChecks.LikeSettings,
            Notify != 0, Trades != 0, Market != 0, Purchases != 0);
    }
}
