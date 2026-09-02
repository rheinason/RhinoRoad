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
