using BetterSteamAuth.Steam;
using BetterSteamAuth.Steam.Network;

namespace BetterSteamAuth.Tests.Steam;

public class ConfirmationsTests
{
    [Fact]
    public void Client_sends_origin_referer_and_language()
    {
        using var network = new NetworkPolicy(new NetworkOptions());
        using var connection = new AccountConnection(76561197960287930, null, network);

        using var http = Confirmations.CreateClient(connection);

        Assert.Equal(["https://steamcommunity.com"], http.DefaultRequestHeaders.GetValues("Origin"));
        Assert.Equal(new Uri("https://steamcommunity.com"), http.DefaultRequestHeaders.Referrer);
        Assert.Equal("en-US", http.DefaultRequestHeaders.AcceptLanguage.ToString());
        Assert.Equal(MobileClient.UserAgent, http.DefaultRequestHeaders.UserAgent.ToString());
    }
}
