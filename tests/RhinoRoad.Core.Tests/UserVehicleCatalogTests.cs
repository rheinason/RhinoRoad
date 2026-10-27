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

    /// <summary>A file the loader must report rather than crash on; the good file beside it still loads.</summary>
    private static void AssertReportedNotThrown(string json, string expectedFragment)
    {
        var load = Load(("bad.json", json), ("good.json", WithId("GOOD")));

        var error = Assert.Single(load.Errors);
        Assert.StartsWith("bad.json", error);
        Assert.Contains(expectedFragment, error);
        Assert.Equal(ValidationStatus.UserDefined, load.Catalog.Get("GOOD").ValidationStatus);
        Assert.Equal(VehicleCatalog.LoadEmbedded().Vehicles.Count + 1, load.Catalog.Vehicles.Count);
    }

    private static string Edited(Action<JsonNode> edit)
    {
        var root = JsonNode.Parse(WithId("BAD"))!;
        edit(root["vehicles"]![0]!);
        return root.ToJsonString();
    }

    [Fact]
    public void A_null_vehicles_array_is_a_file_error() =>
        AssertReportedNotThrown("{\"vehicles\":null}", "\"vehicles\" is null");

    [Fact]
    public void A_null_document_is_a_file_error() =>
        AssertReportedNotThrown("null", "empty");

    [Fact]
    public void A_null_vehicle_entry_is_skipped_with_its_position() =>
        AssertReportedNotThrown("{\"vehicles\":[null]}", "vehicle 1 is null");

    [Fact]
    public void A_null_driving_mode_is_a_vehicle_error() =>
        AssertReportedNotThrown(Edited(vehicle => vehicle["drivingModes"]!["A"] = null), "driving mode 'A' is null");

    [Fact]
    public void Null_driving_modes_are_a_vehicle_error() =>
        AssertReportedNotThrown(Edited(vehicle => vehicle["drivingModes"] = null), "drivingModes is null");

    [Fact]
    public void A_null_outline_point_is_a_vehicle_error() =>
        AssertReportedNotThrown(Edited(vehicle => vehicle["bodyOutline"]![1] = null), "point 2 of bodyOutline");

    [Fact]
    public void A_short_outline_point_is_a_vehicle_error() =>
        AssertReportedNotThrown(Edited(vehicle => vehicle["bodyOutline"]![0] = new JsonArray(1.0)), "point 1 of bodyOutline");

    [Fact]
    public void A_null_outline_is_a_vehicle_error() =>
        AssertReportedNotThrown(Edited(vehicle => vehicle["bodyOutline"] = null), "bodyOutline is null");

    [Fact]
    public void A_null_towed_unit_is_a_vehicle_error() =>
        AssertReportedNotThrown(Edited(vehicle => vehicle["towedUnits"] = new JsonArray((JsonNode?)null)), "towed unit 1 is null");

    [Fact]
    public void Null_strings_and_optional_lists_read_as_absent()
    {
        var json = Edited(vehicle =>
        {
            vehicle["name"] = null;
            vehicle["version"] = null;
            vehicle["source"] = null;
            vehicle["validationNotes"] = null;
            vehicle["towedUnits"] = null;
            vehicle["protrusions"] = null;
        });

        var load = Load(("nulls.json", json));

        Assert.Empty(load.Errors);
        var vehicle = load.Catalog.Get("BAD");
        Assert.Equal(string.Empty, vehicle.Name);
        Assert.Empty(vehicle.TowedUnits);
        Assert.NotNull(vehicle.Source);
    }

    [Theory]
    [InlineData("maximumWheelAngleDegrees", 95.0, "wheel angle")]
    [InlineData("maximumWheelAngleDegrees", 90.0, "wheel angle")]
    [InlineData("maximumWheelAngleDegrees", 0.0, "wheel angle")]
    [InlineData("defaultClearanceMetres", -0.1, "clearance")]
    public void Out_of_range_mode_values_are_rejected(string field, double value, string expectedFragment) =>
        AssertReportedNotThrown(Edited(vehicle => vehicle["drivingModes"]!["A"]![field] = value), expectedFragment);

    [Theory]
    [InlineData("frontOverhangMetres")]
    [InlineData("rearOverhangMetres")]
    public void A_negative_overhang_is_rejected(string field) =>
        AssertReportedNotThrown(Edited(vehicle => vehicle[field] = -0.5), "overhangs cannot be negative");

    [Fact]
    public void Every_built_in_preset_passes_the_tightened_validation()
    {
        foreach (var vehicle in VehicleCatalog.LoadEmbedded().Vehicles) vehicle.Validate();
    }
}
