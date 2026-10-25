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
}
