using SteamKit2;

namespace BetterSteamAuth.Steam;

/// <summary>
/// What the Steam mobile app sends about itself, identical for every account. Values follow
/// node-steam-session; update them here when the real app changes.
/// </summary>
public static class MobileClient
{
    /// <summary>User-Agent of the mobile app's HTTP library, sent on every request.</summary>
    public const string UserAgent = "okhttp/4.9.2";

    /// <summary>Value of the <c>mobileClientVersion</c> cookie.</summary>
    public const string ClientVersionCookie = "777777 3.10.3";

    /// <summary>Friendly name for accounts without a generated profile (imported maFiles).</summary>
    public const string DefaultDeviceName = "Galaxy S25";

    /// <summary>Steam's public EOSType list stops at Android 9; open-source clients send AndroidUnknown.</summary>
    public const EOSType OsType = EOSType.AndroidUnknown;

    /// <summary>Steam's gaming device type for a phone.</summary>
    public const uint PhoneDeviceType = 528;

    /// <summary>website_id the mobile app uses when it signs in.</summary>
    public const string WebsiteId = "Mobile";
}
