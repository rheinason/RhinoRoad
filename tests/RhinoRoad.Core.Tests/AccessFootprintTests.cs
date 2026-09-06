using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class AccessFootprintTests
{
    private static IReadOnlyList<Point2> Box(double x, double y, double w, double h) =>
        [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];
    private static SweptRegion Rect(double x, double y, double w, double h) => new(Box(x, y, w, h), []);

    [Fact]
    public void Combined_footprint_preserves_disconnected_areas_and_does_not_double_count_overlap()
    {
        var result = AccessFootprint.Combine([Rect(0, 0, 4, 4), Rect(2, 0, 4, 4), Rect(10, 0, 2, 2)]);
        Assert.Equal(2, result.Count);
        Assert.Equal(28, result.Sum(r => r.AreaSquareMetres), 5);
        var intervals = AccessFootprint.Measure(result, new(-1, 1), new(13, 1));
        Assert.Equal(2, intervals.Count);
        Assert.Equal(6.0, intervals[0].WidthMetres, 6);
        Assert.Equal(2.0, intervals[1].WidthMetres, 6);
    }

    [Fact]
    public void Section_preserves_island_and_overlapping_journey_can_fill_part_of_it()
    {
        var ring = new SweptRegion(Box(0, 0, 10, 10), [Box(2, 2, 6, 6)]);
        var result = AccessFootprint.Combine([ring, Rect(4, 3, 2, 4)]);
        var intervals = AccessFootprint.Measure(result, new(-1, 5), new(11, 5));
        Assert.Equal(3, intervals.Count);
        Assert.All(intervals, i => Assert.Equal(2, i.WidthMetres, 5));
        Assert.Equal(72, result.Sum(r => r.AreaSquareMetres), 5);
    }

    [Fact]
    public void Section_reversing_direction_preserves_widths_and_diagonal_measures_actual_length()
    {
        var regions = new[] { Rect(0, 0, 4, 4) };
        var forward = Assert.Single(AccessFootprint.Measure(regions, new(-1, -1), new(5, 5)));
        var reverse = Assert.Single(AccessFootprint.Measure(regions, new(5, 5), new(-1, -1)));
        Assert.Equal(Math.Sqrt(32), forward.WidthMetres, 6);
        Assert.Equal(forward.Start, reverse.End);
        Assert.Equal(forward.End, reverse.Start);
    }

    [Fact]
    public void Section_outside_or_touching_only_a_corner_has_no_width()
    {
        var regions = new[] { Rect(0, 0, 4, 4) };
        Assert.Empty(AccessFootprint.Measure(regions, new(-1, 5), new(5, 5)));
        Assert.Empty(AccessFootprint.Measure(regions, new(-1, 1), new(1, -1)));
        Assert.Throws<ArgumentException>(() => AccessFootprint.Measure(regions, new(1, 1), new(1, 1)));
    }

    [Theory]
    [InlineData(false, true, true, "Sweep unavailable")]
    [InlineData(true, true, false, "Sweep generated")]
    [InlineData(true, true, true, "This movement fits")]
    [InlineData(true, false, true, "This movement conflicts")]
    public void Fit_summary_never_claims_unchecked_access(bool envelope, bool feasible, bool site, string expected)
    {
        Assert.StartsWith(expected, MovementFit.Describe(envelope, feasible, site));
    }

    [Fact]
    public void Obstacle_checks_distinguish_body_contact_clearance_only_and_island()
    {
        var body = Rect(0, 0, 4, 4);
        var clearance = SweptRegionBuilder.Inflate(body, 0.3).Region!;
        var obstacle = Box(4.1, 1, 1, 1);
        Assert.Null(AccessFootprint.ConflictPoint(body, obstacle, true));
        Assert.NotNull(AccessFootprint.ConflictPoint(clearance, obstacle, true));
        Assert.NotNull(AccessFootprint.ConflictPoint(body, Box(1, 1, 1, 1), true));
        Assert.NotNull(AccessFootprint.ConflictPoint(body, [new(1, 1), new(2, 2)], false));
        var ring = new SweptRegion(Box(0, 0, 10, 10), [Box(2, 2, 6, 6)]);
        Assert.Null(AccessFootprint.ConflictPoint(ring, Box(3, 3, 2, 2), true));
        Assert.Null(AccessFootprint.ConflictPoint(ring, [new(3, 3), new(5, 5)], false));
    }

    [Fact]
    public void Boundary_fit_detects_concave_notch_between_sweep_vertices_and_unions_adjacent_areas()
    {
        var body = Rect(0, 0, 4, 4);
        IReadOnlyList<Point2> notched = [new(-1, -1), new(5, -1), new(5, 5), new(3, 5),
            new(3, 2), new(2, 2), new(2, 5), new(-1, 5)];
        Assert.All(body.OuterBoundary, p => Assert.True(Geometry2D.Contains(notched, p)));
        Assert.NotNull(AccessFootprint.Outside(body, [notched]));
        Assert.Null(AccessFootprint.Outside(body, [Box(-1, -1, 3, 6), Box(2, -1, 3, 6)]));
    }
}
