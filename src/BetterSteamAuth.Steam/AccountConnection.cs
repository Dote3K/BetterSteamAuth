using System.Net;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Guard;
using SteamKit2;

namespace BetterSteamAuth.Steam;

/// <summary>
/// Everything one account uses to talk to Steam. Nothing here is shared between accounts:
/// its own connection pool, cookies, proxy, clock and SteamKit2 configuration.
/// </summary>
public sealed class AccountConnection : IDisposable
{
    private readonly SocketsHttpHandler _handler;

    public AccountConnection(ulong steamId, ProxySettings? proxy, string userAgent)
    {
        SteamId = steamId;
        Proxy = proxy;
        UserAgent = userAgent;

        _handler = new SocketsHttpHandler
        {
            // Proxy assigned: always use it, never fall back to a direct connection (fail-closed).
            // No proxy: connect directly and ignore any system-wide proxy.
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : CreateProxy(proxy),
            UseCookies = true,
            CookieContainer = Cookies,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        // SteamKit2 disposes the HttpClient it gets after each interface, so hand out
        // fresh clients that share (and never dispose) this account's handler.
        Configuration = SteamConfiguration.Create(builder => builder.WithHttpClientFactory(_ => CreateHttpClient()));
    }

    public ulong SteamId { get; }

    public ProxySettings? Proxy { get; }

    public string UserAgent { get; }

    public CookieContainer Cookies { get; } = new();

    public SteamClock Clock { get; } = new();

    public SteamConfiguration Configuration { get; }

    /// <summary>A client on this account's connection, for non-WebAPI requests (community pages).</summary>
    public HttpClient CreateHttpClient()
    {
        var client = new HttpClient(_handler, disposeHandler: false);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public WebAPI.AsyncInterface WebApi(string iface) => Configuration.GetAsyncWebAPIInterface(iface);

    public void Dispose() => _handler.Dispose();

    private static WebProxy CreateProxy(ProxySettings proxy) => new(proxy.Uri)
    {
        BypassProxyOnLocal = false,
        Credentials = proxy.Username is null ? null : new NetworkCredential(proxy.Username, proxy.Password),
    };
}
