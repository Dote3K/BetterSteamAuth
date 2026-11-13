using BetterSteamAuth.Core.Accounts;

namespace BetterSteamAuth.Tests.Core;

public class ProxyParserTests
{
    [Theory]
    [InlineData("203.0.113.12:1080", ProxyScheme.Socks5, "203.0.113.12", 1080, null, null)]
    [InlineData("203.0.113.12:1080:user:pass", ProxyScheme.Socks5, "203.0.113.12", 1080, "user", "pass")]
    [InlineData("socks5://user:p%40ss@proxy.example:1080", ProxyScheme.Socks5, "proxy.example", 1080, "user", "p@ss")]
    [InlineData("http://198.51.100.9:3128", ProxyScheme.Http, "198.51.100.9", 3128, null, null)]
    [InlineData("  HTTPS://u:p@h.example:443  ", ProxyScheme.Https, "h.example", 443, "u", "p")]
    public void Parses_supported_formats(string line, ProxyScheme scheme, string host, int port, string? user, string? pass)
    {
        Assert.True(ProxyParser.TryParse(line, out var proxy));
        Assert.Equal(new ProxySettings(scheme, host, port, user, pass), proxy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a proxy")]
    [InlineData("host:99999")]
    [InlineData("host:port")]
    [InlineData("ftp://host:21")]
    [InlineData("a:1:b")]
    public void Rejects_invalid_lines(string line) => Assert.False(ProxyParser.TryParse(line, out _));
}
