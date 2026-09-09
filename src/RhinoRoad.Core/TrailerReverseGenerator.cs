namespace RhinoRoad.Core;

/// <summary>
/// Reverses an articulated vehicle by steering the trailer rather than the tractor.
/// </summary>
/// <remarks>
/// <para>
/// Aiming the tractor's rear axle at a point is the right model going forward and the wrong one
/// going back. Reversing, the fold is an unstable equilibrium — it grows rather than settles, with
/// an e-folding distance of about one trailer wheelbase — so a tractor driven at a target simply
/// jackknifes, which is what a driver would do if they never countersteered. What a driver actually
/// does is decide where the trailer should go and move the tractor to put it there.
/// </para>
/// <para>
/// That is what this does, and the structure follows from one observation: for a unit hitched at its
/// tower's axle, its own curvature is <c>tan(fold) / wheelbase</c> — the fold <em>is</em> its
/// steering angle. So the chain is a stack of bicycles, each steered by the joint ahead of it, and
/// the control cascade writes itself:
/// </para>
/// <list type="number">
/// <item>Pure pursuit on the last unit gives the curvature its axle should follow.</item>
/// <item>That curvature asks for a fold at its joint: <c>atan(curvature · wheelbase)</c>.</item>
/// <item>Driving the fold to that value asks for a curvature of the unit towing it — the joint's own
/// rate equation solved for the tower — and the same question is put again one joint further in.</item>
/// <item>The tractor's curvature is a steering angle, which goes through the ordinary rate-limited
/// step, so lock, slew rate and the kinematics stay in one place.</item>
/// </list>
/// <para>
/// Each joint contributes a first-order loop, and they nest correctly by construction: the loop
/// closed last is the tractor's, which is also the one that responds fastest. Where the chain asks
/// for more lock than the vehicle has, the request is clamped and the trailer simply turns more
/// slowly — the leg stays drivable rather than becoming a fiction.
/// </para>
/// </remarks>
public static class TrailerReverseGenerator
{
    /// <summary>
    /// How hard each joint is driven toward its target fold, per metre travelled. Chosen as a
    /// settling distance rather than a number: at 0.35 a fold closes most of its error over about
    /// three metres, which is brisk enough to follow a dock approach and slack enough that the
    /// tractor is not asked for lock it does not have on every step.
    /// </summary>
    public const double FoldGainPerMetre = 0.35;

    /// <summary>
    /// The largest fold the controller will ever ask for. Well inside the right angle past which the
    /// model stops describing a real vehicle, because asking for a fold is asking to approach it and
    /// the approach should not be the thing that jackknifes.
    /// </summary>
    private const double MaximumCommandedFoldRadians = 55.0 * Math.PI / 180.0;

    /// <summary>
    /// The fold at which the leg stops, short of the commanded maximum and well short of the right
    /// angle that makes a run unusable. Reaching it means the target cannot be reversed to from here
    /// — which is not a failure to report so much as the answer: a driver in that position stops,
    /// pulls forward, and starts the reverse again from a better line. Ending the leg lets them do
    /// exactly that, with the leg they have so far still drivable.
    /// </summary>
    private const double MaximumSafeFoldRadians = 65.0 * Math.PI / 180.0;

    /// <summary>Close enough to the target to stop, in metres of the aimed unit's axle.</summary>
    private const double ArrivalToleranceMetres = 0.05;

    /// <summary>
    /// Pursuit closes on a point asymptotically, so a leg often turns away a few centimetres out
    /// rather than landing inside the arrival tolerance. Anything this close counts as arrived.
    /// </summary>
    private const double ClosestApproachToleranceMetres = 0.25;

    /// <summary>Stops a leg that is circling rather than arriving.</summary>
    private const double MaximumTravelMetres = 400.0;

    /// <summary>
    /// How hard the trailer is turned onto the heading the approach asks of it, per radian of error
    /// and per metre travelled.
    /// </summary>
    public const double DockHeadingGain = 0.23;

    /// <summary>
    /// The steepest angle the trailer will approach the line at. Coming in much steeper than this
    /// leaves nothing to straighten out with.
    /// </summary>
    private const double MaximumApproachAngleRadians = 50.0 * Math.PI / 180.0;

    /// <summary>Heading within which the trailer counts as square to the dock.</summary>
    private const double DockHeadingToleranceRadians = 2.0 * Math.PI / 180.0;

    /// <summary>
    /// Reverses until the last towed unit's axle reaches <paramref name="targetMetres"/>.
    /// </summary>
    /// <param name="chain">
    /// The fold the route has already established. Reversing is history-dependent, so a leg that
    /// started from a straight chain when the trailer was folded would be a different manoeuvre.
    /// </param>
    /// <returns>
    /// The leg, and the chain state it ends in. <see cref="ReverseLeg.ReachedTarget"/> is false when
    /// the leg ran out of travel or the vehicle could not turn tightly enough to close on the point.
    /// </returns>
    public static ReverseLeg AimTowedUnit(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        ArticulationChain chain,
        Point2 targetMetres,
        double maximumStepMetres = 0.10,
        double foldGainPerMetre = FoldGainPerMetre,
        bool wheelSetAtStandstill = false)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(chain);
        if (!vehicle.IsArticulated) throw new ArgumentException("The vehicle tows nothing.", nameof(vehicle));
        if (maximumStepMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(maximumStepMetres));

        var state = start;
        var working = chain.Clone();
        var samples = new List<RouteSample> { Seed(state, vehicle) };
        var travelled = 0.0;
        var reached = false;
        var lastRange = double.PositiveInfinity;
        var closestApproach = double.PositiveInfinity;

        while (travelled < MaximumTravelMetres)
        {
            var poses = working.Poses(state.RearAxleCentreMetres.XY, state.VehicleHeadingRadians);
            var aimed = poses[^1];
            var range = aimed.AxleCentreMetres.DistanceTo(targetMetres);
            closestApproach = Math.Min(closestApproach, range);
            if (range <= ArrivalToleranceMetres)
            {
                reached = true;
                break;
            }

            // The leg ends where it stops being drivable, rather than driving on into a shape no
            // vehicle holds.
            if (poses.Any(pose => Math.Abs(pose.ArticulationAngleRadians) >= MaximumSafeFoldRadians)) break;

            // Give up on a target the combination is circling rather than closing on, but only once
            // it has had room to establish the fold -- the first metres of any reverse open the range
            // while the trailer is still being lined up.
            if (travelled > 12.0 && range > lastRange + 1e-9) break;
            lastRange = range;

            var steering = SteeringFor(
                vehicle, mode, state, working, PursuitCurvature(aimed, targetMetres), foldGainPerMetre);
            var step = Math.Min(maximumStepMetres, Math.Max(range, ArrivalToleranceMetres));
            var advanced = RateLimitedTrajectoryGenerator.Advance(
                vehicle, mode, state, steering, step, wheelSetAtStandstill && samples.Count == 1);
            working.Advance(
                state.VehicleHeadingRadians,
                advanced.HeadingChangeRadians,
                step * (double)state.Direction);
            state = advanced.State;
            samples.Add(advanced.Sample);
            travelled += step;
        }

        return new ReverseLeg(
            new GeneratedTrajectory(state, samples),
            working,
            reached || closestApproach <= ClosestApproachToleranceMetres,
            closestApproach);
    }

    /// <summary>
    /// Whether <see cref="DockTowedUnit"/> can be trusted for this vehicle.
    /// </summary>
    /// <remarks>
    /// One joint only. A two-joint chain does not hold: the heading loop has to be slower than the
    /// folds it commands, and the folds cannot be driven fast enough to leave room for it — every
    /// pairing of the two gains measured on PVT either crawled, converged and then wandered off, or
    /// ran straight into the fold stop. It is the second joint that does it, not the drawbar it
    /// happens to hang from: rebuilding PVT with its dolly on a fifth wheel, and again with the hitch
    /// on the axle, changed nothing. Aiming at a point is stable for both, so a chain that cannot be
    /// docked is aimed instead, and the heading is reported as unavailable rather than missed.
    /// </remarks>
    public static bool CanDock(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return vehicle.TowedUnits.Count == 1;
    }

    /// <summary>
    /// Reverses until the last towed unit is at <paramref name="targetMetres"/> <em>and</em> square
    /// to <paramref name="exitMovementHeadingRadians"/> — backing a trailer onto a dock rather than
    /// merely near it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Position and heading cannot both be chased by aiming at a point: pure pursuit closes on the
    /// point from whatever direction it happens to be approached from, which is how a trailer ends up
    /// at the dock across the face of it. What is followed instead is the <em>approach line</em> —
    /// the line through the target along the heading asked for — and the target point becomes the
    /// place along that line to stop.
    /// </para>
    /// <para>
    /// Two errors describe the trailer against that line: how far off it sits, and how far off it
    /// points. For a unit reversing along its own axis those obey <c>de/ds = sin(psi)</c> and
    /// <c>dpsi/ds = curvature</c>, and running the arc length backwards flips the sign of both — which
    /// is why the forward law diverges here and the signs have to be derived rather than reused. The
    /// law that stabilises the pair going backwards is <c>k = -k1*e + k2*psi</c>, whose characteristic
    /// polynomial is <c>l^2 + k2*l + k1</c>; taking <c>k1 = k2^2/4</c> makes it critically damped, so
    /// the trailer settles onto the line rather than crossing and re-crossing it.
    /// </para>
    /// </remarks>
    /// <param name="exitMovementHeadingRadians">
    /// The direction of <em>travel</em> at the end, matching how an exit heading is meant everywhere
    /// else. Reversing, the trailer faces the opposite way, and the approach line runs out ahead of
    /// its nose.
    /// </param>
    public static ReverseLeg DockTowedUnit(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        ArticulationChain chain,
        Point2 targetMetres,
        double exitMovementHeadingRadians,
        double maximumStepMetres = 0.10,
        double foldGainPerMetre = FoldGainPerMetre,
        double headingGain = DockHeadingGain,
        bool wheelSetAtStandstill = false)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(chain);
        if (!CanDock(vehicle))
        {
            throw new ArgumentException(
                "Only a single-joint combination can be docked; check CanDock first.", nameof(vehicle));
        }

        if (maximumStepMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(maximumStepMetres));

        // The trailer faces back up the approach line, because it is reversing down it.
        var facing = Geometry2D.NormalizeAngle(exitMovementHeadingRadians + Math.PI);
        var tangent = new Point2(Math.Cos(facing), Math.Sin(facing));
        var normal = new Point2(-tangent.Y, tangent.X);
        var trailerWheelbase = vehicle.TowedUnits[^1].WheelbaseMetres;
        var curvatureLimit = Math.Tan(MaximumCommandedFoldRadians) / trailerWheelbase;

        var state = start;
        var working = chain.Clone();
        var samples = new List<RouteSample> { Seed(state, vehicle) };
        var travelled = 0.0;
        var reached = false;
        var closestApproach = double.PositiveInfinity;
        var headingError = double.NaN;

        while (travelled < MaximumTravelMetres)
        {
            var poses = working.Poses(state.RearAxleCentreMetres.XY, state.VehicleHeadingRadians);
            var aimed = poses[^1];
            var offset = aimed.AxleCentreMetres - targetMetres;
            var along = (offset.X * tangent.X) + (offset.Y * tangent.Y);
            var cross = (offset.X * normal.X) + (offset.Y * normal.Y);
            var facingError = Geometry2D.NormalizeAngle(aimed.HeadingRadians - facing);
            closestApproach = Math.Min(closestApproach, aimed.AxleCentreMetres.DistanceTo(targetMetres));
            headingError = facingError;

            // Run in until the trailer reaches the target's station on the line. Cross-track and
            // heading are what the approach was for; arriving is just running out of line.
            if (along <= 0.0)
            {
                reached = Math.Abs(cross) <= ClosestApproachToleranceMetres
                    && Math.Abs(facingError) <= DockHeadingToleranceRadians;
                break;
            }

            if (poses.Any(pose => Math.Abs(pose.ArticulationAngleRadians) >= MaximumSafeFoldRadians)) break;

            // Line-of-sight guidance. Cross-track error does not command a curvature directly --
            // that is only valid near the line, and eight metres off it asks for a radius no
            // combination can hold, which is what drove PVT into the fold stop within seven metres.
            // It commands an approach *angle* instead, bounded by construction, and the curvature
            // only ever closes the gap between the heading held and the heading wanted.
            // The cross-track error is traded for approach angle over a distance of 4 / gain: the
            // critically damped pairing, so the trailer settles onto the line instead of weaving
            // across it, and weaving is what makes a dock unusable even when it technically arrives.
            var approach = Math.Clamp(
                Math.Atan(cross * headingGain / 4.0),
                -MaximumApproachAngleRadians,
                MaximumApproachAngleRadians);
            var desired = Math.Clamp(
                headingGain * Geometry2D.NormalizeAngle(facingError - approach),
                -curvatureLimit,
                curvatureLimit);
            var steering = SteeringFor(vehicle, mode, state, working, desired, foldGainPerMetre);
            var step = Math.Min(maximumStepMetres, Math.Max(along, ArrivalToleranceMetres));
            var advanced = RateLimitedTrajectoryGenerator.Advance(
                vehicle, mode, state, steering, step, wheelSetAtStandstill && samples.Count == 1);
            working.Advance(
                state.VehicleHeadingRadians,
                advanced.HeadingChangeRadians,
                step * (double)state.Direction);
            state = advanced.State;
            samples.Add(advanced.Sample);
            travelled += step;
        }

        return new ReverseLeg(
            new GeneratedTrajectory(state, samples),
            working,
            reached,
            closestApproach)
        {
            HeadingErrorRadians = double.IsNaN(headingError) ? null : headingError
        };
    }

    /// <summary>
    /// Curvature of the circle that leaves the aimed unit's axle along its own axis and passes
    /// through the target. Measured per unit of travel along that axis, so it does not care which way
    /// the unit is going — which is the whole point, since it is going backwards.
    /// </summary>
    private static double PursuitCurvature(TowedUnitPose aimed, Point2 targetMetres)
    {
        var delta = targetMetres - aimed.AxleCentreMetres;
        var localY = (-delta.X * Math.Sin(aimed.HeadingRadians)) + (delta.Y * Math.Cos(aimed.HeadingRadians));
        var chordSquared = (delta.X * delta.X) + (delta.Y * delta.Y);
        return chordSquared < 1e-6 ? 0.0 : 2.0 * localY / chordSquared;
    }

    /// <summary>
    /// The wheel angle that gives the last unit the curvature asked of it, worked back through every
    /// joint between. This is the cascade, and it is all either aiming law needs from the chain.
    /// </summary>
    private static double SteeringFor(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState state,
        ArticulationChain chain,
        double desiredCurvature,
        double foldGainPerMetre)
    {
        var desired = desiredCurvature;
        var directionSign = (double)state.Direction;
        for (var index = vehicle.TowedUnits.Count - 1; index >= 0; index--)
        {
            var unit = vehicle.TowedUnits[index];
            var fold = chain.ArticulationAngleRadians(index, state.VehicleHeadingRadians);
            var commandedFold = Math.Clamp(
                Math.Atan(desired * unit.WheelbaseMetres),
                -MaximumCommandedFoldRadians,
                MaximumCommandedFoldRadians);

            // The joint's own rate equation, solved for the tower's curvature:
            //     dfold/dlambda = sign * [ k_tower * (1 - h*cos/L) - sin/L ]
            // The denominator is 1 - h*cos(fold)/L, which no preset can drive to zero: a hitch ahead
            // of the axle is a fraction of the wheelbase, and one behind it makes the term larger.
            // Every joint is driven at the same gain. The textbook cascade rule says to slow the
            // outer loops down, and it was tried: dividing the outer joint's gain by two made PVT
            // worse at every base gain measured, because the chain already separates its own loops
            // by wheelbase -- the dolly is 2.9 m and answers quickly, the trailer 5.8 m and does not.
            // Slowing the slow one further only left it unable to follow.
            var denominator = 1.0 - (unit.HitchOffsetMetres * Math.Cos(fold) / unit.WheelbaseMetres);
            desired = ((Math.Sin(fold) / unit.WheelbaseMetres)
                + (directionSign * foldGainPerMetre * (commandedFold - fold))) / denominator;
        }

        return Math.Clamp(
            Math.Atan(vehicle.WheelbaseMetres * desired),
            -mode.MaximumWheelAngleRadians,
            mode.MaximumWheelAngleRadians);
    }

    private static RouteSample Seed(VehicleState state, VehicleDefinition vehicle)
    {
        var pathHeading = Geometry2D.NormalizeAngle(
            state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
        var curvature = (double)state.Direction * Math.Tan(state.SteeringAngleRadians) / vehicle.WheelbaseMetres;
        return new RouteSample(state.StationMetres, state.RearAxleCentreMetres, pathHeading, curvature, state.Direction);
    }
}

/// <summary>A reversing leg, with the fold it leaves behind and whether it got where it was sent.</summary>
/// <param name="ClosestApproachMetres">
/// How near the aimed unit's axle came to the target. More use than the flag on its own: a leg that
/// stops half a metre short of an aggressive shift is a different thing from one that never closed.
/// </param>
public sealed record ReverseLeg(
    GeneratedTrajectory Trajectory,
    ArticulationChain EndChain,
    bool ReachedTarget,
    double ClosestApproachMetres)
{
    /// <summary>
    /// How far off square the towed unit finished, when a heading was asked for. Null when the leg
    /// was aimed at a point and no heading was requested.
    /// </summary>
    public double? HeadingErrorRadians { get; init; }

    public VehicleState EndState => Trajectory.EndState;

    public IReadOnlyList<RouteSample> Samples => Trajectory.Samples;
}
