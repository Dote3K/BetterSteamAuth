using SteamKit2;

namespace BetterSteamAuth.Steam;

/// <summary>
/// What the Steam mobile app sends about itself. Used as the default device profile for
/// accounts without a generated one (imported maFiles).
/// To be checked against the current Steam mobile app.
/// </summary>
public static class MobileClient
{
    public const string DefaultUserAgent = "okhttp/4.9.2";

    public const string DefaultDeviceName = "Pixel 7";

    public const EOSType DefaultOsType = EOSType.Android9;

    /// <summary>Steam's gaming device type for a phone.</summary>
    public const uint PhoneDeviceType = 528;

    /// <summary>website_id the mobile app uses when it signs in.</summary>
    public const string WebsiteId = "Mobile";
}
