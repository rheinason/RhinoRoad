using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class SweptRegionBuilderTests
{
    private const double HalfWidth = 1.25;
    private const double HalfLength = 2.5;

    /// <summary>A rectangular body centred on, and aligned with, a pose.</summary>
    private static Point2[] Footprint(Point2 centre, double heading)
    {
        Point2[] local =
        [
            new(-HalfLength, -HalfWidth), new(HalfLength, -HalfWidth),
            new(HalfLength, HalfWidth), new(-HalfLength, HalfWidth)
        ];
        return local.Select(point => Geometry2D.Transform(point, centre, heading)).ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<Point2>> StraightSweep(double length, int steps) =>
        Enumerable.Range(0, steps + 1)
            .Select(i => (IReadOnlyList<Point2>)Footprint(new Point2(length * i / steps, 0.0), 0.0))
            .ToArray();

    /// <summary>A full circular circulation, as around a roundabout island.</summary>
    private static IReadOnlyList<IReadOnlyList<Point2>> CircularSweep(double radius, int steps) =>
        Enumerable.Range(0, steps + 1)
            .Select(i =>
            {
                var t = 2.0 * Math.PI * i / steps;
                return (IReadOnlyList<Point2>)Footprint(
                    new Point2(radius * Math.Cos(t), radius * Math.Sin(t)),
                    t + (Math.PI / 2.0));
            })
            .ToArray();

    [Fact]
    public void StraightSweepIsOneRectangleOfThePredictableArea()
    {
        var result = SweptRegionBuilder.FromOutlines(StraightSweep(40.0, 400));

        Assert.Equal(SweptRegionStatus.Success, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Region!.Holes);

        // Body length 5 m swept 40 m gives a 45 m by 2.5 m rectangle.
        Assert.Equal(45.0 * 2.0 * HalfWidth, result.Region.AreaSquareMetres, 2);
    }

    [Fact]
    public void CirculatingASmallIslandLeavesAHole()
    {
        const double radius = 15.0;
        var result = SweptRegionBuilder.FromOutlines(CircularSweep(radius, 720));

        Assert.Equal(SweptRegionStatus.Success, result.Status);
        var region = result.Region!;

        // The island the vehicle drives around must survive as a hole, not be reported as occupied.
        Assert.Single(region.Holes);
        Assert.Contains("hole", result.Message, StringComparison.OrdinalIgnoreCase);

        // The swept annulus is set by the body corners, not its centreline: a rectangle held
        // tangent to the circle throws its corners outside the path radius. That corner swing is
        // off-tracking, so the expected area must account for it.
        var expected = AnnulusArea(radius, 0.0);
        Assert.InRange(region.AreaSquareMetres, expected * 0.98, expected * 1.02);

        // Every hole vertex must sit inside the outer boundary.
        Assert.All(region.Holes[0], point => Assert.True(Geometry2D.Contains(region.OuterBoundary, point)));
    }

    [Fact]
    public void TightTurnsUnionWithoutFragmenting()
    {
        // A 162 gon (about 146 degree) turn at a radius the body barely clears -- the shape that
        // fragmented the curve Boolean into 129 pieces.
        var outlines = Enumerable.Range(0, 1200)
            .Select(i =>
            {
                var t = 2.55 * i / 1199.0;
                return (IReadOnlyList<Point2>)Footprint(
                    new Point2(8.0 * Math.Sin(t), 8.0 * (1.0 - Math.Cos(t))),
                    t);
            })
            .ToArray();

        var result = SweptRegionBuilder.FromOutlines(outlines);

        Assert.Equal(SweptRegionStatus.Success, result.Status);
        Assert.Equal(1, result.RegionCount);
        Assert.True(result.Region!.AreaSquareMetres > 0.0);
    }

    [Fact]
    public void DisconnectedFootprintsAreReportedRatherThanReducedToTheLargest()
    {
        IReadOnlyList<IReadOnlyList<Point2>> apart =
        [
            Footprint(new Point2(0.0, 0.0), 0.0),
            Footprint(new Point2(500.0, 0.0), 0.0)
        ];

        var result = SweptRegionBuilder.FromOutlines(apart);

        Assert.Equal(SweptRegionStatus.Disjoint, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.RegionCount);
        Assert.Contains("disconnected", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisjointRegionsAreAllKeptEachWithItsOwnHole()
    {
        // Two separate roundabout circulations: two regions, one island each -- not one region
        // carrying both islands and a second one counted and thrown away.
        var outlines = CircularSweep(15.0, 720)
            .Concat(CircularSweep(10.0, 480).Select(outline =>
                (IReadOnlyList<Point2>)outline.Select(point => point + new Point2(100.0, 0.0)).ToArray()))
            .ToArray();

        var result = SweptRegionBuilder.FromOutlines(outlines);

        Assert.Equal(SweptRegionStatus.Disjoint, result.Status);
        Assert.Equal(2, result.RegionCount);
        Assert.Equal(2, result.Regions.Count);
        Assert.Same(result.Region, result.Regions[0]);
        Assert.All(result.Regions, region => Assert.Single(region.Holes));
        Assert.All(result.Regions[1].Holes[0], point => Assert.True(Geometry2D.Contains(result.Regions[1].OuterBoundary, point)));
    }

    [Fact]
    public void AnIslandInsideAHoleIsItsOwnRegionWithoutTheHole()
    {
        // A footprint parked on the island of a circulation is a separate area inside the hole. It
        // must not take the hole it stands in, and the ring must keep it.
        var outlines = CircularSweep(15.0, 720).Append(Footprint(new Point2(0.0, 0.0), 0.0)).ToArray();

        var result = SweptRegionBuilder.FromOutlines(outlines);

        Assert.Equal(2, result.Regions.Count);
        var ring = result.Regions[0];
        var island = result.Regions[1];
        Assert.Single(ring.Holes);
        Assert.Empty(island.Holes);
        Assert.Equal(4.0 * HalfLength * HalfWidth, island.AreaSquareMetres, 3);
    }

    [Fact]
    public void SuccessfulResultListsItsSingleRegion()
    {
        var result = SweptRegionBuilder.FromOutlines(StraightSweep(10.0, 100));

        Assert.Same(result.Region, Assert.Single(result.Regions));
    }

    [Fact]
    public void ClearanceInflatesTheRegionAndShrinksItsHoles()
    {
        const double radius = 15.0;
        const double clearance = 0.30;
        var swept = SweptRegionBuilder.FromOutlines(CircularSweep(radius, 720)).Region!;

        var inflated = SweptRegionBuilder.Inflate(swept, clearance);

        Assert.True(inflated.IsSuccess);
        Assert.Single(inflated.Region!.Holes);

        // Growing outward widens the annulus by the clearance on both faces.
        var expected = AnnulusArea(radius, clearance);
        Assert.InRange(inflated.Region.AreaSquareMetres, expected * 0.98, expected * 1.02);
        Assert.True(inflated.Region.AreaSquareMetres > swept.AreaSquareMetres);
    }

    /// <summary>
    /// Area swept by the rectangular body circulating at <paramref name="radius"/>, grown by
    /// <paramref name="clearance"/>. The outer bound is the corner sweep radius; the inner bound is
    /// the mid-face of the inside flank.
    /// </summary>
    private static double AnnulusArea(double radius, double clearance)
    {
        var outer = Math.Sqrt(Math.Pow(radius + HalfWidth, 2.0) + (HalfLength * HalfLength)) + clearance;
        var inner = radius - HalfWidth - clearance;
        return Math.PI * ((outer * outer) - (inner * inner));
    }

    [Fact]
    public void EmptyInputIsRejected()
    {
        var result = SweptRegionBuilder.FromOutlines([]);

        Assert.Equal(SweptRegionStatus.NoInput, result.Status);
        Assert.Null(result.Region);
    }

    [Fact]
    public void ResultIsDeterministicAcrossRuns()
    {
        var outlines = CircularSweep(12.0, 360);

        var first = SweptRegionBuilder.FromOutlines(outlines);
        var second = SweptRegionBuilder.FromOutlines(outlines);

        Assert.Equal(first.Region!.OuterBoundary.Count, second.Region!.OuterBoundary.Count);
        Assert.Equal(first.Region.AreaSquareMetres, second.Region.AreaSquareMetres, 9);
    }
}
