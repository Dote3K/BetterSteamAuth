using System.Security.Cryptography;
using System.Text;
using BetterSteamAuth.Core.Updates;

namespace BetterSteamAuth.Tests.Core;

// Keys are generated per test; nothing here is a real release key.
public class ReleaseSignatureTests
{
    private static readonly byte[] Sums = Encoding.UTF8.GetBytes(
        "1111111111111111111111111111111111111111111111111111111111111111  BetterSteamAuth-1.0.0-full.nupkg\n"
        + "2222222222222222222222222222222222222222222222222222222222222222  BetterSteamAuth-win-Setup.exe\n");

    [Fact]
    public void Verifies_with_any_trusted_key_and_rejects_tampering_or_unknown_keys()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = ReleaseSignature.Sign(Sums, signer);

        // Rotation: the old and new key are both trusted for a while.
        string[] trusted = [ReleaseSignature.ExportPublicKey(other), ReleaseSignature.ExportPublicKey(signer)];
        Assert.True(ReleaseSignature.Verify(Sums, signature, trusted));

        byte[] tampered = (byte[])Sums.Clone();
        tampered[0] = (byte)'3';
        Assert.False(ReleaseSignature.Verify(tampered, signature, trusted));
        Assert.False(ReleaseSignature.Verify(Sums, signature, [ReleaseSignature.ExportPublicKey(other)]));
        Assert.False(ReleaseSignature.Verify(Sums, signature, []));
    }

    // A mistyped key would make the app reject every update without any build error.
    [Fact]
    public void Trusted_release_keys_are_valid_p256_public_keys()
    {
        Assert.NotEmpty(ReleaseKeys.Trusted);
        foreach (string publicKey in ReleaseKeys.Trusted)
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out int read);
            Assert.Equal(Convert.FromBase64String(publicKey).Length, read);
            Assert.Equal("nistP256", key.ExportParameters(false).Curve.Oid.FriendlyName);
        }
    }

    [Fact]
    public void Parses_sha256sum_output()
    {
        var sums = ReleaseSignature.ParseSums(Sums);

        Assert.Equal(2, sums.Count);
        Assert.Equal(new string('2', 64), sums["BetterSteamAuth-win-Setup.exe"]);
    }

    // 1.0.0 → 1.3.0 skips two releases: one delta per release, each listed only in its own release's SHA256SUMS.
    [Fact]
    public async Task Update_that_skips_versions_checks_each_delta_against_its_own_release()
    {
        string Hash(char c) => new(c, 64);
        var signed = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["1.1.0"] = new Dictionary<string, string> { ["App-1.1.0-delta.nupkg"] = Hash('a') },
            ["1.2.0"] = new Dictionary<string, string> { ["App-1.2.0-delta.nupkg"] = Hash('b') },
            ["1.3.0"] = new Dictionary<string, string> { ["App-1.3.0-delta.nupkg"] = Hash('c'), ["App-1.3.0-full.nupkg"] = Hash('d') },
        };
        var fetched = new List<string>();
        Task<IReadOnlyDictionary<string, string>> Fetch(string version)
        {
            fetched.Add(version);
            return Task.FromResult(signed[version]);
        }

        ReleaseFile[] files =
        [
            new("App-1.1.0-delta.nupkg", "1.1.0", Hash('a')),
            new("App-1.2.0-delta.nupkg", "1.2.0", Hash('B')), // feeds may use upper-case hex
            new("App-1.3.0-delta.nupkg", "1.3.0", Hash('c')),
            new("App-1.3.0-full.nupkg", "1.3.0", Hash('d')),
        ];
        Assert.Null(await SignedRelease.FindUnsignedAsync(files, Fetch));
        Assert.Equal(["1.1.0", "1.2.0", "1.3.0"], fetched);

        // A delta whose hash differs from its release's signed list, or that has no hash, is rejected.
        files[1] = files[1] with { Sha256 = Hash('e') };
        Assert.Equal("App-1.2.0-delta.nupkg", await SignedRelease.FindUnsignedAsync(files, Fetch));
        files[1] = files[1] with { Sha256 = null };
        Assert.Equal("App-1.2.0-delta.nupkg", await SignedRelease.FindUnsignedAsync(files, Fetch));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA  a.zip")] // upper case
    [InlineData("aaaa  a.zip")] // short hash
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  ../a.zip")] // path
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  a.zip\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  a.zip")] // repeated
    public void Rejects_malformed_sums(string text)
        => Assert.Throws<FormatException>(() => ReleaseSignature.ParseSums(Encoding.UTF8.GetBytes(text)));
}
