using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Reversing PVT — lorry, drawbar dolly, trailer body: two joints — onto a point and onto a dock.
/// The feedback cascade that docks a semitrailer cannot hold a second joint, so these fence the
/// planner that replaced it for chains of two.
/// </summary>
/// <remarks>
/// Every arrival is measured the way everything downstream measures it: by the analyzer, walking the
/// samples. Measuring with the planner's own chain is what hid the failure this replaced — reversing,
/// two integrations of the same samples part company exponentially, and a leg the cascade believed
/// had arrived was, re-walked at the replay step, a jackknife 7 m from the point.
/// </remarks>
public sealed class MultiJointReverseDockingTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();
    private static readonly VehicleAccessAnalyzer Analyzer = new();
    private static readonly double FoldStopRadians = 65.0 * Math.PI / 180.0;

    private static VehicleDefinition Pvt => Catalog.Get("PVT");

    private static VehicleState StraightReverse => new(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);

    private static Point2 BehindTheTrailer(double back, double across) =>
        new(Pvt.StraightAxleOffsetsMetres[^1] - back, across);

    private static double SquarenessDegrees(TowedUnitPose trailer, double exitMovementHeadingRadians) =>
        Math.Abs(Geometry2D.NormalizeAngle(trailer.HeadingRadians - exitMovementHeadingRadians - Math.PI))
        * 180.0 / Math.PI;

    /// <summary>
    /// Straight behind, 18 to 40 m back and up to 14 m across, arriving on the point and square to
    /// the line it started on. The single-joint law needs about 50 m of run-in for this on SVT; the
    /// planner does it in under 20 on PVT.
    /// </summary>
    [Theory]
    [InlineData(30.0, 3.0)]
    [InlineData(30.0, -5.0)]
    [InlineData(20.0, 4.0)]
    [InlineData(20.0, 8.0)]
    [InlineData(18.0, 6.0)]
    [InlineData(25.0, 10.0)]
    [InlineData(25.0, -6.0)]
    [InlineData(40.0, 12.0)]
    [InlineData(40.0, 14.0)]
    [InlineData(35.0, -14.0)]
    public void PvtBacksSquareOntoADock(double back, double across)
    {
        var vehicle = Pvt;
        var mode = vehicle.DrivingModes["B"];
        var target = BehindTheTrailer(back, across);

        var leg = MultiJointReverseDocking.Dock(
            vehicle, mode, StraightReverse, ArticulationChain.StartAligned(vehicle, 0.0), target, Math.PI);
        var analysis = Analyzer.Analyze(vehicle, mode, leg.Samples);
        var trailer = analysis.Poses[^1].TowedUnits[^1];

        Assert.True(leg.ReachedTarget, $"stopped {trailer.AxleCentreMetres.DistanceTo(target):0.00} m out");
        Assert.True(trailer.AxleCentreMetres.DistanceTo(target) <= TrailerReversePlanner.ArrivalToleranceMetres,
            $"position {trailer.AxleCentreMetres.DistanceTo(target):0.000} m");
        Assert.True(SquarenessDegrees(trailer, Math.PI) <= TrailerReversePlanner.ArrivalToleranceDegrees,
            $"squareness {SquarenessDegrees(trailer, Math.PI):0.00}°");
        Assert.All(analysis.MaximumArticulationAnglesRadians, fold => Assert.True(fold < FoldStopRadians));
        Assert.True(analysis.IsFeasible, string.Join("; ", analysis.Violations.Select(item => item.Message)));
        Assert.True(Math.Abs(leg.HeadingErrorRadians!.Value) * 180.0 / Math.PI
            <= TrailerReversePlanner.ArrivalToleranceDegrees);
    }

    /// <summary>
    /// The eight aiming targets the README measures. The cascade reached the moderate ones and stopped
    /// short of shifts steeper than about 0.35 across per metre back; the planner reaches all eight.
    /// </summary>
    [Theory]
    [InlineData(30.0, 0.0)]
    [InlineData(30.0, 3.0)]
    [InlineData(30.0, -5.0)]
    [InlineData(20.0, 8.0)]
    [InlineData(40.0, 12.0)]
    [InlineData(25.0, 10.0)]
    [InlineData(35.0, -14.0)]
    [InlineData(18.0, 6.0)]
    public void PvtReversesOntoThePointItIsSent(double back, double across)
    {
        var vehicle = Pvt;
        var mode = vehicle.DrivingModes["B"];
        var target = BehindTheTrailer(back, across);

        var leg = MultiJointReverseDocking.Aim(
            vehicle, mode, StraightReverse, ArticulationChain.StartAligned(vehicle, 0.0), target);
        var analysis = Analyzer.Analyze(vehicle, mode, leg.Samples);
        var trailer = analysis.Poses[^1].TowedUnits[^1].AxleCentreMetres;

        Assert.True(leg.ReachedTarget);
        Assert.Null(leg.HeadingErrorRadians);
        Assert.True(trailer.DistanceTo(target) <= 0.10, $"stopped {trailer.DistanceTo(target):0.000} m out");
        Assert.All(analysis.MaximumArticulationAnglesRadians, fold => Assert.True(fold < FoldStopRadians));
        Assert.True(analysis.IsFeasible, string.Join("; ", analysis.Violations.Select(item => item.Message)));
    }

    /// <summary>
    /// The live-testing bay case, on PVT: drive parallel to (25,0), turn away to (35,±5), reverse into
    /// a bay beside the line square to it. The turn-away leaves the lorry 47° across the trailer, so
    /// the reverse starts already folded, and the nearer bay leaves PVT's trailer under 16 m of run-in.
    /// All of it goes through the command's own path, and replays sample for sample.
    /// </summary>
    [Theory]
    [InlineData(5.0, 10.0, -4.0)]
    [InlineData(-5.0, 10.0, 4.0)]
    [InlineData(5.0, 5.0, -4.0)]
    [InlineData(-5.0, 5.0, 4.0)]
    [InlineData(5.0, 0.0, -4.0)]
    public void PvtReversesIntoABayAfterTurningAway(double turnAwayY, double bayX, double bayY)
    {
        var vehicle = Pvt;
        var mode = vehicle.DrivingModes["B"];
        var state = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(state, vehicle) };
        var controls = new List<ManoeuvreControl>();
        foreach (var point in new[] { new Point3(25, 0, 0), new Point3(35, turnAwayY, 0) })
        {
            var control = new ManoeuvreControl(point, TravelDirection.Forward);
            var leg = ManoeuvreReplayService.PlanControl(vehicle, mode, route, route.Count - 1, state, control);
            if (leg.FromIndex < route.Count - 1)
                route.RemoveRange(leg.FromIndex + 1, route.Count - leg.FromIndex - 1);
            route.AddRange(leg.Samples.Skip(1));
            state = leg.EndState;
            controls.Add(control);
        }

        var forwardCount = route.Count;
        state = state with { Direction = TravelDirection.Reverse };
        var bay = new Point2(bayX, bayY);
        var dockControl = new ManoeuvreControl(new(bay.X, bay.Y, 0), TravelDirection.Reverse,
            ManoeuvreControlKind.Aim, Math.PI);
        var docked = ManoeuvreReplayService.PlanControl(vehicle, mode, route, route.Count - 1, state, dockControl);
        var complete = route.Concat(docked.Samples.Skip(1)).ToArray();
        var analysis = Analyzer.Analyze(vehicle, mode, complete);
        var trailer = analysis.Poses[^1].TowedUnits[^1];

        Assert.False(docked.ExitHeadingUnavailable);
        Assert.False(docked.RequestedAngleExceeded);
        Assert.True(docked.Samples.Count > 1);
        Assert.True(trailer.AxleCentreMetres.DistanceTo(bay) <= TrailerReversePlanner.ArrivalToleranceMetres,
            $"trailer ended {trailer.AxleCentreMetres.DistanceTo(bay):0.000} m from the bay");
        Assert.True(SquarenessDegrees(trailer, Math.PI) <= TrailerReversePlanner.ArrivalToleranceDegrees,
            $"{SquarenessDegrees(trailer, Math.PI):0.00}°");
        Assert.All(analysis.Poses.Skip(forwardCount - 1), pose => Assert.All(pose.TowedUnits,
            unit => Assert.True(Math.Abs(unit.ArticulationAngleRadians) < FoldStopRadians)));
        Assert.True(analysis.IsFeasible, string.Join("; ", analysis.Violations.Select(item => item.Message)));

        controls.Add(dockControl);
        var replay = ManoeuvreReplayService.Replay(vehicle, mode,
            new ManoeuvreDefinition(1, new(0, 0, 0), 0, TravelDirection.Forward, controls));
        Assert.Equal(complete, replay.Samples);
        Assert.Equal(docked.EndState, replay.EndState);
    }

    /// <summary>
    /// A dock with too little room is not claimed. The leg is the best drivable attempt, ends short
    /// of the fold stop, and says it did not arrive.
    /// </summary>
    [Theory]
    [InlineData(12.0, 6.0)]
    [InlineData(15.0, -10.0)]
    [InlineData(12.0, 14.0)]
    public void ADockWithTooLittleRoomStopsShortAndSaysSo(double back, double across)
    {
        var vehicle = Pvt;
        var mode = vehicle.DrivingModes["B"];
        var target = BehindTheTrailer(back, across);

        var leg = MultiJointReverseDocking.Dock(
            vehicle, mode, StraightReverse, ArticulationChain.StartAligned(vehicle, 0.0), target, Math.PI);
        var analysis = Analyzer.Analyze(vehicle, mode, leg.Samples);

        Assert.False(leg.ReachedTarget);
        Assert.True(leg.Samples.Count > 1);
        Assert.All(analysis.MaximumArticulationAnglesRadians, fold => Assert.True(fold < FoldStopRadians));
        Assert.True(analysis.IsFeasible, string.Join("; ", analysis.Violations.Select(item => item.Message)));
    }

    /// <summary>
    /// The planner's trailer is the analyzer's trailer, to the last digit. This is the property the
    /// whole approach rests on, because a reversing trailer amplifies any disagreement between them.
    /// </summary>
    [Theory]
    [InlineData(40.0, 12.0)]
    [InlineData(18.0, 6.0)]
    public void ThePlannedChainIsTheChainTheAnalyzerWalks(double back, double across)
    {
        var vehicle = Pvt;
        var mode = vehicle.DrivingModes["B"];
        var target = BehindTheTrailer(back, across);
        var leg = MultiJointReverseDocking.Dock(
            vehicle, mode, StraightReverse, ArticulationChain.StartAligned(vehicle, 0.0), target, Math.PI);

        var walked = ArticulationTrace.AtEndOf(vehicle, leg.Samples)!;

        Assert.Equal(walked.HeadingsRadians, leg.EndChain.HeadingsRadians);
    }

    [Fact]
    public void PlanningIsRepeatable()
    {
        var vehicle = Pvt;
        var mode = vehicle.DrivingModes["B"];
        var target = BehindTheTrailer(20.0, 8.0);
        ReverseLeg Plan() => MultiJointReverseDocking.Dock(
            vehicle, mode, StraightReverse, ArticulationChain.StartAligned(vehicle, 0.0), target, Math.PI);

        var first = Plan();
        var second = Plan();

        Assert.Equal(first.Samples, second.Samples);
        Assert.Equal(first.EndChain.HeadingsRadians, second.EndChain.HeadingsRadians);
    }

    [Fact]
    public void OnlyChainsOfMoreThanOneJointAreHandled()
    {
        Assert.True(MultiJointReverseDocking.Handles(Pvt));
        Assert.False(MultiJointReverseDocking.Handles(Catalog.Get("SVT")));
        Assert.False(MultiJointReverseDocking.Handles(Catalog.Get("PV")));
    }
}
