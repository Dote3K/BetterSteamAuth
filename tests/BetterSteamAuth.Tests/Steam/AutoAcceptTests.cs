using System.Text.Json;
using BetterSteamAuth.Steam;

namespace BetterSteamAuth.Tests.Steam;

public sealed class AutoAcceptTests
{
    [Fact]
    public void Each_type_follows_only_its_own_switch()
    {
        var listing = Confirmation(3, "List for $12.40", "You receive $10.78");
        var purchase = Confirmation(12, "Purchase confirmation needed");
        var trade = Confirmation(2, "You will give 1 item", "You will receive 2 items");

        Assert.True(AutoAccept.Selects(listing, trades: false, marketListings: true, marketPurchases: false));
        Assert.False(AutoAccept.Selects(listing, trades: true, marketListings: false, marketPurchases: true));
        Assert.True(AutoAccept.Selects(purchase, trades: false, marketListings: false, marketPurchases: true));
        Assert.False(AutoAccept.Selects(purchase, trades: true, marketListings: true, marketPurchases: false));
        Assert.True(AutoAccept.Selects(trade, trades: true, marketListings: false, marketPurchases: false));
        Assert.False(AutoAccept.Selects(trade, trades: false, marketListings: true, marketPurchases: true));
    }

    [Fact]
    public void Trades_that_need_a_person_are_left_for_review()
    {
        var givesOnly = Confirmation(2, "You will give 3 items", "You will receive nothing");
        var unrecognized = Confirmation(2, "Trade with buyer_88");
        var warned = Confirmation(2, "You will give 1 item", "You will receive 1 item");
        warned.Extra = new() { ["warn"] = JsonDocument.Parse("\"The other party recently changed their password\"").RootElement };

        Assert.False(AutoAccept.Selects(givesOnly, true, true, true));
        Assert.False(AutoAccept.Selects(unrecognized, true, true, true));
        Assert.False(AutoAccept.Selects(warned, true, true, true));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(99)]
    public void Account_changes_and_unknown_types_are_never_selected(int type)
        => Assert.False(AutoAccept.Selects(Confirmation(type, "Account change"), true, true, true));

    private static MobileConfirmation Confirmation(int type, params string[] summary)
        => new() { Id = 1, Nonce = 2, Type = type, Summary = [.. summary] };
}
