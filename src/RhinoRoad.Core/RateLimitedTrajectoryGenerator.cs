namespace RhinoRoad.Core;

public sealed class RateLimitedTrajectoryGenerator
{
    public GeneratedTrajectory GenerateLeg(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        double targetSteeringAngleRadians,
        double travelDistanceMetres,
        double maximumStepMetres = 0.10)
    {
        if (travelDistanceMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(travelDistanceMetres));
        if (maximumStepMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(maximumStepMetres));

        targetSteeringAngleRadians = Math.Clamp(
            targetSteeringAngleRadians,
            -mode.MaximumWheelAngleRadians,
            mode.MaximumWheelAngleRadians);

        var sampleCount = Math.Max(1, (int)Math.Ceiling(travelDistanceMetres / maximumStepMetres));
        var step = travelDistanceMetres / sampleCount;
        var state = start;
        var samples = new List<RouteSample>(sampleCount + 1)
        {
            ToRouteSample(start, vehicle)
        };

        for (var index = 0; index < sampleCount; index++)
        {
            var advanced = Advance(vehicle, mode, state, targetSteeringAngleRadians, step);
            state = advanced.State;
            samples.Add(advanced.Sample);
        }

        return new GeneratedTrajectory(state, samples);
    }

    /// <summary>
    /// One integration step of the rate-limited bicycle model.
    /// </summary>
    /// <remarks>
    /// The single place the kinematics live. A leg holds one steering target for its whole length;
    /// pursuit re-aims every step. Both are the same motion underneath, so they share this rather
    /// than each carrying a copy that can drift from the other.
    /// </remarks>
    public static AdvanceResult Advance(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState state,
        double targetSteeringAngleRadians,
        double stepMetres)
    {
        targetSteeringAngleRadians = Math.Clamp(
            targetSteeringAngleRadians,
            -mode.MaximumWheelAngleRadians,
            mode.MaximumWheelAngleRadians);

        var directionSign = (double)state.Direction;
        var elapsed = stepMetres / mode.SpeedMetresPerSecond;
        var steeringDeltaLimit = mode.MaximumSteeringRateRadiansPerSecond * elapsed;
        var nextSteering = MoveTowards(state.SteeringAngleRadians, targetSteeringAngleRadians, steeringDeltaLimit);
        var averageSteering = (state.SteeringAngleRadians + nextSteering) * 0.5;
        var headingChange = directionSign * Math.Tan(averageSteering) * stepMetres / vehicle.WheelbaseMetres;
        var movementHeading = state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var midpointMovementHeading = movementHeading + (headingChange * 0.5);
        var point = new Point3(
            state.RearAxleCentreMetres.X + (Math.Cos(midpointMovementHeading) * stepMetres),
            state.RearAxleCentreMetres.Y + (Math.Sin(midpointMovementHeading) * stepMetres),
            state.RearAxleCentreMetres.Z);
        var vehicleHeading = Geometry2D.NormalizeAngle(state.VehicleHeadingRadians + headingChange);
        var station = state.StationMetres + stepMetres;
        var pathHeading = Geometry2D.NormalizeAngle(
            vehicleHeading + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
        var pathCurvature = directionSign * Math.Tan(nextSteering) / vehicle.WheelbaseMetres;
        return new AdvanceResult(
            new VehicleState(point, vehicleHeading, nextSteering, state.Direction, station),
            new RouteSample(station, point, pathHeading, pathCurvature, state.Direction),
            headingChange);
    }

    /// <summary>
    /// The arc from the rear axle through a picked point: the steering to hold, and how far to run.
    /// </summary>
    /// <remarks>
    /// Driven to its end this arc leaves the vehicle turned by twice the bearing of the point, an
    /// inscribed-angle result. That is a real property of aiming rather than a fault: it is why a
    /// leg aimed at the line you mean to leave along overshoots it, and why the wheel has to be run
    /// back to centre deliberately rather than by aiming at another point.
    /// </remarks>
    public static (double TargetSteeringRadians, double TravelDistanceMetres, bool RequestedAngleExceeded) ControlsFromCursor(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState state,
        Point2 cursorMetres)
    {
        var movementHeading = state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var delta = cursorMetres - state.RearAxleCentreMetres.XY;
        var cosine = Math.Cos(movementHeading);
        var sine = Math.Sin(movementHeading);
        var localX = (delta.X * cosine) + (delta.Y * sine);
        var localY = (-delta.X * sine) + (delta.Y * cosine);
        var chord = Math.Max(Math.Sqrt((localX * localX) + (localY * localY)), 0.01);
        var pathCurvature = 2.0 * localY / (chord * chord);
        var requestedSteering = Math.Atan(vehicle.WheelbaseMetres * pathCurvature / (double)state.Direction);
        var exceeded = Math.Abs(requestedSteering) > mode.MaximumWheelAngleRadians;
        var clamped = Math.Clamp(requestedSteering, -mode.MaximumWheelAngleRadians, mode.MaximumWheelAngleRadians);
        var absoluteCurvature = Math.Abs(pathCurvature);
        var distance = absoluteCurvature < 1e-6
            ? chord
            : Math.Abs(2.0 * Math.Asin(Math.Clamp(chord * absoluteCurvature * 0.5, -1.0, 1.0)) / absoluteCurvature);
        return (clamped, Math.Max(distance, 0.05), exceeded);
    }

    private static RouteSample ToRouteSample(VehicleState state, VehicleDefinition vehicle)
    {
        var directionSign = (double)state.Direction;
        var pathHeading = Geometry2D.NormalizeAngle(
            state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
        var pathCurvature = directionSign * Math.Tan(state.SteeringAngleRadians) / vehicle.WheelbaseMetres;
        return new RouteSample(state.StationMetres, state.RearAxleCentreMetres, pathHeading, pathCurvature, state.Direction);
    }

    private static double MoveTowards(double current, double target, double maximumDelta)
    {
        if (Math.Abs(target - current) <= maximumDelta) return target;
        return current + (Math.Sign(target - current) * maximumDelta);
    }
}
