using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BetterSteamAuth.Core.Security;
using Dapper;

namespace BetterSteamAuth.Storage;

/// <summary>One accept or reject, as shown in an account's history.</summary>
/// <param name="Summary">Steam's headline at the time; null when it can't be read.</param>
public sealed record HistoryEntry(
    long Id, ulong SteamId, int Type, string? Summary, bool Accepted, bool Automatic, bool Ok, DateTimeOffset At);

/// <summary>
/// Confirmation history. The headline names items and trade partners, so with an app password it is stored
/// encrypted with the data key, like a saved Steam password.
/// </summary>
public sealed partial class Vault
{
    /// <summary>Records an accepted or rejected confirmation.</summary>
    public void LogConfirmation(ulong steamId, ulong confirmationId, int type, string? summary, bool accepted, bool automatic, bool ok)
    {
        var (plain, secret, nonce) = SealSummary(steamId, summary);
        using var connection = _db.Open();
        connection.Execute(
            "INSERT INTO confirmation_log(steam_id, conf_id, conf_type, summary, summary_enc, summary_nonce, action, source, ok, at) " +
            "VALUES (@steamId, @confId, @type, @plain, @secret, @nonce, @action, @source, @ok, @at)",
            new
            {
                steamId = (long)steamId,
                confId = confirmationId.ToString(CultureInfo.InvariantCulture),
                type,
                plain,
                secret,
                nonce,
                action = accepted ? "accept" : "deny",
                source = automatic ? "auto" : "manual",
                ok = ok ? 1 : 0,
                at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    /// <summary>An account's history, newest first: up to <paramref name="limit"/> entries older than <paramref name="beforeId"/>.</summary>
    public IReadOnlyList<HistoryEntry> ListHistory(ulong steamId, int limit, long? beforeId = null)
    {
        using var connection = _db.Open();
        return connection.Query<HistoryRow>(
                "SELECT id AS Id, steam_id AS SteamId, conf_type AS Type, summary AS Summary, summary_enc AS Secret, " +
                "summary_nonce AS Nonce, action AS Action, source AS Source, ok AS Ok, at AS At FROM confirmation_log " +
                "WHERE steam_id = @id AND (@before IS NULL OR id < @before) ORDER BY id DESC LIMIT @limit",
                new { id = (long)steamId, before = beforeId, limit })
            .Select(r => new HistoryEntry(
                r.Id, (ulong)r.SteamId, (int)r.Type, r.Summary ?? OpenSummary((ulong)r.SteamId, r.Secret, r.Nonce),
                r.Action == "accept", r.Source == "auto", r.Ok != 0, DateTimeOffset.FromUnixTimeSeconds(r.At)))
            .ToList();
    }

    /// <summary>
    /// Deletes entries older than <paramref name="keepDays"/>, and in a vault with a password encrypts summaries
    /// that versions before 1.3 logged in plain text. Run at unlock and when the setting changes.
    /// </summary>
    public void MaintainHistory(int keepDays)
    {
        using var connection = _db.Open();
        long cutoff = DateTimeOffset.UtcNow.AddDays(-keepDays).ToUnixTimeSeconds();
        connection.Execute("DELETE FROM confirmation_log WHERE at < @cutoff", new { cutoff });
        if (!IsEncrypted || !IsUnlocked)
        {
            return;
        }

        var plain = connection.Query<(long Id, long SteamId, string Summary)>(
            "SELECT id, steam_id, summary FROM confirmation_log WHERE summary IS NOT NULL").ToList();
        if (plain.Count == 0)
        {
            return;
        }

        using var tx = connection.BeginTransaction();
        foreach (var row in plain)
        {
            var (_, secret, nonce) = SealSummary((ulong)row.SteamId, row.Summary);
            connection.Execute(
                "UPDATE confirmation_log SET summary = NULL, summary_enc = @secret, summary_nonce = @nonce WHERE id = @id",
                new { secret, nonce, id = row.Id }, tx);
        }

        tx.Commit();
    }

    private (string? Plain, byte[]? Secret, byte[]? Nonce) SealSummary(ulong steamId, string? summary)
    {
        if (summary is null || !IsEncrypted)
        {
            return (summary, null, null);
        }

        if (!IsUnlocked)
        {
            // Nothing logs while locked; if something does, the summary is dropped rather than stored readable.
            return (null, null, null);
        }

        byte[] plain = Encoding.UTF8.GetBytes(summary);
        try
        {
            var (nonce, secret) = VaultCrypto.Seal(RequireKey(), plain, SummaryAad(steamId));
            return (null, secret, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private string? OpenSummary(ulong steamId, byte[]? secret, byte[]? nonce)
    {
        if (secret is null || nonce is null || !IsUnlocked)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(VaultCrypto.Open(RequireKey(), nonce, secret, SummaryAad(steamId)));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] SummaryAad(ulong steamId)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"confirmation-summary:{steamId}"));

    private sealed class HistoryRow
    {
        public long Id { get; init; }

        public long SteamId { get; init; }

        public long Type { get; init; }

        public string? Summary { get; init; }

        public byte[]? Secret { get; init; }

        public byte[]? Nonce { get; init; }

        public string Action { get; init; } = "";

        public string Source { get; init; } = "";

        public long Ok { get; init; }

        public long At { get; init; }
    }
}
