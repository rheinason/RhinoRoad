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
        var point = start.RearAxleCentreMetres;
        var vehicleHeading = start.VehicleHeadingRadians;
        var steering = start.SteeringAngleRadians;
        var station = start.StationMetres;
        var directionSign = (double)start.Direction;
        var samples = new List<RouteSample>(sampleCount + 1)
        {
            ToRouteSample(start, vehicle)
        };

        for (var index = 0; index < sampleCount; index++)
        {
            var elapsed = step / mode.SpeedMetresPerSecond;
            var steeringDeltaLimit = mode.MaximumSteeringRateRadiansPerSecond * elapsed;
            var nextSteering = MoveTowards(steering, targetSteeringAngleRadians, steeringDeltaLimit);
            var averageSteering = (steering + nextSteering) * 0.5;
            var headingChange = directionSign * Math.Tan(averageSteering) * step / vehicle.WheelbaseMetres;
            var movementHeading = vehicleHeading + (start.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
            var midpointMovementHeading = movementHeading + (headingChange * 0.5);
            point = new Point3(
                point.X + (Math.Cos(midpointMovementHeading) * step),
                point.Y + (Math.Sin(midpointMovementHeading) * step),
                point.Z);
            vehicleHeading = Geometry2D.NormalizeAngle(vehicleHeading + headingChange);
            steering = nextSteering;
            station += step;
            var pathHeading = Geometry2D.NormalizeAngle(
                vehicleHeading + (start.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
            var pathCurvature = directionSign * Math.Tan(steering) / vehicle.WheelbaseMetres;
            samples.Add(new RouteSample(station, point, pathHeading, pathCurvature, start.Direction));
        }

        return new GeneratedTrajectory(
            new VehicleState(point, vehicleHeading, steering, start.Direction, station),
            samples);
    }

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
