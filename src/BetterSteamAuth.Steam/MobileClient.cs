using SteamKit2;

namespace BetterSteamAuth.Steam;

/// <summary>
/// What the Steam mobile app sends about itself. Used as the default device profile for
/// accounts without a generated one (imported maFiles).
/// To be checked against the current Steam mobile app.
/// </summary>
public static class MobileClient
{
    /// <summary>User-Agent of the mobile app's web views (also what SDA's SteamAuth sends).</summary>
    public const string DefaultUserAgent = "Dalvik/2.1.0 (Linux; U; Android 9; Valve Steam App Version/3)";

    /// <summary>Value of the <c>mobileClientVersion</c> cookie on community pages.</summary>
    public const string ClientVersionCookie = "777777 3.6.4";

    public const string DefaultDeviceName = "Pixel 7";

    public const EOSType DefaultOsType = EOSType.Android9;

    /// <summary>Steam's gaming device type for a phone.</summary>
    public const uint PhoneDeviceType = 528;

    /// <summary>website_id the mobile app uses when it signs in.</summary>
    public const string WebsiteId = "Mobile";
}
