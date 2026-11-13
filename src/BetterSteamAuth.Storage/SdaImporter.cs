using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Security;

namespace BetterSteamAuth.Storage;

public enum ImportStatus
{
    Ready,
    AlreadyAdded,
    Unreadable,
}

/// <summary>One maFile found during import, before anything is written.</summary>
public sealed record ImportCandidate(string SourcePath, ImportStatus Status, MaFile? MaFile, string? Problem)
{
    public ulong? SteamId => MaFile?.SteamId;

    public string DisplayName => MaFile?.AccountName ?? Path.GetFileName(SourcePath);
}

/// <summary>
/// Reads SDA maFiles folders (plain or passkey-encrypted) and loose maFiles.
/// Sources are only read, never modified.
/// </summary>
public static class SdaImporter
{
    public const string ManifestFileName = "manifest.json";

    // SDA's FileEncryptor: PBKDF2-SHA1 (50k iterations, 32-byte key) + AES-256-CBC/PKCS7.
    private const int SdaIterations = 50_000;
    private const int SdaKeySize = 32;

    public static bool IsSdaFolder(string folder) => File.Exists(Path.Combine(folder, ManifestFileName));

    public static bool IsEncrypted(string folder) => ReadManifest(folder).Encrypted;

    /// <summary>
    /// Reads every account of an SDA folder. Throws <see cref="WrongPasswordException"/>
    /// when the folder is encrypted and the passkey is missing or wrong.
    /// </summary>
    public static IReadOnlyList<ImportCandidate> ReadFolder(string folder, string? passkey, Vault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var manifest = ReadManifest(folder);
        if (manifest.Encrypted && string.IsNullOrEmpty(passkey))
        {
            throw new WrongPasswordException("This SDA folder is encrypted. Enter its passkey.");
        }

        var candidates = new List<ImportCandidate>();
        int decrypted = 0;
        foreach (var entry in manifest.Entries)
        {
            string path = Path.Combine(folder, entry.Filename);
            if (!File.Exists(path))
            {
                candidates.Add(new ImportCandidate(path, ImportStatus.Unreadable, null, "File not found"));
                continue;
            }

            string content = File.ReadAllText(path);
            if (manifest.Encrypted)
            {
                string? plain = DecryptSda(passkey!, entry.Salt, entry.Iv, content);
                if (plain is null)
                {
                    candidates.Add(new ImportCandidate(path, ImportStatus.Unreadable, null, "Can't decrypt file"));
                    continue;
                }

                decrypted++;
                content = plain;
            }

            candidates.Add(Classify(path, content, vault));
        }

        if (manifest.Encrypted && decrypted == 0 && manifest.Entries.Count > 0)
        {
            throw new WrongPasswordException("Wrong passkey.");
        }

        return candidates;
    }

    /// <summary>Reads loose maFiles. SDA-encrypted files can't be read without their manifest.</summary>
    public static IReadOnlyList<ImportCandidate> ReadFiles(IEnumerable<string> paths, Vault vault)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(vault);

        return paths.Select(path =>
        {
            string content = File.ReadAllText(path);
            return content.TrimStart().StartsWith('{')
                ? Classify(path, content, vault)
                : new ImportCandidate(path, ImportStatus.Unreadable, null, "Encrypted by SDA. Import the SDA folder instead");
        }).ToList();
    }

    /// <summary>Adds every <see cref="ImportStatus.Ready"/> candidate. Returns the number added.</summary>
    public static int Import(Vault vault, IEnumerable<ImportCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(candidates);

        int added = 0;
        foreach (var candidate in candidates.Where(c => c.Status == ImportStatus.Ready && c.MaFile is not null))
        {
            vault.Add(candidate.MaFile!);
            added++;
        }

        return added;
    }

    private static ImportCandidate Classify(string path, string json, Vault vault)
    {
        MaFile maFile;
        try
        {
            maFile = MaFile.Parse(json);
        }
        catch (JsonException)
        {
            return new ImportCandidate(path, ImportStatus.Unreadable, null, "Can't read file");
        }

        if (maFile.SteamId is not { } steamId || string.IsNullOrEmpty(maFile.SharedSecret))
        {
            return new ImportCandidate(path, ImportStatus.Unreadable, maFile, "Not a Steam Guard maFile");
        }

        return vault.Contains(steamId)
            ? new ImportCandidate(path, ImportStatus.AlreadyAdded, maFile, "Already added")
            : new ImportCandidate(path, ImportStatus.Ready, maFile, null);
    }

    private static string? DecryptSda(string passkey, string? salt, string? iv, string base64)
    {
        if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(iv))
        {
            return null;
        }

        try
        {
#pragma warning disable CA5379 // SDA's format uses PBKDF2-SHA1; we only read it.
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(passkey, Convert.FromBase64String(salt), SdaIterations, HashAlgorithmName.SHA1, SdaKeySize);
#pragma warning restore CA5379
            using var aes = Aes.Create();
            aes.Key = key;
            CryptographicOperations.ZeroMemory(key);
            byte[] plain = aes.DecryptCbc(Convert.FromBase64String(base64.Trim()), Convert.FromBase64String(iv));
            string json = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return json.TrimStart().StartsWith('{') ? json : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static SdaManifest ReadManifest(string folder)
    {
        string json = File.ReadAllText(Path.Combine(folder, ManifestFileName));
        return JsonSerializer.Deserialize<SdaManifest>(json) ?? new SdaManifest();
    }

    private sealed class SdaManifest
    {
        [JsonPropertyName("encrypted")] public bool Encrypted { get; set; }
        [JsonPropertyName("entries")] public List<SdaManifestEntry> Entries { get; set; } = [];
    }

    private sealed class SdaManifestEntry
    {
        [JsonPropertyName("encryption_iv")] public string? Iv { get; set; }
        [JsonPropertyName("encryption_salt")] public string? Salt { get; set; }
        [JsonPropertyName("filename")] public string Filename { get; set; } = "";
        [JsonPropertyName("steamid")] public ulong SteamId { get; set; }
    }
}
