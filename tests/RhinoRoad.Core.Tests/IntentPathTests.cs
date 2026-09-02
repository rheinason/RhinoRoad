using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class IntentPathTests
{
    private static List<Point3> Arc(double radiusMetres, double sweepDegrees, double spacing = 0.05)
    {
        var points = new List<Point3>();
        var sweep = sweepDegrees * Math.PI / 180.0;
        var count = Math.Max(2, (int)(radiusMetres * sweep / spacing));
        for (var index = 0; index <= count; index++)
        {
            var angle = index * sweep / count;
            points.Add(new Point3(radiusMetres * Math.Sin(angle), radiusMetres * (1.0 - Math.Cos(angle)), 0.0));
        }

        return points;
    }

    [Fact]
    public void StationsRunFromZeroToTheLineLength()
    {
        var path = IntentPath.FromPoints([new Point3(0, 0, 0), new Point3(3, 4, 0), new Point3(3, 14, 0)]);

        Assert.Equal(0.0, path.Stations[0]);
        Assert.Equal(15.0, path.LengthMetres, 9);
    }

    [Fact]
    public void AStraightLineHasNoCurvature()
    {
        var path = IntentPath.FromPoints([new Point3(0, 0, 0), new Point3(10, 0, 0), new Point3(20, 0, 0)]);

        Assert.All(path.Curvatures, curvature => Assert.Equal(0.0, curvature, 9));
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(25.0)]
    [InlineData(50.0)]
    public void AnArcReportsTheCurvatureOfItsRadius(double radius)
    {
        var path = IntentPath.FromPoints(Arc(radius, 90.0));

        // Ends use one-sided neighbours, so the interior is what carries the true value.
        foreach (var curvature in path.Curvatures.Skip(2).SkipLast(2))
        {
            Assert.Equal(1.0 / radius, curvature, 3);
        }
    }

    /// <summary>Left-hand bends are positive, matching the sign convention used for steering.</summary>
    [Fact]
    public void CurvatureIsSignedByTheDirectionOfTheBend()
    {
        var left = IntentPath.FromPoints(Arc(20.0, 60.0));
        var right = IntentPath.FromPoints(Arc(20.0, 60.0).Select(p => new Point3(p.X, -p.Y, p.Z)).ToList());

        Assert.True(left.Curvatures[left.Count / 2] > 0.0);
        Assert.True(right.Curvatures[right.Count / 2] < 0.0);
    }

    [Fact]
    public void TheNearestSampleIsFoundWithinTheLeash()
    {
        var path = IntentPath.FromPoints(Arc(20.0, 90.0));
        var target = path.Points[300].XY;

        Assert.Equal(300, path.NearestIndex(target, 295, 5.0));
        Assert.Equal(300, path.NearestIndex(target, 250, 10.0));
    }

    /// <summary>
    /// A line that loops back near itself must not let the search snap to another pass, or the
    /// vehicle would be told it is somewhere it has not driven to yet.
    /// </summary>
    [Fact]
    public void TheSearchDoesNotJumpToAnotherPass()
    {
        var points = Arc(6.0, 350.0);
        var path = IntentPath.FromPoints(points);
        var nearTheEnd = path.Count - 5;

        Assert.True(path.NearestIndex(path.Points[nearTheEnd].XY, nearTheEnd - 20, 5.0) >= nearTheEnd - 20);
    }

    [Fact]
    public void IndexAtClampsToTheEndsOfTheLine()
    {
        var path = IntentPath.FromPoints(Arc(20.0, 90.0));

        Assert.Equal(0, path.IndexAt(-5.0));
        Assert.Equal(path.Count - 1, path.IndexAt(path.LengthMetres + 5.0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ALineNeedsTwoPoints(int count)
    {
        var points = Enumerable.Range(0, count).Select(index => new Point3(index, 0, 0)).ToList();

        Assert.Throws<ArgumentException>(() => IntentPath.FromPoints(points));
    }

    [Fact]
    public void ALineWithNoPlanLengthIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            IntentPath.FromPoints([new Point3(2, 2, 0), new Point3(2, 2, 5)]));
    }
}
