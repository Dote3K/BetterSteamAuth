using BetterSteamAuth.Core.Devices;

namespace BetterSteamAuth.Tests.Core;

public sealed class DeviceCatalogTests
{
    private sealed class JsonSource(string? json) : IDeviceCatalogSource
    {
        public string? Read() => json;
    }

    [Fact]
    public void Shipped_catalog_is_valid_and_picks_only_listed_model_and_build_pairs()
    {
        var catalog = DeviceCatalog.Load(new ShippedDeviceCatalogSource());

        for (int i = 0; i < 200; i++)
        {
            var profile = catalog.Pick();
            var device = catalog.Devices.Single(d => d.Model == profile.Model);
            Assert.Contains(device.Builds, b => b.Android == profile.AndroidVersion && b.Id == profile.BuildId);
            Assert.Equal(16, profile.MachineId.Length);
        }
    }

    [Fact]
    public void A_newer_but_invalid_catalog_falls_back_to_the_shipped_one()
    {
        const string bad = """{ "schema": 1, "version": "2099-01-01", "devices": [ { "model": "X<script>", "name": "X", "weight": 1, "builds": [ { "android": "14", "id": "UP1A.231005.007" } ] } ] }""";
        const string futureSchema = """{ "schema": 2, "version": "2099-01-01", "devices": [] }""";

        var catalog = DeviceCatalog.Load(new ShippedDeviceCatalogSource(), new JsonSource(bad), new JsonSource(futureSchema), new JsonSource(null));

        Assert.Equal(DeviceCatalog.Load(new ShippedDeviceCatalogSource()).Version, catalog.Version);
    }
}
