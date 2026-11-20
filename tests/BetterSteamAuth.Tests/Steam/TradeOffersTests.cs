using System.Text.Json;
using BetterSteamAuth.Steam;

namespace BetterSteamAuth.Tests.Steam;

public class TradeOffersTests
{
    [Fact]
    public void ParseOffer_matches_items_to_descriptions()
    {
        const string json = """
            {"response":{"offer":{"tradeofferid":"6213448712","accountid_other":12345,
              "items_to_give":[{"appid":730,"contextid":"2","assetid":"1","classid":"310776560","instanceid":"302028390","amount":"1"}],
              "items_to_receive":[]},
             "descriptions":[{"appid":730,"classid":"310776560","instanceid":"302028390","name":"AK-47 | Redline",
               "market_name":"AK-47 | Redline (Field-Tested)","type":"Classified Rifle","icon_url":"abc"}]}}
            """;
        using var doc = JsonDocument.Parse(json);

        var offer = TradeOffers.ParseOffer(doc.RootElement);

        Assert.Equal(76561197960265728UL + 12345, offer.PartnerSteamId);
        var item = Assert.Single(offer.ItemsToGive);
        Assert.Equal("AK-47 | Redline (Field-Tested)", item.Name);
        Assert.Equal("Classified Rifle", item.Type);
        Assert.EndsWith("/economy/image/abc/128fx128f", item.ImageUrl, StringComparison.Ordinal);
        Assert.Empty(offer.ItemsToReceive);
    }

    [Fact]
    public void ParseProfile_reads_age_and_flags()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <profile><steamID><![CDATA[buyer_88]]></steamID><privacyState>public</privacyState>
            <vacBanned>0</vacBanned><tradeBanState>Probation</tradeBanState><isLimitedAccount>1</isLimitedAccount>
            <memberSince>November 12, 2026</memberSince><avatarMedium><![CDATA[https://avatars.example/a.jpg]]></avatarMedium></profile>
            """;

        var profile = TradeOffers.ParseProfile(xml, 1);

        Assert.Equal("buyer_88", profile.Name);
        Assert.Equal(new DateOnly(2026, 11, 12), profile.MemberSince);
        Assert.False(profile.IsPrivate);
        Assert.True(profile.TradeBanned);
        Assert.True(profile.IsLimited);
        Assert.False(profile.VacBanned);
    }

    [Theory]
    [InlineData("""{"response":{"player_level":27}}""", 27)]
    [InlineData("""{"response":{}}""", null)]
    public void ParseSteamLevel_reads_the_level_or_nothing(string json, int? expected)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(expected, TradeOffers.ParseSteamLevel(doc.RootElement));
    }
}
