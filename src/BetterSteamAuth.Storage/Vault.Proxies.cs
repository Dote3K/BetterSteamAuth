using System.Globalization;
using System.Text;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Security;
using Dapper;

namespace BetterSteamAuth.Storage;

public enum ProxyStatus
{
    Unknown,
    Ok,
    Dead,
}

/// <summary>A proxy in the pool, without its password.</summary>
public sealed record ProxyEntry(
    long Id,
    ProxyScheme Scheme,
    string Host,
    int Port,
    string? Username,
    ProxyStatus Status,
    int? LatencyMs,
    string? LastError,
    IReadOnlyList<ulong> AccountIds)
{
    public string Address => string.Create(CultureInfo.InvariantCulture, $"{Scheme.ToString().ToLowerInvariant()} · {Host}:{Port}");
}

public sealed class ProxyInUseException : InvalidOperationException
{
    public ProxyInUseException() : base("The proxy is still assigned to accounts.")
    {
    }

    public ProxyInUseException(string message) : base(message)
    {
    }

    public ProxyInUseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Proxy pool and account assignment.</summary>
public sealed partial class Vault
{
    public IReadOnlyList<ProxyEntry> ListProxies()
    {
        using var connection = _db.Open();
        var assigned = connection.Query<(long ProxyId, long SteamId)>(
                "SELECT proxy_id, steam_id FROM accounts WHERE proxy_id IS NOT NULL ORDER BY sort_order, steam_id")
            .ToLookup(a => a.ProxyId, a => (ulong)a.SteamId);

        return connection.Query<ProxyRow>(
                "SELECT id AS Id, scheme AS Scheme, host AS Host, port AS Port, username AS Username, status AS Status, " +
                "latency_ms AS LatencyMs, last_error AS LastError FROM proxies ORDER BY id")
            .Select(r => new ProxyEntry(
                r.Id, ParseScheme(r.Scheme), r.Host, (int)r.Port, r.Username, ParseStatus(r.Status),
                r.LatencyMs is null ? null : (int)r.LatencyMs, r.LastError, [.. assigned[r.Id]]))
            .ToList();
    }

    /// <summary>Adds proxies, skipping ones already in the pool. Returns the number added.</summary>
    public int AddProxies(IEnumerable<ProxySettings> proxies)
    {
        ArgumentNullException.ThrowIfNull(proxies);
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();
        int added = 0;
        foreach (var proxy in proxies)
        {
            // UNIQUE treats NULL usernames as distinct, so check for duplicates explicitly.
            var key = new { scheme = SchemeName(proxy.Scheme), host = proxy.Host, port = proxy.Port, user = proxy.Username };
            if (connection.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM proxies WHERE scheme = @scheme AND host = @host AND port = @port AND username IS @user", key, tx) > 0)
            {
                continue;
            }

            var (secret, nonce) = ProtectPassword(proxy);
            added += connection.Execute(
                "INSERT INTO proxies(scheme, host, port, username, password_enc, password_nonce) " +
                "VALUES (@scheme, @host, @port, @user, @secret, @nonce)",
                new { key.scheme, key.host, key.port, key.user, secret, nonce },
                tx);
        }

        tx.Commit();
        return added;
    }

    /// <summary>The proxy with its password, ready to connect. Needs the vault unlocked if it has a password.</summary>
    public ProxySettings? GetProxySettings(long proxyId)
    {
        using var connection = _db.Open();
        var row = connection.QuerySingleOrDefault<ProxySecretRow>(
            "SELECT scheme AS Scheme, host AS Host, port AS Port, username AS Username, password_enc AS Secret, password_nonce AS Nonce " +
            "FROM proxies WHERE id = @proxyId", new { proxyId });
        if (row is null)
        {
            return null;
        }

        var settings = new ProxySettings(ParseScheme(row.Scheme), row.Host, (int)row.Port, row.Username);
        return settings with { Password = UnprotectPassword(settings, row.Secret, row.Nonce) };
    }

    /// <summary>Assigns a proxy to an account, or null for a direct connection.</summary>
    public void SetAccountProxy(ulong steamId, long? proxyId)
    {
        using var connection = _db.Open();
        connection.Execute(
            "UPDATE accounts SET proxy_id = @proxyId, updated_at = @now WHERE steam_id = @id",
            new { proxyId, id = (long)steamId, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    /// <summary>
    /// Removes a proxy. Throws <see cref="ProxyInUseException"/> if accounts still use it, unless the user
    /// explicitly chose to switch them to a direct connection.
    /// </summary>
    public void RemoveProxy(long proxyId, bool switchAccountsToDirect)
    {
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();
        long inUse = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts WHERE proxy_id = @proxyId", new { proxyId }, tx);
        if (inUse > 0 && !switchAccountsToDirect)
        {
            throw new ProxyInUseException();
        }

        connection.Execute("UPDATE accounts SET proxy_id = NULL WHERE proxy_id = @proxyId", new { proxyId }, tx);
        connection.Execute("DELETE FROM proxies WHERE id = @proxyId", new { proxyId }, tx);
        tx.Commit();
    }

    public void RecordProxyHealth(long proxyId, bool ok, int? latencyMs, string? error)
    {
        using var connection = _db.Open();
        connection.Execute(
            "UPDATE proxies SET status = @status, latency_ms = @latencyMs, last_error = @error, last_check_at = @now WHERE id = @proxyId",
            new { status = ok ? "ok" : "dead", latencyMs, error, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), proxyId });
    }

    private (byte[]? Secret, byte[]? Nonce) ProtectPassword(ProxySettings proxy)
    {
        if (string.IsNullOrEmpty(proxy.Password))
        {
            return (null, null);
        }

        byte[] plain = Encoding.UTF8.GetBytes(proxy.Password);
        if (!IsEncrypted)
        {
            return (plain, null);
        }

        var (nonce, cipher) = VaultCrypto.Seal(RequireKey(), plain, ProxyAad(proxy));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        return (cipher, nonce);
    }

    private string? UnprotectPassword(ProxySettings proxy, byte[]? secret, byte[]? nonce)
    {
        if (secret is null)
        {
            return null;
        }

        return nonce is null
            ? Encoding.UTF8.GetString(secret)
            : Encoding.UTF8.GetString(VaultCrypto.Open(RequireKey(), nonce, secret, ProxyAad(proxy)));
    }

    private static byte[] ProxyAad(ProxySettings proxy)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{SchemeName(proxy.Scheme)}:{proxy.Host}:{proxy.Port}:{proxy.Username}"));

    private static string SchemeName(ProxyScheme scheme) => scheme.ToString().ToLowerInvariant();

    private static ProxyScheme ParseScheme(string value) => Enum.Parse<ProxyScheme>(value, ignoreCase: true);

    private static ProxyStatus ParseStatus(string value) => value switch
    {
        "ok" => ProxyStatus.Ok,
        "dead" => ProxyStatus.Dead,
        _ => ProxyStatus.Unknown,
    };

    private sealed record ProxyRow(long Id, string Scheme, string Host, long Port, string? Username, string Status, long? LatencyMs, string? LastError);

    private sealed record ProxySecretRow(string Scheme, string Host, long Port, string? Username, byte[]? Secret, byte[]? Nonce);
}
