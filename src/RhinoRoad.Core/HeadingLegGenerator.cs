namespace RhinoRoad.Core;

/// <summary>
/// Drives the vehicle until it is travelling in a chosen direction with the wheel centred.
/// </summary>
/// <remarks>
/// <para>
/// The manoeuvre a driver actually makes leaving a corner: hold the lock until there is exactly
/// enough turn left in the wheel, then unwind, arriving straight and pointing where you meant. What
/// makes it solvable is that the heading still to come from unwinding is not a guess —
/// </para>
/// <code>
///     Δψ = direction · (speed / (wheelbase · slewRate)) · −ln(cos δ)
/// </code>
/// <para>
/// — the exact heading change produced by running the wheel from its current angle δ back to centre
/// at the mode's slew rate. Comparing that against the turn still required says, at every step,
/// whether to keep turning or start unwinding. Overshoot is impossible by construction: the vehicle
/// stops turning in at the last moment it still can and arrives on the chosen heading.
/// </para>
/// <para>
/// It also covers the case where the wheel is already turned too far — where unwinding now would
/// carry the vehicle past the chosen direction. There the same comparison calls for counter-steer,
/// which is what a driver does, and the leg still ends straight and on the heading asked for.
/// </para>
/// </remarks>
public static class HeadingLegGenerator
{
    /// <summary>How close to the chosen heading, and to a centred wheel, counts as arrived.</summary>
    private const double ToleranceRadians = 1e-4;

    /// <summary>Guards against a target the vehicle somehow cannot settle onto.</summary>
    private const double MaximumTravelMetres = 2000.0;

    /// <summary>
    /// Generates the leg that ends with the vehicle travelling along
    /// <paramref name="targetMovementHeadingRadians"/>, wheel centred.
    /// </summary>
    /// <param name="targetMovementHeadingRadians">
    /// The direction of travel wanted at the end, not the direction the vehicle faces. Reversing
    /// makes those differ by half a turn, and it is the direction of travel that is being chosen.
    /// </param>
    public static GeneratedTrajectory ToHeading(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        double targetMovementHeadingRadians,
        double maximumStepMetres = 0.10)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        if (maximumStepMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(maximumStepMetres));

        var state = start;
        var samples = new List<RouteSample> { Seed(start, vehicle) };

        // Two phases, and the switch between them is one-way. Turning in, the wheel is held at the
        // lock that closes the gap; once unwinding would land on the heading, the wheel runs back to
        // centre and the decision is not revisited. Re-deciding every step instead leaves the
        // command flipping either side of the answer and the wheel never settles.
        var unwinding = Math.Abs(Shortfall(vehicle, mode, state, targetMovementHeadingRadians))
            <= ToleranceRadians;

        while (state.StationMetres - start.StationMetres < MaximumTravelMetres)
        {
            if (unwinding)
            {
                if (Math.Abs(state.SteeringAngleRadians) <= ToleranceRadians) break;
                var unwound = RateLimitedTrajectoryGenerator.Advance(vehicle, mode, state, 0.0, maximumStepMetres);
                state = unwound.State;
                samples.Add(unwound.Sample);
                continue;
            }

            var shortfall = Shortfall(vehicle, mode, state, targetMovementHeadingRadians);
            var command = Math.Sign(shortfall) * (double)state.Direction * mode.MaximumWheelAngleRadians;
            var advanced = RateLimitedTrajectoryGenerator.Advance(vehicle, mode, state, command, maximumStepMetres);

            // The moment to stop turning in almost never falls on a step boundary, and stopping at
            // the next one instead overshoots by a whole step's worth of turn — most of a degree at
            // full lock. Where the step crosses that moment, its length is bisected so the turn ends
            // exactly on it. The motion is still the same rate-limited model, just measured finely.
            if (Math.Sign(Shortfall(vehicle, mode, advanced.State, targetMovementHeadingRadians))
                != Math.Sign(shortfall))
            {
                var shorter = CrossingStep(
                    vehicle, mode, state, command, maximumStepMetres, targetMovementHeadingRadians, shortfall);
                if (shorter > 0.0)
                {
                    advanced = RateLimitedTrajectoryGenerator.Advance(vehicle, mode, state, command, shorter);
                    state = advanced.State;
                    samples.Add(advanced.Sample);
                }

                unwinding = true;
                continue;
            }

            state = advanced.State;
            samples.Add(advanced.Sample);
        }

        return new GeneratedTrajectory(state, samples);
    }

    /// <summary>
    /// Where along a driven route the wheel should have started coming back, and the leg that does it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A driver eases out of a corner from partway through it, not from the end of it. By the time
    /// a leg has been committed the vehicle has usually driven past the point where the ease-out
    /// should have begun, and from there the only ways onto the chosen exit are to turn in further
    /// or to counter-steer — both of which look and feel wrong, because neither is what a driver
    /// does.
    /// </para>
    /// <para>
    /// So the route is rewound instead. Unwinding from a point early in the corner finishes on a
    /// shallower heading than unwinding from a point late in it, and that relationship is monotonic,
    /// so there is exactly one station whose unwind lands on the chosen direction. Everything after
    /// it is discarded and replaced by the ease-out. The result is one continuous turn that opens
    /// out into the exit, with no correction anywhere in it.
    /// </para>
    /// <para>
    /// Where no station works the route is left alone and the leg is generated from its end: the
    /// exit wanted needs more turn than the corner has produced, or less than it has already
    /// committed to, and then holding on or counter-steering really is the answer.
    /// </para>
    /// </remarks>
    /// <param name="earliestIndex">
    /// How far back the ease-out may reach. The caller holds this at the last direction change,
    /// because rewinding through a cusp would silently undo a reversing leg.
    /// </param>
    public static (int FromIndex, GeneratedTrajectory Leg) EaseOntoHeading(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<RouteSample> route,
        int earliestIndex,
        double targetMovementHeadingRadians,
        double maximumStepMetres = 0.10)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Count == 0) throw new ArgumentException("A route is required.", nameof(route));

        var last = route.Count - 1;
        var first = Math.Clamp(earliestIndex, 0, last);

        // Where unwinding from each sample would finish, as a heading that keeps counting past half
        // a turn instead of wrapping. Wrapping would put spurious sign changes into the search, and
        // a corner that turns far enough to wrap is exactly where the exit matters most.
        var finish = new double[route.Count];
        var running = MovementHeading(route[0]);
        finish[0] = running + HeadingChangeFromUnwinding(vehicle, mode, StateAt(route[0], vehicle));
        for (var index = 1; index < route.Count; index++)
        {
            running += Geometry2D.NormalizeAngle(MovementHeading(route[index]) - MovementHeading(route[index - 1]));
            finish[index] = running + HeadingChangeFromUnwinding(vehicle, mode, StateAt(route[index], vehicle));
        }

        // The chosen direction is a compass bearing, so it names a whole family of headings a turn
        // apart. The one meant is the one nearest what the corner is already about to finish on.
        var target = Geometry2D.NormalizeAngle(targetMovementHeadingRadians);
        target += 2.0 * Math.PI * Math.Round((finish[last] - target) / (2.0 * Math.PI));

        // Walking back from the end takes the latest crossing, which gives back as little of the
        // corner as will do. Earlier ones are the same bearing reached on a previous lap.
        var sign = Math.Sign(finish[last] - target);
        if (sign != 0)
        {
            for (var index = last - 1; index >= first; index--)
            {
                if (Math.Sign(finish[index] - target) == sign) continue;
                return (index, ToHeading(vehicle, mode, StateAt(route[index], vehicle), target, maximumStepMetres));
            }
        }

        return (last, ToHeading(vehicle, mode, StateAt(route[last], vehicle), target, maximumStepMetres));
    }

    private static double MovementHeading(RouteSample sample) => sample.PathHeadingRadians;

    /// <summary>
    /// The vehicle state a route sample was taken at.
    /// </summary>
    /// <remarks>
    /// The wheel angle is recoverable because curvature carries it: a sample records where the
    /// vehicle was and how tightly it was turning, and the wheelbase turns the second into the
    /// first. Rewinding a route to a sample therefore restores the vehicle exactly, wheel included.
    /// </remarks>
    public static VehicleState StateAt(RouteSample sample, VehicleDefinition vehicle) => new(
        sample.PositionMetres,
        Geometry2D.NormalizeAngle(
            sample.PathHeadingRadians + (sample.Direction == TravelDirection.Reverse ? Math.PI : 0.0)),
        Math.Atan(sample.SignedCurvaturePerMetre * vehicle.WheelbaseMetres * (double)sample.Direction),
        sample.Direction,
        sample.StationMetres);

    /// <summary>
    /// How much more the vehicle must turn than unwinding from here would give it.
    /// </summary>
    /// <remarks>
    /// Positive means keep turning left, negative means keep turning right, and zero is the moment
    /// to let the wheel run back — the whole manoeuvre is the search for that zero.
    /// </remarks>
    private static double Shortfall(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState state,
        double targetMovementHeadingRadians)
    {
        var movementHeading = state.VehicleHeadingRadians +
            (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var remaining = Geometry2D.NormalizeAngle(targetMovementHeadingRadians - movementHeading);
        return remaining - HeadingChangeFromUnwinding(vehicle, mode, state);
    }

    /// <summary>The step length that lands on the moment to stop turning in.</summary>
    private static double CrossingStep(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState state,
        double command,
        double fullStepMetres,
        double targetMovementHeadingRadians,
        double shortfallNow)
    {
        var low = 0.0;
        var high = fullStepMetres;
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var middle = (low + high) * 0.5;
            var trial = RateLimitedTrajectoryGenerator.Advance(vehicle, mode, state, command, middle);
            if (Math.Sign(Shortfall(vehicle, mode, trial.State, targetMovementHeadingRadians))
                == Math.Sign(shortfallNow))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// The heading change still to come if the wheel is run back to centre from here.
    /// </summary>
    /// <remarks>
    /// Closed form, from integrating the bicycle model while the wheel slews at a constant rate:
    /// the vehicle keeps turning all the way through the unwind, and this is how much of the turn
    /// is already committed to. Everything else in the manoeuvre is a comparison against it.
    /// </remarks>
    public static double HeadingChangeFromUnwinding(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState state)
    {
        var steering = state.SteeringAngleRadians;
        if (Math.Abs(steering) <= Geometry2D.Epsilon) return 0.0;

        var metresPerRadianOfWheel = mode.SpeedMetresPerSecond / mode.MaximumSteeringRateRadiansPerSecond;
        var magnitude = -Math.Log(Math.Cos(Math.Abs(steering)));
        return (double)state.Direction * Math.Sign(steering) * metresPerRadianOfWheel * magnitude
            / vehicle.WheelbaseMetres;
    }

    private static RouteSample Seed(VehicleState state, VehicleDefinition vehicle) => new(
        state.StationMetres,
        state.RearAxleCentreMetres,
        Geometry2D.NormalizeAngle(
            state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0)),
        (double)state.Direction * Math.Tan(state.SteeringAngleRadians) / vehicle.WheelbaseMetres,
        state.Direction);
}
