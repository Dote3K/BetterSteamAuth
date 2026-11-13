using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterSteamAuth.Core.Security;

/// <summary>
/// On-disk form of an encrypted maFile:
/// <c>{ "v": 2, "alg": "AES-256-GCM", "nonce": "…", "data": "…" }</c>.
/// The SteamID64 is bound as associated data, so ciphertext can't be moved between files.
/// </summary>
public sealed record MaFileEnvelope(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("alg")] string Algorithm,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("data")] string Data)
{
    public const int CurrentVersion = 2;
    public const string AesGcm = "AES-256-GCM";

    public static MaFileEnvelope Encrypt(ReadOnlySpan<byte> dataKey, ulong steamId, ReadOnlySpan<byte> plaintext)
    {
        var (nonce, ciphertext) = VaultCrypto.Seal(dataKey, plaintext, AssociatedData(steamId));
        return new MaFileEnvelope(CurrentVersion, AesGcm, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext));
    }

    public byte[] Decrypt(ReadOnlySpan<byte> dataKey, ulong steamId)
    {
        if (Version != CurrentVersion || Algorithm != AesGcm)
        {
            throw new NotSupportedException($"Unsupported maFile envelope v{Version} ({Algorithm}).");
        }

        return VaultCrypto.Open(dataKey, Convert.FromBase64String(Nonce), Convert.FromBase64String(Data), AssociatedData(steamId));
    }

    /// <summary>Returns the envelope if <paramref name="json"/> is one, or null for a plain maFile.</summary>
    public static MaFileEnvelope? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out _)
                || !root.TryGetProperty("data", out _))
            {
                return null;
            }

            return root.Deserialize<MaFileEnvelope>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    private static byte[] AssociatedData(ulong steamId)
        => Encoding.ASCII.GetBytes(steamId.ToString(CultureInfo.InvariantCulture));
}
