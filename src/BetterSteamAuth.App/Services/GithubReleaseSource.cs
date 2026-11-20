using BetterSteamAuth.Core.Updates;
using Velopack.Sources;

namespace BetterSteamAuth.App.Services;

/// <summary>
/// GitHub Releases without the REST API, which allows only 60 unauthenticated requests per hour per IP and then
/// answers 403. Releases come from the public <c>releases.atom</c> feed and files from
/// <c>releases/download/&lt;tag&gt;/&lt;file&gt;</c>; the API is used only when the feed can't be read.
/// Each release's feed lists only its own files, so every file (deltas included) downloads from its own release.
/// </summary>
internal sealed class GithubReleaseSource(string repoUrl, bool prerelease) : GithubSource(repoUrl, accessToken: null, prerelease)
{
    private const int MaxReleases = 10;

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        IReadOnlyList<AtomRelease> feed;
        try
        {
            string xml = await Downloader.DownloadString(
                RepoUri + "/releases.atom", new Dictionary<string, string> { ["Accept"] = "application/atom+xml" }).ConfigureAwait(false);
            feed = ReleaseAtom.Parse(xml);
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException or TaskCanceledException)
        {
            return await base.GetReleases(includePrereleases).ConfigureAwait(false);
        }

        // Tag stands in for the name: GetAssetUrlFromName builds download URLs from it.
        return [.. feed
            .Where(r => includePrereleases || !r.IsPrerelease)
            .Take(MaxReleases)
            .Select(r => new GithubRelease { Name = r.Tag, Prerelease = r.IsPrerelease, PublishedAt = r.Updated.UtcDateTime })];
    }

    protected override string GetAssetUrlFromName(GithubRelease release, string assetName)
    {
        // Releases from the API fallback carry their asset list; the feed's don't.
        if (release.Assets is { Length: > 0 })
        {
            return base.GetAssetUrlFromName(release, assetName);
        }

        return $"{RepoUri}/releases/download/{Uri.EscapeDataString(release.Name!)}/{Uri.EscapeDataString(assetName)}";
    }
}
