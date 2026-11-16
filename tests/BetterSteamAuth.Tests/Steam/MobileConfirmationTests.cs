using System.Text.Json;
using BetterSteamAuth.Steam;

namespace BetterSteamAuth.Tests.Steam;

public class MobileConfirmationTests
{
    [Fact]
    public void Reads_ids_sent_as_strings_and_a_warning()
    {
        const string json = """
            {"type":2,"type_name":"Trade Offer","id":"13891872001","nonce":"7349128843119022211","creator_id":"6213448712",
             "creation_time":1763030400,"headline":"buyer_88","summary":["You will give 3 items"],"multi":false,
             "warn":"An item in this trade was recently in another trade that was cancelled."}
            """;

        var confirmation = JsonSerializer.Deserialize<MobileConfirmation>(json)!;

        Assert.Equal(13891872001UL, confirmation.Id);
        Assert.Equal(7349128843119022211UL, confirmation.Nonce);
        Assert.Equal(ConfirmationKind.Trade, confirmation.Kind);
        Assert.StartsWith("An item in this trade", confirmation.Warning, StringComparison.Ordinal);
    }
}
