using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BetterSteamAuth.Core.Guard;

/// <summary>Tags Steam expects when signing mobile confirmation requests.</summary>
public static class ConfirmationTag
{
    public const string List = "list";
    public const string Details = "details";
    public const string Accept = "accept";
    public const string Reject = "reject";
}

/// <summary>
/// The <c>k</c> parameter of <c>mobileconf</c> requests:
/// Base64(HMAC-SHA1(identity_secret, time as 8-byte big-endian + tag)).
/// </summary>
public static class ConfirmationKey
{
    private const int MaxTagBytes = 32;

    public static string Generate(ReadOnlySpan<byte> identitySecret, long steamTime, string tag)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steamTime);
        ArgumentNullException.ThrowIfNull(tag);

        byte[] tagBytes = Encoding.UTF8.GetBytes(tag);
        int tagLength = Math.Min(tagBytes.Length, MaxTagBytes);

        Span<byte> data = stackalloc byte[sizeof(long) + MaxTagBytes];
        BinaryPrimitives.WriteInt64BigEndian(data, steamTime);
        tagBytes.AsSpan(0, tagLength).CopyTo(data[sizeof(long)..]);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        #pragma warning disable CA5350 // Steam's protocol mandates HMAC-SHA1.
        HMACSHA1.HashData(identitySecret, data[..(sizeof(long) + tagLength)], hash);
        #pragma warning restore CA5350
        return Convert.ToBase64String(hash);
    }

    public static string Generate(string identitySecretBase64, long steamTime, string tag)
        => Generate(Convert.FromBase64String(identitySecretBase64), steamTime, tag);
}
