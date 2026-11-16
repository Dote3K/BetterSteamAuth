using BetterSteamAuth.Core.Security;
using Dapper;

namespace BetterSteamAuth.Storage;

/// <summary>Groups, notes and password checks.</summary>
public sealed partial class Vault
{
    public IReadOnlyList<string> ListGroups()
    {
        using var connection = _db.Open();
        return connection.Query<string>("SELECT name FROM groups ORDER BY name COLLATE NOCASE").ToList();
    }

    public string? GetGroupName(long? groupId)
    {
        if (groupId is null)
        {
            return null;
        }

        using var connection = _db.Open();
        return connection.ExecuteScalar<string?>("SELECT name FROM groups WHERE id = @groupId", new { groupId });
    }

    /// <summary>Puts the account in a group by name, creating the group if needed. Null or blank means no group.</summary>
    public void SetAccountGroup(ulong steamId, string? groupName)
    {
        string? name = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();
        long? groupId = null;
        if (name is not null)
        {
            connection.Execute("INSERT INTO groups(name) VALUES (@name) ON CONFLICT(name) DO NOTHING", new { name }, tx);
            groupId = connection.ExecuteScalar<long>("SELECT id FROM groups WHERE name = @name", new { name }, tx);
        }

        connection.Execute(
            "UPDATE accounts SET group_id = @groupId, updated_at = @now WHERE steam_id = @id",
            new { groupId, id = (long)steamId, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, tx);
        connection.Execute("DELETE FROM groups WHERE id NOT IN (SELECT group_id FROM accounts WHERE group_id IS NOT NULL)", transaction: tx);
        tx.Commit();
    }

    public void SetAccountNote(ulong steamId, string? note)
    {
        using var connection = _db.Open();
        connection.Execute(
            "UPDATE accounts SET note = @note, updated_at = @now WHERE steam_id = @id",
            new { note = string.IsNullOrWhiteSpace(note) ? null : note, id = (long)steamId, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    /// <summary>Checks the app password without changing the lock state (for revealing secrets). Runs the KDF, so it's slow.</summary>
    public bool VerifyPassword(string password)
    {
        if (!IsEncrypted)
        {
            return true;
        }

        using var connection = _db.Open();
        try
        {
            byte[] key = VaultCrypto.UnwrapDataKey(ReadWrappedKey(connection), password);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            return true;
        }
        catch (WrongPasswordException)
        {
            return false;
        }
    }
}
