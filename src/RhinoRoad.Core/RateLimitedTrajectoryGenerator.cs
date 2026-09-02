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
