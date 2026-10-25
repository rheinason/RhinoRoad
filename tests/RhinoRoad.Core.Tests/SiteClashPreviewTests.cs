using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class SiteClashPreviewTests
{
    private static Point2[] Square(double minX, double minY, double maxX, double maxY) =>
        [new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY)];

    private static readonly SweptRegion Sweep = new(Square(0, 0, 10, 4), []);

    private static double Area(IReadOnlyList<Point2> loop) =>
        Math.Abs(loop.Select((point, index) => (point.X * loop[(index + 1) % loop.Count].Y) - (loop[(index + 1) % loop.Count].X * point.Y)).Sum()) / 2.0;

    [Fact]
    public void Closed_obstacle_overlap_is_the_intersection_with_the_sweep()
    {
        var site = new SiteConstraints([new SiteObstacle(Square(8, 2, 12, 6), true)], []);

        var clash = SiteClashPreview.Find(Sweep, site);

        Assert.Equal(1, clash.ObstaclesHit);
        Assert.Equal(4.0, clash.ObstacleOverlaps.Sum(Area), 3);
        Assert.False(clash.LeavesAllowedArea);
    }

    [Fact]
    public void Open_obstacle_is_clipped_as_a_line_not_closed_into_a_polygon()
    {
        // A wall line crossing the sweep. Closed with a chord it would enclose most of the sweep.
        var site = new SiteConstraints([new SiteObstacle([new Point2(5, -2), new Point2(5, 6), new Point2(20, 6)], false)], []);

        var clash = SiteClashPreview.Find(Sweep, site);

        Assert.Equal(1, clash.ObstaclesHit);
        Assert.Empty(clash.ObstacleOverlaps);
        var run = Assert.Single(clash.ObstacleCrossings);
        Assert.Equal(4.0, run[0].DistanceTo(run[^1]), 3);
    }

    [Fact]
    public void Clear_obstacles_and_a_containing_yard_report_no_clash()
    {
        var site = new SiteConstraints(
            [new SiteObstacle(Square(20, 20, 22, 22), true), new SiteObstacle([new Point2(-5, 8), new Point2(15, 8)], false)],
            [Square(-1, -1, 11, 5)]);

        var clash = SiteClashPreview.Find(Sweep, site);

        Assert.False(clash.Any);
        Assert.Same(SiteClash.None, clash);
    }

    [Fact]
    public void Part_of_the_sweep_outside_the_allowed_area_is_returned()
    {
        var site = new SiteConstraints([], [Square(-1, -1, 7, 5)]);

        var clash = SiteClashPreview.Find(Sweep, site);

        Assert.True(clash.LeavesAllowedArea);
        Assert.Equal(0, clash.ObstaclesHit);
        Assert.Equal(12.0, clash.OutsideAllowedArea.Sum(Area), 3);
    }

    [Fact]
    public void An_obstacle_standing_in_a_hole_of_the_sweep_is_clear()
    {
        // A roundabout's island is a hole in what the vehicle occupies, not a clash.
        var ring = new SweptRegion(Square(0, 0, 20, 20), [Square(5, 5, 15, 15)]);
        var site = new SiteConstraints([new SiteObstacle(Square(8, 8, 12, 12), true)], []);

        Assert.False(SiteClashPreview.Find(ring, site).Any);
    }

    [Fact]
    public void Several_obstacles_are_counted_once_each()
    {
        var site = new SiteConstraints(
            [
                new SiteObstacle(Square(1, 1, 2, 2), true),
                new SiteObstacle(Square(3, 1, 4, 2), true),
                new SiteObstacle([new Point2(6, -1), new Point2(6, 5)], false)
            ],
            []);

        Assert.Equal(3, SiteClashPreview.Find(Sweep, site).ObstaclesHit);
    }
}
