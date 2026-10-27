using System.Globalization;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// The reversing features, run against every preset in vehicles.json and every driving mode it has:
/// docking from a turn-away, docking straight behind with a lateral shift, the planner from a
/// straight approach, the area search, and the capability predicates that say which of those a
/// vehicle supports. Distances are multiples of <see cref="ReversePlanner.ManoeuvreScaleMetres"/> so
/// the same layout means something for a car and for a B-double.
/// </summary>
public sealed class AllPresetReversingTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();
    private static readonly VehicleAccessAnalyzer Analyzer = new();
    private const double HeadingToleranceRadians = ReversePlanner.ArrivalToleranceDegrees * Math.PI / 180.0;

    public static IEnumerable<object[]> Presets() =>
        Catalog.Vehicles.Select(vehicle => new object[] { vehicle.Id });

    public static IEnumerable<object[]> PresetModes() => All();

    // Every preset and mode runs every case and must pass. Mode A of SVT, SK and MVT used to be
    // skipped here: the single-joint cascade could not dock at mode A's slew per metre, and the
    // planner's pull-ups stopped short of the run a two-joint reverse needs at it. Both are fixed
    // (TrailerReverseGenerator.DockTowedUnitInAvailableSpace, ReversePlanner.Find), so no row is
    // skipped; a case found not to work belongs back here as a skipped row with its measurement,
    // not as a weakened assertion.

    private static IEnumerable<object[]> All() =>
        Catalog.Vehicles.SelectMany(vehicle => vehicle.DrivingModes.Keys.Select(mode => new object[] { vehicle.Id, mode }));

    private static string Deg(double radians) => (radians * 180.0 / Math.PI).ToString("0.00", CultureInfo.InvariantCulture);

    private static ManoeuvreDefinition Definition(VehicleState start, IEnumerable<ManoeuvreControl> controls) =>
        new(1, start.RearAxleCentreMetres, start.VehicleHeadingRadians, start.Direction, controls.ToArray());

    private static (VehicleState Start, List<RouteSample> Route, ManoeuvreControl Turn, VehicleState End) TurnAway(
        VehicleDefinition vehicle, DrivingModeDefinition mode, double scale, double sideSign)
    {
        var start = new VehicleState(new(-4.0 * scale, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };
        var turn = new ManoeuvreControl(new(1.5 * scale, sideSign * 0.4 * scale, 0), TravelDirection.Forward);
        var leg = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start, turn);
        route.AddRange(leg.Samples.Skip(1));
        return (start, route, turn, leg.EndState);
    }

    private static void AssertArrived(
        VehicleDefinition vehicle, DrivingModeDefinition mode, IReadOnlyList<RouteSample> samples,
        VehicleState endState, Point2 axle, string label)
    {
        var miss = ReverseBay.Miss(vehicle, samples, endState, axle, Math.PI);
        var fold = FoldLimit.Measure(vehicle, samples);
        var detail = $"{label}: miss {miss.PositionMetres:0.000} m, {Deg(miss.HeadingRadians!.Value)} deg off square, "
            + $"max fold {Deg(fold.MaximumRadians)} deg, limit reached {fold.LimitReached}";
        Assert.True(miss.PositionMetres <= ReversePlanner.ArrivalToleranceMetres, detail);
        Assert.True(miss.HeadingRadians <= HeadingToleranceRadians, detail);
        Assert.False(fold.LimitReached, detail);
        var analysis = Analyzer.Analyze(vehicle, mode, samples);
        Assert.False(FoldLimit.IsReached(vehicle, analysis.MaximumArticulationAnglesRadians), detail);
    }

    /// <summary>
    /// The live-testing case: drive forward parallel to the road, turn away, and reverse into a bay
    /// square to 180 degrees, either side of the road. The bay is six manoeuvre scales behind and a
    /// scale to the side. The saved controls replay the planned samples exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(PresetModes))]
    public void DockingIntoABayAfterTurningAwayArrivesWithinTolerance(string id, string modeId)
    {
        var vehicle = Catalog.Get(id);
        var mode = vehicle.DrivingModes[modeId];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);

        foreach (var side in new[] { 1.0, -1.0 })
        {
            var (start, route, turn, end) = TurnAway(vehicle, mode, scale, side);
            var bay = ReverseBay.AxleFromRear(vehicle, new Point3(-6.0 * scale, -side * 1.0 * scale, 0), Math.PI);
            var state = end with { Direction = TravelDirection.Reverse };
            var dock = new ManoeuvreControl(bay, TravelDirection.Reverse, ManoeuvreControlKind.Aim, Math.PI);

            var docked = ManoeuvreReplayService.PlanControl(vehicle, mode, route, route.Count - 1, state, dock);
            var complete = route.Concat(docked.Samples.Skip(1)).ToArray();

            Assert.False(docked.ExitHeadingUnavailable);
            Assert.False(docked.RequestedAngleExceeded, $"{id} {modeId} side {side}: requested angle exceeded");
            AssertArrived(vehicle, mode, complete, docked.EndState, bay.XY, $"{id} {modeId} side {side}");

            var replay = ManoeuvreReplayService.Replay(vehicle, mode, Definition(start, [turn, dock]));
            Assert.Equal(complete, replay.Samples);
            Assert.Equal(docked.EndState, replay.EndState);
        }
    }

    /// <summary>
    /// Straight behind, six scales back and a scale to either side of the line the docking body
    /// stands on: it arrives on the point and square to the line it started on, and the saved
    /// control replays the planned samples exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(PresetModes))]
    public void DockingStraightBehindWithALateralShiftArrivesWithinTolerance(string id, string modeId)
    {
        var vehicle = Catalog.Get(id);
        var mode = vehicle.DrivingModes[modeId];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var rearAxle = vehicle.StraightAxleOffsetsMetres.Count > 0 ? vehicle.StraightAxleOffsetsMetres[^1] : 0.0;

        foreach (var across in new[] { 1.0, -1.0 })
        {
            var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
            var bay = new Point3(rearAxle - (6.0 * scale), across * scale, 0);
            var dock = new ManoeuvreControl(bay, TravelDirection.Reverse, ManoeuvreControlKind.Aim, Math.PI);
            var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };

            var docked = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start, dock);

            Assert.False(docked.ExitHeadingUnavailable);
            Assert.False(docked.RequestedAngleExceeded, $"{id} {modeId} across {across}: requested angle exceeded");
            AssertArrived(vehicle, mode, docked.Samples, docked.EndState, bay.XY, $"{id} {modeId} across {across}");

            var replay = ManoeuvreReplayService.Replay(vehicle, mode, Definition(start, [dock]));
            Assert.Equal(docked.Samples, replay.Samples);
            Assert.Equal(docked.EndState, replay.EndState);
        }
    }

    /// <summary>
    /// A straight approach, then a bay to the side behind: the planner pulls up if it must, reverses
    /// in, reaches the target honouring the heading, and its controls replay to the same samples.
    /// </summary>
    [Theory]
    [MemberData(nameof(PresetModes))]
    public void PlanningFromAStraightApproachReachesABayToTheSide(string id, string modeId)
    {
        var vehicle = Catalog.Get(id);
        var mode = vehicle.DrivingModes[modeId];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var start = new VehicleState(new(-2.0 * scale, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };
        var bay = ReverseBay.AxleFromRear(vehicle, new Point3(-2.0 * scale, -0.6 * scale, 0), Math.PI);

        var proposal = ReversePlanner.Find(vehicle, mode, route, 0, start, bay, Math.PI);

        var detail = $"{id} {modeId}: miss {proposal.MissMetres:0.000} m, heading {Deg(proposal.HeadingMissRadians!.Value)} deg, "
            + $"max fold {Deg(proposal.MaximumFoldRadians)} deg";
        Assert.True(proposal.ReachedTarget, detail);
        Assert.True(proposal.HeadingHonoured, detail);
        Assert.True(proposal.MissMetres <= ReversePlanner.ArrivalToleranceMetres, detail);
        Assert.True(proposal.HeadingMissRadians <= HeadingToleranceRadians, detail);

        var replay = ManoeuvreReplayService.Replay(vehicle, mode, Definition(start, proposal.Controls));
        AssertArrived(vehicle, mode, replay.Samples, replay.EndState, bay.XY, detail);
        Assert.Equal(route.Take(proposal.Leg.FromIndex + 1).Concat(proposal.Leg.Samples.Skip(1)), replay.Samples);
    }

    /// <summary>
    /// The area search in a yard far larger than the manoeuvre: a journey that turns away and
    /// reverses into the fixed bay gets at least one suggestion, and every suggestion arrives within
    /// tolerance, stays inside the fold limit and replays as ordinary controls.
    /// </summary>
    [Theory]
    [MemberData(nameof(PresetModes))]
    public void TheAreaSearchFindsAPassingApproachInAGenerousYard(string id, string modeId)
    {
        var vehicle = Catalog.Get(id);
        var mode = vehicle.DrivingModes[modeId];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var (start, route, turn, end) = TurnAway(vehicle, mode, scale, 1.0);
        var bay = ReverseBay.AxleFromRear(vehicle, new Point3(-2.0 * scale, -0.6 * scale, 0), Math.PI);
        var reverse = ReversePlanner.Find(vehicle, mode, route, 0, end, bay, Math.PI);
        Assert.True(reverse.ReachedTarget, $"{id} {modeId}: the journey to search did not reach its bay");
        var journey = Definition(start, [turn, .. reverse.Controls]);
        Point2[] yard =
        [
            new(-14 * scale, -8 * scale), new(10 * scale, -8 * scale), new(10 * scale, 8 * scale), new(-14 * scale, 8 * scale)
        ];

        var result = ManoeuvringAreaSearch.Find(vehicle, mode, journey, 0, yard, [], 0.30, TimeSpan.FromSeconds(1.5));

        Assert.NotNull(result.Current);
        Assert.NotEmpty(result.Suggestions);
        foreach (var candidate in result.Suggestions)
        {
            Assert.True(candidate.PositionErrorMetres <= ReversePlanner.ArrivalToleranceMetres);
            Assert.True(candidate.HeadingErrorDegrees <= ReversePlanner.ArrivalToleranceDegrees);
            Assert.False(FoldLimit.IsReached(vehicle, candidate.Route), $"{id} {modeId}: suggestion folds to the limit");
            Assert.Equal(candidate.Route, ManoeuvreReplayService.Replay(vehicle, mode, candidate.Manoeuvre).Samples);
            Assert.Equal(journey.Controls[^1].PositionMetres, candidate.Manoeuvre.Controls[^1].PositionMetres);
        }
    }

    /// <summary>
    /// The predicates say what actually works: every preset has at most two joints, so each can be
    /// docked, planned and searched, its solver is the one for its joint count, and the planner
    /// honours a heading exactly when the vehicle can be docked.
    /// </summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void TheCapabilityPredicatesAgreeWithTheDispatch(string id)
    {
        var vehicle = Catalog.Get(id);
        var mode = vehicle.DrivingModes.Values.First();
        var joints = vehicle.TowedUnits.Count;

        Assert.True(joints <= 2);
        Assert.True(ReverseCapabilities.CanDock(vehicle));
        Assert.True(ReverseCapabilities.CanPlanReverse(vehicle));
        Assert.True(ReverseCapabilities.CanSearchArea(vehicle));
        Assert.NotNull(ReverseDocking.SolverFor(vehicle));
        Assert.Equal(joints == 2, MultiJointReverseDocking.Handles(vehicle));

        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var proposal = ReversePlanner.Find(vehicle, mode, route, 0, start,
            new Point3(-6.0 * scale, -scale, 0), Math.PI);
        Assert.Equal(ReverseCapabilities.CanDock(vehicle), proposal.HeadingHonoured);
        Assert.False(proposal.Leg.ExitHeadingUnavailable);
    }
}
