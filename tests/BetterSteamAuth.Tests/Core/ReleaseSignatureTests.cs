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

    [Fact]
    public void Parses_sha256sum_output()
    {
        var sums = ReleaseSignature.ParseSums(Sums);

        Assert.Equal(2, sums.Count);
        Assert.Equal(new string('2', 64), sums["BetterSteamAuth-win-Setup.exe"]);
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA  a.zip")] // upper case
    [InlineData("aaaa  a.zip")] // short hash
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  ../a.zip")] // path
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  a.zip\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  a.zip")] // repeated
    public void Rejects_malformed_sums(string text)
        => Assert.Throws<FormatException>(() => ReleaseSignature.ParseSums(Encoding.UTF8.GetBytes(text)));
}
