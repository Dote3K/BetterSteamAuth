namespace BetterSteamAuth.Core.Updates;

/// <summary>
/// Public keys whose signature on SHA256SUMS the app accepts for updates (base64 SubjectPublicKeyInfo, P-256).
/// Each entry is a base64 SubjectPublicKeyInfo, as ReleaseSignature.ExportPublicKey returns it.
/// To rotate: add the new key in one release, remove the old one in a later release.
/// </summary>
public static class ReleaseKeys
{
    public static IReadOnlyList<string> Trusted { get; } =
    [
    ];
}
