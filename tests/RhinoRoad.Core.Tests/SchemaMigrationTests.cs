using System.Text.Json;
using System.Text.Json.Nodes;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class SchemaMigrationTests
{
    private static readonly Guid Obstacle = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
    private static readonly Guid Boundary = Guid.Parse("BBBBBBBB-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

    /// <summary>
    /// The definition the frozen v1 fixture was serialized from, as it reads today: the same intent,
    /// at the current schema, with every property added since at its default.
    /// </summary>
    internal static VehicleAccessDefinition FixtureDefinition() => new(
        VehicleAccessDefinition.CurrentSchemaVersion,
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Guid.Parse("99999999-8888-7777-6666-555555555555"),
        PathSourceKind.Interactive,
        "PV",
        "B",
        TravelDirection.Forward,
        new ManoeuvreDefinition(
            ManoeuvreDefinition.CurrentSchemaVersion,
            new Point3(1.5, -2.25, 0.0),
            0.5,
            TravelDirection.Forward,
            [
                new ManoeuvreControl(new Point3(10.0, 0.0, 0.0), TravelDirection.Forward),
                new ManoeuvreControl(new Point3(12.0, 3.0, 0.0), TravelDirection.Forward, ManoeuvreControlKind.Turn, Math.PI / 2.0),
                new ManoeuvreControl(new Point3(12.0, -4.0, 0.0), TravelDirection.Reverse, ManoeuvreControlKind.Aim, null, true),
                new ManoeuvreControl(new Point3(12.5, -9.0, 0.0), TravelDirection.Reverse, ManoeuvreControlKind.Finish, -Math.PI / 2.0)
            ]),
        0.3,
        RoadEdgeMethod.Both,
        3.25,
        2.75,
        true,
        8.0,
        true,
        true,
        FootprintMode.AtInterval,
        2.0,
        false,
        true,
        [Obstacle],
        [Boundary],
        new Dictionary<Guid, string> { [Obstacle] = "ABC", [Boundary] = "DEF" },
        "SRC",
        "analysis-1");

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "access-definition-v1.json");

    [Fact]
    public void V1_fixture_always_loads_and_round_trips()
    {
        var json = File.ReadAllText(FixturePath);
        var loaded = AccessDefinitionSerializer.Deserialize(json);

        // Read at the current schema, and saying that it was not written at it.
        Assert.Equal(VehicleAccessDefinition.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(ManoeuvreDefinition.CurrentSchemaVersion, loaded.Manoeuvre!.SchemaVersion);
        Assert.Equal(1, loaded.UpgradedFromSchema);
        Assert.Contains("RhinoRoad 0.5 or earlier", loaded.UpgradeWarning);
        Assert.Equal(PathSourceKind.Interactive, loaded.SourceKind);
        var controls = loaded.Manoeuvre!.Controls;
        Assert.Equal(4, controls.Count);
        Assert.Equal(ManoeuvreControlKind.Turn, controls[1].Kind);
        Assert.Equal(Math.PI / 2.0, controls[1].ExitHeadingRadians);
        Assert.Equal(TravelDirection.Reverse, controls[2].Direction);
        Assert.True(controls[2].FromStandstill);
        Assert.Equal(ManoeuvreControlKind.Finish, controls[3].Kind);
        Assert.Equal(-Math.PI / 2.0, controls[3].ExitHeadingRadians);
        // Written before a leg could have a speed of its own or be sealed.
        Assert.All(controls, control => Assert.Null(control.SpeedKilometresPerHour));
        Assert.All(controls, control => Assert.False(control.SealsLeg));
        Assert.Equal([Obstacle], loaded.ObstacleObjectIds);
        Assert.Equal([Boundary], loaded.AllowedBoundaryObjectIds);
        Assert.Equal("DEF", loaded.ReferenceFingerprints[Boundary]);

        // The frozen text is the format; what is written today must read back the same.
        Assert.Equal(AccessDefinitionSerializer.Serialize(loaded), AccessDefinitionSerializer.Serialize(
            AccessDefinitionSerializer.Deserialize(AccessDefinitionSerializer.Serialize(loaded))));
        Assert.Equal(AccessDefinitionSerializer.Serialize(FixtureDefinition()), AccessDefinitionSerializer.Serialize(loaded));
    }

    [Fact]
    public void A_road_saved_again_is_written_at_the_current_schema_and_no_longer_reads_as_upgraded()
    {
        var loaded = AccessDefinitionSerializer.Deserialize(File.ReadAllText(FixturePath));

        var saved = AccessDefinitionSerializer.Serialize(loaded);
        var root = JsonNode.Parse(saved)!.AsObject();
        var reread = AccessDefinitionSerializer.Deserialize(saved);

        Assert.Equal(2, VehicleAccessDefinition.CurrentSchemaVersion);
        Assert.Equal(2, ManoeuvreDefinition.CurrentSchemaVersion);
        Assert.Equal(VehicleAccessDefinition.CurrentSchemaVersion, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal(ManoeuvreDefinition.CurrentSchemaVersion, root["manoeuvre"]!["schemaVersion"]!.GetValue<int>());
        Assert.False(root.ContainsKey("upgradedFromSchema"));
        Assert.Null(reread.UpgradedFromSchema);
        Assert.Null(reread.UpgradeWarning);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":2,"manoeuvre":{"schemaVersion":2,"schemaVersion":2}}""")]
    public void A_duplicated_key_is_malformed_rather_than_an_escaping_exception(string json)
    {
        Assert.False(AccessDefinitionSerializer.TryDeserialize(json, out var definition, out var error));
        Assert.Null(definition);
        Assert.Contains("malformed", error);
    }

    [Fact]
    public void Missing_optional_properties_take_their_defaults()
    {
        var root = JsonNode.Parse(File.ReadAllText(FixturePath))!.AsObject();
        foreach (var control in root["manoeuvre"]!["controls"]!.AsArray())
        {
            var item = control!.AsObject();
            item.Remove("kind");
            item.Remove("exitHeadingRadians");
            item.Remove("fromStandstill");
        }

        var loaded = AccessDefinitionSerializer.Deserialize(root.ToJsonString());

        Assert.All(loaded.Manoeuvre!.Controls, control =>
        {
            Assert.Equal(ManoeuvreControlKind.Aim, control.Kind);
            Assert.Null(control.ExitHeadingRadians);
            Assert.False(control.FromStandstill);
        });
    }

    [Fact]
    public void A_document_from_a_newer_plugin_is_refused_with_a_clear_message()
    {
        var root = JsonNode.Parse(File.ReadAllText(FixturePath))!.AsObject();
        root["schemaVersion"] = VehicleAccessDefinition.CurrentSchemaVersion + 1;

        Assert.False(AccessDefinitionSerializer.TryDeserialize(root.ToJsonString(), out _, out var error));
        Assert.Contains("newer RhinoRoad", error);
    }

    [Fact]
    public void A_newer_manoeuvre_schema_is_refused()
    {
        var root = JsonNode.Parse(File.ReadAllText(FixturePath))!.AsObject();
        root["manoeuvre"]!["schemaVersion"] = ManoeuvreDefinition.CurrentSchemaVersion + 1;

        Assert.False(AccessDefinitionSerializer.TryDeserialize(root.ToJsonString(), out _, out var error));
        Assert.Contains("newer RhinoRoad", error);
    }

    [Fact]
    public void Upgraders_run_in_order_up_to_the_current_version()
    {
        var root = JsonNode.Parse("""{"schemaVersion":1,"name":"a","manoeuvre":{"schemaVersion":1}}""")!.AsObject();
        var order = new List<string>();
        var definitionUpgraders = new Dictionary<int, Action<JsonObject>>
        {
            [1] = node => { order.Add("d1"); node["b"] = node["name"]!.GetValue<string>() + "b"; },
            [2] = node => { order.Add("d2"); node["c"] = node["b"]!.GetValue<string>() + "c"; }
        };
        var manoeuvreUpgraders = new Dictionary<int, Action<JsonObject>>
        {
            [1] = node => { order.Add("m1"); node["upgraded"] = true; }
        };

        SchemaMigration.UpgradeDefinition(root, 3, 2, definitionUpgraders, manoeuvreUpgraders);

        Assert.Equal(["d1", "d2", "m1"], order);
        Assert.Equal(3, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal("abc", root["c"]!.GetValue<string>());
        Assert.Equal(2, root["manoeuvre"]!["schemaVersion"]!.GetValue<int>());
        Assert.True(root["manoeuvre"]!["upgraded"]!.GetValue<bool>());
    }

    [Fact]
    public void A_missing_upgrader_step_is_reported_rather_than_skipped()
    {
        var root = JsonNode.Parse("""{"schemaVersion":1}""")!.AsObject();
        var upgraders = new Dictionary<int, Action<JsonObject>> { [2] = _ => { } };

        var exception = Assert.Throws<InvalidDataException>(() => SchemaMigration.Upgrade(root, 3, upgraders, "thing"));
        Assert.Contains("no upgrade", exception.Message);
    }

    [Fact]
    public void A_missing_version_is_refused()
    {
        Assert.False(AccessDefinitionSerializer.TryDeserialize("{}", out _, out var error));
        Assert.Contains("schema version", error);
    }

    [Fact]
    public void Positional_record_with_missing_defaulted_parameters_deserializes()
    {
        // The behaviour the "missing optional property" guarantee rests on.
        var control = JsonSerializer.Deserialize<ManoeuvreControl>(
            """{"positionMetres":{"x":1,"y":2,"z":0},"direction":"Forward"}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

        Assert.NotNull(control);
        Assert.Equal(ManoeuvreControlKind.Aim, control.Kind);
        Assert.False(control.FromStandstill);
    }
}
