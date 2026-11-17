using System.Security.Cryptography;
using System.Text;

namespace BetterSteamAuth.Core.Updates;

/// <summary>
/// Our own signature over a release's SHA256SUMS: ECDSA P-256 with SHA-256, made offline with a key that never
/// touches GitHub. The app refuses an update unless the signature verifies with one of <see cref="ReleaseKeys.Trusted"/>
/// and every file it downloads is listed with the same hash.
/// </summary>
public static class ReleaseSignature
{
    public const string SumsFileName = "SHA256SUMS";
    public const string SignatureFileName = "SHA256SUMS.sig";

    /// <summary>Signs the exact bytes of SHA256SUMS. The signature is IEEE P1363 (64 bytes).</summary>
    public static byte[] Sign(ReadOnlySpan<byte> sums, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.SignData(sums, HashAlgorithmName.SHA256);
    }

    /// <summary>True if <paramref name="signature"/> is valid for <paramref name="sums"/> under any of the public keys.</summary>
    /// <param name="publicKeys">Base64 SubjectPublicKeyInfo of each trusted P-256 key.</param>
    public static bool Verify(ReadOnlySpan<byte> sums, ReadOnlySpan<byte> signature, IEnumerable<string> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        foreach (string publicKey in publicKeys)
        {
            using var key = ImportPublicKey(publicKey);
            if (key.VerifyData(sums, signature, HashAlgorithmName.SHA256))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Base64 SubjectPublicKeyInfo of a key, the form <see cref="ReleaseKeys.Trusted"/> holds.</summary>
    public static string ExportPublicKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Parses <c>sha256sum</c> output ("&lt;64 hex&gt;  &lt;file name&gt;" per line) into lower-case hashes by file
    /// name. Throws <see cref="FormatException"/> on anything else, including duplicate names.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseSums(ReadOnlySpan<byte> sums)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in Encoding.UTF8.GetString(sums).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ' || !IsLowerHex(line.AsSpan(0, 64)))
            {
                throw new FormatException("Not a SHA256SUMS line.");
            }

            string name = line[66..];
            if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal)
                || !result.TryAdd(name, line[..64]))
            {
                throw new FormatException("Bad or repeated file name in SHA256SUMS.");
            }
        }

        return result;
    }

    /// <summary>Lower-case hex SHA-256 of a stream, as SHA256SUMS lists it.</summary>
    public static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken = default)
        => Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

    private static ECDsa ImportPublicKey(string base64)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64), out _);
            if (key.KeySize != 256)
            {
                throw new CryptographicException("Release keys are P-256.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static bool IsLowerHex(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
