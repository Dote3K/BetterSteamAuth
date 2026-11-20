using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BetterSteamAuth.Core.Updates;

/// <summary>A published release read from a repository's <c>releases.atom</c> feed.</summary>
public sealed record AtomRelease(string Tag, DateTimeOffset Updated)
{
    /// <summary>Semantic-version tags with a pre-release part (<c>v1.2.0-beta.1</c>) are betas.</summary>
    public bool IsPrerelease => Tag.Contains('-', StringComparison.Ordinal);
}

/// <summary>
/// Reads GitHub's public <c>/releases.atom</c> feed. Unlike the REST API it isn't limited to 60 requests per hour
/// per IP, so the update check keeps working behind shared IPs. Drafts never appear in it.
/// </summary>
public static class ReleaseAtom
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private const string TagPath = "/releases/tag/";

    /// <summary>Releases in the feed, newest first. Entries without a release tag link are skipped.</summary>
    /// <exception cref="FormatException">The text isn't an Atom feed.</exception>
    public static IReadOnlyList<AtomRelease> Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Not an Atom feed.", ex);
        }

        if (doc.Root?.Name != Atom + "feed")
        {
            throw new FormatException("Not an Atom feed.");
        }

        var releases = new List<AtomRelease>();
        foreach (var entry in doc.Root.Elements(Atom + "entry"))
        {
            string? href = entry.Elements(Atom + "link").Select(l => (string?)l.Attribute("href")).FirstOrDefault(h => h is not null);
            int at = href?.IndexOf(TagPath, StringComparison.Ordinal) ?? -1;
            if (href is null || at < 0)
            {
                continue;
            }

            string tag = Uri.UnescapeDataString(href[(at + TagPath.Length)..].TrimEnd('/'));
            if (tag.Length == 0 || tag.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            DateTimeOffset updated = DateTimeOffset.TryParse(
                (string?)entry.Element(Atom + "updated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var u)
                ? u : DateTimeOffset.MinValue;
            releases.Add(new AtomRelease(tag, updated));
        }

        return [.. releases.OrderByDescending(r => r.Updated)];
    }
}
