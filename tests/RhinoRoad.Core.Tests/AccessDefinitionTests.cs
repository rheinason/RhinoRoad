using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class AccessDefinitionTests
{
    [Fact]
    public void Json_round_trip_is_invariant_and_preserves_exact_numbers()
    {
        var source = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var obstacle = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
        var definition = new VehicleAccessDefinition(
            VehicleAccessDefinition.CurrentSchemaVersion,
            source,
            Guid.Parse("99999999-8888-7777-6666-555555555555"),
            PathSourceKind.Interactive,
            "PV",
            "B",
            TravelDirection.Reverse,
            new ManoeuvreDefinition(
                ManoeuvreDefinition.CurrentSchemaVersion,
                new Point3(1.2345678901234567, -2.345678901234567, 3.45678901234567),
                Math.PI - 1e-13,
                TravelDirection.Reverse,
                [new ManoeuvreControl(new Point3(4.5678901234567, 5.678901234567, 0.0), TravelDirection.Reverse, ManoeuvreControlKind.Finish)]),
            0.30000000000000004,
            RoadEdgeMethod.Both,
            3.2500000000000004,
            2.7499999999999996,
            true,
            8.125,
            true,
            false,
            FootprintMode.AtInterval,
            1.9999999999999998,
            false,
            true,
            [obstacle],
            [],
            new Dictionary<Guid, string> { [obstacle] = "ABC" },
            "DEF",
            "analysis");

        var json = AccessDefinitionSerializer.Serialize(definition);
        var restored = AccessDefinitionSerializer.Deserialize(json);

        Assert.Equal(json, AccessDefinitionSerializer.Serialize(restored));
        Assert.Equal(definition.Manoeuvre!.StartPositionMetres.X, restored.Manoeuvre!.StartPositionMetres.X);
        Assert.Equal(definition.Manoeuvre.StartHeadingRadians, restored.Manoeuvre.StartHeadingRadians);
        Assert.Equal(definition.ClearanceMetres, restored.ClearanceMetres);
        Assert.Equal(definition.LeftWidthMetres, restored.LeftWidthMetres);
        Assert.Equal(definition.FootprintIntervalMetres, restored.FootprintIntervalMetres);
        Assert.Equal("ABC", restored.ReferenceFingerprints[obstacle]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"schemaVersion\":99}")]
    public void Malformed_or_unknown_metadata_is_rejected(string json)
    {
        Assert.False(AccessDefinitionSerializer.TryDeserialize(json, out var definition, out var error));
        Assert.Null(definition);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Reconcile_preserves_intent_through_moves_insertions_and_deletions()
    {
        var stored = new ManoeuvreDefinition(
            1,
            new Point3(0, 0, 0),
            0,
            TravelDirection.Forward,
            [
                new ManoeuvreControl(new Point3(10, 0, 0), TravelDirection.Forward),
                new ManoeuvreControl(new Point3(10, 10, 0), TravelDirection.Reverse),
                new ManoeuvreControl(new Point3(20, 10, 0), TravelDirection.Reverse, ManoeuvreControlKind.Finish)
            ]);

        var moved = ManoeuvreControlReconciler.Reconcile(stored,
        [
            new Point3(1, 2, 0),
            new Point3(100, 100, 0),
            new Point3(10, 10, 0),
            new Point3(20, 11, 0)
        ]);
        Assert.Equal(new Point3(1, 2, 0), moved.StartPositionMetres);
        Assert.Equal(TravelDirection.Forward, moved.Controls[0].Direction);

        var inserted = ManoeuvreControlReconciler.Reconcile(stored,
        [
            new Point3(0, 0, 0), new Point3(10, 0, 0), new Point3(10, 5, 0),
            new Point3(10, 10, 0), new Point3(20, 10, 0)
        ]);
        Assert.Equal(4, inserted.Controls.Count);
        Assert.Equal(TravelDirection.Forward, inserted.Controls[1].Direction);
        Assert.Equal(ManoeuvreControlKind.Aim, inserted.Controls[1].Kind);

        var removed = ManoeuvreControlReconciler.Reconcile(stored,
            [new Point3(0, 0, 0), new Point3(10, 0, 0), new Point3(20, 11, 0)]);
        Assert.Equal(2, removed.Controls.Count);
        Assert.Equal(TravelDirection.Forward, removed.Controls[0].Direction);
        Assert.Equal(TravelDirection.Reverse, removed.Controls[1].Direction);
        Assert.Equal(ManoeuvreControlKind.Finish, removed.Controls[1].Kind);
    }

    [Fact]
    public void Finish_displaced_from_the_end_becomes_aim()
    {
        var stored = new ManoeuvreDefinition(1, new Point3(0, 0, 0), 0, TravelDirection.Forward,
            [new ManoeuvreControl(new Point3(10, 0, 0), TravelDirection.Forward, ManoeuvreControlKind.Finish)]);
        var reconciled = ManoeuvreControlReconciler.Reconcile(stored,
            [new Point3(0, 0, 0), new Point3(10, 0, 0), new Point3(20, 0, 0)]);
        Assert.All(reconciled.Controls, control => Assert.Equal(ManoeuvreControlKind.Aim, control.Kind));
    }

    [Fact]
    public void Changing_start_direction_flips_every_leg_and_preserves_reversals()
    {
        var stored = new ManoeuvreDefinition(1, new Point3(0, 0, 0), 0, TravelDirection.Forward,
        [
            new ManoeuvreControl(new Point3(10, 0, 0), TravelDirection.Forward),
            new ManoeuvreControl(new Point3(5, 5, 0), TravelDirection.Reverse),
            new ManoeuvreControl(new Point3(20, 5, 0), TravelDirection.Forward, ManoeuvreControlKind.Finish)
        ]);

        var changed = ManoeuvreControlReconciler.WithStartDirection(stored, TravelDirection.Reverse);

        Assert.Equal(TravelDirection.Reverse, changed.StartDirection);
        Assert.Equal(
            [TravelDirection.Reverse, TravelDirection.Forward, TravelDirection.Reverse],
            changed.Controls.Select(control => control.Direction));
        Assert.Equal(ManoeuvreControlKind.Finish, changed.Controls[^1].Kind);
    }
}
