using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// The site checks on a body whose sweep has a closed form: a 5 m by 2.5 m rectangle driven straight
/// along the X axis, or circulated round an island. Every station, margin and area asserted below can
/// be worked out by hand from that rectangle.
/// </summary>
public sealed class SiteFitAnalyzerTests
{
    private const double HalfWidth = 1.25;
    private const double HalfLength = 2.5;

    private static Point2[] Box(double x0, double y0, double x1, double y1) =>
        [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    private static VehiclePose Pose(double station, Point2 centre, double heading)
    {
        Point2[] local =
        [
            new(-HalfLength, -HalfWidth), new(HalfLength, -HalfWidth),
            new(HalfLength, HalfWidth), new(-HalfLength, HalfWidth)
        ];
        return new VehiclePose(
            station,
            new Point3(centre.X, centre.Y, 0.0),
            Geometry2D.Transform(new Point2(3.0, 0.0), centre, heading),
            heading,
            0.0,
            TravelDirection.Forward,
            local.Select(point => Geometry2D.Transform(point, centre, heading)).ToArray());
    }

    /// <summary>Body centred on X = station, from station 0 to 40 every 0.1 m.</summary>
    private static IReadOnlyList<VehiclePose> Straight() =>
        Enumerable.Range(0, 401).Select(index => Pose(index * 0.1, new Point2(index * 0.1, 0.0), 0.0)).ToArray();

    private static IReadOnlyList<VehiclePose> Circle(double radius, int steps) =>
        Enumerable.Range(0, steps + 1).Select(index =>
        {
            var t = 2.0 * Math.PI * index / steps;
            return Pose(radius * t, new Point2(radius * Math.Cos(t), radius * Math.Sin(t)), t + (Math.PI / 2.0));
        }).ToArray();

    private static (SweptRegion Body, SweptRegion Clearance) Sweep(IReadOnlyList<VehiclePose> poses, double clearance)
    {
        var body = SweptRegionBuilder.FromPoses(poses).Region!;
        return (body, clearance > 0.0 ? SweptRegionBuilder.Inflate(body, clearance).Region! : body);
    }

    private static SiteFitOutcome Check(
        IReadOnlyList<VehiclePose> poses,
        double clearance,
        IReadOnlyList<SiteCurve>? obstacles = null,
        IReadOnlyList<IReadOnlyList<Point2>>? allowed = null,
        SweptRegion? road = null)
    {
        var (body, inflated) = Sweep(poses, clearance);
        return SiteFitAnalyzer.Check(poses, body, inflated, obstacles ?? [], allowed ?? [], road, clearance);
    }

    [Fact]
    public void ObstacleConflictIsReportedOverTheStationsThatReachIt()
    {
        // 0.25 m above the body's flank, under a 0.30 m clearance. A pose reaches it while its 5 m
        // body overlaps the obstacle's 1 m in X, plus the few centimetres where a body corner is still
        // within 0.30 m diagonally: centres from about 16.83 m to 23.17 m.
        var poses = Straight();
        var outcome = Check(poses, 0.30, [new SiteCurve(Box(19.5, 1.5, 20.5, 2.5), true)]);

        var violation = Assert.Single(outcome.Violations);
        Assert.Equal(ViolationKind.ObstacleClearance, violation.Kind);
        Assert.Equal(16.9, violation.StationMetres, 6);
        Assert.Equal(23.1, violation.EndStationMetres!.Value, 6);
        Assert.Contains("clearance allowance", violation.Message);
        Assert.InRange(violation.PositionMetres.X, 19.5, 20.5);

        var conflict = Assert.Single(outcome.Conflicts);
        Assert.False(conflict.BodyConflict);
        Assert.Equal(-0.05, conflict.Ranges.Single().WorstMarginMetres, 6);

        // The clearance edge sits 1.55 m out, so it overlaps the obstacle by a 1 m by 0.05 m strip.
        Assert.Equal(0.05, conflict.AreaSquareMetres, 3);
        Assert.Equal(0.05, conflict.Regions.Sum(region => region.AreaSquareMetres), 3);
        Assert.Equal(0.25, outcome.MinimumClearanceMetres!.Value, 6);
    }

    [Fact]
    public void BollardWhollyUnderTheBodyReadsAsOverlapNotAsClearance()
    {
        var poses = Straight();
        var bollard = Box(19.8, -0.2, 20.2, 0.2);

        // The trap: outline-to-outline distance never sees an obstacle that the outline surrounds.
        var atBollard = poses.Single(pose => Math.Abs(pose.StationMetres - 20.0) < 1e-9);
        Assert.True(Geometry2D.MinimumDistance(atBollard.BodyOutlineWorldMetres, bollard) > 1.0);

        var outcome = Check(poses, 0.30, [new SiteCurve(bollard, true)]);
        var margins = outcome.ObstacleMargins!;
        var index = poses.ToList().IndexOf(atBollard);

        // Deepest bollard corner is 1.05 m inside the body flank: margin is that depth, less clearance.
        Assert.Equal(-1.05 - 0.30, margins[index], 6);
        Assert.All(Enumerable.Range(0, poses.Count).Where(i => Math.Abs(poses[i].StationMetres - 20.0) < 2.2),
            i => Assert.True(margins[i] < -0.30));

        var conflict = Assert.Single(outcome.Conflicts);
        Assert.True(conflict.BodyConflict);
        Assert.Equal(0.16, conflict.AreaSquareMetres, 4);
        Assert.Equal(0.0, outcome.MinimumClearanceMetres);
        Assert.Contains("body sweep intersects", Assert.Single(outcome.Violations).Message);
    }

    [Fact]
    public void BodyDrivenIntoABuildingReadsAsOverlap()
    {
        // The other containment: the body wholly inside a closed obstacle.
        var poses = new[] { Pose(0.0, new Point2(0.0, 0.0), 0.0), Pose(0.1, new Point2(0.1, 0.0), 0.0) };
        var outcome = Check(poses, 0.0, [new SiteCurve(Box(-10, -10, 10, 10), true)]);

        Assert.All(outcome.ObstacleMargins!, margin => Assert.True(margin < 0.0));
        Assert.True(Assert.Single(outcome.Conflicts).BodyConflict);
    }

    [Fact]
    public void ObstacleOnARoundaboutIslandIsMeasuredToTheIslandEdge()
    {
        // A 15 m circulation: the island hole's edge is about 13.75 m out and the outside edge over
        // 16 m. An obstacle at 12.7 m is about a metre from the kerb it actually faces.
        var poses = Circle(15.0, 720);
        var outcome = Check(poses, 0.30, [new SiteCurve(Box(12.3, -0.2, 12.7, 0.2), true)]);

        Assert.Empty(outcome.Conflicts);
        Assert.InRange(outcome.MinimumClearanceMetres!.Value, 0.9, 1.1);
    }

    [Fact]
    public void AllowedAreaNotchBetweenBodyCornersIsCaught()
    {
        // Every body corner is inside the yard, but a V notch in the yard edge pokes its tip down to
        // the route centreline -- 1.25 m into the body at station 20.
        var poses = Straight();
        Point2[] yard = [new(-10, -5), new(50, -5), new(50, 5), new(20.2, 5), new(20.0, 0.0), new(19.8, 5), new(-10, 5)];
        var outcome = Check(poses, 0.0, allowed: [yard]);
        var index = poses.ToList().FindIndex(pose => Math.Abs(pose.StationMetres - 20.0) < 1e-9);

        Assert.Equal(-1.25, outcome.AllowedAreaMargins![index], 6);
        Assert.True(outcome.AllowedAreaMargins[0] > 0.0);
        var conflict = Assert.Single(outcome.Conflicts);
        Assert.Equal(ViolationKind.OutsideAllowedArea, conflict.Kind);
        Assert.True(conflict.AreaSquareMetres > 0.0);
        Assert.InRange(conflict.Ranges.Single().StartStationMetres, 17.0, 18.0);
        Assert.InRange(conflict.Ranges.Single().EndStationMetres, 22.0, 23.0);
    }

    [Fact]
    public void OverlappingAllowedBoundariesActAsOneArea()
    {
        // Two yards that overlap in the middle: crossing from one into the other is still inside.
        var poses = Straight();
        var outcome = Check(poses, 0.0, allowed: [Box(-5, -3, 25, 3), Box(15, -3, 45, 3)]);

        Assert.Empty(outcome.Conflicts);
        Assert.All(outcome.AllowedAreaMargins!, margin => Assert.True(margin > 0.0));
    }

    [Fact]
    public void RoadConflictPolygonsMeasureTheOverhang()
    {
        // A 2 m road under a 2.5 m body: 0.25 m overhangs each side, over the body's 45 m sweep.
        var poses = Straight();
        var outcome = Check(poses, 0.0, road: new SweptRegion(Box(-5, -1, 45, 1), []));

        var conflict = Assert.Single(outcome.Conflicts);
        Assert.Equal(ViolationKind.FixedWidthRoad, conflict.Kind);
        Assert.True(conflict.BodyConflict);
        Assert.Equal(2, conflict.Regions.Count);
        Assert.Equal(22.5, conflict.AreaSquareMetres, 3);
        var range = conflict.Ranges.Single();
        Assert.Equal(0.0, range.StartStationMetres);
        Assert.Equal(40.0, range.EndStationMetres, 6);
        Assert.All(outcome.RoadMargins!, margin => Assert.Equal(-0.25, margin, 6));
    }

    [Fact]
    public void OpenObstacleConflictIsMeasuredAsLength()
    {
        // A kerb line 0.15 m off the flank, inside a 0.30 m clearance along all 20 m of it.
        var poses = Straight();
        var outcome = Check(poses, 0.30, [new SiteCurve([new(10, 1.4), new(30, 1.4)], false)]);

        var conflict = Assert.Single(outcome.Conflicts);
        Assert.Empty(conflict.Regions);
        Assert.Equal(20.0, conflict.LengthMetres, 3);
        Assert.Equal(-0.15, conflict.Ranges.Single().WorstMarginMetres, 6);
        Assert.Contains("along 20.00 m", Assert.Single(outcome.Violations).Message);
    }

    [Fact]
    public void FarObstaclesDoNotChangeTheNearestMargin()
    {
        // The broad phase skips the far obstacle's exact measure; the answer must not move.
        var poses = Straight();
        var near = new SiteCurve(Box(19.5, 2.0, 20.5, 3.0), true);
        var far = new SiteCurve(Box(19.5, 50.0, 20.5, 51.0), true);
        var both = Check(poses, 0.30, [far, near]).ObstacleMargins!;
        var nearOnly = Check(poses, 0.30, [near]).ObstacleMargins!;

        Assert.Equal(nearOnly, both);
    }

    [Fact]
    public void MinimumDistanceBroadPhaseMatchesBruteForce()
    {
        var random = new Random(7);
        for (var trial = 0; trial < 200; trial++)
        {
            var first = Polygon(random, 3 + random.Next(8));
            var second = Polygon(random, 3 + random.Next(8));
            Assert.Equal(BruteForce(first, second), Geometry2D.MinimumDistance(first, second), 9);
        }

        static Point2[] Polygon(Random random, int count)
        {
            var centre = new Point2(random.NextDouble() * 20.0, random.NextDouble() * 20.0);
            return Enumerable.Range(0, count)
                .Select(i => 2.0 * Math.PI * i / count)
                .Select(angle => centre + (new Point2(Math.Cos(angle), Math.Sin(angle)) * (0.5 + (random.NextDouble() * 3.0))))
                .ToArray();
        }

        static double BruteForce(Point2[] a, Point2[] b)
        {
            var best = double.PositiveInfinity;
            for (var i = 0; i < a.Length; i++)
            for (var j = 0; j < b.Length; j++)
            {
                var (p, q, r, s) = (a[i], a[(i + 1) % a.Length], b[j], b[(j + 1) % b.Length]);
                if (Geometry2D.SegmentsIntersect(p, q, r, s)) return 0.0;
                best = Math.Min(best, Math.Min(
                    Math.Min(Geometry2D.DistancePointToSegment(p, r, s), Geometry2D.DistancePointToSegment(q, r, s)),
                    Math.Min(Geometry2D.DistancePointToSegment(r, p, q), Geometry2D.DistancePointToSegment(s, p, q))));
            }

            return best;
        }
    }

    /// <summary>The 5 m by 2.5 m body standing at (17.5, 0): corners at x 15 and 20, y ±1.25.</summary>
    private static IReadOnlyList<VehiclePose> Standing() => [Pose(0.0, new Point2(17.5, 0.0), 0.0)];

    [Fact]
    public void A_line_through_an_outline_corner_and_across_the_body_is_a_body_conflict()
    {
        // The line touches the body exactly at its corner (20, -1.25), then crosses its far edge.
        // The nearest-pair search stopped at the touch, so this read as the clearance alone not
        // fitting while the region test said the body was in conflict.
        var line = new SiteCurve([new(22.0, -3.25), new(16.0, 2.75)], false);

        var contact = Geometry2D.Closest(Standing()[0].BodyOutlineWorldMetres, line.Points, true, false);
        Assert.True(contact.Crosses);

        var outcome = Check(Standing(), 0.30, [line]);
        var conflict = Assert.Single(outcome.Conflicts);
        Assert.True(conflict.BodyConflict);
        Assert.True(conflict.Ranges.Single().WorstMarginMetres < -0.30);
        var message = Assert.Single(outcome.Violations).Message;
        Assert.Contains("body sweep intersects", message);
        Assert.DoesNotContain("body clears", message);
    }

    [Fact]
    public void A_line_drawn_corner_to_corner_through_the_body_is_a_body_conflict()
    {
        // Through (15, -1.25) and (20, 1.25): it never properly crosses an edge and has no vertex
        // inside the body, yet it runs straight through it.
        var line = new SiteCurve([new(13.0, -2.25), new(22.0, 2.25)], false);

        var outcome = Check(Standing(), 0.30, [line]);

        var conflict = Assert.Single(outcome.Conflicts);
        Assert.True(conflict.BodyConflict);
        Assert.True(conflict.Ranges.Single().WorstMarginMetres < -1.0 - 0.30);
        Assert.Contains("body sweep intersects", Assert.Single(outcome.Violations).Message);
    }

    [Fact]
    public void A_line_along_an_edge_touches_but_does_not_overlap()
    {
        var line = new SiteCurve([new(10.0, 1.25), new(25.0, 1.25)], false);

        var margins = Check(Standing(), 0.0, [line]).ObstacleMargins!;

        Assert.True(margins[0] > -1e-5);
    }

    [Fact]
    public void Adjacent_allowed_areas_a_hairline_apart_read_as_one()
    {
        // Two yards drawn side by side with a 0.5 mm gap between them. The vehicle crosses the join;
        // the sliver between them is not ground it has left.
        var poses = Straight();
        var outcome = Check(poses, 0.30, allowed: [Box(-10.0, -5.0, 20.0, 5.0), Box(20.0005, -5.0, 50.0, 5.0)]);

        Assert.Empty(outcome.Conflicts);
        Assert.Empty(outcome.Violations);
        Assert.All(outcome.AllowedAreaMargins!, margin => Assert.True(margin > 3.0));
    }

    [Fact]
    public void A_real_gap_between_allowed_areas_is_still_outside()
    {
        var poses = Straight();
        var outcome = Check(poses, 0.30, allowed: [Box(-10.0, -5.0, 20.0, 5.0), Box(20.05, -5.0, 50.0, 5.0)]);

        var conflict = Assert.Single(outcome.Conflicts);
        Assert.Equal(ViolationKind.OutsideAllowedArea, conflict.Kind);
    }

    [Fact]
    public void A_conflict_in_a_smaller_piece_of_a_disjoint_sweep_is_found()
    {
        // Two separate pieces: the large one clear, the small one standing in a building.
        Point2[] large = Box(0.0, -1.25, 10.0, 1.25);
        Point2[] small = Box(30.0, -1.0, 33.0, 1.0);
        VehiclePose PoseOf(double station, Point2[] outline) =>
            new(station, new Point3(outline[0].X, 0.0, 0.0), outline[1], 0.0, 0.0, TravelDirection.Forward, outline);
        var poses = new[] { PoseOf(0.0, large), PoseOf(1.0, small) };
        var sweep = SweptRegionBuilder.FromPoses(poses);
        Assert.Equal(SweptRegionStatus.Disjoint, sweep.Status);
        var building = new SiteCurve(Box(32.0, -3.0, 36.0, 3.0), true);

        var largestOnly = SiteFitAnalyzer.Check(poses, sweep.Region, sweep.Region, [building], [], null, 0.0);
        var everyPiece = SiteFitAnalyzer.Check(poses, sweep.Regions, sweep.Regions, [building], [], null, 0.0);

        Assert.Empty(largestOnly.Conflicts);
        var conflict = Assert.Single(everyPiece.Conflicts);
        Assert.True(conflict.BodyConflict);
        Assert.Equal(1.0 * 2.0, conflict.AreaSquareMetres, 6);
        Assert.Equal(0.0, everyPiece.MinimumClearanceMetres);
    }

    [Fact]
    public void Inflating_every_piece_of_a_sweep_keeps_them_all()
    {
        var sweep = SweptRegionBuilder.FromOutlines([Box(0.0, 0.0, 4.0, 2.0), Box(10.0, 0.0, 11.0, 1.0)]);

        var inflated = SweptRegionBuilder.Inflate(sweep.Regions, 0.5);

        Assert.Equal(2, inflated.Regions.Count);
        Assert.Equal(SweptRegionStatus.Disjoint, inflated.Status);
        Assert.True(inflated.Regions[1].AreaSquareMetres > 1.0);
    }
}
