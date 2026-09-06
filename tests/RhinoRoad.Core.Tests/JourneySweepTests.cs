using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class JourneySweepTests
{
    [Fact]
    public void Ren_continuous_hairpin_right_angle_and_small_turn_preview_matches_commit_and_replay()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("REN");
        var mode = vehicle.DrivingModes["B"];
        var initial = new VehicleState(new(0, 0, 0), 0, 0, TravelDirection.Forward, 0);
        var state = initial;
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(state, vehicle) };
        var controls = new List<ManoeuvreControl>();
        var legStart = 0;
        // Relative cursor bearings request a hairpin, then a right angle, then a small bend.
        // Straight exit picks let each corner ease before the following one begins.
        Point2[] picks = [new(0, 24), new(35, 0), new(18, -18), new(35, 0), new(30, 5), new(35, 0)];
        var turnAngles = new List<double>();
        foreach (var relative in picks)
        {
            var point = Geometry2D.Transform(relative, state.RearAxleCentreMetres.XY, state.VehicleHeadingRadians);
            var control = new ManoeuvreControl(new(point.X, point.Y, 0), TravelDirection.Forward);
            var planned = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStart, state, control);
            var builder = new JourneySweepBuilder(route, vehicle, 0.3);
            var preview = builder.Build(planned);
            Assert.Equal(preview.Body.Region!.AreaSquareMetres, builder.Build(planned).Body.Region!.AreaSquareMetres);
            turnAngles.Add(Math.Abs(Geometry2D.NormalizeAngle(planned.EndState.VehicleHeadingRadians - state.VehicleHeadingRadians)));
            route.RemoveRange(planned.FromIndex + 1, route.Count - planned.FromIndex - 1);
            legStart = route.Count - 1;
            route.AddRange(planned.Samples.Skip(1));
            state = planned.EndState;
            controls.Add(control);
            var analysis = new VehicleAccessAnalyzer().Analyze(vehicle, mode, route);
            var body = SweptRegionBuilder.FromPoses(analysis.Poses);
            var clearance = SweptRegionBuilder.Inflate(body.Region!, 0.3);
            Assert.True(preview.Body.IsSuccess && preview.Clearance.IsSuccess);
            // Cached union has an extra 1 mm simplification pass; compare boundary distances rather than vertex counts.
            AssertBoundaryNear(body.Region!, preview.Body.Region!);
            AssertBoundaryNear(clearance.Region!, preview.Clearance.Region!);
        }
        Assert.True(turnAngles[0] > 150 * Math.PI / 180);
        Assert.InRange(turnAngles[2], 60 * Math.PI / 180, 110 * Math.PI / 180);
        Assert.InRange(turnAngles[4], 5 * Math.PI / 180, 35 * Math.PI / 180);
        var saved = new ManoeuvreDefinition(1, initial.RearAxleCentreMetres, 0, TravelDirection.Forward, controls);
        var replay = ManoeuvreReplayService.Replay(vehicle, mode, saved);
        Assert.Equal(route, replay.Samples);
        Assert.Equal(state, replay.EndState);
    }

    [Fact]
    public void Preview_invalidates_retained_sweep_when_corner_is_rewound()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("REN");
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new(0, 0, 0), 0, 0, TravelDirection.Forward, 0);
        var leg = new RateLimitedTrajectoryGenerator().GenerateLeg(vehicle, mode, start, 0.4, 25);
        var builder = new JourneySweepBuilder(leg.Samples, vehicle, 0.3);
        foreach (var index in new[] { leg.Samples.Count - 1, 100, 30, 100 })
        {
            var samples = leg.Samples.Take(index + 1).ToArray();
            var preview = builder.Build(new PlannedManoeuvreLeg(index, [samples[^1]], leg.EndState, false));
            var expected = SweptRegionBuilder.FromPoses(new VehicleAccessAnalyzer().Analyze(vehicle, mode, samples).Poses);
            AssertBoundaryNear(expected.Region!, preview.Body.Region!);
        }
    }

    private static void AssertBoundaryNear(SweptRegion expected, SweptRegion actual)
    {
        static double Distance(Point2 p, IReadOnlyList<Point2> loop) => Enumerable.Range(0, loop.Count)
            .Min(i => Geometry2D.DistancePointToSegment(p, loop[i], loop[(i + 1) % loop.Count]));
        Assert.All(expected.OuterBoundary, p => Assert.InRange(Distance(p, actual.OuterBoundary), 0, 0.004));
        Assert.All(actual.OuterBoundary, p => Assert.InRange(Distance(p, expected.OuterBoundary), 0, 0.004));
        Assert.Equal(expected.Holes.Count, actual.Holes.Count);
    }
}
