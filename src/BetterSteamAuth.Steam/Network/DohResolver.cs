using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace BetterSteamAuth.Steam.Network;

public enum EncryptedDns
{
    Off,
    Cloudflare,
    Google,
}

/// <summary>
/// DNS over HTTPS (JSON API) against IP-literal endpoints, so it needs no DNS itself.
/// Results are cached for their TTL.
/// </summary>
public sealed class DohResolver : IDisposable
{
    private const int TypeA = 1;
    private const int TypeAaaa = 28;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DohResolver(EncryptedDns provider)
    {
        _endpoint = provider switch
        {
            EncryptedDns.Cloudflare => new Uri("https://1.1.1.1/dns-query"),
            EncryptedDns.Google => new Uri("https://8.8.8.8/resolve"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        if (_cache.TryGetValue(host, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Addresses;
        }

        var (v4, ttl4) = await QueryAsync(host, TypeA, cancellationToken).ConfigureAwait(false);
        var addresses = v4;
        int ttl = ttl4;
        if (addresses.Length == 0)
        {
            (addresses, ttl) = await QueryAsync(host, TypeAaaa, cancellationToken).ConfigureAwait(false);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"Encrypted DNS found no address for {host}.");
        }

        _cache[host] = (addresses, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(ttl, 30, 3600)));
        return addresses;
    }

    public void Dispose() => _http.Dispose();

    private async Task<(IPAddress[] Addresses, int Ttl)> QueryAsync(string host, int type, CancellationToken cancellationToken)
    {
        var uri = new Uri(_endpoint, string.Create(
            CultureInfo.InvariantCulture, $"?name={Uri.EscapeDataString(host)}&type={type}"));
        await using var stream = await _http.GetStreamAsync(uri, cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("Answer", out var answers))
        {
            return ([], 0);
        }

        var addresses = new List<IPAddress>();
        int ttl = int.MaxValue;
        foreach (var answer in answers.EnumerateArray())
        {
            if (answer.GetProperty("type").GetInt32() == type
                && IPAddress.TryParse(answer.GetProperty("data").GetString(), out var address))
            {
                addresses.Add(address);
                ttl = Math.Min(ttl, answer.GetProperty("TTL").GetInt32());
            }
        }

        return ([.. addresses], addresses.Count > 0 ? ttl : 0);
    }
}
