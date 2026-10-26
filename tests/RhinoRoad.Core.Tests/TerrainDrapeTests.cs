using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class TerrainDrapeTests
{
    private static IReadOnlyList<RouteSample> Line(int count) => Enumerable.Range(0, count)
        .Select(index => new RouteSample(index, new Point3(index, 0.0, 5.0), 0.0, 0.0, TravelDirection.Forward))
        .ToArray();

    [Fact]
    public void Heights_replace_Z_and_leave_plan_position_alone()
    {
        var (route, missed) = TerrainDrape.Apply(Line(3), [1.0, 2.0, 3.0]);

        Assert.Equal(0, missed);
        Assert.Equal([1.0, 2.0, 3.0], route.Select(sample => sample.PositionMetres.Z));
        Assert.Equal([0.0, 1.0, 2.0], route.Select(sample => sample.PositionMetres.X));
    }

    [Fact]
    public void A_miss_keeps_the_previous_height_and_a_leading_miss_takes_the_first_hit()
    {
        var (route, missed) = TerrainDrape.Apply(Line(5), [null, 2.0, null, null, 4.0]);

        Assert.Equal(3, missed);
        Assert.Equal([2.0, 2.0, 2.0, 2.0, 4.0], route.Select(sample => sample.PositionMetres.Z));
    }

    [Fact]
    public void A_route_wholly_off_the_terrain_is_unchanged()
    {
        var original = Line(3);
        var (route, missed) = TerrainDrape.Apply(original, [null, null, null]);

        Assert.Equal(3, missed);
        Assert.Same(original, route);
    }

    [Fact]
    public void Mismatched_counts_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => TerrainDrape.Apply(Line(3), [1.0]));
    }

    [Fact]
    public void Draped_slope_is_seen_by_the_grade_check_on_a_flat_route()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
        var route = Enumerable.Range(0, 301)
            .Select(index => new RouteSample(index * 0.1, new Point3(index * 0.1, 0.0, 0.0), 0.0, 0.0, TravelDirection.Forward))
            .ToArray();
        var (draped, _) = TerrainDrape.Apply(route, route.Select(sample => (double?)(sample.PositionMetres.X * 0.10)).ToArray());

        var result = new VehicleAccessAnalyzer().Analyze(vehicle, vehicle.DrivingModes["A"], draped, maximumAbsoluteGrade: 0.08);

        Assert.Contains(result.Violations, violation => violation.Kind == ViolationKind.Grade);
    }

    [Fact]
    public void Miss_warning_counts_stations()
    {
        Assert.Null(TerrainDrape.MissWarning(0));
        Assert.Contains("1 station is off the terrain", TerrainDrape.MissWarning(1));
        Assert.Contains("4 stations are off the terrain", TerrainDrape.MissWarning(4));
    }
}
