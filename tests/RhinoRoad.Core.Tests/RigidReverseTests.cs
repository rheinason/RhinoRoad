using System.Text.Json;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// A rigid vehicle reversing into a bay: its rear axle on the point and square to the heading, the
/// commonest bay manoeuvre on a site. Before this, a reverse with a heading squared up and then ran
/// straight, and finished parallel to the bay and beside it.
/// </summary>
public sealed class RigidReverseTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();
    private static readonly VehicleAccessAnalyzer Analyzer = new();
    private const double HeadingToleranceRadians = ReversePlanner.ArrivalToleranceDegrees * Math.PI / 180.0;

    /// <summary>
    /// Drives in along a road, past the bay entrance, turning away from it: the set-up a driver
    /// makes before backing into a bay on the other side. Returns the route and where it ends.
    /// </summary>
    private static (List<RouteSample> Route, VehicleState State, ManoeuvreControl Turn) TurnAway(
        VehicleDefinition vehicle, DrivingModeDefinition mode, double scale, double past, double away)
    {
        var start = new VehicleState(new(-4.0 * scale, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };
        var turn = new ManoeuvreControl(new(past * scale, away * scale, 0), TravelDirection.Forward);
        var leg = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start, turn);
        route.AddRange(leg.Samples.Skip(1));
        return (route, leg.EndState, turn);
    }

    /// <summary>A bay on the south side of the road, entered 3 m from the centreline, a metre longer than the body.</summary>
    private static Point3 PerpendicularBayAxle(VehicleDefinition vehicle) =>
        ReverseBay.AxleFromRear(vehicle, new Point3(0, -3.0 - vehicle.OverallLengthMetres - 1.0, 0), -Math.PI / 2.0);

    private static ManoeuvreDefinition Definition(VehicleState start, IEnumerable<ManoeuvreControl> controls) =>
        new(ManoeuvreDefinition.CurrentSchemaVersion, start.RearAxleCentreMetres, start.VehicleHeadingRadians, start.Direction, controls.ToArray());

    /// <summary>
    /// Straight back along the road into a bay set a bay's width to the side: the approach line is
    /// followed rather than the heading squared and the point projected onto it, so the rear axle
    /// arrives on the point, not beside it.
    /// </summary>
    [Theory]
    [InlineData("PV")]
    [InlineData("REN")]
    [InlineData("BUS12")]
    public void FromAParallelApproachTheRearAxleArrivesOnTheBay(string id)
    {
        var vehicle = Catalog.Get(id);
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        var bay = new Point3(-6.0 * vehicle.WheelbaseMetres, 3.0, 0);
        var control = new ManoeuvreControl(bay, TravelDirection.Reverse, ManoeuvreControlKind.Aim, Math.PI);

        var replay = ManoeuvreReplayService.Replay(vehicle, mode, Definition(start, [control]));
        var planned = ManoeuvreReplayService.PlanControl(vehicle, mode,
            [ManoeuvreReplayService.StateSample(start, vehicle)], 0, start, control);
        var miss = ReverseBay.Miss(vehicle, replay.Samples, replay.EndState, bay.XY, Math.PI);
        var analysis = Analyzer.Analyze(vehicle, mode, replay.Samples);

        Assert.False(planned.RequestedAngleExceeded);
        Assert.False(planned.ExitHeadingUnavailable);
        Assert.True(miss.PositionMetres <= ReversePlanner.ArrivalToleranceMetres, $"missed by {miss.PositionMetres:0.00} m");
        Assert.True(miss.HeadingRadians <= HeadingToleranceRadians, $"{miss.HeadingRadians * 180 / Math.PI:0.0} deg off square");
        Assert.True(analysis.IsFeasible, string.Join("; ", analysis.Violations.Select(item => item.Message)));
        Assert.All(replay.Samples, sample => Assert.Equal(TravelDirection.Reverse, sample.Direction));
    }

    /// <summary>
    /// The squaring leg a Finish drives — and the one a reverse with a heading used to drive — ends on
    /// the heading but beside the bay. Docking is what moved it onto the point.
    /// </summary>
    [Fact]
    public void SquaringUpAloneWouldHaveFinishedBesideTheBay()
    {
        var vehicle = Catalog.Get("BUS12");
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        var bay = new Point3(-36.0, 3.0, 0);
        var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };

        var squared = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start,
            new ManoeuvreControl(bay, TravelDirection.Reverse, ManoeuvreControlKind.Finish, Math.PI));
        var docked = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start,
            new ManoeuvreControl(bay, TravelDirection.Reverse, ManoeuvreControlKind.Aim, Math.PI));

        Assert.True(Math.Abs(squared.EndState.RearAxleCentreMetres.Y - bay.Y) > 2.5);
        Assert.True(docked.EndState.RearAxleCentreMetres.XY.DistanceTo(bay.XY) <= ReversePlanner.ArrivalToleranceMetres);
    }

    /// <summary>
    /// The everyday case: drive past a perpendicular bay, turn away from it, and let PlanReverse back
    /// the vehicle in. The proposal must arrive within the planner's tolerance and replay as the
    /// ordinary controls it is made of.
    /// </summary>
    [Theory]
    [InlineData("PV", "B")]
    [InlineData("REN", "B")]
    [InlineData("BUS12", "B")]
    [InlineData("PV", "A")]
    [InlineData("BUS12", "A")]
    public void FromATurnAwayPlanReverseBacksSquareIntoAPerpendicularBay(string id, string modeId)
    {
        var vehicle = Catalog.Get(id);
        var mode = vehicle.DrivingModes[modeId];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var (route, state, turn) = TurnAway(vehicle, mode, scale, 1.5, 0.4);
        var bay = PerpendicularBayAxle(vehicle);

        var proposal = ReversePlanner.Find(vehicle, mode, route, 0, state, bay, -Math.PI / 2.0);
        var start = HeadingLegGenerator.StateAt(route[0], vehicle);
        var replay = ManoeuvreReplayService.Replay(vehicle, mode, Definition(start, [turn, .. proposal.Controls]));
        var miss = ReverseBay.Miss(vehicle, replay.Samples, replay.EndState, bay.XY, -Math.PI / 2.0);
        var analysis = Analyzer.Analyze(vehicle, mode, replay.Samples);

        Assert.True(proposal.ReachedTarget, $"miss {proposal.MissMetres:0.00} m, {proposal.HeadingMissRadians * 180 / Math.PI:0.0} deg");
        Assert.True(proposal.HeadingHonoured);
        Assert.Equal(0.0, proposal.MaximumFoldRadians);
        Assert.Equal(route.Take(proposal.Leg.FromIndex + 1).Concat(proposal.Leg.Samples.Skip(1)), replay.Samples);
        Assert.True(miss.PositionMetres <= ReversePlanner.ArrivalToleranceMetres);
        Assert.True(miss.HeadingRadians <= HeadingToleranceRadians);
        Assert.Equal(TravelDirection.Reverse, replay.EndState.Direction);
        Assert.True(analysis.IsFeasible, string.Join("; ", analysis.Violations.Select(item => item.Message)));
    }

    /// <summary>
    /// The semitrailer's parallel-bay case, laid out in each vehicle's own scale: run along, turn
    /// away, and back into a bay beside the road.
    /// </summary>
    [Theory]
    [InlineData("PV")]
    [InlineData("REN")]
    [InlineData("BUS12")]
    public void FromATurnAwayPlanReverseBacksIntoAParallelBay(string id)
    {
        var vehicle = Catalog.Get(id);
        var mode = vehicle.DrivingModes["B"];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };
        var turn = new ManoeuvreControl(new(1.5 * scale, 0.4 * scale, 0), TravelDirection.Forward);
        var first = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start, turn);
        route.AddRange(first.Samples.Skip(1));
        var bay = ReverseBay.AxleFromRear(vehicle, new Point3(-2.0 * scale, -0.6 * scale, 0), Math.PI);

        var proposal = ReversePlanner.Find(vehicle, mode, route, 0, first.EndState, bay, Math.PI);
        var replay = ManoeuvreReplayService.Replay(vehicle, mode, Definition(start, [turn, .. proposal.Controls]));
        var miss = ReverseBay.Miss(vehicle, replay.Samples, replay.EndState, bay.XY, Math.PI);

        Assert.True(proposal.ReachedTarget);
        Assert.True(miss.PositionMetres <= ReversePlanner.ArrivalToleranceMetres);
        Assert.True(miss.HeadingRadians <= HeadingToleranceRadians);
    }

    /// <summary>
    /// A planned rigid dock is saved as controls and replayed later, perhaps on another machine: it
    /// has to come out sample for sample the same, through a save and a load, every time.
    /// </summary>
    [Fact]
    public void APlannedRigidDockReplaysIdenticallyFromItsSavedControls()
    {
        var vehicle = Catalog.Get("REN");
        var mode = vehicle.DrivingModes["B"];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var (route, state, turn) = TurnAway(vehicle, mode, scale, 1.0, 0.3);
        var bay = PerpendicularBayAxle(vehicle);

        var first = ReversePlanner.Find(vehicle, mode, route, 0, state, bay, -Math.PI / 2.0);
        var second = ReversePlanner.Find(vehicle, mode, route, 0, state, bay, -Math.PI / 2.0);
        Assert.Equal(first.Controls, second.Controls);
        Assert.Equal(first.Leg.Samples, second.Leg.Samples);

        var start = HeadingLegGenerator.StateAt(route[0], vehicle);
        var definition = Definition(start, [turn, .. first.Controls]);
        var saved = JsonSerializer.Deserialize<ManoeuvreDefinition>(JsonSerializer.Serialize(definition))!;
        var replayed = ManoeuvreReplayService.Replay(vehicle, mode, definition);
        var reloaded = ManoeuvreReplayService.Replay(vehicle, mode, saved);

        Assert.Equal(replayed.Samples, reloaded.Samples);
        Assert.Equal(replayed.EndState, reloaded.EndState);
        Assert.Equal(route.Take(first.Leg.FromIndex + 1).Concat(first.Leg.Samples.Skip(1)), replayed.Samples);
    }

    /// <summary>
    /// A bay that cannot be reached from here is not claimed: the leg is the best drivable attempt,
    /// and says it missed, as the trailer's does.
    /// </summary>
    [Fact]
    public void ABayTooCloseToSquareOntoIsReportedAsNotReached()
    {
        var vehicle = Catalog.Get("BUS12");
        var mode = vehicle.DrivingModes["A"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);

        var planned = ManoeuvreReplayService.PlanControl(vehicle, mode,
            [ManoeuvreReplayService.StateSample(start, vehicle)], 0, start,
            new ManoeuvreControl(new(-4, 4, 0), TravelDirection.Reverse, ManoeuvreControlKind.Aim, -Math.PI / 2.0));

        Assert.True(planned.RequestedAngleExceeded);
        Assert.False(planned.ExitHeadingUnavailable);
    }

    /// <summary>The ordinary reverse is untouched: with no heading, the rear axle is aimed as before.</summary>
    [Fact]
    public void WithoutAHeadingARigidReverseIsStillAnAimedLeg()
    {
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };
        var target = new Point2(-12, 3);

        var planned = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start,
            new ManoeuvreControl(new(target.X, target.Y, 0), TravelDirection.Reverse));
        var aim = RateLimitedTrajectoryGenerator.ControlsFromCursor(vehicle, mode, start, target);
        var arc = new RateLimitedTrajectoryGenerator().GenerateLeg(
            vehicle, mode, start, aim.TargetSteeringRadians, aim.TravelDistanceMetres, 0.10);

        Assert.Equal(arc.Samples, planned.Samples);
    }
}
