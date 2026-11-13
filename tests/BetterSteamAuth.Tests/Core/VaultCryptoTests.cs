using System.Security.Cryptography;
using System.Text;
using BetterSteamAuth.Core.Security;

namespace BetterSteamAuth.Tests.Core;

public class VaultCryptoTests
{
    // Small parameters keep tests fast; production uses KdfParameters.Default.
    private static readonly KdfParameters FastKdf = new(MemoryKiB: 1024, Iterations: 1, Parallelism: 1);

    [Fact]
    public void Wrapped_data_key_unwraps_with_the_right_password()
    {
        byte[] dek = VaultCrypto.NewDataKey();

        var wrapped = VaultCrypto.WrapDataKey(dek, "correct horse", FastKdf);

        Assert.Equal(dek, VaultCrypto.UnwrapDataKey(wrapped, "correct horse"));
    }

    [Fact]
    public void Unwrap_with_wrong_password_throws_WrongPasswordException()
    {
        var wrapped = VaultCrypto.WrapDataKey(VaultCrypto.NewDataKey(), "correct horse", FastKdf);

        Assert.Throws<WrongPasswordException>(() => VaultCrypto.UnwrapDataKey(wrapped, "battery staple"));
    }

    [Fact]
    public void Wrapping_twice_uses_fresh_salt_and_nonce()
    {
        byte[] dek = VaultCrypto.NewDataKey();

        var a = VaultCrypto.WrapDataKey(dek, "pw", FastKdf);
        var b = VaultCrypto.WrapDataKey(dek, "pw", FastKdf);

        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Nonce, b.Nonce);
    }
}

public class MaFileEnvelopeTests
{
    private const ulong SteamId = 76561198000000001;
    private static readonly byte[] Plaintext = Encoding.UTF8.GetBytes("""{"shared_secret":"fake"}""");

    [Fact]
    public void Roundtrip_restores_plaintext()
    {
        byte[] dek = VaultCrypto.NewDataKey();

        var envelope = MaFileEnvelope.Encrypt(dek, SteamId, Plaintext);
        var parsed = MaFileEnvelope.TryParse(envelope.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(Plaintext, parsed.Decrypt(dek, SteamId));
    }

    [Fact]
    public void Decrypt_for_another_account_fails()
    {
        byte[] dek = VaultCrypto.NewDataKey();
        var envelope = MaFileEnvelope.Encrypt(dek, SteamId, Plaintext);

        Assert.ThrowsAny<CryptographicException>(() => envelope.Decrypt(dek, SteamId + 1));
    }

    [Fact]
    public void Tampered_data_fails()
    {
        byte[] dek = VaultCrypto.NewDataKey();
        var envelope = MaFileEnvelope.Encrypt(dek, SteamId, Plaintext);
        byte[] data = Convert.FromBase64String(envelope.Data);
        data[0] ^= 0xFF;

        var tampered = envelope with { Data = Convert.ToBase64String(data) };

        Assert.ThrowsAny<CryptographicException>(() => tampered.Decrypt(dek, SteamId));
    }

    [Fact]
    public void TryParse_returns_null_for_plain_maFile()
        => Assert.Null(MaFileEnvelope.TryParse("""{"shared_secret":"abc","account_name":"x"}"""));
}
