using BetterSteamAuth.Steam;

namespace BetterSteamAuth.Tests.Steam;

public class ConfirmationSummaryTests
{
    [Fact]
    public void Reads_a_give_only_trade()
    {
        var counts = ConfirmationSummary.ReadTrade(["You will give 3 items", "You will receive nothing"]);

        Assert.Equal(new TradeCounts(3, 0), counts);
        Assert.True(counts!.Value.GivesOnly);
    }

    [Fact]
    public void Reads_both_sides_in_any_order()
    {
        var counts = ConfirmationSummary.ReadTrade(["You will receive 2 items", "You will give 1 item"]);

        Assert.Equal(new TradeCounts(1, 2), counts);
        Assert.False(counts!.Value.GivesOnly);
    }

    [Theory]
    [InlineData("Trade with buyer_88")]
    [InlineData("You will give some items")]
    public void Unknown_wording_gives_nothing(string line)
        => Assert.Null(ConfirmationSummary.ReadTrade([line, "You will receive nothing"]));

    [Fact]
    public void Finds_the_payout_only_when_it_has_an_amount()
    {
        Assert.Equal("You receive $10.78", ConfirmationSummary.ReadPayout(["List for $12.40", "You receive $10.78"]));
        Assert.Null(ConfirmationSummary.ReadPayout(["Purchase confirmation needed"]));
    }
}
