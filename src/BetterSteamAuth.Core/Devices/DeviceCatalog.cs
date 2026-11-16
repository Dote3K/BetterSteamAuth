using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BetterSteamAuth.Core.Devices;

/// <summary>
/// The device identity an account linked in this app presents to Steam. Generated once, never changed.
/// </summary>
public sealed record DeviceProfile(string Model, string FriendlyName, string AndroidVersion, string BuildId, byte[] MachineId)
{
    /// <summary>User-Agent in the format Android's HTTP stack sends.</summary>
    public string UserAgent => $"Dalvik/2.1.0 (Linux; U; Android {AndroidVersion}; {Model} Build/{BuildId})";

    /// <summary>Android major version, e.g. 14.</summary>
    public int AndroidMajor => int.TryParse(AndroidVersion.Split('.')[0], out int major) ? major : 0;

    /// <summary>A new authenticator device ID: <c>android:</c> plus a random UUID, never derived from the SteamID.</summary>
    public static string NewDeviceId() => "android:" + Guid.NewGuid().ToString("D");
}

/// <summary>Where a catalog comes from: the file shipped with the app now, a downloaded one later.</summary>
public interface IDeviceCatalogSource
{
    /// <summary>The catalog as JSON, or null when this source has none.</summary>
    string? Read();
}

/// <summary>The catalog shipped inside this assembly.</summary>
public sealed class ShippedDeviceCatalogSource : IDeviceCatalogSource
{
    public string? Read()
    {
        using var stream = typeof(DeviceCatalog).Assembly.GetManifestResourceStream("BetterSteamAuth.Core.Devices.devices.json");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Curated real Android devices to pick generated profiles from. Only valid model/version pairs are
/// picked. A changed or broken catalog never affects existing profiles, which store their own values.
/// </summary>
public sealed partial class DeviceCatalog
{
    public const int SupportedSchema = 1;

    private DeviceCatalog(string version, IReadOnlyList<Device> devices)
    {
        Version = version;
        Devices = devices;
    }

    /// <summary>Release date of the catalog (<c>yyyy-MM-dd</c>); newer wins.</summary>
    public string Version { get; }

    public IReadOnlyList<Device> Devices { get; }

    /// <summary>The newest valid catalog among the sources. Invalid or unsupported ones are skipped.</summary>
    public static DeviceCatalog Load(params IDeviceCatalogSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        DeviceCatalog? best = null;
        foreach (var source in sources)
        {
            if (TryParse(source.Read(), out var catalog)
                && (best is null || string.CompareOrdinal(catalog!.Version, best.Version) > 0))
            {
                best = catalog;
            }
        }

        return best ?? throw new InvalidDataException("No valid device catalog.");
    }

    /// <summary>Parses and validates a catalog. Any invalid entry rejects the whole catalog.</summary>
    public static bool TryParse(string? json, out DeviceCatalog? catalog)
    {
        catalog = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        CatalogFile? file;
        try
        {
            file = JsonSerializer.Deserialize<CatalogFile>(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (file is not { Schema: SupportedSchema, Version: { } version, Devices: { Count: > 0 } devices }
            || !DateOnly.TryParseExact(version, "yyyy-MM-dd", out _)
            || !devices.All(IsValid))
        {
            return false;
        }

        catalog = new DeviceCatalog(version, devices);
        return true;
    }

    /// <summary>Picks a device (by weight), then one of its Android builds, and makes a new profile.</summary>
    public DeviceProfile Pick()
    {
        int total = Devices.Sum(d => d.Weight);
        int roll = RandomNumberGenerator.GetInt32(total);
        var device = Devices[^1];
        foreach (var candidate in Devices)
        {
            if (roll < candidate.Weight)
            {
                device = candidate;
                break;
            }

            roll -= candidate.Weight;
        }

        var build = device.Builds[RandomNumberGenerator.GetInt32(device.Builds.Count)];
        return new DeviceProfile(device.Model, device.Name, build.Android, build.Id, RandomNumberGenerator.GetBytes(16));
    }

    private static bool IsValid(Device? d)
        => d is { Model: { } model, Name: { } name, Builds: { Count: > 0 } builds }
           && d.Weight is >= 1 and <= 100
           && SafeText().IsMatch(model) && SafeText().IsMatch(name)
           && builds.All(b => b is { Android: { } a, Id: { } id } && AndroidVersion().IsMatch(a) && BuildId().IsMatch(id));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._+-]{0,39}$")]
    private static partial Regex SafeText();

    [GeneratedRegex(@"^\d{1,2}(\.\d{1,2})?$")]
    private static partial Regex AndroidVersion();

    [GeneratedRegex(@"^[A-Z0-9]{4}\.\d{6}\.\d{3}(\.[A-Z0-9]{1,4})?$")]
    private static partial Regex BuildId();

    public sealed record Build(
        [property: JsonPropertyName("android")] string Android,
        [property: JsonPropertyName("id")] string Id);

    public sealed record Device(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("weight")] int Weight,
        [property: JsonPropertyName("builds")] IReadOnlyList<Build> Builds);

    private sealed record CatalogFile(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("devices")] IReadOnlyList<Device>? Devices);
}
