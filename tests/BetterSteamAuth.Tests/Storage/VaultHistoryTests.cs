using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Security;
using BetterSteamAuth.Storage;
using Dapper;

namespace BetterSteamAuth.Tests.Storage;

public sealed class VaultHistoryTests : IDisposable
{
    private const ulong SteamId = 76561198000000001;
    private static readonly KdfParameters FastKdf = new(MemoryKiB: 1024, Iterations: 1, Parallelism: 1);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bsa-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Automation_defaults_until_saved_and_is_dropped_with_the_account()
    {
        using var vault = Vault.Create(_folder, password: null);
        vault.Add(FakeMaFile());
        Assert.Equal(AccountAutomation.Default, vault.GetAutomation(SteamId));

        var rules = new AccountAutomation(ConfirmationChecks.Always, Notify: false, Trades: true, Market: false, Purchases: true);
        vault.SetAutomation(SteamId, rules);
        Assert.Equal(rules, vault.GetAutomation(SteamId));
        Assert.Equal(rules, vault.ListAutomation()[SteamId]);

        vault.Remove(SteamId);
        Assert.Equal(AccountAutomation.Default, vault.GetAutomation(SteamId));
    }

    [Fact]
    public void History_summary_is_encrypted_with_a_password_and_readable_after_unlock()
    {
        using (var vault = Vault.Create(_folder, "pw", FastKdf))
        {
            vault.Add(FakeMaFile());
            vault.LogConfirmation(SteamId, 1, 2, "Trade with buyer_88", accepted: true, automatic: true, ok: true);
            vault.LogConfirmation(SteamId, 2, 3, "Sell AK-47", accepted: false, automatic: false, ok: false);
        }

        Assert.DoesNotContain("buyer_88", ReadRawSummaries());

        using var reopened = Vault.Open(_folder);
        Assert.All(reopened.ListHistory(SteamId, 10), e => Assert.Null(e.Summary));

        reopened.Unlock("pw");
        var history = reopened.ListHistory(SteamId, 10);
        Assert.Equal(["Sell AK-47", "Trade with buyer_88"], history.Select(e => e.Summary));
        Assert.True(history[1] is { Accepted: true, Automatic: true, Ok: true, Type: 2 });
        Assert.True(history[0] is { Accepted: false, Automatic: false, Ok: false });
        Assert.Single(reopened.ListHistory(SteamId, 10, after: history[0]));
    }

    [Fact]
    public void Maintain_deletes_old_entries_and_seals_plain_summaries_from_older_versions()
    {
        using var vault = Vault.Create(_folder, "pw", FastKdf);
        vault.Add(FakeMaFile());
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_folder, "vault.db")}"))
        {
            long old = DateTimeOffset.UtcNow.AddDays(-100).ToUnixTimeSeconds();
            long recent = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            connection.Execute(
                "INSERT INTO confirmation_log(steam_id, conf_id, conf_type, summary, action, source, ok, at) VALUES " +
                "(@id, '1', 2, 'old trade', 'accept', 'manual', 1, @old), (@id, '2', 2, 'Trade with buyer_88', 'accept', 'manual', 1, @recent)",
                new { id = (long)SteamId, old, recent });
        }

        vault.MaintainHistory(keepDays: 90);

        Assert.DoesNotContain("buyer_88", ReadRawSummaries());
        Assert.Equal(["Trade with buyer_88"], vault.ListHistory(SteamId, 10).Select(e => e.Summary));
    }

    private string ReadRawSummaries()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_folder, "vault.db")}");
        return string.Join("|", connection.Query<string?>("SELECT summary FROM confirmation_log"));
    }

    private static MaFile FakeMaFile() => new()
    {
        AccountName = "acc_01",
        SharedSecret = "bTvsR2DJ3XG8ZXOoMZ4LXsbbOAI=",
        IdentitySecret = "Zm9vYmFyYmF6cXV4",
        DeviceId = "android:00000000-0000-0000-0000-000000000001",
        Session = new MaFileSession { SteamId = SteamId },
    };
}
