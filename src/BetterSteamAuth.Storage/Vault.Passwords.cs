using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BetterSteamAuth.Core.Security;
using Dapper;

namespace BetterSteamAuth.Storage;

/// <summary>Optional saved Steam passwords for automatic sign-in.</summary>
public sealed partial class Vault
{
    /// <summary>Saving a Steam password needs an app password: it's encrypted with the data key.</summary>
    public bool CanSaveSteamPasswords => IsEncrypted;

    public bool HasSteamPassword(ulong steamId)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM accounts WHERE steam_id = @id AND password_enc IS NOT NULL", new { id = (long)steamId }) > 0;
    }

    /// <summary>Saves the password encrypted, or deletes it when null.</summary>
    public void SetSteamPassword(ulong steamId, string? password)
    {
        byte[]? secret = null;
        byte[]? nonce = null;
        if (!string.IsNullOrEmpty(password))
        {
            if (!CanSaveSteamPasswords)
            {
                throw new InvalidOperationException("Steam passwords can only be saved when the app has a password.");
            }

            byte[] plain = Encoding.UTF8.GetBytes(password);
            (nonce, secret) = VaultCrypto.Seal(RequireKey(), plain, SteamPasswordAad(steamId));
            CryptographicOperations.ZeroMemory(plain);
        }

        using var connection = _db.Open();
        connection.Execute(
            "UPDATE accounts SET password_enc = @secret, password_nonce = @nonce, updated_at = @now WHERE steam_id = @id",
            new { secret, nonce, id = (long)steamId, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    /// <summary>The saved password, or null. Needs the vault unlocked.</summary>
    public string? GetSteamPassword(ulong steamId)
    {
        using var connection = _db.Open();
        var row = connection.QuerySingleOrDefault<(byte[]? Secret, byte[]? Nonce)>(
            "SELECT password_enc, password_nonce FROM accounts WHERE steam_id = @id", new { id = (long)steamId });
        if (row.Secret is null || row.Nonce is null)
        {
            return null;
        }

        byte[] plain = VaultCrypto.Open(RequireKey(), row.Nonce, row.Secret, SteamPasswordAad(steamId));
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] SteamPasswordAad(ulong steamId)
        => Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"steam-password:{steamId}"));
}
