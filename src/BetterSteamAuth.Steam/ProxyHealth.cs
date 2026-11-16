using System.Diagnostics;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Steam.Network;

namespace BetterSteamAuth.Steam;

public sealed record ProxyCheckResult(bool Ok, int? LatencyMs, string? Error);

/// <summary>
/// Checks that a proxy can reach Steam (not just that it's alive), with the current network
/// settings.
/// </summary>
public static class ProxyHealth
{
    private static readonly Uri Probe = new("https://api.steampowered.com/ISteamWebAPIUtil/GetServerInfo/v1/");

    public static async Task<ProxyCheckResult> CheckAsync(ProxySettings proxy, NetworkPolicy network, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        // A throwaway connection: no cookies, no account.
        using var connection = new AccountConnection(0, proxy, network);
        using var http = connection.CreateHttpClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(Probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new ProxyCheckResult(true, (int)watch.ElapsedMilliseconds, null)
                : new ProxyCheckResult(false, null, $"Steam answered HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ProxyCheckResult(false, null, ex.GetBaseException().Message);
        }
    }
}
