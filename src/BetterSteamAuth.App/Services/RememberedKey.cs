using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace BetterSteamAuth.App.Services;

/// <summary>
/// "Remember on this device": the data key wrapped with Windows DPAPI (current user) and stored under
/// <c>%LOCALAPPDATA%</c>, never inside the data folder, so a copied data folder still needs the password.
/// </summary>
public static class RememberedKey
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterSteamAuth", "remembered");

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool Exists(string dataFolder) => IsSupported && File.Exists(PathFor(dataFolder));

    public static void Save(string dataFolder, byte[] dataKey)
    {
        ArgumentNullException.ThrowIfNull(dataKey);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Root);
        File.WriteAllBytes(PathFor(dataFolder), Protect(dataKey, Entropy(dataFolder)));
    }

    /// <summary>The saved key, or null when there is none or it can't be unwrapped (other user, other PC).</summary>
    public static byte[]? Load(string dataFolder)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(PathFor(dataFolder)))
        {
            return null;
        }

        try
        {
            return Unprotect(File.ReadAllBytes(PathFor(dataFolder)), Entropy(dataFolder));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return null;
        }
    }

    public static void Forget(string dataFolder)
    {
        string path = PathFor(dataFolder);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] data, byte[] entropy) => ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] data, byte[] entropy) => ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);

    /// <summary>One file per data folder, named by a hash of its full path.</summary>
    private static string PathFor(string dataFolder)
        => Path.Combine(Root, Convert.ToHexStringLower(Entropy(dataFolder).AsSpan(0, 12)) + ".key");

    private static byte[] Entropy(string dataFolder)
        => SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataFolder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()));
}
