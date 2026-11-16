using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterSteamAuth.Storage;

/// <summary>The target folder already holds SDA data; exports never overwrite a manifest.</summary>
public sealed class SdaFolderNotEmptyException : IOException
{
    public SdaFolderNotEmptyException() : base("This folder already has SDA data.")
    {
    }

    public SdaFolderNotEmptyException(string message) : base(message)
    {
    }

    public SdaFolderNotEmptyException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Writes accounts as an SDA <c>maFiles</c> folder that SDA and ASF can read: plain maFiles, or encrypted the
/// way SDA does with a passkey. Only Steam data goes out.
/// </summary>
public static class SdaExporter
{
    public const string FolderName = "maFiles";

    // SDA's FileEncryptor: PBKDF2-SHA1 (50k iterations, 32-byte key), 8-byte salt, AES-256-CBC/PKCS7, 16-byte IV.
    private const int Iterations = 50_000;
    private const int KeySize = 32;
    private const int SaltSize = 8;
    private const int IvSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>
    /// Exports the accounts into <c>{folder}\maFiles</c>. Returns that folder. Throws
    /// <see cref="SdaFolderNotEmptyException"/> when it already has a <c>manifest.json</c>.
    /// </summary>
    public static string Export(Vault vault, IEnumerable<ulong> steamIds, string folder, string? passkey)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(steamIds);
        string target = Path.Combine(folder, FolderName);
        string manifestPath = Path.Combine(target, SdaImporter.ManifestFileName);
        if (File.Exists(manifestPath))
        {
            throw new SdaFolderNotEmptyException();
        }

        Directory.CreateDirectory(target);
        bool encrypt = !string.IsNullOrEmpty(passkey);
        var entries = new List<ManifestEntry>();
        foreach (ulong steamId in steamIds)
        {
            string fileName = steamId.ToString(CultureInfo.InvariantCulture) + ".maFile";
            string json = vault.Load(steamId).ToJson();
            string? salt = null;
            string? iv = null;
            if (encrypt)
            {
                byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
                byte[] ivBytes = RandomNumberGenerator.GetBytes(IvSize);
                json = Encrypt(passkey!, saltBytes, ivBytes, json);
                salt = Convert.ToBase64String(saltBytes);
                iv = Convert.ToBase64String(ivBytes);
            }

            File.WriteAllText(Path.Combine(target, fileName), json);
            entries.Add(new ManifestEntry(iv, salt, fileName, steamId));
        }

        var manifest = new Manifest(encrypt, FirstRun: false, entries, PeriodicChecking: false, PeriodicCheckingInterval: 5,
            PeriodicCheckingCheckAll: false, AutoConfirmMarket: false, AutoConfirmTrades: false);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        return target;
    }

    private static string Encrypt(string passkey, byte[] salt, byte[] iv, string json)
    {
#pragma warning disable CA5379 // SDA's format uses PBKDF2-SHA1; exported files have to match it.
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(passkey, salt, Iterations, HashAlgorithmName.SHA1, KeySize);
#pragma warning restore CA5379
        byte[] plain = Encoding.UTF8.GetBytes(json);
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return Convert.ToBase64String(aes.EncryptCbc(plain, iv));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private sealed record Manifest(
        [property: JsonPropertyName("encrypted")] bool Encrypted,
        [property: JsonPropertyName("first_run")] bool FirstRun,
        [property: JsonPropertyName("entries")] List<ManifestEntry> Entries,
        [property: JsonPropertyName("periodic_checking")] bool PeriodicChecking,
        [property: JsonPropertyName("periodic_checking_interval")] int PeriodicCheckingInterval,
        [property: JsonPropertyName("periodic_checking_checkall")] bool PeriodicCheckingCheckAll,
        [property: JsonPropertyName("auto_confirm_market_transactions")] bool AutoConfirmMarket,
        [property: JsonPropertyName("auto_confirm_trades")] bool AutoConfirmTrades);

    private sealed record ManifestEntry(
        [property: JsonPropertyName("encryption_iv")] string? Iv,
        [property: JsonPropertyName("encryption_salt")] string? Salt,
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("steamid")] ulong SteamId);
}
