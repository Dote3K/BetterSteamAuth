using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BetterSteamAuth.Core.Security;
using Dapper;

namespace BetterSteamAuth.Storage;

/// <summary>One accept or reject, as shown in an account's history.</summary>
/// <param name="Summary">Steam's headline at the time; null when it can't be read.</param>
/// <param name="Details">Steam's summary lines at the time, one per line; null for entries from before 1.3.1.</param>
public sealed record HistoryEntry(
    long Id, ulong SteamId, int Type, string? Summary, bool Accepted, bool Automatic, bool Ok, DateTimeOffset At,
    string? Details = null);

/// <summary>
/// Confirmation history. The headline names items and trade partners, so with an app password it is stored
/// encrypted with the data key, like a saved Steam password.
/// </summary>
public sealed partial class Vault
{
    /// <summary>Records an accepted or rejected confirmation, with Steam's headline and summary lines.</summary>
    public void LogConfirmation(
        ulong steamId, ulong confirmationId, int type, string? summary, bool accepted, bool automatic, bool ok, string? details = null)
    {
        var (plain, secret, nonce) = SealSummary(steamId, summary);
        var (detailsPlain, detailsSecret, detailsNonce) = Seal(steamId, details, DetailsAad);
        using var connection = _db.Open();
        connection.Execute(
            "INSERT INTO confirmation_log(steam_id, conf_id, conf_type, summary, summary_enc, summary_nonce, " +
            "details, details_enc, details_nonce, action, source, ok, at) " +
            "VALUES (@steamId, @confId, @type, @plain, @secret, @nonce, @detailsPlain, @detailsSecret, @detailsNonce, " +
            "@action, @source, @ok, @at)",
            new
            {
                steamId = (long)steamId,
                confId = confirmationId.ToString(CultureInfo.InvariantCulture),
                type,
                plain,
                secret,
                nonce,
                detailsPlain,
                detailsSecret,
                detailsNonce,
                action = accepted ? "accept" : "deny",
                source = automatic ? "auto" : "manual",
                ok = ok ? 1 : 0,
                at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    /// <summary>An account's history, newest first: up to <paramref name="limit"/> entries after <paramref name="after"/>.</summary>
    public IReadOnlyList<HistoryEntry> ListHistory(ulong steamId, int limit, HistoryEntry? after = null)
    {
        using var connection = _db.Open();
        return connection.Query<HistoryRow>(
                "SELECT id AS Id, steam_id AS SteamId, conf_type AS Type, summary AS Summary, summary_enc AS Secret, " +
                "summary_nonce AS Nonce, details AS Details, details_enc AS DetailsSecret, details_nonce AS DetailsNonce, " +
                "action AS Action, source AS Source, ok AS Ok, at AS At FROM confirmation_log " +
                "WHERE steam_id = @id AND (@afterId IS NULL OR at < @afterAt OR (at = @afterAt AND id < @afterId)) " +
                "ORDER BY at DESC, id DESC LIMIT @limit",
                new { id = (long)steamId, afterId = after?.Id, afterAt = after?.At.ToUnixTimeSeconds(), limit })
            .Select(r => new HistoryEntry(
                r.Id, (ulong)r.SteamId, (int)r.Type, r.Summary ?? OpenSummary((ulong)r.SteamId, r.Secret, r.Nonce),
                r.Action == "accept", r.Source == "auto", r.Ok != 0, DateTimeOffset.FromUnixTimeSeconds(r.At),
                r.Details ?? Open((ulong)r.SteamId, r.DetailsSecret, r.DetailsNonce, DetailsAad)))
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
        var plainDetails = connection.Query<(long Id, long SteamId, string Details)>(
            "SELECT id, steam_id, details FROM confirmation_log WHERE details IS NOT NULL").ToList();
        if (plain.Count == 0 && plainDetails.Count == 0)
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

        foreach (var row in plainDetails)
        {
            var (_, secret, nonce) = Seal((ulong)row.SteamId, row.Details, DetailsAad);
            connection.Execute(
                "UPDATE confirmation_log SET details = NULL, details_enc = @secret, details_nonce = @nonce WHERE id = @id",
                new { secret, nonce, id = row.Id }, tx);
        }

        tx.Commit();
    }

    private (string? Plain, byte[]? Secret, byte[]? Nonce) SealSummary(ulong steamId, string? summary)
        => Seal(steamId, summary, SummaryAad);

    private string? OpenSummary(ulong steamId, byte[]? secret, byte[]? nonce) => Open(steamId, secret, nonce, SummaryAad);

    private (string? Plain, byte[]? Secret, byte[]? Nonce) Seal(ulong steamId, string? summary, Func<ulong, byte[]> aad)
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
            var (nonce, secret) = VaultCrypto.Seal(RequireKey(), plain, aad(steamId));
            return (null, secret, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private string? Open(ulong steamId, byte[]? secret, byte[]? nonce, Func<ulong, byte[]> aad)
    {
        if (secret is null || nonce is null || !IsUnlocked)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(VaultCrypto.Open(RequireKey(), nonce, secret, aad(steamId)));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] SummaryAad(ulong steamId)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"confirmation-summary:{steamId}"));

    private static byte[] DetailsAad(ulong steamId)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"confirmation-details:{steamId}"));

    private sealed class HistoryRow
    {
        public long Id { get; init; }

        public long SteamId { get; init; }

        public long Type { get; init; }

        public string? Summary { get; init; }

        public byte[]? Secret { get; init; }

        public byte[]? Nonce { get; init; }

        public string? Details { get; init; }

        public byte[]? DetailsSecret { get; init; }

        public byte[]? DetailsNonce { get; init; }

        public string Action { get; init; } = "";

        public string Source { get; init; } = "";

        public long Ok { get; init; }

        public long At { get; init; }
    }
}
