namespace RhinoRoad.Core;

/// <summary>
/// Turns at the wheel's lock through a chosen amount of heading, and stops there still turning.
/// </summary>
/// <remarks>
/// <para>
/// The tightest turn a vehicle can make is the one check every swept-path standard asks for, and it
/// is the one thing aiming at a point cannot express. An aimed leg drives the arc <em>through</em>
/// the point, and that arc has to pay for its own lock-up out of its own length: at PV's minimum
/// radius the whole half-circle is 15.2 m and the wheel needs 12.5 m of it to reach lock, so the
/// leg finishes 102 degrees round instead of 180. More turn is then only available by clicking
/// further out, on a slacker radius — the model approaches a U-turn from below and never arrives.
/// </para>
/// <para>
/// So a locked turn is specified as an <em>amount of heading</em> instead of a place to reach. The
/// wheel goes to full lock and stays there until the movement heading has swung by the amount
/// asked, however long that takes. There is nothing to overshoot and nothing to converge on: the
/// radius is whatever the vehicle can hold, which is the answer the check wants.
/// </para>
/// <para>
/// A turn that begins from the opposite lock — the cusp of a three-point turn — spends its first
/// metres crossing the wheel through centre, and the vehicle goes on rotating the old way while it
/// does. That arc is part of the cost of the manoeuvre and is driven, not skipped: nothing here
/// turns the wheel while stationary, because these vehicles are not modelled as doing so.
/// </para>
/// <para>
/// The leg deliberately ends with the wheel <em>still at lock</em>. Straightening belongs to the
/// next control — <see cref="HeadingLegGenerator.EaseOntoHeading"/> already opens a corner out the
/// way a driver does — and unwinding here would widen exactly the turn that was asked to be tight.
/// </para>
/// </remarks>
public static class LockedTurnGenerator
{
    /// <summary>Below this there is no turn worth driving.</summary>
    private const double NegligibleSweepRadians = 1e-4;

    /// <summary>
    /// Guards a sweep the vehicle somehow cannot close out. A full circle at PV's minimum radius is
    /// 30 m, so this is far past any legitimate request.
    /// </summary>
    private const double MaximumTravelMetres = 2000.0;

    /// <summary>
    /// Drives at full lock until the direction of travel has swung by <paramref name="sweepRadians"/>.
    /// </summary>
    /// <param name="sweepRadians">
    /// Signed: positive swings the direction of travel anticlockwise, negative clockwise, and the
    /// magnitude is how much heading to gain. It is the <em>movement</em> heading that swings, so a
    /// reversing turn is specified the same way as a forward one — which is what makes a three-point
    /// turn three of these with the travel direction flipped between them.
    /// </param>
    public static GeneratedTrajectory Turn(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        double sweepRadians,
        double maximumStepMetres = 0.10,
        bool wheelSetAtStandstill = false)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        if (maximumStepMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(maximumStepMetres));

        var state = start;
        var samples = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };
        if (Math.Abs(sweepRadians) <= NegligibleSweepRadians) return new GeneratedTrajectory(state, samples);

        // Which way the wheel must go to swing the movement heading the way asked. Reversing turns
        // the vehicle the other way for the same wheel angle, so the travel direction belongs in the
        // sign — the same convention HeadingLegGenerator uses.
        var sign = Math.Sign(sweepRadians);
        var command = sign * (double)start.Direction * mode.MaximumWheelAngleRadians;

        // Progress is the *signed* heading gained, and it has to be, because a turn does not always
        // begin by turning the right way. Starting from the opposite lock — which is every leg of a
        // three-point turn, where the wheel must cross centre at the cusp — the vehicle carries on
        // rotating the old way until the wheel passes straight. Counting the size of each step's
        // rotation instead of its direction credits that wrong-way arc as progress: a PV asked to
        // swing 55 degrees on the reverse leg closed the count out having actually gone 3.6 degrees
        // the other way, and the manoeuvre finished nowhere near reversed.
        var swept = 0.0;

        while (state.StationMetres - start.StationMetres < MaximumTravelMetres)
        {
            var advanced = RateLimitedTrajectoryGenerator.Advance(
                vehicle, mode, state, command, maximumStepMetres, wheelSetAtStandstill && samples.Count == 1);
            var reached = swept + advanced.HeadingChangeRadians;
            if (sign * reached >= sign * sweepRadians)
            {
                // The sweep almost never closes on a step boundary, and stopping at the next one
                // overshoots by a whole step's worth of turn — most of a degree at full lock, which
                // is enough to make a 180 read as 181. The crossing step is bisected instead, so a
                // turn asked for in whole degrees lands on whole degrees.
                var shorter = ClosingStep(
                    vehicle, mode, state, command, maximumStepMetres, sweepRadians - swept, sign);
                if (shorter > 0.0)
                {
                    var closing = RateLimitedTrajectoryGenerator.Advance(vehicle, mode, state, command, shorter);
                    state = closing.State;
                    samples.Add(closing.Sample);
                }

                break;
            }

            swept = reached;
            state = advanced.State;
            samples.Add(advanced.Sample);
        }

        return new GeneratedTrajectory(state, samples);
    }

    /// <summary>
    /// The sweep a cursor asks for, read the same way an aimed leg reads one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <em>twice</em> the bearing of the cursor, because that is already what aiming at
    /// a point delivers — an inscribed-angle result — so the two controls agree about where you
    /// click for a given turn and the muscle memory carries over. Clicking abeam has always meant a
    /// U-turn; here it means one that is actually 180 degrees.
    /// </para>
    /// <para>
    /// Doubling is also what makes the whole circle reachable. A bearing alone spans half a turn and
    /// puts the U-turn on the seam where left and right change places, which is the one request that
    /// must not be ambiguous. Doubled, a bearing of 90 degrees is a clean 180, a cursor behind the
    /// vehicle asks for 270, and only dead astern — a full circle either way — is ambiguous.
    /// </para>
    /// <para>
    /// Range does not enter into it. How far the cursor sits from the vehicle changes nothing, which
    /// is the point: an aimed leg grows without bound as the cursor recedes, and a locked turn does
    /// not move at all.
    /// </para>
    /// </remarks>
    public static double SweepFromCursor(VehicleState state, Point2 cursorMetres)
    {
        var movementHeading = state.VehicleHeadingRadians +
            (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var offset = cursorMetres - state.RearAxleCentreMetres.XY;
        if (offset.Length <= Geometry2D.Epsilon) return 0.0;
        var bearing = Math.Atan2(offset.Y, offset.X);
        return 2.0 * Geometry2D.NormalizeAngle(bearing - movementHeading);
    }

    /// <summary>
    /// A cursor position that asks for <paramref name="sweepRadians"/> exactly.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="SweepFromCursor"/>, so that a sweep typed on the command line is
    /// stored as an ordinary control point and replays through the same path as a clicked one. A
    /// typed turn and a clicked turn are then the same kind of thing, and the saved journey needs no
    /// field to tell them apart.
    /// </remarks>
    public static Point3 CursorForSweep(VehicleState state, double sweepRadians, double rangeMetres)
    {
        var movementHeading = state.VehicleHeadingRadians +
            (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var bearing = movementHeading + (sweepRadians * 0.5);
        return new Point3(
            state.RearAxleCentreMetres.X + (Math.Cos(bearing) * rangeMetres),
            state.RearAxleCentreMetres.Y + (Math.Sin(bearing) * rangeMetres),
            state.RearAxleCentreMetres.Z);
    }

    /// <summary>The step length that closes the last of the sweep exactly.</summary>
    private static double ClosingStep(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState state,
        double command,
        double fullStepMetres,
        double remainingRadians,
        int sign)
    {
        var low = 0.0;
        var high = fullStepMetres;
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var middle = (low + high) * 0.5;
            var trial = RateLimitedTrajectoryGenerator.Advance(vehicle, mode, state, command, middle);
            if (sign * trial.HeadingChangeRadians < sign * remainingRadians) low = middle;
            else high = middle;
        }

        return low;
    }
}
