using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterSteamAuth.Core.Guard;

namespace BetterSteamAuth.Steam;

/// <summary>Safety tier of a confirmation.</summary>
public enum ConfirmationKind
{
    Trade,
    Market,

    /// <summary>Account-level changes (phone, recovery, API key, …) and anything unknown: never quick-accepted.</summary>
    Dangerous,
}

/// <summary>One item from <c>mobileconf/getlist</c>.</summary>
public sealed class MobileConfirmation
{
    [JsonPropertyName("id")] public ulong Id { get; set; }
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("type_name")] public string? TypeName { get; set; }
    [JsonPropertyName("creator_id")] public ulong CreatorId { get; set; }
    [JsonPropertyName("creation_time")] public long CreationTime { get; set; }
    [JsonPropertyName("headline")] public string? Headline { get; set; }
    [JsonPropertyName("summary")] public List<string> Summary { get; set; } = [];
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("multi")] public bool Multi { get; set; }

    // Steam's EMobileConfirmationType: 2 = trade, 3 = market listing.
    [JsonIgnore]
    public ConfirmationKind Kind => Type switch
    {
        2 => ConfirmationKind.Trade,
        3 => ConfirmationKind.Market,
        _ => ConfirmationKind.Dangerous,
    };
}

/// <summary>
/// Mobile confirmations on steamcommunity.com, through the account's own connection.
/// Request shape follows SDA's SteamAuth library.
/// </summary>
public static class Confirmations
{
    private const string BaseUrl = "https://steamcommunity.com/mobileconf/";

    public static async Task<IReadOnlyList<MobileConfirmation>> GetAsync(
        AccountConnection connection, string identitySecret, string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string url = BaseUrl + "getlist?" + Query(connection, identitySecret, deviceId, ConfirmationTag.List);

        using var http = connection.CreateHttpClient();
        using var response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var list = await JsonSerializer.DeserializeAsync<ListResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (list is null || list.NeedAuth || !list.Success)
        {
            throw list?.NeedAuth == true || list?.Success == false
                ? new SessionExpiredException(list.Message ?? "Steam asked to sign in again.")
                : new HttpRequestException("Steam returned an empty confirmation list.");
        }

        return list.Conf;
    }

    /// <summary>Accepts or rejects confirmations. Returns true when Steam reports success.</summary>
    public static async Task<bool> RespondAsync(
        AccountConnection connection, string identitySecret, string deviceId,
        IReadOnlyCollection<MobileConfirmation> confirmations, bool accept, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(confirmations);
        if (confirmations.Count == 0)
        {
            return true;
        }

        string op = accept ? "allow" : "cancel";
        string tag = accept ? ConfirmationTag.Accept : ConfirmationTag.Reject;
        string query = $"op={op}&" + Query(connection, identitySecret, deviceId, tag);

        using var http = connection.CreateHttpClient();
        HttpResponseMessage response;
        if (confirmations.Count == 1)
        {
            var c = confirmations.First();
            response = await http.GetAsync(
                new Uri(BaseUrl + "ajaxop?" + query + string.Create(CultureInfo.InvariantCulture, $"&cid={c.Id}&ck={c.Nonce}")),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string body = query + string.Concat(confirmations.Select(c =>
                string.Create(CultureInfo.InvariantCulture, $"&cid[]={c.Id}&ck[]={c.Nonce}")));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUrl + "multiajaxop"))
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
            };
            request.Headers.Add("Origin", "https://steamcommunity.com");
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<ListResponse>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result?.Success == true;
        }
    }

    private static string Query(AccountConnection connection, string identitySecret, string deviceId, string tag)
    {
        long time = connection.Clock.Now;
        string key = ConfirmationKey.Generate(identitySecret, time, tag);
        return string.Create(CultureInfo.InvariantCulture,
            $"p={Uri.EscapeDataString(deviceId)}&a={connection.SteamId}&k={Uri.EscapeDataString(key)}&t={time}&m=react&tag={tag}");
    }

    private sealed class ListResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("needauth")] public bool NeedAuth { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("conf")] public List<MobileConfirmation> Conf { get; set; } = [];
    }
}
