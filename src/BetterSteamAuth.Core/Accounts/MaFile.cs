using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterSteamAuth.Core.Accounts;

/// <summary>
/// An SDA-compatible maFile. Field names match SDA so plain files stay usable in SDA and ASF.
/// Unknown fields are kept in <see cref="Extra"/> and written back unchanged.
/// </summary>
public sealed class MaFile
{
    [JsonPropertyName("shared_secret")] public string? SharedSecret { get; set; }
    [JsonPropertyName("serial_number")] public string? SerialNumber { get; set; }
    [JsonPropertyName("revocation_code")] public string? RevocationCode { get; set; }
    [JsonPropertyName("uri")] public string? Uri { get; set; }
    [JsonPropertyName("server_time")] public long ServerTime { get; set; }
    [JsonPropertyName("account_name")] public string? AccountName { get; set; }
    [JsonPropertyName("token_gid")] public string? TokenGid { get; set; }
    [JsonPropertyName("identity_secret")] public string? IdentitySecret { get; set; }
    [JsonPropertyName("secret_1")] public string? Secret1 { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("fully_enrolled")] public bool FullyEnrolled { get; set; }
    [JsonPropertyName("Session")] public MaFileSession? Session { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// SteamID64 from the session, or from the legacy <c>SteamLoginSecure</c> cookie
    /// (<c>steamid%7C%7C…</c>) in old SDA files. Null if neither is present.
    /// </summary>
    [JsonIgnore]
    public ulong? SteamId
    {
        get
        {
            if (Session?.SteamId is > 0)
            {
                return Session.SteamId;
            }

            string? legacy = Session?.LegacyString("SteamLoginSecure");
            if (legacy is null)
            {
                return null;
            }

            int end = legacy.IndexOf('%', StringComparison.Ordinal);
            if (end < 0)
            {
                end = legacy.IndexOf('|', StringComparison.Ordinal);
            }

            return end > 0 && ulong.TryParse(legacy.AsSpan(0, end), out ulong id) ? id : null;
        }
    }

    public static MaFile Parse(string json)
        => JsonSerializer.Deserialize(json, MaFileJsonContext.Default.MaFile)
           ?? throw new JsonException("The maFile is empty.");

    public string ToJson() => JsonSerializer.Serialize(this, MaFileJsonContext.Default.MaFile);
}

public sealed class MaFileSession
{
    [JsonPropertyName("SteamID")] public ulong SteamId { get; set; }
    [JsonPropertyName("AccessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("RefreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("SessionID")] public string? SessionId { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal string? LegacyString(string name)
        => Extra is not null && Extra.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(MaFile))]
internal sealed partial class MaFileJsonContext : JsonSerializerContext;
