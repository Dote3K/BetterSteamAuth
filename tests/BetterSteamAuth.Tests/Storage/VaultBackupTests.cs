using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Security;
using BetterSteamAuth.Storage;

namespace BetterSteamAuth.Tests.Storage;

public sealed class VaultBackupTests : IDisposable
{
    private static readonly KdfParameters FastKdf = new(MemoryKiB: 1024, Iterations: 1, Parallelism: 1);
    private const ulong First = 76561198000000001;
    private const ulong Second = 76561198000000002;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bsa-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_backup_restores_into_another_app_adding_only_missing_accounts()
    {
        string backup = Path.Combine(_root, "test" + VaultBackup.Extension);
        using (var source = Vault.Create(Path.Combine(_root, "source"), "old-pw", FastKdf))
        {
            source.AddProxies([new ProxySettings(ProxyScheme.Http, "10.0.0.2", 8080, "fake-user", "fake-proxy-pw")]);
            long proxyId = source.ListProxies().Single().Id;
            source.Add(NewMaFile(First, "first", "aaa="), proxyId);
            source.Add(NewMaFile(Second, "second", "bbb="));
            source.SetAccountGroup(First, "farm-1");
            source.SetAccountNote(First, "note");
            source.SetSteamPassword(First, "fake-steam-password");
            source.Remove(Second);

            var manifest = VaultBackup.Create(source, backup, newPassword: null, appVersion: "test");
            Assert.Equal((1, true), (manifest.Accounts, manifest.Encrypted));
        }

        using var target = Vault.Create(Path.Combine(_root, "target"), "new-pw", FastKdf);
        target.Add(NewMaFile(Second, "second-here", "ccc="));
        using var opened = VaultBackup.Open(backup);
        Assert.Throws<WrongPasswordException>(() => opened.Vault.Unlock("new-pw"));
        opened.Vault.Unlock("old-pw");

        Assert.Equal(1, target.MergeFrom(opened.Vault));
        Assert.Equal("aaa=", target.Load(First).SharedSecret);
        Assert.Equal("ccc=", target.Load(Second).SharedSecret);
        var entry = target.ListAccounts().Single(a => a.SteamId == First);
        Assert.Equal(("farm-1", "note"), (target.GetGroupName(entry.GroupId), entry.Note));
        Assert.Equal("fake-proxy-pw", target.GetProxySettings(entry.ProxyId!.Value)?.Password);
        Assert.Equal("fake-steam-password", target.GetSteamPassword(First));
    }

    [Fact]
    public void A_backup_of_an_app_without_a_password_can_be_protected_with_a_new_one()
    {
        string backup = Path.Combine(_root, "plain" + VaultBackup.Extension);
        using (var source = Vault.Create(Path.Combine(_root, "source"), password: null))
        {
            source.Add(NewMaFile(First, "first", "aaa="));
            Assert.True(VaultBackup.Create(source, backup, "backup-pw", appVersion: null, FastKdf).Encrypted);
        }

        using var opened = VaultBackup.Open(backup);
        Assert.True(opened.Vault.IsEncrypted);
        Assert.Throws<VaultLockedException>(() => opened.Vault.Load(First));
        opened.Vault.Unlock("backup-pw");
        Assert.Equal("aaa=", opened.Vault.Load(First).SharedSecret);
    }

    private static MaFile NewMaFile(ulong steamId, string name, string sharedSecret)
        => new() { AccountName = name, SharedSecret = sharedSecret, Session = new MaFileSession { SteamId = steamId } };
}
