using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Security;
using BetterSteamAuth.Storage;

namespace BetterSteamAuth.Tests.Storage;

public sealed class SdaImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bsa-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Encrypted_SDA_folder_imports_with_passkey_and_flags_duplicates()
    {
        string sda = Path.Combine(_root, "sda");
        WriteEncryptedSdaFolder(sda, "passkey", [76561198000000001, 76561198000000002]);
        using var vault = Vault.Create(Path.Combine(_root, "vault"), password: null);
        vault.Add(FakeMaFile(76561198000000002));

        Assert.Throws<WrongPasswordException>(() => SdaImporter.ReadFolder(sda, "wrong", vault));

        var candidates = SdaImporter.ReadFolder(sda, "passkey", vault);
        Assert.Equal([ImportStatus.Ready, ImportStatus.AlreadyAdded], candidates.Select(c => c.Status));

        Assert.Equal(1, SdaImporter.Import(vault, candidates));
        Assert.Equal(2, vault.ListAccounts().Count);
    }

    private static MaFile FakeMaFile(ulong steamId) => new()
    {
        AccountName = $"acc_{steamId % 100}",
        SharedSecret = "bTvsR2DJ3XG8ZXOoMZ4LXsbbOAI=",
        Session = new MaFileSession { SteamId = steamId },
    };

    // Writes files the way SDA's FileEncryptor does: PBKDF2-SHA1 50k + AES-256-CBC, base64.
    private static void WriteEncryptedSdaFolder(string folder, string passkey, ulong[] steamIds)
    {
        Directory.CreateDirectory(folder);
        var entries = new List<object>();
        foreach (ulong id in steamIds)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(8);
            byte[] iv = RandomNumberGenerator.GetBytes(16);
            using var aes = Aes.Create();
            aes.Key = Rfc2898DeriveBytes.Pbkdf2(passkey, salt, 50_000, HashAlgorithmName.SHA1, 32);
            byte[] cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(FakeMaFile(id).ToJson()), iv);

            File.WriteAllText(Path.Combine(folder, $"{id}.maFile"), Convert.ToBase64String(cipher));
            entries.Add(new Dictionary<string, object>
            {
                ["encryption_iv"] = Convert.ToBase64String(iv),
                ["encryption_salt"] = Convert.ToBase64String(salt),
                ["filename"] = $"{id}.maFile",
                ["steamid"] = id,
            });
        }

        File.WriteAllText(
            Path.Combine(folder, SdaImporter.ManifestFileName),
            JsonSerializer.Serialize(new Dictionary<string, object> { ["encrypted"] = true, ["entries"] = entries }));
    }
}
