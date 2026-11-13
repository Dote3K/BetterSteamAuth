namespace BetterSteamAuth.Steam;

/// <summary>Values the Steam mobile app sends that are the same for every account.</summary>
public static class MobileClient
{
    /// <summary>
    /// Default User-Agent for accounts without a generated device profile (imported maFiles).
    /// To be checked against the current Steam mobile app during the login spike.
    /// </summary>
    public const string DefaultUserAgent = "okhttp/4.9.2";
}
