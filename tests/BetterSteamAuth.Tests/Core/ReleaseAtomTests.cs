using BetterSteamAuth.Core.Updates;

namespace BetterSteamAuth.Tests.Core;

public class ReleaseAtomTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom" xml:lang="en-US">
          <id>tag:github.com,2008:https://github.com/owner/app/releases</id>
          <link type="text/html" rel="alternate" href="https://github.com/owner/app/releases"/>
          <title>Release notes from app</title>
          <updated>2026-11-19T10:00:00Z</updated>
          <entry>
            <id>tag:github.com,2008:Repository/1/v0.1.0-beta.1</id>
            <updated>2026-11-10T08:00:00Z</updated>
            <link rel="alternate" type="text/html" href="https://github.com/owner/app/releases/tag/v0.1.0-beta.1"/>
            <title>0.1.0-beta.1</title>
          </entry>
          <entry>
            <id>tag:github.com,2008:Repository/1/v1.0.0</id>
            <updated>2026-11-19T10:00:00Z</updated>
            <link rel="alternate" type="text/html" href="https://github.com/owner/app/releases/tag/v1.0.0"/>
            <title>1.0.0</title>
          </entry>
          <entry>
            <id>tag:github.com,2008:Repository/1/other</id>
            <updated>2026-11-18T10:00:00Z</updated>
            <link rel="alternate" type="text/html" href="https://github.com/owner/app/commits"/>
          </entry>
        </feed>
        """;

    [Fact]
    public void Reads_tags_newest_first_and_marks_betas()
    {
        var releases = ReleaseAtom.Parse(Feed);

        Assert.Equal(["v1.0.0", "v0.1.0-beta.1"], releases.Select(r => r.Tag));
        Assert.False(releases[0].IsPrerelease);
        Assert.True(releases[1].IsPrerelease);
    }

    [Theory]
    [InlineData("<html><body>rate limited</body></html>")]
    [InlineData("not xml")]
    public void Rejects_anything_but_an_atom_feed(string text)
        => Assert.Throws<FormatException>(() => ReleaseAtom.Parse(text));
}
