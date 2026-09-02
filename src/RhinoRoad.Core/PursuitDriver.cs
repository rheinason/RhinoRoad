namespace RhinoRoad.Core;

/// <summary>Why a segment stopped.</summary>
public enum SegmentOutcome
{
    /// <summary>The rear axle reached the target.</summary>
    Arrived,

    /// <summary>The target cannot be reached from here: the vehicle turned a full circle trying.</summary>
    Unreachable,

    /// <summary>The step budget ran out, which means a bug or an absurdly distant target.</summary>
    StepLimit
}

public sealed record DrivenSegment(
    VehicleState EndState,
    IReadOnlyList<RouteSample> Samples,
    SegmentOutcome Outcome)
{
    public bool Arrived => Outcome == SegmentOutcome.Arrived;
}

public sealed record DrivenRoute(IReadOnlyList<RouteSample> Samples, IReadOnlyList<SegmentOutcome> Outcomes)
{
    public bool Arrived => Outcomes.All(outcome => outcome == SegmentOutcome.Arrived);
}

/// <summary>
/// Drives the vehicle to a point by re-aiming at it every step.
/// </summary>
/// <remarks>
/// <para>
/// The steering law is the same one a single arc uses — the arc from the rear axle through the
/// target — but it is recomputed after every 0.10 m rather than driven to its end. That difference
/// is the whole interaction model. Committing to one arc leaves the vehicle turned by twice the
/// bearing picked, an inscribed-angle result, so a click on the line you meant to leave along
/// overshoots it and has to be corrected back; re-aiming never travels far enough on one curvature
/// for that to accumulate, and the wheel unwinds on approach because the law asks it to.
/// </para>
/// <para>
/// The target is fixed for the duration of a segment, not dragged. A path that followed the mouse
/// would depend on the trail the mouse took and could not be reproduced from anything storable, so
/// there would be no way to edit it afterwards. Aiming at a stored point means recording and
/// replaying are the same code, and an edited waypoint replays into a route that is still drivable.
/// </para>
/// </remarks>
public static class PursuitDriver
{
    /// <summary>Distance between re-aims. Also the sample spacing of everything downstream.</summary>
    public const double StepMetres = 0.10;

    /// <summary>How close the rear axle must come to count as arrived.</summary>
    /// <remarks>
    /// 5 mm. A waypoint is where the designer said the axle should pass, and on a tight stretch the
    /// question being asked of this tool is often decided by centimetres, so the answer must not be
    /// blurred by where the integrator happened to stop.
    /// </remarks>
    public const double ArrivalToleranceMetres = 0.005;

    // A vehicle circling a target it cannot reach — one inside its turning circle — accumulates a
    // full turn per orbit, while no honest approach to a point ever needs one. That makes total
    // rotation the exact signature of the failure, and cheaper to test for than the geometry.
    private const double MaximumRotationRadians = 2.0 * Math.PI;

    // 2 km of travel. Only a bug or a target on the far side of the site reaches this.
    private const int MaximumSteps = 20000;

    /// <summary>Drives from <paramref name="start"/> to <paramref name="target"/>.</summary>
    /// <remarks>
    /// A direction that differs from the one the vehicle is already travelling in reverses it in
    /// place, leaving the wheels where they are — the cusp of a three-point turn.
    /// </remarks>
    public static DrivenSegment Drive(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        Point2 target,
        TravelDirection direction)
    {
        var state = start.Direction == direction ? start : start with { Direction = direction };
        var samples = new List<RouteSample>();
        var rotationRadians = 0.0;
        var outcome = SegmentOutcome.Arrived;

        for (var step = 0; ; step++)
        {
            var remaining = state.RearAxleCentreMetres.XY.DistanceTo(target);
            if (remaining <= ArrivalToleranceMetres) break;

            if (Math.Abs(rotationRadians) >= MaximumRotationRadians)
            {
                outcome = SegmentOutcome.Unreachable;
                break;
            }

            if (step >= MaximumSteps)
            {
                outcome = SegmentOutcome.StepLimit;
                break;
            }

            // Pursuit steers along the arc from the rear axle through the target, and for a
            // target behind the vehicle that arc is an enormous circle taken the long way round —
            // the solve technically converges, but over kilometres. While the target is astern the
            // only sensible command is the tightest turn towards it, which is also what a driver
            // does. Pursuit resumes the moment the target comes ahead.
            var steering = AheadDistance(state, target) < 0.0
                ? mode.MaximumWheelAngleRadians * TurnSign(state, target)
                : RateLimitedTrajectoryGenerator
                    .ControlsFromCursor(vehicle, mode, state, target)
                    .TargetSteeringRadians;

            var advanced = RateLimitedTrajectoryGenerator.Advance(
                vehicle,
                mode,
                state,
                steering,
                Math.Min(StepMetres, remaining));
            rotationRadians += advanced.HeadingChangeRadians;
            state = advanced.State;
            samples.Add(advanced.Sample);
        }

        return new DrivenSegment(state, samples, outcome);
    }

    /// <summary>Which way to turn to bring the target ahead: left is positive.</summary>
    /// <remarks>
    /// Reversing flips it. The wheel that swings the nose left drives the rear axle right when the
    /// vehicle is backing, and the rear axle is what is being steered onto the target. A target
    /// exactly on the axis has no side, so it goes left, deterministically — replay reproduces the
    /// route only if every arbitrary choice is made the same way every time.
    /// </remarks>
    private static double TurnSign(VehicleState state, Point2 target)
    {
        var movementHeading = state.VehicleHeadingRadians +
            (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var delta = target - state.RearAxleCentreMetres.XY;
        var lateral = (-delta.X * Math.Sin(movementHeading)) + (delta.Y * Math.Cos(movementHeading));
        return (lateral < 0.0 ? -1.0 : 1.0) * (double)state.Direction;
    }

    /// <summary>How far ahead of the rear axle the target lies, along the direction of travel.</summary>
    private static double AheadDistance(VehicleState state, Point2 target)
    {
        var movementHeading = state.VehicleHeadingRadians +
            (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var delta = target - state.RearAxleCentreMetres.XY;
        return (delta.X * Math.Cos(movementHeading)) + (delta.Y * Math.Sin(movementHeading));
    }

    /// <summary>Rebuilds the whole route from its waypoints.</summary>
    public static DrivenRoute Replay(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        Manoeuvre manoeuvre)
    {
        var recorder = new RouteRecorder(vehicle, mode, manoeuvre.StartMetres, manoeuvre.StartHeadingRadians);
        var outcomes = new List<SegmentOutcome>(manoeuvre.Waypoints.Count);
        foreach (var waypoint in manoeuvre.Waypoints)
        {
            recorder.Direction = waypoint.Direction;
            outcomes.Add(recorder.Commit(waypoint.TargetMetres).Outcome);
        }

        return new DrivenRoute(recorder.Samples, outcomes);
    }
}

/// <summary>
/// A manoeuvre under construction: the committed route so far, plus the segment being aimed.
/// </summary>
/// <remarks>
/// Recording is replaying. <see cref="Preview"/> and <see cref="Commit"/> run the same drive a
/// stored waypoint will run later, so a route cannot come out of an edit differing from the one
/// that was drawn — not because two implementations agree, but because there is only one.
/// </remarks>
public sealed class RouteRecorder
{
    private readonly VehicleDefinition _vehicle;
    private readonly DrivingModeDefinition _mode;
    private readonly List<RouteSample> _samples;
    private readonly Point3 _startMetres;
    private readonly double _startHeadingRadians;
    private readonly List<RouteWaypoint> _waypoints = [];

    // One entry per waypoint: the state and sample count to restore when it is undone.
    private readonly Stack<(VehicleState State, int SampleCount)> _history = new();

    public RouteRecorder(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        Point3 startMetres,
        double startHeadingRadians)
    {
        _vehicle = vehicle;
        _mode = mode;
        _startMetres = startMetres;
        _startHeadingRadians = Geometry2D.NormalizeAngle(startHeadingRadians);
        State = new VehicleState(startMetres, startHeadingRadians, 0.0, TravelDirection.Forward, 0.0);
        _samples =
        [
            new RouteSample(0.0, startMetres, Geometry2D.NormalizeAngle(startHeadingRadians), 0.0, TravelDirection.Forward)
        ];
    }

    /// <summary>The direction the next segment will be driven in.</summary>
    public TravelDirection Direction { get; set; } = TravelDirection.Forward;

    /// <summary>The state at the end of the committed route.</summary>
    public VehicleState State { get; private set; }

    public IReadOnlyList<RouteSample> Samples => _samples;

    public IReadOnlyList<RouteWaypoint> Waypoints => _waypoints;

    public bool CanUndo => _history.Count > 0;

    /// <summary>Drives towards <paramref name="target"/> without committing — the aiming preview.</summary>
    public DrivenSegment Preview(Point2 target) =>
        PursuitDriver.Drive(_vehicle, _mode, State, target, Direction);

    /// <summary>Appends the segment to <paramref name="target"/> as a waypoint.</summary>
    public DrivenSegment Commit(Point2 target)
    {
        var segment = Preview(target);
        if (segment.Samples.Count == 0) return segment;
        _history.Push((State, _samples.Count));
        _waypoints.Add(new RouteWaypoint(target, Direction));
        _samples.AddRange(segment.Samples);
        State = segment.EndState;
        return segment;
    }

    /// <summary>Drops the last waypoint and everything it drew.</summary>
    public bool Undo()
    {
        if (_history.Count == 0) return false;
        var (state, sampleCount) = _history.Pop();
        _samples.RemoveRange(sampleCount, _samples.Count - sampleCount);
        _waypoints.RemoveAt(_waypoints.Count - 1);
        State = state;
        Direction = state.Direction;
        return true;
    }

    public Manoeuvre ToManoeuvre() => new(
        _vehicle.Id,
        _mode.Id,
        _startMetres,
        _startHeadingRadians,
        _waypoints.ToArray());
}
