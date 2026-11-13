using System.Globalization;

namespace BetterSteamAuth.Core.Accounts;

public enum ProxyScheme
{
    Http,
    Https,
    Socks5,
}

/// <summary>A proxy assigned to one account.</summary>
public sealed record ProxySettings(ProxyScheme Scheme, string Host, int Port, string? Username = null, string? Password = null)
{
    public Uri Uri => new(string.Create(
        CultureInfo.InvariantCulture, $"{Scheme.ToString().ToLowerInvariant()}://{Host}:{Port}"));

    public override string ToString() => $"{Scheme.ToString().ToLowerInvariant()} · {Host}:{Port}";
}
