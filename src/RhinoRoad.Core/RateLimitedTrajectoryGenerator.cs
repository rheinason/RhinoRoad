namespace RhinoRoad.Core;

public sealed class RateLimitedTrajectoryGenerator
{
    public GeneratedTrajectory GenerateLeg(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        double targetSteeringAngleRadians,
        double travelDistanceMetres,
        double maximumStepMetres = 0.10,
        bool wheelSetAtStandstill = false)
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
            var advanced = Advance(
                vehicle, mode, state, targetSteeringAngleRadians, step, wheelSetAtStandstill && index == 0);
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
        double stepMetres,
        bool wheelSetAtStandstill = false)
    {
        targetSteeringAngleRadians = Math.Clamp(
            targetSteeringAngleRadians,
            -mode.MaximumWheelAngleRadians,
            mode.MaximumWheelAngleRadians);

        var directionSign = (double)state.Direction;
        var elapsed = stepMetres / mode.SpeedMetresPerSecond;
        var steeringDeltaLimit = mode.MaximumSteeringRateRadiansPerSecond * elapsed;
        // A wheel turned while the vehicle is standing still costs no distance, because the rate
        // limit is a rate per unit of travel and there is no travel. The wheel is already where it is
        // wanted when the step begins, so it is also the angle held across the whole step rather than
        // the average of moving onto it.
        var nextSteering = wheelSetAtStandstill
            ? targetSteeringAngleRadians
            : MoveTowards(state.SteeringAngleRadians, targetSteeringAngleRadians, steeringDeltaLimit);
        var averageSteering = wheelSetAtStandstill
            ? nextSteering
            : (state.SteeringAngleRadians + nextSteering) * 0.5;
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
            new RouteSample(
                station,
                point,
                pathHeading,
                pathCurvature,
                state.Direction,
                IsTangentDiscontinuous: false,
                StartsFromStandstill: wheelSetAtStandstill),
            headingChange);
    }

    /// <summary>
    /// The arc from the rear axle through a picked point: the steering to hold, and how far to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven to its end this arc leaves the vehicle turned by twice the bearing of the point, an
    /// inscribed-angle result. That is a real property of aiming rather than a fault: it is why a leg
    /// aimed at the line you mean to leave along overshoots it, and why the wheel has to be run back
    /// to centre deliberately rather than by aiming at another point.
    /// </para>
    /// <para>
    /// The law's honest domain is the half-plane ahead of the beam, where the arc through the point is
    /// at most a half turn and the leg is the scale of the click. A point behind the beam is brought
    /// round to it — same range, hardest turn aiming can express — and reported, because the arc that
    /// really reaches such a point is a loop the size of whatever circle passes through the cursor.
    /// Turning further than that is what a locked turn is for.
    /// </remarks>
    public static (
        double TargetSteeringRadians,
        double TravelDistanceMetres,
        bool RequestedAngleExceeded,
        bool AimedBehindTheBeam) ControlsFromCursor(
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

        // The law's domain is the half-plane ahead of the beam. Behind it the arc that really reaches
        // the point is a loop the size of whatever circle happens to pass through the cursor — 358 m
        // to reach a point 2 m astern, 10.7 km to reach one 60 m astern — because a circle through a
        // point nearly dead astern is nearly straight, and half of a nearly straight circle is
        // enormous. Reversing walks straight into this, since reversing puts "behind the direction of
        // travel" directly in front of the nose.
        //
        // So the request is projected onto the boundary of the domain: same range, brought round to
        // the beam, which asks for the hardest turn aiming has ever been able to express. Clamping
        // the bearing rather than the swept angle is what keeps it bounded — curvature and distance
        // then both follow from one consistent point, where clamping the angle alone still left the
        // vehicle driving half of a kilometre-wide circle.
        var behindTheBeam = localX < 0.0;
        if (behindTheBeam)
        {
            localY = (localY < 0.0 ? -1.0 : 1.0) * chord;
            localX = 0.0;
        }

        var pathCurvature = 2.0 * localY / (chord * chord);
        var requestedSteering = Math.Atan(vehicle.WheelbaseMetres * pathCurvature / (double)state.Direction);
        var exceeded = Math.Abs(requestedSteering) > mode.MaximumWheelAngleRadians;
        var clamped = Math.Clamp(requestedSteering, -mode.MaximumWheelAngleRadians, mode.MaximumWheelAngleRadians);
        // How far round the requested arc goes, measured the way the vehicle drives it: from where it
        // stands, in the direction it is travelling, all the way round to the point. Reaching a point
        // *behind* the vehicle on that circle takes a reflex arc, and this is the term that has to
        // know it — `2·asin(chord·k/2)` cannot exceed half a turn, so it answered 90 degrees for an
        // arc that is really 270 and the leg stopped at the mirror position instead, metres from the
        // point with nothing to say it had missed.
        //
        // It also put a fold in the middle of the cursor's range. Swept angle peaked at half a turn
        // when the point was abeam and fell away again behind, so the biggest turns lived on a knife
        // edge across the beam; measured this way it grows all the way round, and a hard turn is a
        // region of the viewport rather than a line through it.
        var requestedCurvature = Math.Abs(pathCurvature);
        var drivableCurvature = Math.Abs(Math.Tan(clamped)) / vehicle.WheelbaseMetres;
        double distance;
        if (requestedCurvature < 1e-6 || drivableCurvature < 1e-6)
        {
            distance = chord;
        }
        else
        {
            // Angle subtended at the arc's centre, which sits abeam at the requested radius. Having
            // brought the request onto the beam this is at most a half turn, which is the most an arc
            // through a point ahead of the beam ever needs — and is why an aimed leg is always the
            // scale of the click that made it.
            var radius = 1.0 / requestedCurvature;
            var sweptRadians = Math.Atan2(localX, radius - Math.Abs(localY));
            if (sweptRadians < 0.0) sweptRadians += 2.0 * Math.PI;

            // Where the request is inside the wheel's lock this is exactly the arc through the point.
            // Where it is not, it is the turn that was asked for driven on the tightest circle the
            // vehicle can hold — the honest substitute. Running the *requested* curvature's arc
            // length at the clamped lock instead made a tighter click turn less than a slacker one,
            // so aiming further inside the turning circle opened the corner out rather than closing
            // it.
            distance = sweptRadians / drivableCurvature;
        }

        return (clamped, Math.Max(distance, 0.05), exceeded, behindTheBeam);
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
