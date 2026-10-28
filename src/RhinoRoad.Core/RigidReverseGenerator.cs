namespace RhinoRoad.Core;

/// <summary>
/// Reverses a rigid vehicle onto a bay: its rear axle to a point <em>and</em> square to a direction.
/// </summary>
/// <remarks>
/// <para>
/// The ordinary exit-heading leg squares the heading first and then runs straight for as far as
/// the point lies along it, which ignores how far off to the side the point is: a rigid vehicle
/// sent into a bay that way finishes parallel to it, a metre or two beside it. Reversing into a bay
/// is the commonest bay manoeuvre on a site, so it gets the same treatment the trailer does.
/// </para>
/// <para>
/// A rigid vehicle reversing is simpler than a trailer, not harder: there is no lateral slip at the
/// rear axle, so its midpoint moves along the body axis and is the stable reference to follow, and
/// the wheel sets the curvature directly instead of through a fold. What is followed is the
/// <em>approach line</em> — the line through the point along the heading asked for — with the
/// errors and signs <see cref="TrailerReverseGenerator.DockTowedUnit"/> derives: cross-track error
/// commands a bounded approach angle, the heading error against that angle commands a curvature, and
/// running the arc length backwards is what fixes the signs. The curvature becomes a wheel angle and
/// goes through the ordinary rate-limited step, so lock, slew rate and the kinematics stay in one
/// place.
/// </para>
/// <para>
/// What the trailer law leaves out is the wheel's own lag, and for a rigid vehicle that is the
/// whole story: the wheel slews at a rate per second, so per metre it moves one radian in
/// <c>speed / slew rate</c> — 6.6 m for a car in mode B, 24 m in mode A — which is as long as the
/// manoeuvre itself. Commanding a curvature as if it arrived at once had the car weaving into the
/// bay. So curvature is a third state, driven towards what the heading loop asks for at its own
/// rate, and the three loops are placed together: with a triple pole at <c>p</c> per metre the
/// approach angle trades cross-track error over <c>3 / p</c>, the heading loop runs at <c>p</c> and
/// the curvature follows at <c>3p</c>, which is critically damped as a chain and so settles
/// rather than weaving.
/// </para>
/// <para>
/// <c>p</c> is set by how fast the curvature can actually move. A metre of cross-track error asks
/// for a curvature rate of <c>p^3</c> per metre, and the wheel can supply <c>1 / (lag · wheelbase)</c>,
/// so <c>p</c> scales as the cube root of that — one number that suits a car and a 15 m bus alike,
/// in either mode, where a gain per wheelbase suited neither.
/// </para>
/// </remarks>
public static class RigidReverseGenerator
{
    /// <summary>
    /// The loop bandwidth as a multiple of the cube root of the rate the wheel can move the
    /// curvature, <c>1 / (lag · wheelbase)</c>. Measured over the rigid presets in both modes: 1.4
    /// docked every approach the wheel's slew rate physically allows within the run-in, while half
    /// of it was too slow to settle and twice it saturated the wheel and swung wide.
    /// </summary>
    public const double DockBandwidthFactor = 1.4;

    /// <summary>
    /// The steepest angle the vehicle will approach the line at. Steeper than the trailer's 50
    /// degrees, because a rigid body straightens within about one turning radius where a trailer
    /// needs its fold taken out as well: held to 50, a vehicle that had turned away from a
    /// perpendicular bay spent its run-in crabbing towards the line and arrived across it, mode A
    /// worst. Measured over the rigid presets, 70 degrees docked every perpendicular bay tried, and
    /// steeper bought nothing more.
    /// </summary>
    private const double MaximumApproachAngleRadians = 70.0 * Math.PI / 180.0;

    /// <summary>
    /// Stops a leg that is circling rather than arriving. It is a distance, not a time, because the
    /// leg is replayed from a saved journey and must come out the same on every machine.
    /// </summary>
    private const double MaximumTravelMetres = 400.0;

    /// <summary>Close enough to the target's station on the line to stop stepping towards it.</summary>
    private const double StationToleranceMetres = 0.05;

    /// <summary>
    /// Alternative bandwidth factors, tried in order when the base one does not arrive. A short
    /// run-in wants a brisker response and a large initial error a gentler one; a fixed, ordered list
    /// keeps the choice deterministic, so the preview, the committed leg and a later replay all pick
    /// the same one.
    /// </summary>
    private static readonly double[] AlternativeBandwidthFactors = [1.0, 2.0, 0.7];

    /// <summary>
    /// The loop bandwidth, per metre, for a bandwidth factor: the factor times the cube root of the
    /// rate, per metre squared, at which this vehicle's wheel can move its curvature.
    /// </summary>
    public static double BandwidthPerMetre(VehicleDefinition vehicle, DrivingModeDefinition mode, double factor)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        var lagMetresPerRadian = mode.SpeedMetresPerSecond / mode.MaximumSteeringRateRadiansPerSecond;
        return factor * Math.Cbrt(1.0 / (lagMetresPerRadian * vehicle.WheelbaseMetres));
    }

    /// <summary>
    /// Reverses until the rear axle reaches <paramref name="targetMetres"/>'s station on the
    /// approach line, trying a stronger or gentler response when the base law does not arrive, and
    /// returns the most accurate drivable arrival.
    /// </summary>
    /// <param name="exitMovementHeadingRadians">
    /// The direction of <em>travel</em> at the end. Reversing, the vehicle faces the opposite way.
    /// </param>
    public static ReverseLeg DockInAvailableSpace(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        Point2 targetMetres,
        double exitMovementHeadingRadians,
        double maximumStepMetres = 0.10,
        bool wheelSetAtStandstill = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var best = Dock(vehicle, mode, start, targetMetres, exitMovementHeadingRadians,
            maximumStepMetres, BandwidthPerMetre(vehicle, mode, DockBandwidthFactor), wheelSetAtStandstill);
        if (best.ReachedTarget) return best;

        var bestError = DockError(best, targetMetres, exitMovementHeadingRadians);
        foreach (var factor in AlternativeBandwidthFactors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Dock(vehicle, mode, start, targetMetres, exitMovementHeadingRadians,
                maximumStepMetres, BandwidthPerMetre(vehicle, mode, factor), wheelSetAtStandstill);
            var error = DockError(candidate, targetMetres, exitMovementHeadingRadians);
            // An arrival beats any miss; among equals, the closer finish wins.
            if ((candidate.ReachedTarget && !best.ReachedTarget)
                || (candidate.ReachedTarget == best.ReachedTarget && error < bestError))
            {
                best = candidate;
                bestError = error;
            }
        }

        return best;
    }

    /// <summary>
    /// Reverses the rear axle down the approach line through <paramref name="targetMetres"/> along
    /// <paramref name="exitMovementHeadingRadians"/>, stopping at the target's station on it.
    /// </summary>
    /// <returns>
    /// The leg. <see cref="ReverseLeg.ReachedTarget"/> is true only when it stopped within
    /// <see cref="ReversePlanner.ArrivalToleranceMetres"/> of the line and
    /// <see cref="ReversePlanner.ArrivalToleranceDegrees"/> of square, the tolerance the
    /// planner and the area search accept. <see cref="ReverseLeg.EndChain"/> is empty.
    /// </returns>
    public static ReverseLeg Dock(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        Point2 targetMetres,
        double exitMovementHeadingRadians,
        double maximumStepMetres,
        double bandwidthPerMetre,
        bool wheelSetAtStandstill = false)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(start);
        if (vehicle.IsArticulated)
            throw new ArgumentException("A rigid vehicle is required; a combination docks its trailer.", nameof(vehicle));
        if (maximumStepMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(maximumStepMetres));
        if (bandwidthPerMetre <= 0.0) throw new ArgumentOutOfRangeException(nameof(bandwidthPerMetre));

        var state = start.Direction == TravelDirection.Reverse
            ? start
            : start with { Direction = TravelDirection.Reverse };

        // The body faces back up the approach line, because it is reversing down it.
        var facing = Geometry2D.NormalizeAngle(exitMovementHeadingRadians + Math.PI);
        var tangent = new Point2(Math.Cos(facing), Math.Sin(facing));
        var normal = new Point2(-tangent.Y, tangent.X);
        var curvatureLimit = Math.Tan(mode.MaximumWheelAngleRadians) / vehicle.WheelbaseMetres;
        var positionTolerance = ReversePlanner.ArrivalToleranceMetres;
        var headingTolerance = ReversePlanner.ArrivalToleranceDegrees * Math.PI / 180.0;

        var samples = new List<RouteSample> { ManoeuvreReplayService.StateSample(state, vehicle) };
        var travelled = 0.0;
        var reached = false;
        var closestApproach = double.PositiveInfinity;
        double? headingError = null;
        double? startAlong = null;

        // A vehicle pointed the wrong way backs away from the bay before the law can turn it, and
        // one sent somewhere it cannot turn into can circle. Neither ends by itself, so the leg also
        // ends once it has backed this much further from the bay's station than where it began --
        // two full-lock circles of room, which a real approach never needs.
        var retreatAllowance = 4.0 / curvatureLimit;

        while (travelled < MaximumTravelMetres)
        {
            var offset = state.RearAxleCentreMetres.XY - targetMetres;
            var along = (offset.X * tangent.X) + (offset.Y * tangent.Y);
            var cross = (offset.X * normal.X) + (offset.Y * normal.Y);
            var facingError = Geometry2D.NormalizeAngle(state.VehicleHeadingRadians - facing);
            closestApproach = Math.Min(closestApproach, state.RearAxleCentreMetres.XY.DistanceTo(targetMetres));
            headingError = facingError;
            startAlong ??= along;

            // Run in until the axle reaches the target's station on the line. Cross-track and
            // heading are what the approach was for; arriving is just running out of line.
            if (along <= 0.0)
            {
                reached = Math.Abs(cross) <= positionTolerance && Math.Abs(facingError) <= headingTolerance;
                break;
            }

            if (along > startAlong.Value + retreatAllowance) break;

            // Line-of-sight guidance, as for the trailer: cross-track error buys an approach angle,
            // bounded by construction, and the curvature asked for only ever closes the gap between
            // the heading held and the heading wanted. The curvature actually commanded then moves
            // towards that at 3p, so the wheel's lag is part of the loop rather than a surprise to it.
            var p = bandwidthPerMetre;
            var approach = Math.Clamp(
                Math.Atan(cross * p / 3.0),
                -MaximumApproachAngleRadians,
                MaximumApproachAngleRadians);
            var desired = Math.Clamp(
                p * Geometry2D.NormalizeAngle(facingError - approach),
                -curvatureLimit,
                curvatureLimit);
            var step = Math.Min(maximumStepMetres, Math.Max(along, StationToleranceMetres));
            var held = Math.Tan(state.SteeringAngleRadians) / vehicle.WheelbaseMetres;
            var next = held + ((desired - held) * Math.Min(1.0, 3.0 * p * step));

            // Curvature here is heading change per metre of travel along the body's own axis, signed
            // by direction, which is what the bicycle model's tan(wheel) / wheelbase already is.
            var steering = Math.Atan(vehicle.WheelbaseMetres * next);
            var advanced = RateLimitedTrajectoryGenerator.Advance(
                vehicle, mode, state, steering, step, wheelSetAtStandstill && samples.Count == 1);
            state = advanced.State;
            samples.Add(advanced.Sample);
            travelled += step;
        }

        return new ReverseLeg(
            new GeneratedTrajectory(state, samples),
            ArticulationChain.StartAligned(vehicle, state.VehicleHeadingRadians),
            reached,
            closestApproach)
        {
            HeadingErrorRadians = headingError
        };
    }

    private static double DockError(ReverseLeg leg, Point2 target, double exitHeading)
    {
        var headingError = Math.Abs(Geometry2D.NormalizeAngle(
            leg.EndState.VehicleHeadingRadians - exitHeading - Math.PI));
        // The same weighting the trailer uses: position first, but a near point reached across the
        // bay must not win.
        return leg.EndState.RearAxleCentreMetres.XY.DistanceTo(target) + (2.0 * headingError);
    }
}
