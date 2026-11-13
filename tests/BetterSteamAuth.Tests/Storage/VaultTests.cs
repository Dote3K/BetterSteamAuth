using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Security;
using BetterSteamAuth.Storage;

namespace BetterSteamAuth.Tests.Storage;

public sealed class VaultTests : IDisposable
{
    private static readonly KdfParameters FastKdf = new(MemoryKiB: 1024, Iterations: 1, Parallelism: 1);
    private const ulong SteamId = 76561198000000001;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bsa-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private static MaFile FakeMaFile() => new()
    {
        AccountName = "fake_account",
        SharedSecret = "bTvsR2DJ3XG8ZXOoMZ4LXsbbOAI=",
        IdentitySecret = "V2hhdEFuSWRlbnRpdHlTZWNyZXQ9",
        Session = new MaFileSession { SteamId = SteamId, RefreshToken = "fake-refresh" },
    };

    [Fact]
    public void Encrypted_vault_keeps_secrets_off_disk_and_needs_the_password()
    {
        using (var created = Vault.Create(_folder, "pw", FastKdf))
        {
            created.Add(FakeMaFile());
        }

        string onDisk = File.ReadAllText(Path.Combine(_folder, $"{SteamId}.maFile"));
        Assert.DoesNotContain("bTvsR2DJ3XG8ZXOoMZ4LXsbbOAI=", onDisk, StringComparison.Ordinal);

        using var vault = Vault.Open(_folder);
        Assert.True(vault.IsEncrypted);
        Assert.Throws<VaultLockedException>(() => vault.Load(SteamId));
        Assert.Throws<WrongPasswordException>(() => vault.Unlock("nope"));

        vault.Unlock("pw");
        Assert.Equal("bTvsR2DJ3XG8ZXOoMZ4LXsbbOAI=", vault.Load(SteamId).SharedSecret);
        Assert.Null(vault.ListAccounts().Single().AccountName);
    }

    [Fact]
    public void ChangePassword_keeps_files_readable_with_the_new_password_only()
    {
        using (var created = Vault.Create(_folder, "old", FastKdf))
        {
            created.Add(FakeMaFile());
            created.ChangePassword("old", "new");
        }

        using var vault = Vault.Open(_folder);
        Assert.Throws<WrongPasswordException>(() => vault.Unlock("old"));
        vault.Unlock("new");
        Assert.Equal("fake-refresh", vault.Load(SteamId).Session?.RefreshToken);
    }

    [Fact]
    public void Remove_moves_maFile_to_trash_instead_of_deleting_it()
    {
        using var vault = Vault.Create(_folder, password: null);
        vault.Add(FakeMaFile());

        vault.Remove(SteamId);

        Assert.Empty(vault.ListAccounts());
        Assert.False(File.Exists(Path.Combine(_folder, $"{SteamId}.maFile")));
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, ".trash")));
    }

    [Fact]
    public void Plain_maFile_roundtrip_keeps_unknown_SDA_fields()
    {
        const string sda = """
            {"shared_secret":"abc=","account_name":"x","phone_number_hint":"12","Session":{"SteamID":76561198000000001,"WebCookie":"w"}}
            """;
        using var vault = Vault.Create(_folder, password: null);
        vault.Add(MaFile.Parse(sda));

        string saved = File.ReadAllText(Path.Combine(_folder, $"{SteamId}.maFile"));

        Assert.Contains("\"phone_number_hint\":\"12\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"WebCookie\":\"w\"", saved, StringComparison.Ordinal);
    }
}
