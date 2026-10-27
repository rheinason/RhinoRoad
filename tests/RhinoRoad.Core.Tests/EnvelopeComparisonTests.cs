using RhinoRoad.Core;
using Xunit;

namespace RhinoRoad.Core.Tests;

public sealed class EnvelopeComparisonTests
{
    private static SweptRegion Rect(double x0, double y0, double x1, double y1, params Point2[][] holes) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) }, holes);

    [Fact]
    public void IdenticalRegionsHaveNoDifferences()
    {
        var result = EnvelopeComparison.Compare(new[] { Rect(0, 0, 4, 3) }, new[] { Rect(0, 0, 4, 3) });
        Assert.Equal(0, result.OnlyAArea, 6);
        Assert.Equal(0, result.OnlyBArea, 6);
        Assert.Equal(12, result.BothArea, 6);
        Assert.Empty(result.OnlyA);
        Assert.Empty(result.OnlyB);
        Assert.Equal(0, result.NetDifference, 6);
    }

    [Fact]
    public void OffsetRectanglesSplitIntoExpectedAreas()
    {
        var result = EnvelopeComparison.Compare(new[] { Rect(0, 0, 4, 2) }, new[] { Rect(1, 0, 6, 2) });
        Assert.Equal(8, result.AreaA, 6);
        Assert.Equal(10, result.AreaB, 6);
        Assert.Equal(2, result.OnlyAArea, 6);
        Assert.Equal(4, result.OnlyBArea, 6);
        Assert.Equal(6, result.BothArea, 6);
        Assert.Equal(2, result.NetDifference, 6);
        Assert.Single(result.OnlyA);
        Assert.Single(result.OnlyB);
    }

    [Fact]
    public void HoleCountsAsUnoccupiedSpace()
    {
        var island = new[] { new Point2(4, 4), new Point2(6, 4), new Point2(6, 6), new Point2(4, 6) };
        var ring = Rect(0, 0, 10, 10, island);
        var result = EnvelopeComparison.Compare(new[] { ring }, new[] { Rect(0, 0, 10, 10) });
        Assert.Equal(96, result.AreaA, 6);
        Assert.Equal(100, result.AreaB, 6);
        Assert.Equal(0, result.OnlyAArea, 6);
        Assert.Equal(4, result.OnlyBArea, 6);
        Assert.Equal(96, result.BothArea, 6);
    }

    /// <summary>
    /// A quadrilateral (area 90) with a triangle (area 10) cut from it that touches its slanted left
    /// edge at (1, 5), beside a larger square. Clipper starts the triangle's hole at that touching
    /// vertex, and a ray cast from a point on a slanted edge reads it as outside by rounding, so the
    /// hole was assigned to no region at all and the piece drew and measured as 90 m² instead of 80.
    /// </summary>
    internal static readonly Point2[] Slanted = [new(2, 0), new(10, 0), new(10, 10), new(0, 10)];

    internal static readonly Point2[] TouchingTriangle = [new(1, 5), new(6, 3), new(6, 7)];

    internal static void AssertHoleStaysWithItsOwnRegion(IReadOnlyList<SweptRegion> regions)
    {
        Assert.Equal(2, regions.Count);
        Assert.Empty(regions[0].Holes);
        Assert.Equal(400.0, regions[0].AreaSquareMetres, 6);
        Assert.Equal(80.0, regions[1].AreaSquareMetres, 6);
        var inside = new Point2(4.0, 5.0);
        Assert.Contains(regions[1].Holes, hole => Geometry2D.Contains(hole, inside));
    }

    [Fact]
    public void A_hole_touching_its_outer_boundary_stays_with_its_own_region()
    {
        var result = EnvelopeComparison.Compare(
            [new SweptRegion(Slanted, []), Rect(20, 0, 40, 20)],
            [new SweptRegion(TouchingTriangle, [])]);

        Assert.Equal(480.0, result.OnlyAArea, 6);
        Assert.Equal(result.OnlyAArea, result.OnlyA.Sum(region => region.AreaSquareMetres), 6);
        AssertHoleStaysWithItsOwnRegion(result.OnlyA);
    }
}
