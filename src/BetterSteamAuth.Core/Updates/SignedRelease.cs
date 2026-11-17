namespace BetterSteamAuth.Core.Updates;

/// <summary>A file an update downloads: its name, the version of the release it belongs to, and the hash the update feed lists.</summary>
public readonly record struct ReleaseFile(string FileName, string Version, string? Sha256);

public static class SignedRelease
{
    /// <summary>
    /// Checks every file against the signed SHA256SUMS of the release it belongs to. An update that skips versions
    /// downloads one delta per skipped release, and each delta is listed only in its own release's SHA256SUMS.
    /// Each release's list is fetched once. Returns the first file that isn't listed with the same hash, or null.
    /// </summary>
    /// <param name="signedSums">Returns the verified SHA256SUMS of a release version, or throws if its signature is invalid.</param>
    public static async Task<string?> FindUnsignedAsync(
        IEnumerable<ReleaseFile> files, Func<string, Task<IReadOnlyDictionary<string, string>>> signedSums)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(signedSums);

        var byVersion = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!byVersion.TryGetValue(file.Version, out var sums))
            {
                sums = await signedSums(file.Version).ConfigureAwait(false);
                byVersion[file.Version] = sums;
            }

            if (file.Sha256 is null || !sums.TryGetValue(file.FileName, out string? hash)
                || !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return file.FileName;
            }
        }

        return null;
    }
}
