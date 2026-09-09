namespace RhinoRoad.Core;

/// <param name="AimedBehindTheBeam">
/// Whether the point was behind the vehicle's beam, where aiming saturates at a half turn and the leg
/// does not reach it. Worth saying out loud: the leg is legal and drivable, it simply is not the one
/// asked for, and reversing is what puts a designer there without their noticing.
/// </param>
public sealed record PlannedManoeuvreLeg(
    int FromIndex,
    IReadOnlyList<RouteSample> Samples,
    VehicleState EndState,
    bool RequestedAngleExceeded,
    bool AimedBehindTheBeam = false);

public sealed record ReplayedManoeuvre(
    IReadOnlyList<RouteSample> Samples,
    VehicleState EndState,
    bool RequestedAngleExceeded);

/// <summary>The one authoritative implementation of an interactive click and its later replay.</summary>
public static class ManoeuvreReplayService
{
    private const double StraightEnoughRadians = 1e-3;
    private const double ExitingFraction = 0.85;
    private const double StepMetres = 0.10;

    public static PlannedManoeuvreLeg PlanControl(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<RouteSample> route,
        int legStartIndex,
        VehicleState state,
        ManoeuvreControl control)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(control);
        if (route.Count == 0) throw new ArgumentException("A route is required.", nameof(route));

        // A direction change is a standstill by definition -- the vehicle cannot swap gear rolling --
        // so the wheel is free there whether or not it was asked for. It is read from the route
        // rather than from the state, because callers set the new direction on the state before
        // planning, which would hide the cusp from here.
        var standstill = control.FromStandstill
            || (route.Count > 0 && route[^1].Direction != control.Direction);
        if (state.Direction != control.Direction) state = state with { Direction = control.Direction };
        var target = control.PositionMetres.XY;
        var bearing = Math.Atan2(
            target.Y - state.RearAxleCentreMetres.Y,
            target.X - state.RearAxleCentreMetres.X);
        var requested = RateLimitedTrajectoryGenerator.ControlsFromCursor(vehicle, mode, state, target);
        var finishing = control.Kind == ManoeuvreControlKind.Finish;
        var turning = Math.Abs(state.SteeringAngleRadians) > StraightEnoughRadians;
        var leavingTheCorner = Math.Abs(requested.TargetSteeringRadians)
            < Math.Abs(state.SteeringAngleRadians) * ExitingFraction;

        // A locked turn is not aimed at anything: the point says how far round to go, the wheel goes
        // to its lock, and the leg ends still turning. It never eases first, because easing is how
        // the wheel comes back and this control exists to keep it out there.
        //
        // It is an ordinary leg afterwards, though. Barring the next control from rewinding into it
        // — on the argument that a turn asked for at a size should keep that size — leaves the exit
        // to be corrected from the end rather than opened out of the middle, which is the S this
        // whole model exists to avoid, and it showed: the same U-turn came out with a kinked, splayed
        // exit instead of a clean parallel return. The size is what the leg is driven at; what a
        // later control does with its tail is that control's business.
        if (control.Kind == ManoeuvreControlKind.Turn)
        {
            var sweep = LockedTurnGenerator.SweepFromCursor(state, target);
            var turned = LockedTurnGenerator.Turn(vehicle, mode, state, sweep, StepMetres, standstill);
            return new PlannedManoeuvreLeg(route.Count - 1, turned.Samples, turned.EndState, false);
        }

        var fromIndex = route.Count - 1;
        var samples = new List<RouteSample>();
        var current = state;
        if (control.ExitHeadingRadians is double exitHeading && control.Kind != ManoeuvreControlKind.Turn)
        {
            var alignment = turning && route[^1].Direction == state.Direction
                ? HeadingLegGenerator.EaseOntoHeading(vehicle, mode, route, legStartIndex, exitHeading, StepMetres)
                : (FromIndex: route.Count - 1,
                    Leg: HeadingLegGenerator.ToHeading(vehicle, mode, state, exitHeading, StepMetres, standstill));
            var aligned = alignment.Leg;
            fromIndex = alignment.FromIndex;
            var offset = target - aligned.EndState.RearAxleCentreMetres.XY;
            var remaining = Math.Max(0.0, offset.X * Math.Cos(exitHeading) + offset.Y * Math.Sin(exitHeading));
            if (finishing || remaining <= Geometry2D.Epsilon)
                return new PlannedManoeuvreLeg(fromIndex, aligned.Samples, aligned.EndState, false);
            var straight = new RateLimitedTrajectoryGenerator().GenerateLeg(
                vehicle, mode, aligned.EndState, 0.0, remaining, StepMetres);
            return new PlannedManoeuvreLeg(fromIndex,
                aligned.Samples.Concat(straight.Samples.Skip(1)).ToArray(), straight.EndState, false);
        }
        var easedFirst = turning && (finishing || leavingTheCorner);
        if (easedFirst)
        {
            // At a cusp the stored last sample still describes the arriving gear.
            // Reconstructing from it would silently switch back to that gear.
            var eased = route[^1].Direction == state.Direction
                ? HeadingLegGenerator.EaseOntoHeading(vehicle, mode, route, legStartIndex, bearing, StepMetres)
                : (FromIndex: route.Count - 1,
                    Leg: HeadingLegGenerator.ToHeading(vehicle, mode, state, bearing, StepMetres, standstill));
            fromIndex = eased.FromIndex;
            samples.AddRange(eased.Leg.Samples);
            current = eased.Leg.EndState;
        }
        else
        {
            samples.Add(StateSample(state, vehicle));
        }

        if (finishing) return new PlannedManoeuvreLeg(fromIndex, samples, current, false);

        var aim = RateLimitedTrajectoryGenerator.ControlsFromCursor(vehicle, mode, current, target);
        var leg = new RateLimitedTrajectoryGenerator().GenerateLeg(
            vehicle,
            mode,
            current,
            aim.TargetSteeringRadians,
            aim.TravelDistanceMetres,
            StepMetres,
            // Only where the leg actually begins at the stop. Where the corner was eased first the
            // vehicle has already rolled, and the wheel is wherever that easing left it.
            standstill && !easedFirst);
        samples.AddRange(leg.Samples.Skip(1));
        return new PlannedManoeuvreLeg(
            fromIndex, samples, leg.EndState, aim.RequestedAngleExceeded, aim.AimedBehindTheBeam);
    }

    public static ReplayedManoeuvre Replay(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        ManoeuvreDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion != ManoeuvreDefinition.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported manoeuvre schema {definition.SchemaVersion}.");

        var state = definition.StartState;
        var route = new List<RouteSample> { StateSample(state, vehicle) };
        var legStartIndex = 0;
        var exceeded = false;
        foreach (var control in definition.Controls)
        {
            if (state.Direction != control.Direction)
            {
                state = state with { Direction = control.Direction };
                legStartIndex = route.Count - 1;
            }

            var planned = PlanControl(vehicle, mode, route, legStartIndex, state, control);
            if (planned.FromIndex < route.Count - 1)
                route.RemoveRange(planned.FromIndex + 1, route.Count - planned.FromIndex - 1);
            legStartIndex = route.Count - 1;
            route.AddRange(planned.Samples.Skip(1));
            state = planned.EndState;
            exceeded |= planned.RequestedAngleExceeded;
        }

        return new ReplayedManoeuvre(route, state, exceeded);
    }

    public static RouteSample StateSample(VehicleState state, VehicleDefinition vehicle)
    {
        var pathHeading = Geometry2D.NormalizeAngle(
            state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
        var curvature = (double)state.Direction * Math.Tan(state.SteeringAngleRadians) / vehicle.WheelbaseMetres;
        return new RouteSample(state.StationMetres, state.RearAxleCentreMetres, pathHeading, curvature, state.Direction);
    }
}
