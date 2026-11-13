using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BetterSteamAuth.Core.Accounts;

namespace BetterSteamAuth.Steam;

/// <summary>Thrown when the account's tokens no longer work and the user must sign in again.</summary>
public sealed class SessionExpiredException : Exception
{
    public SessionExpiredException() : base("The Steam session has expired.")
    {
    }

    public SessionExpiredException(string message) : base(message)
    {
    }

    public SessionExpiredException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Keeps an account's web session usable: refreshes the access token from the refresh token
/// before it expires and puts the session cookies on the account's own cookie jar.
/// </summary>
public static class WebSession
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly string[] CookieDomains = ["steamcommunity.com", "store.steampowered.com"];

    public static string NewSessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Expiry from a Steam JWT's <c>exp</c> claim, or null if it can't be read.</summary>
    public static DateTimeOffset? GetExpiry(string? jwt)
    {
        string[]? parts = jwt?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            string base64 = parts[1].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(base64));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes sure <paramref name="session"/> has a valid access token (refreshing it if needed) and
    /// applies the session cookies. Returns true when the session changed and must be saved.
    /// Throws <see cref="SessionExpiredException"/> when the user has to sign in again.
    /// </summary>
    public static async Task<bool> EnsureAsync(AccountConnection connection, MaFileSession? session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (session is null || string.IsNullOrEmpty(session.RefreshToken) || IsExpired(session.RefreshToken, TimeSpan.Zero))
        {
            throw new SessionExpiredException();
        }

        bool changed = false;
        if (string.IsNullOrEmpty(session.SessionId))
        {
            session.SessionId = NewSessionId();
            changed = true;
        }

        if (string.IsNullOrEmpty(session.AccessToken) || IsExpired(session.AccessToken, RefreshMargin))
        {
            try
            {
                var (access, refresh) = await SteamLogin.RefreshAccessTokenAsync(connection, session.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
                session.AccessToken = access;
                session.RefreshToken = refresh ?? session.RefreshToken;
                changed = true;
            }
            catch (LoginFailedException ex)
            {
                throw new SessionExpiredException("Steam rejected the refresh token.", ex);
            }
        }

        ApplyCookies(connection, session);
        return changed;
    }

    private static bool IsExpired(string token, TimeSpan margin)
        => GetExpiry(token) is { } expiry && expiry - margin <= DateTimeOffset.UtcNow;

    private static void ApplyCookies(AccountConnection connection, MaFileSession session)
    {
        string steamLoginSecure = string.Create(
            CultureInfo.InvariantCulture, $"{connection.SteamId}%7C%7C{session.AccessToken}");
        foreach (string domain in CookieDomains)
        {
            connection.Cookies.Add(new Cookie("steamLoginSecure", steamLoginSecure, "/", domain) { Secure = true, HttpOnly = true });
            connection.Cookies.Add(new Cookie("sessionid", session.SessionId, "/", domain));
            connection.Cookies.Add(new Cookie("mobileClient", "android", "/", domain));
            connection.Cookies.Add(new Cookie("mobileClientVersion", MobileClient.ClientVersionCookie, "/", domain));
            connection.Cookies.Add(new Cookie("Steam_Language", "english", "/", domain));
        }
    }
}
