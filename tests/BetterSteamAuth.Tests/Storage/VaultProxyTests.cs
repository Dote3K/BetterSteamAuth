using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Security;
using BetterSteamAuth.Storage;

namespace BetterSteamAuth.Tests.Storage;

public sealed class VaultProxyTests : IDisposable
{
    private static readonly KdfParameters FastKdf = new(MemoryKiB: 1024, Iterations: 1, Parallelism: 1);
    private const ulong SteamId = 76561198000000001;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bsa-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Proxy_passwords_are_encrypted_and_duplicates_skipped()
    {
        using var vault = Vault.Create(_folder, "pw", FastKdf);
        var withLogin = new ProxySettings(ProxyScheme.Socks5, "10.0.0.1", 1080, "user", "secret-pass");
        var noLogin = new ProxySettings(ProxyScheme.Http, "10.0.0.2", 3128);

        Assert.Equal(2, vault.AddProxies([withLogin, noLogin, noLogin]));
        Assert.Equal(0, vault.AddProxies([noLogin]));

        // The DB is open in WAL mode, so check both files without locking them.
        foreach (string file in Directory.GetFiles(_folder, "vault.db*"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Assert.Equal(-1, memory.ToArray().AsSpan().IndexOf("secret-pass"u8));
        }

        long id = vault.ListProxies().Single(p => p.Username == "user").Id;
        Assert.Equal(withLogin, vault.GetProxySettings(id));
    }

    [Fact]
    public void Removing_a_proxy_in_use_needs_an_explicit_switch_to_direct()
    {
        using var vault = Vault.Create(_folder, password: null);
        vault.Add(new MaFile { SharedSecret = "abc=", Session = new MaFileSession { SteamId = SteamId } });
        vault.AddProxies([new ProxySettings(ProxyScheme.Socks5, "10.0.0.1", 1080)]);
        long id = vault.ListProxies().Single().Id;
        vault.SetAccountProxy(SteamId, id);

        Assert.Throws<ProxyInUseException>(() => vault.RemoveProxy(id, switchAccountsToDirect: false));

        vault.RemoveProxy(id, switchAccountsToDirect: true);
        Assert.Empty(vault.ListProxies());
        Assert.Null(vault.ListAccounts().Single().ProxyId);
    }

    [Fact]
    public void Editing_a_proxy_keeps_its_accounts_and_rejects_duplicates()
    {
        using var vault = Vault.Create(_folder, "pw", FastKdf);
        vault.Add(new MaFile { SharedSecret = "abc=", Session = new MaFileSession { SteamId = SteamId } });
        vault.AddProxies([new ProxySettings(ProxyScheme.Socks5, "10.0.0.1", 1080), new ProxySettings(ProxyScheme.Http, "10.0.0.2", 3128)]);
        long id = vault.ListProxies()[0].Id;
        vault.SetAccountProxy(SteamId, id);
        var edited = new ProxySettings(ProxyScheme.Http, "10.0.0.9", 8080, "user", "new-pass");

        Assert.False(vault.UpdateProxy(id, new ProxySettings(ProxyScheme.Http, "10.0.0.2", 3128)));
        Assert.True(vault.UpdateProxy(id, edited));

        Assert.Equal(edited, vault.GetProxySettings(id));
        Assert.Equal(id, vault.ListAccounts().Single().ProxyId);
        Assert.True(ProxyParser.TryParse(ProxyParser.ToLine(edited with { Password = "p@ss:word" }), out var roundTrip));
        Assert.Equal("p@ss:word", roundTrip!.Password);
    }

    [Fact]
    public void Linked_account_is_saved_with_its_proxy_and_device_profile()
    {
        using var vault = Vault.Create(_folder, password: null);
        vault.AddProxies([new ProxySettings(ProxyScheme.Socks5, "10.0.0.1", 1080)]);
        long proxyId = vault.ListProxies().Single().Id;
        var profile = new BetterSteamAuth.Core.Devices.DeviceProfile("SM-S911B", "Galaxy S23", "14", "UP1A.231005.007", [1, 2, 3]);

        vault.Add(new MaFile { SharedSecret = "abc=", Session = new MaFileSession { SteamId = SteamId } }, proxyId, profile);

        Assert.Equal(proxyId, vault.ListAccounts().Single().ProxyId);
        var saved = vault.GetDeviceProfile(SteamId);
        Assert.Equal(profile with { MachineId = saved!.MachineId }, saved);
        Assert.Equal(profile.MachineId, saved.MachineId);

        vault.Remove(SteamId);
        Assert.Null(vault.GetDeviceProfile(SteamId));
    }
}
