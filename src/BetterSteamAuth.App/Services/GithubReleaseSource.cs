using System.Net;
using System.Text;
using BetterSteamAuth.Core.Updates;
using Velopack.Sources;

namespace BetterSteamAuth.App.Services;

/// <summary>
/// GitHub Releases without the REST API, which allows only 60 unauthenticated requests per hour per IP and then
/// answers 403. Releases come from the public <c>releases.atom</c> feed and files from
/// <c>releases/download/&lt;tag&gt;/&lt;file&gt;</c>; the API is used only when the feed can't be read.
/// Each release's feed lists only its own files, so every file (deltas included) downloads from its own release.
/// </summary>
internal sealed class GithubReleaseSource(string repoUrl, bool prerelease)
    : GithubSource(repoUrl, accessToken: null, prerelease, new MissingIndexIsEmpty(new HttpClientFileDownloader()))
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

    /// <summary>
    /// The feed lists a tag as soon as it's pushed, before its release is published (or when it never is). Until then
    /// the tag has no files, and one missing release index would fail the whole check; it counts as an empty release.
    /// </summary>
    private sealed class MissingIndexIsEmpty(IFileDownloader inner) : IFileDownloader
    {
        private static readonly byte[] EmptyIndex = Encoding.UTF8.GetBytes("""{"Assets":[]}""");

        public Task DownloadFile(
            string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken) =>
            inner.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout) =>
            inner.DownloadString(url, headers, timeout);

        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
        {
            try
            {
                return await inner.DownloadBytes(url, headers, timeout).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound && IsReleaseIndex(url))
            {
                return EmptyIndex;
            }
        }

        private static bool IsReleaseIndex(string url)
        {
            string name = url[(url.LastIndexOf('/') + 1)..];
            return name.StartsWith("releases.", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }
    }
}
