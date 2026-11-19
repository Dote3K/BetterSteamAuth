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
    public void Remembered_key_unlocks_only_its_own_vault()
    {
        byte[] key;
        using (var created = Vault.Create(_folder, "pw", FastKdf))
        {
            created.Add(FakeMaFile());
            key = created.ExportDataKey();
        }

        using var vault = Vault.Open(_folder);
        Assert.False(vault.TryUnlockWithKey(VaultCrypto.NewDataKey()));
        Assert.False(vault.IsUnlocked);
        Assert.True(vault.TryUnlockWithKey(key));
        Assert.Equal("fake-refresh", vault.Load(SteamId).Session?.RefreshToken);
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

    // Changing the password re-wraps the same data key: the old password stops working, a remembered key keeps working.
    [Fact]
    public void Changing_the_password_keeps_the_data_and_the_remembered_key()
    {
        byte[] remembered;
        using (var vault = Vault.Create(_folder, "old-password", FastKdf))
        {
            vault.Add(FakeMaFile());
            remembered = vault.ExportDataKey();
            Assert.Throws<WrongPasswordException>(() => vault.ChangePassword("not-it", "new-password"));
            vault.ChangePassword("old-password", "new-password");
        }

        using (var reopened = Vault.Open(_folder))
        {
            Assert.Throws<WrongPasswordException>(() => reopened.Unlock("old-password"));
            reopened.Unlock("new-password");
            Assert.NotNull(reopened.Load(SteamId).SharedSecret);
        }

        using var withKey = Vault.Open(_folder);
        Assert.True(withKey.TryUnlockWithKey(remembered));
    }

    // Data from an older version: pending migrations run, but only after a copy of vault.db is kept.
    [Fact]
    public void Migrating_older_data_keeps_a_copy_from_before()
    {
        using (var created = Vault.Create(_folder, password: null))
        {
            created.Add(FakeMaFile());
        }

        // Turn it back into a schema-2 database, as an app from before migration 003 left it.
        Sql("DROP TABLE removed_accounts; PRAGMA user_version = 2;");

        using (var opened = Vault.Open(_folder))
        {
            Assert.Single(opened.ListAccounts());
        }

        string copy = Path.Combine(_folder, ".backup", "vault.db.before-003");
        Assert.True(File.Exists(copy));
        Assert.Equal(2L, UserVersion(copy));
        Assert.Equal(3L, UserVersion(Path.Combine(_folder, "vault.db")));
    }

    // Data from a newer version: refused, and left exactly as it was.
    [Fact]
    public void Opening_data_from_a_newer_version_is_refused_without_changes()
    {
        Vault.Create(_folder, password: null).Dispose();
        Sql("PRAGMA user_version = 99;");

        var ex = Assert.Throws<VaultTooNewException>(() => Vault.Open(_folder));

        Assert.Equal(99L, ex.DataVersion);
        Assert.Equal(99L, UserVersion(Path.Combine(_folder, "vault.db")));
        Assert.False(Directory.Exists(Path.Combine(_folder, ".backup")));
    }

    private void Sql(string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_folder, "vault.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long UserVersion(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }
}
