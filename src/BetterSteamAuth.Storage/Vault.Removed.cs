using System.Globalization;
using System.Text;
using System.Text.Json;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Devices;
using BetterSteamAuth.Core.Security;
using Dapper;

namespace BetterSteamAuth.Storage;

/// <summary>Why an account left the app.</summary>
public enum RemovalReason
{
    /// <summary>Remove from app.</summary>
    Removed,

    /// <summary>An unfinished link was cancelled or started over.</summary>
    LinkCancelled,

    /// <summary>The authenticator was removed on Steam; the maFile's codes no longer work.</summary>
    AuthenticatorRemoved,
}

/// <summary>A maFile in <c>.trash/</c>, with or without the metadata saved when it was removed.</summary>
/// <param name="Id">The <c>removed_accounts</c> row; null for trash files from before it existed.</param>
/// <param name="AccountName">From the row or, in encrypted vaults, from the file; null if unreadable.</param>
/// <param name="CanRead">False when the file can't be decrypted with this app's key.</param>
public sealed record RemovedEntry(
    long? Id, ulong SteamId, string? AccountName, string TrashFile, RemovalReason Reason, DateTimeOffset RemovedAt, bool CanRead);

/// <summary>Removed accounts: the trash, with what's needed to restore them.</summary>
public sealed partial class Vault
{
    /// <summary>Everything in <c>.trash/</c>, newest first. Needs the vault unlocked to read names in encrypted vaults.</summary>
    public IReadOnlyList<RemovedEntry> ListRemoved()
    {
        string trash = Path.Combine(Folder, TrashFolder);
        using var connection = _db.Open();
        var rows = connection.Query<RemovedRow>(
            "SELECT id AS Id, steam_id AS SteamId, account_name AS AccountName, trash_file AS TrashFile, reason AS Reason, removed_at AS RemovedAt " +
            "FROM removed_accounts").ToDictionary(r => r.TrashFile, StringComparer.OrdinalIgnoreCase);

        var result = new List<RemovedEntry>();
        if (!Directory.Exists(trash))
        {
            return result;
        }

        foreach (string path in Directory.EnumerateFiles(trash))
        {
            string file = Path.GetFileName(path);
            if (!TryParseTrashName(file, out ulong steamId, out long stamp))
            {
                continue;
            }

            var maFile = TryReadMaFile(path, steamId);
            if (rows.TryGetValue(file, out var row))
            {
                result.Add(new RemovedEntry(row.Id, steamId, row.AccountName ?? maFile?.AccountName, file, ParseReason(row.Reason),
                    DateTimeOffset.FromUnixTimeSeconds(row.RemovedAt), maFile is not null));
            }
            else
            {
                result.Add(new RemovedEntry(null, steamId, maFile?.AccountName, file, RemovalReason.Removed,
                    DateTimeOffset.FromUnixTimeMilliseconds(stamp), maFile is not null));
            }
        }

        return result.OrderByDescending(r => r.RemovedAt).ToList();
    }

    /// <summary>
    /// Puts a removed account back: the maFile (re-written for this app's current encryption), its proxy if
    /// still in the pool, group, note, saved password, and device profile.
    /// </summary>
    public void RestoreRemoved(RemovedEntry removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        if (Contains(removed.SteamId))
        {
            throw new DuplicateAccountException(removed.SteamId);
        }

        string trashPath = Path.Combine(Folder, TrashFolder, removed.TrashFile);
        var maFile = TryReadMaFile(trashPath, removed.SteamId)
                     ?? throw new InvalidDataException("This removed account can't be read.");
        maFile.Session ??= new MaFileSession();
        if (maFile.Session.SteamId == 0)
        {
            maFile.Session.SteamId = removed.SteamId;
        }

        using var connection = _db.Open();
        var row = removed.Id is { } id
            ? connection.QuerySingleOrDefault<RemovedDetailsRow>(
                "SELECT proxy_id AS ProxyId, group_name AS GroupName, note AS Note, password_enc AS PasswordSecret, " +
                "password_nonce AS PasswordNonce, device_profile AS DeviceProfile FROM removed_accounts WHERE id = @id", new { id })
            : null;

        WriteMaFile(removed.SteamId, maFile);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var tx = connection.BeginTransaction())
        {
            long? groupId = null;
            if (!string.IsNullOrWhiteSpace(row?.GroupName))
            {
                connection.Execute("INSERT INTO groups(name) VALUES (@name) ON CONFLICT(name) DO NOTHING", new { name = row.GroupName }, tx);
                groupId = connection.ExecuteScalar<long>("SELECT id FROM groups WHERE name = @name", new { name = row.GroupName }, tx);
            }

            connection.Execute(
                "INSERT INTO accounts(steam_id, account_name, filename, proxy_id, group_id, note, password_enc, password_nonce, created_at, updated_at) " +
                "VALUES (@id, @name, @file, (SELECT id FROM proxies WHERE id = @proxyId), @groupId, @note, @secret, @nonce, @now, @now)",
                new
                {
                    id = (long)removed.SteamId, name = IsEncrypted ? null : maFile.AccountName, file = FileNameFor(removed.SteamId),
                    proxyId = row?.ProxyId, groupId, note = row?.Note, secret = row?.PasswordSecret, nonce = row?.PasswordNonce, now,
                },
                tx);
            if (row?.DeviceProfile is { } json && JsonSerializer.Deserialize<DeviceProfile>(json) is { } p)
            {
                connection.Execute(
                    "INSERT INTO device_profiles(steam_id, model, friendly_name, android_version, build_id, machine_id, created_at) " +
                    "VALUES (@id, @Model, @FriendlyName, @AndroidVersion, @BuildId, @MachineId, @now)",
                    new { id = (long)removed.SteamId, p.Model, p.FriendlyName, p.AndroidVersion, p.BuildId, p.MachineId, now },
                    tx);
            }

            if (removed.Id is { } removedId)
            {
                connection.Execute("DELETE FROM removed_accounts WHERE id = @removedId", new { removedId }, tx);
            }

            tx.Commit();
        }

        File.Delete(trashPath);
    }

    /// <summary>Deletes a removed account's maFile for good. The only hard delete of a maFile, and only on request.</summary>
    public void DeleteRemoved(RemovedEntry removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        string trashPath = Path.Combine(Folder, TrashFolder, removed.TrashFile);
        File.Delete(trashPath);
        using var connection = _db.Open();
        connection.Execute("DELETE FROM removed_accounts WHERE trash_file = @file", new { file = removed.TrashFile });

        // History stays while the account can be restored; once it's gone for good (and not back), so is its history.
        connection.Execute(
            "DELETE FROM confirmation_log WHERE steam_id = @id AND NOT EXISTS (SELECT 1 FROM accounts WHERE steam_id = @id) " +
            "AND NOT EXISTS (SELECT 1 FROM removed_accounts WHERE steam_id = @id)",
            new { id = (long)removed.SteamId });
    }

    /// <summary>Saves what Restore needs, next to the trash file. Called by <see cref="Remove"/>.</summary>
    private static long RecordRemoved(
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction tx, ulong steamId, AccountEntry? entry,
        string trashFile, RemovalReason reason, DeviceProfile? profile, byte[]? passwordSecret, byte[]? passwordNonce)
    {
        return connection.ExecuteScalar<long>(
            "INSERT INTO removed_accounts(steam_id, account_name, trash_file, reason, removed_at, proxy_id, group_name, note, password_enc, password_nonce, device_profile) " +
            "VALUES (@id, @name, @file, @reason, @now, @proxyId, (SELECT name FROM groups WHERE id = @groupId), @note, @secret, @nonce, @profile); " +
            "SELECT last_insert_rowid();",
            new
            {
                id = (long)steamId, name = entry?.AccountName, file = trashFile, reason = ReasonText(reason), now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                proxyId = entry?.ProxyId, groupId = entry?.GroupId, note = entry?.Note, secret = passwordSecret, nonce = passwordNonce,
                profile = profile is null ? null : JsonSerializer.Serialize(profile),
            },
            tx);
    }

    /// <summary>Reads a maFile at any path (plain or encrypted with this app's key); null if it can't be read.</summary>
    private MaFile? TryReadMaFile(string path, ulong steamId)
    {
        try
        {
            string json = File.ReadAllText(path);
            var envelope = MaFileEnvelope.TryParse(json);
            if (envelope is null)
            {
                return MaFile.Parse(json);
            }

            if (_dataKey is null)
            {
                return null;
            }

            byte[] plaintext = envelope.Decrypt(_dataKey, steamId);
            try
            {
                return MaFile.Parse(Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or FormatException
                                       or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Trash files are named <c>{steamid}.maFile.{unix ms}</c>.</summary>
    private static bool TryParseTrashName(string file, out ulong steamId, out long stamp)
    {
        steamId = 0;
        stamp = 0;
        string[] parts = file.Split('.');
        return parts.Length == 3
               && string.Equals(parts[1], "maFile", StringComparison.OrdinalIgnoreCase)
               && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out steamId)
               && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out stamp);
    }

    private static string ReasonText(RemovalReason reason) => reason switch
    {
        RemovalReason.LinkCancelled => "link_cancelled",
        RemovalReason.AuthenticatorRemoved => "authenticator_removed",
        _ => "removed",
    };

    private static RemovalReason ParseReason(string text) => text switch
    {
        "link_cancelled" => RemovalReason.LinkCancelled,
        "authenticator_removed" => RemovalReason.AuthenticatorRemoved,
        _ => RemovalReason.Removed,
    };

    private sealed record RemovedRow(long Id, long SteamId, string? AccountName, string TrashFile, string Reason, long RemovedAt);

    private sealed record RemovedDetailsRow(long? ProxyId, string? GroupName, string? Note, byte[]? PasswordSecret, byte[]? PasswordNonce, string? DeviceProfile);
}
