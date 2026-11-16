using System.Globalization;

namespace BetterSteamAuth.Core.Accounts;

/// <summary>
/// Reads one proxy per line: <c>host:port</c>, <c>host:port:user:pass</c> or
/// <c>scheme://user:pass@host:port</c>. The default scheme is SOCKS5.
/// </summary>
public static class ProxyParser
{
    /// <summary>The proxy as one line that <see cref="TryParse"/> reads back, including its login.</summary>
    public static string ToLine(ProxySettings proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        string login = proxy.Username is null
            ? ""
            : $"{Uri.EscapeDataString(proxy.Username)}:{Uri.EscapeDataString(proxy.Password ?? "")}@";
        return string.Create(CultureInfo.InvariantCulture,
            $"{proxy.Scheme.ToString().ToLowerInvariant()}://{login}{proxy.Host}:{proxy.Port}");
    }

    public static bool TryParse(string? line, out ProxySettings? proxy)
    {
        proxy = null;
        string text = line?.Trim() ?? "";
        if (text.Length == 0)
        {
            return false;
        }

        var scheme = ProxyScheme.Socks5;
        int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            if (!TryScheme(text[..schemeEnd], out scheme))
            {
                return false;
            }

            text = text[(schemeEnd + 3)..];
        }

        string? user = null;
        string? pass = null;
        int at = text.LastIndexOf('@');
        if (at >= 0)
        {
            int colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || colon > at)
            {
                return false;
            }

            user = Uri.UnescapeDataString(text[..colon]);
            pass = Uri.UnescapeDataString(text[(colon + 1)..at]);
            text = text[(at + 1)..];
        }

        string[] parts = text.Split(':');
        if (parts.Length == 4 && user is null)
        {
            user = parts[2];
            pass = parts[3];
        }
        else if (parts.Length != 2)
        {
            return false;
        }

        string host = parts[0];
        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            return false;
        }

        proxy = new ProxySettings(scheme, host, port,
            string.IsNullOrEmpty(user) ? null : user,
            string.IsNullOrEmpty(user) ? null : pass);
        return true;
    }

    private static bool TryScheme(string value, out ProxyScheme scheme)
    {
        switch (value.ToLowerInvariant())
        {
            case "socks5":
            case "socks5h":
                scheme = ProxyScheme.Socks5;
                return true;
            case "http":
                scheme = ProxyScheme.Http;
                return true;
            case "https":
                scheme = ProxyScheme.Https;
                return true;
            default:
                scheme = default;
                return false;
        }
    }
}
