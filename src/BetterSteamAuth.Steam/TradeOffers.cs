using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace BetterSteamAuth.Steam;

public sealed record TradeItem(string Name, string? Type, string? ImageUrl, int Amount);

public sealed record TradeOfferDetails(ulong PartnerSteamId, IReadOnlyList<TradeItem> ItemsToGive, IReadOnlyList<TradeItem> ItemsToReceive);

public sealed record PartnerProfile(
    string Name,
    string? AvatarUrl,
    DateOnly? MemberSince,
    bool IsPrivate,
    bool VacBanned,
    bool TradeBanned,
    bool IsLimited);

/// <summary>
/// Details behind a trade confirmation, through the account's own connection.
/// </summary>
public static class TradeOffers
{
    private const ulong SteamId64Base = 76561197960265728;
    private const string ImageBase = "https://community.cloudflare.steamstatic.com/economy/image/";

    /// <summary>The trade offer behind a trade confirmation (its <c>creator_id</c>).</summary>
    public static async Task<TradeOfferDetails> GetAsync(
        AccountConnection connection, string accessToken, ulong tradeOfferId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var url = new Uri(string.Create(CultureInfo.InvariantCulture,
            $"https://api.steampowered.com/IEconService/GetTradeOffer/v1/?access_token={Uri.EscapeDataString(accessToken)}&tradeofferid={tradeOfferId}&language=english&get_descriptions=1"));

        using var http = connection.CreateHttpClient();
        await using var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseOffer(doc.RootElement);
    }

    internal static TradeOfferDetails ParseOffer(JsonElement root)
    {
        var response = root.GetProperty("response");
        if (!response.TryGetProperty("offer", out var offer))
        {
            throw new HttpRequestException("Steam didn't return the trade offer.");
        }

        var descriptions = new Dictionary<(string, string, string), JsonElement>();
        if (response.TryGetProperty("descriptions", out var list))
        {
            foreach (var d in list.EnumerateArray())
            {
                descriptions[(Text(d, "appid"), Text(d, "classid"), Text(d, "instanceid"))] = d;
            }
        }

        IReadOnlyList<TradeItem> Items(string name)
        {
            if (!offer.TryGetProperty(name, out var items))
            {
                return [];
            }

            return items.EnumerateArray().Select(i =>
            {
                descriptions.TryGetValue((Text(i, "appid"), Text(i, "classid"), Text(i, "instanceid")), out var d);
                bool known = d.ValueKind == JsonValueKind.Object;
                string? icon = known ? Text(d, "icon_url") : null;
                return new TradeItem(
                    known ? (Text(d, "market_name") is { Length: > 0 } market ? market : Text(d, "name")) : "Unknown item",
                    known ? Text(d, "type") : null,
                    string.IsNullOrEmpty(icon) ? null : ImageBase + icon + "/128fx128f",
                    int.TryParse(Text(i, "amount"), out int amount) ? amount : 1);
            }).ToList();
        }

        ulong accountId = ulong.Parse(Text(offer, "accountid_other"), CultureInfo.InvariantCulture);
        return new TradeOfferDetails(SteamId64Base + accountId, Items("items_to_give"), Items("items_to_receive"));
    }

    /// <summary>Public profile facts from <c>/profiles/&lt;id&gt;?xml=1</c>.</summary>
    public static async Task<PartnerProfile> GetProfileAsync(AccountConnection connection, ulong steamId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var url = new Uri(string.Create(CultureInfo.InvariantCulture, $"https://steamcommunity.com/profiles/{steamId}?xml=1&l=english"));

        using var http = connection.CreateHttpClient();
        string xml = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseProfile(xml, steamId);
    }

    internal static PartnerProfile ParseProfile(string xml, ulong steamId)
    {
        var root = XDocument.Parse(xml).Root ?? throw new HttpRequestException("Empty profile.");

        string? Value(string name) => root.Element(name)?.Value.Trim();

        DateOnly? memberSince = DateOnly.TryParseExact(
            Value("memberSince"), ["MMMM d, yyyy", "MMMM d,yyyy"], CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var date)
            ? date
            : null;

        return new PartnerProfile(
            Value("steamID") ?? steamId.ToString(CultureInfo.InvariantCulture),
            Value("avatarMedium"),
            memberSince,
            IsPrivate: !string.Equals(Value("privacyState"), "public", StringComparison.OrdinalIgnoreCase),
            VacBanned: Value("vacBanned") == "1",
            TradeBanned: Value("tradeBanState") is { Length: > 0 } ban && !string.Equals(ban, "None", StringComparison.OrdinalIgnoreCase),
            IsLimited: Value("isLimitedAccount") == "1");
    }

    /// <summary>Downloads an image through the account's connection. Null if it can't be loaded.</summary>
    public static async Task<byte[]?> GetImageAsync(AccountConnection connection, string? url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            using var http = connection.CreateHttpClient();
            return await http.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
            : "";
}
