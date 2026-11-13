using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BetterSteamAuth.Core.Guard;

/// <summary>
/// Steam Guard mobile authenticator codes: TOTP (HMAC-SHA1, 30 s period) rendered
/// as 5 characters from Steam's own alphabet.
/// </summary>
public static class SteamGuardCode
{
    public const int PeriodSeconds = 30;
    public const int Length = 5;

    private const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";

    /// <summary>Generates the code for the given Steam time (Unix seconds).</summary>
    public static string Generate(ReadOnlySpan<byte> sharedSecret, long steamTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steamTime);

        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, steamTime / PeriodSeconds);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        #pragma warning disable CA5350 // Steam's protocol mandates HMAC-SHA1.
        HMACSHA1.HashData(sharedSecret, counter, hash);
        #pragma warning restore CA5350

        int offset = hash[^1] & 0x0F;
        uint value = BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFF_FFFF;

        Span<char> code = stackalloc char[Length];
        for (int i = 0; i < Length; i++)
        {
            code[i] = Alphabet[(int)(value % (uint)Alphabet.Length)];
            value /= (uint)Alphabet.Length;
        }

        return new string(code);
    }

    /// <summary>Generates the code from a base64 shared secret as stored in a maFile.</summary>
    public static string Generate(string sharedSecretBase64, long steamTime)
        => Generate(Convert.FromBase64String(sharedSecretBase64), steamTime);

    /// <summary>Seconds until the code for <paramref name="steamTime"/> rotates (1–30).</summary>
    public static int SecondsRemaining(long steamTime)
        => PeriodSeconds - (int)(steamTime % PeriodSeconds);
}
