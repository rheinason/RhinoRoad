namespace RhinoRoad.Core;

public sealed record PlannedManoeuvreLeg(
    int FromIndex,
    IReadOnlyList<RouteSample> Samples,
    VehicleState EndState,
    bool RequestedAngleExceeded);

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

        var fromIndex = route.Count - 1;
        var samples = new List<RouteSample>();
        var current = state;
        if (turning && (finishing || leavingTheCorner))
        {
            var eased = HeadingLegGenerator.EaseOntoHeading(
                vehicle, mode, route, legStartIndex, bearing, StepMetres);
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
        var leg = new RateLimitedTrajectoryGenerator()
            .GenerateLeg(vehicle, mode, current, aim.TargetSteeringRadians, aim.TravelDistanceMetres, StepMetres);
        samples.AddRange(leg.Samples.Skip(1));
        return new PlannedManoeuvreLeg(fromIndex, samples, leg.EndState, aim.RequestedAngleExceeded);
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
