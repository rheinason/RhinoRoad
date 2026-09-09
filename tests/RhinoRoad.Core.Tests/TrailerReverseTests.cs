using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Reversing an articulated vehicle, which is a different manoeuvre from reversing a rigid one
/// rather than a signed version of the same one.
/// </summary>
/// <remarks>
/// Before the trailer was steered rather than the tractor, every curved reverse jackknifed: SVT
/// passed a right angle after 12.8 m on a 15 m tractor radius and 22.4 m on a 50 m one, and PVT in
/// roughly half that. These fence the behaviour that replaced it.
/// </remarks>
public sealed class TrailerReverseTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();
    private static readonly VehicleAccessAnalyzer Analyzer = new();

    private static ReverseLeg ReverseTo(VehicleDefinition vehicle, Point2 target)
    {
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        return TrailerReverseGenerator.AimTowedUnit(
            vehicle, vehicle.DrivingModes["B"], start, ArticulationChain.StartAligned(vehicle, 0.0), target, 0.05);
    }

    /// <summary>Where the trailer's axle starts, so targets can be written relative to it.</summary>
    private static double TrailerStart(VehicleDefinition vehicle) => vehicle.StraightAxleOffsetsMetres[^1];

    [Theory]
    [InlineData(30.0, 0.0)]
    [InlineData(30.0, 3.0)]
    [InlineData(30.0, -5.0)]
    [InlineData(20.0, 8.0)]
    [InlineData(40.0, 12.0)]
    [InlineData(25.0, 10.0)]
    [InlineData(35.0, -14.0)]
    [InlineData(18.0, 6.0)]
    public void ASemitrailerReversesOntoThePointItIsSent(double back, double across)
    {
        var vehicle = Catalog.Get("SVT");
        var target = new Point2(TrailerStart(vehicle) - back, across);

        var leg = ReverseTo(vehicle, target);
        var analysis = Analyzer.Analyze(vehicle, vehicle.DrivingModes["B"], leg.Samples);
        var trailer = analysis.Poses[^1].TowedUnits[^1].AxleCentreMetres;

        Assert.True(leg.ReachedTarget, $"stopped {leg.ClosestApproachMetres:0.00} m short");
        Assert.True(trailer.DistanceTo(target) <= 0.25);
        Assert.DoesNotContain(analysis.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
    }

    /// <summary>
    /// The manoeuvre that used to be the whole problem: a curved reverse. Aiming the tractor put the
    /// fold past a right angle inside 17 m at this radius; aiming the trailer holds it.
    /// </summary>
    [Fact]
    public void ACurvedReverseNoLongerRunsTheFoldAway()
    {
        foreach (var id in new[] { "SVT", "PVT" })
        {
            var vehicle = Catalog.Get(id);
            var target = new Point2(TrailerStart(vehicle) - 40.0, 12.0);

            var leg = ReverseTo(vehicle, target);
            var analysis = Analyzer.Analyze(vehicle, vehicle.DrivingModes["B"], leg.Samples);

            Assert.True(leg.EndState.StationMetres > 30.0, $"{id}: the leg should actually go somewhere");
            Assert.True(
                analysis.MaximumArticulationAnglesRadians.Max() < VehicleAccessAnalyzer.JackknifeFoldRadians,
                $"{id}: folded to {analysis.MaximumArticulationAnglesRadians.Max() * 180 / Math.PI:0.0}°");
            Assert.True(analysis.IsFeasible, $"{id}: {string.Join("; ", analysis.Violations.Select(v => v.Message))}");
        }
    }

    /// <summary>
    /// A reverse the combination cannot make ends where it stops being drivable, rather than folding
    /// on through it. That is what a driver does: stop, pull forward, start the reverse again on a
    /// better line — and a leg that ends there is one they can build the next leg from.
    /// </summary>
    [Fact]
    public void AReverseThatCannotBeMadeStopsShortInsteadOfFolding()
    {
        var vehicle = Catalog.Get("PVT");
        var target = new Point2(TrailerStart(vehicle) - 12.0, 6.0);

        var leg = ReverseTo(vehicle, target);
        var analysis = Analyzer.Analyze(vehicle, vehicle.DrivingModes["B"], leg.Samples);

        Assert.False(leg.ReachedTarget);
        Assert.True(leg.ClosestApproachMetres > 0.25);
        // Stopped, not jackknifed: still short of the angle that makes a run unusable.
        Assert.True(analysis.MaximumArticulationAnglesRadians.Max() < VehicleAccessAnalyzer.JackknifeFoldRadians);
        Assert.DoesNotContain(analysis.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
    }

    [Fact]
    public void ReversingDeadStraightLeavesTheTrailerStraight()
    {
        var vehicle = Catalog.Get("SVT");
        var leg = ReverseTo(vehicle, new Point2(TrailerStart(vehicle) - 30.0, 0.0));
        var analysis = Analyzer.Analyze(vehicle, vehicle.DrivingModes["B"], leg.Samples);

        Assert.True(leg.ReachedTarget);
        Assert.Equal(0.0, analysis.MaximumArticulationAnglesRadians[0], 6);
    }

    /// <summary>
    /// The whole interaction, through the same path the command uses: drive in, reverse the trailer
    /// into a bay, then save and replay it. Replay has to reproduce the route exactly, and it is the
    /// fold that makes that non-obvious — a reversing leg depends on the fold the route arrived
    /// with, so replay only matches if that state is rebuilt rather than assumed straight.
    /// </summary>
    [Fact]
    public void AnAuthoredReverseReplaysExactly()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };
        var controls = new List<ManoeuvreControl>();
        var state = start;
        var legStart = 0;

        foreach (var (point, direction) in new (Point3, TravelDirection)[]
        {
            (new(40, 0, 0), TravelDirection.Forward),
            (new(55, 6, 0), TravelDirection.Forward),
            (new(30, 14, 0), TravelDirection.Reverse),
            (new(18, 14, 0), TravelDirection.Reverse)
        })
        {
            if (state.Direction != direction)
            {
                state = state with { Direction = direction };
                legStart = route.Count - 1;
            }

            var control = new ManoeuvreControl(point, direction);
            var planned = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStart, state, control);
            if (planned.FromIndex < route.Count - 1)
            {
                route.RemoveRange(planned.FromIndex + 1, route.Count - planned.FromIndex - 1);
            }

            legStart = route.Count - 1;
            route.AddRange(planned.Samples.Skip(1));
            state = planned.EndState;
            controls.Add(control);
        }

        var analysis = Analyzer.Analyze(vehicle, mode, route);
        var trailer = analysis.Poses[^1].TowedUnits[^1].AxleCentreMetres;
        Assert.True(trailer.DistanceTo(new Point2(18, 14)) <= 0.30, $"trailer ended at {trailer}");
        Assert.DoesNotContain(analysis.Violations, item => item.Kind == ViolationKind.ArticulationAngle);

        var replay = ManoeuvreReplayService.Replay(
            vehicle, mode, new ManoeuvreDefinition(1, start.RearAxleCentreMetres, 0.0, TravelDirection.Forward, controls));
        Assert.Equal(route, replay.Samples);
        Assert.Equal(state, replay.EndState);
    }

    private static ReverseLeg DockAt(VehicleDefinition vehicle, Point2 target, double exitDegrees)
    {
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        return TrailerReverseGenerator.DockTowedUnit(
            vehicle, vehicle.DrivingModes["B"], start, ArticulationChain.StartAligned(vehicle, 0.0),
            target, exitDegrees * Math.PI / 180.0, 0.05);
    }

    /// <summary>
    /// The docking case: the trailer has to arrive at the point <em>and</em> square to the direction
    /// asked for. Aiming at a point cannot do this — pursuit closes from whatever direction it
    /// happens to approach from, which is how a trailer ends up across the face of a dock.
    /// </summary>
    private static double SquarenessDegrees(TowedUnitPose trailer, double exitDegrees) =>
        Math.Abs(Geometry2D.NormalizeAngle(
            trailer.HeadingRadians - Geometry2D.NormalizeAngle((exitDegrees * Math.PI / 180.0) + Math.PI)))
        * 180.0 / Math.PI;

    /// <summary>
    /// Given the run-in a dock approach normally has, the trailer arrives on the point and square to
    /// it. Aiming at a point cannot do this — pursuit closes from whatever direction it happens to
    /// approach from, which is how a trailer ends up across the face of a dock.
    /// </summary>
    [Theory]
    [InlineData(50.0, 8.0, 180.0)]
    [InlineData(60.0, 12.0, 180.0)]
    [InlineData(70.0, 16.0, 180.0)]
    [InlineData(80.0, 12.0, 180.0)]
    [InlineData(100.0, 20.0, 180.0)]
    public void ASemitrailerBacksSquareOntoADock(double back, double across, double exitDegrees)
    {
        var vehicle = Catalog.Get("SVT");
        var target = new Point2(TrailerStart(vehicle) - back, across);

        var leg = DockAt(vehicle, target, exitDegrees);
        var analysis = Analyzer.Analyze(vehicle, vehicle.DrivingModes["B"], leg.Samples);
        var trailer = analysis.Poses[^1].TowedUnits[^1];

        Assert.True(leg.ReachedTarget);
        Assert.True(trailer.AxleCentreMetres.DistanceTo(target) <= 0.30, "position");
        Assert.True(SquarenessDegrees(trailer, exitDegrees) <= 1.5, "squareness");
        Assert.DoesNotContain(analysis.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
    }

    /// <summary>
    /// Squaring up costs distance — roughly the first fifteen metres go on establishing the fold and
    /// the last on taking it out again. Asked to dock in less room than that needs, the approach gets
    /// as close as it can and reports that it did not square, rather than claiming a dock it did not
    /// make.
    /// </summary>
    [Theory]
    [InlineData(30.0, 4.0, 180.0)]
    [InlineData(40.0, 8.0, 180.0)]
    [InlineData(45.0, -10.0, 180.0)]
    [InlineData(40.0, 6.0, 160.0)]
    [InlineData(40.0, 6.0, 200.0)]
    public void ADockWithTooLittleRunInStopsCloseAndSaysItIsNotSquare(
        double back, double across, double exitDegrees)
    {
        var vehicle = Catalog.Get("SVT");
        var target = new Point2(TrailerStart(vehicle) - back, across);

        var leg = DockAt(vehicle, target, exitDegrees);
        var analysis = Analyzer.Analyze(vehicle, vehicle.DrivingModes["B"], leg.Samples);
        var trailer = analysis.Poses[^1].TowedUnits[^1];

        Assert.False(leg.ReachedTarget);
        // Close, and honest about it -- not a wild miss.
        Assert.True(trailer.AxleCentreMetres.DistanceTo(target) <= 1.8, "position");
        Assert.True(SquarenessDegrees(trailer, exitDegrees) <= 8.0, "squareness");
        Assert.DoesNotContain(analysis.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
    }

    /// <summary>
    /// Given room, the approach settles onto the line rather than weaving across it — the failure
    /// that makes a docking manoeuvre unusable even when it technically arrives.
    /// </summary>
    [Fact]
    public void TheApproachSettlesOntoTheLineWithoutWeaving()
    {
        var vehicle = Catalog.Get("SVT");
        var target = new Point2(TrailerStart(vehicle) - 120.0, 8.0);

        var leg = DockAt(vehicle, target, 180.0);
        var analysis = Analyzer.Analyze(vehicle, vehicle.DrivingModes["B"], leg.Samples);

        // Weaving is crossing the line and coming back, repeatedly. Settling may cross it once, by
        // a few centimetres; a controller that weaves crosses it again and again.
        var cross = analysis.Poses
            .Where(pose => pose.StationMetres >= 20.0)
            .Select(pose => pose.TowedUnits[^1].AxleCentreMetres.Y - 8.0)
            .ToArray();
        Assert.True(cross.Length > 100);

        // Counted only while the error still means something: once it has settled to millimetres the
        // sign flips on numerical noise, which is not what weaving is.
        const double Meaningful = 0.05;
        var crossings = 0;
        var side = 0;
        foreach (var value in cross)
        {
            if (Math.Abs(value) < Meaningful) continue;
            var current = Math.Sign(value);
            if (side != 0 && current != side) crossings++;
            side = current;
        }

        Assert.True(crossings <= 1, $"the approach crossed the line {crossings} times");
        Assert.True(cross.Skip(cross.Length / 2).Max(Math.Abs) <= 0.15, "overshoot after settling");

        Assert.True(leg.ReachedTarget);
        Assert.True(Math.Abs(leg.HeadingErrorRadians!.Value) * 180.0 / Math.PI <= 1.0);
    }

    /// <summary>
    /// Docking is a single-joint capability, and the boundary is enforced rather than discovered.
    /// A two-joint chain is aimed at the point instead, and says the heading was not honoured.
    /// </summary>
    [Fact]
    public void ATwoJointCombinationIsAimedRatherThanDockedAndSaysSo()
    {
        var svt = Catalog.Get("SVT");
        var pvt = Catalog.Get("PVT");
        Assert.True(TrailerReverseGenerator.CanDock(svt));
        Assert.False(TrailerReverseGenerator.CanDock(pvt));
        Assert.Throws<ArgumentException>(() => DockAt(pvt, new Point2(-40, 8), 180.0));

        var mode = pvt.DrivingModes["B"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, pvt) };
        var control = new ManoeuvreControl(
            new(pvt.StraightAxleOffsetsMetres[^1] - 35.0, 6.0, 0.0),
            TravelDirection.Reverse,
            ManoeuvreControlKind.Aim,
            Math.PI);

        var planned = ManoeuvreReplayService.PlanControl(pvt, mode, route, 0, start, control);

        Assert.True(planned.ExitHeadingUnavailable);
        Assert.True(planned.Samples.Count > 1);

        // The same control on a chain that can be docked honours the heading instead.
        var svtRoute = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, svt) };
        var svtPlanned = ManoeuvreReplayService.PlanControl(
            svt, svt.DrivingModes["B"], svtRoute, 0, start,
            control with { PositionMetres = new(svt.StraightAxleOffsetsMetres[^1] - 35.0, 6.0, 0.0) });
        Assert.False(svtPlanned.ExitHeadingUnavailable);
    }

    /// <summary>
    /// The limit the README states: a locked turn in reverse still steers the tractor open-loop, so
    /// it folds. It is not prevented — the sweep is what a driver holding the lock would really
    /// produce — but it must not pass silently, because the swept area it builds is the shape of a
    /// jackknife.
    /// </summary>
    [Fact]
    public void ALockedTurnInReverseIsFlaggedRatherThanPassingSilently()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };

        var planned = ManoeuvreReplayService.PlanControl(
            vehicle, mode, route, 0, start,
            new ManoeuvreControl(
                LockedTurnGenerator.CursorForSweep(start, Math.PI / 2.0, 10.0),
                TravelDirection.Reverse,
                ManoeuvreControlKind.Turn));

        var analysis = Analyzer.Analyze(vehicle, mode, planned.Samples);

        Assert.True(analysis.MaximumArticulationAnglesRadians[0] > VehicleAccessAnalyzer.JackknifeFoldRadians);
        Assert.Contains(analysis.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
        Assert.False(analysis.IsFeasible);
    }

    /// <summary>A rigid vehicle still reverses the way it always did — the tractor is what is aimed.</summary>
    [Fact]
    public void RigidVehiclesStillAimTheirOwnRearAxle()
    {
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };

        var planned = ManoeuvreReplayService.PlanControl(
            vehicle, mode, route, 0, start, new ManoeuvreControl(new(-12, 3, 0), TravelDirection.Reverse));

        Assert.True(planned.Samples.Count > 1);
        Assert.All(planned.Samples, sample => Assert.Equal(TravelDirection.Reverse, sample.Direction));
        Assert.Empty(Analyzer.Analyze(vehicle, mode, planned.Samples).Poses[0].TowedUnits);
    }
}
