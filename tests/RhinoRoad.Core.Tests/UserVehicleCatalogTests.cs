using System.Text.Json.Nodes;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class UserVehicleCatalogTests
{
    private static string ExampleJson()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "samples", "user-vehicle-example.json")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "samples", "user-vehicle-example.json"));
    }

    private static string WithId(string id, string? status = null)
    {
        var root = JsonNode.Parse(ExampleJson())!;
        var vehicle = root["vehicles"]![0]!;
        vehicle["id"] = id;
        if (status is not null) vehicle["validationStatus"] = status;
        return root.ToJsonString();
    }

    private static UserCatalogLoad Load(params (string Label, string Json)[] sources) =>
        VehicleCatalog.LoadWithUserVehicles(sources.Select(item => new KeyValuePair<string, string>(item.Label, item.Json)));

    [Fact]
    public void ExampleFileLoadsAsUserDefinedWhateverItClaims()
    {
        var load = Load(("a.json", WithId("MY-LORRY", "ReferenceValidated")));

        Assert.Empty(load.Errors);
        Assert.Equal(ValidationStatus.UserDefined, load.Catalog.Get("MY-LORRY").ValidationStatus);
        Assert.Equal(VehicleCatalog.LoadEmbedded().Vehicles.Count + 1, load.Catalog.Vehicles.Count);
        Assert.NotEqual(ValidationStatus.UserDefined, load.Catalog.Get("PV").ValidationStatus);
    }

    [Fact]
    public void BuiltInIdCollisionIsRejectedWithAMessage()
    {
        var load = Load(("clash.json", WithId("pv")));

        var error = Assert.Single(load.Errors);
        Assert.Contains("clash.json", error);
        Assert.Contains("built-in", error);
        Assert.Equal(VehicleCatalog.LoadEmbedded().Vehicles.Count, load.Catalog.Vehicles.Count);
        Assert.NotEqual(ValidationStatus.UserDefined, load.Catalog.Get("PV").ValidationStatus);
    }

    [Fact]
    public void DuplicateUserIdIsRejected()
    {
        var load = Load(("a.json", WithId("X1")), ("b.json", WithId("X1")));

        Assert.Contains("another user vehicle", Assert.Single(load.Errors));
        Assert.Equal(VehicleCatalog.LoadEmbedded().Vehicles.Count + 1, load.Catalog.Vehicles.Count);
    }

    [Fact]
    public void MalformedAndInvalidFilesAreReportedNotThrown()
    {
        var invalid = JsonNode.Parse(WithId("BAD"))!;
        invalid["vehicles"]![0]!["wheelbaseMetres"] = -1;
        var load = Load(("broken.json", "{ not json"), ("empty.json", "{}"), ("invalid.json", invalid.ToJsonString()), ("good.json", WithId("GOOD")));

        Assert.Equal(3, load.Errors.Count);
        Assert.Contains(load.Errors, error => error.StartsWith("broken.json"));
        Assert.Contains(load.Errors, error => error.StartsWith("invalid.json") && error.Contains("BAD"));
        Assert.Equal(ValidationStatus.UserDefined, load.Catalog.Get("GOOD").ValidationStatus);
        Assert.Throws<KeyNotFoundException>(() => load.Catalog.Get("BAD"));
    }

    [Fact]
    public void ArticulatedUserVehicleKeepsItsTowedUnits()
    {
        var assembly = typeof(VehicleCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("vehicles.json", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var root = JsonNode.Parse(reader.ReadToEnd(), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
        var combination = root["vehicles"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "SVT")!.DeepClone();
        combination["id"] = "MY-ARTIC";
        var file = new JsonObject { ["vehicles"] = new JsonArray(combination) }.ToJsonString();

        var load = Load(("artic.json", file));

        Assert.Empty(load.Errors);
        var vehicle = load.Catalog.Get("MY-ARTIC");
        Assert.Equal(ValidationStatus.UserDefined, vehicle.ValidationStatus);
        Assert.True(vehicle.IsArticulated);
        Assert.Equal(load.Catalog.Get("SVT").TowedUnits.Count, vehicle.TowedUnits.Count);
        Assert.Equal(load.Catalog.Get("SVT").OverallLengthMetres, vehicle.OverallLengthMetres, 6);
    }

    [Fact]
    public void MissingUserDirectoryGivesTheBuiltInCatalog()
    {
        var load = VehicleCatalog.LoadWithUserDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Empty(load.Errors);
        Assert.Equal(VehicleCatalog.LoadEmbedded().Vehicles.Count, load.Catalog.Vehicles.Count);
    }

    [Fact]
    public void DirectoryLoadReadsEveryJsonFile()
    {
        var directory = Directory.CreateTempSubdirectory("rr-vehicles").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "one.json"), WithId("ONE"));
            File.WriteAllText(Path.Combine(directory, "bad.json"), "oops");
            File.WriteAllText(Path.Combine(directory, "note.txt"), "ignored");

            var load = VehicleCatalog.LoadWithUserDirectory(directory);

            Assert.Single(load.Errors);
            Assert.Equal(ValidationStatus.UserDefined, load.Catalog.Get("ONE").ValidationStatus);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
