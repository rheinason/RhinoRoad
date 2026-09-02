namespace RhinoRoad.Core;

/// <summary>One leg of a manoeuvre: a line, and the direction it is driven in.</summary>
/// <remarks>
/// A three-point turn is three legs. Splitting at the cusp rather than trying to express a reversal
/// inside one line keeps each leg an ordinary curve, which is what makes the whole thing editable.
/// </remarks>
public sealed record RouteLeg(IntentPath Intent, TravelDirection Direction);

/// <summary>What a vehicle made of an intended line, and how far it had to stray from it.</summary>
public sealed record FollowedRoute(
    IReadOnlyList<RouteSample> Samples,
    double MaximumDeviationMetres,
    double RootMeanSquareDeviationMetres,
    double DeviationAtEndMetres)
{
    /// <summary>Where the vehicle was furthest from the line it was asked to follow.</summary>
    public double MaximumDeviationStationMetres { get; init; }
}

/// <summary>
/// Drives a vehicle along an intended line under the mode's wheel lock and steering rate.
/// </summary>
/// <remarks>
/// <para>
/// The vehicle follows the line rather than being placed on it. That distinction is the whole
/// point: a drawn centreline is a wish, and a rigid sample of it reports a vehicle doing things no
/// vehicle can do — turning a corner with no radius, reaching full lock instantly. Following it
/// produces a route the vehicle can actually drive, and a deviation that says how much the wish
/// cost. On a narrow stretch that deviation is usually the answer being looked for.
/// </para>
/// <para>
/// The command has three parts. Feed-forward sets the wheel where the line's curvature says it
/// belongs, a short distance ahead, so the wheel is already slewing before the bend arrives — it
/// needs metres of travel to get anywhere. Heading and cross-track feedback then correct what is
/// left. Feed-forward does nearly all the work on a line the vehicle can drive; the feedback
/// matters where it cannot, and there it saturates honestly against the lock.
/// </para>
/// </remarks>
public static class PathFollower
{
    /// <summary>Integration step, and the sample spacing of the route produced.</summary>
    public const double StepMetres = 0.05;

    // How far ahead the curvature is read. The wheel cannot be where the bend needs it without
    // starting to move before the bend; two metres is enough to lead it without cutting the corner.
    private const double PreviewMetres = 2.0;

    // Feedback gains. Deliberately gentle: they exist to remove residual error, not to fight the
    // feed-forward, and a stiff cross-track term makes the vehicle weave on a line it is already
    // following. Measured across the vehicle presets rather than derived.
    private const double HeadingGain = 1.0;

    // How far the vehicle's place on the line may move in one step. Ten steps' worth, so cutting a
    // corner cannot outrun it, and short enough that it can never skip to another part of the line.
    private const double NearestAdvanceMetres = StepMetres * 10.0;
    private const double CrossTrackGain = 0.5;

    /// <summary>
    /// Drives a manoeuvre of one or more legs, reversing at each cusp between them.
    /// </summary>
    /// <remarks>
    /// The vehicle carries its pose and its wheel across a cusp rather than being replanted on the
    /// next line: that is what a cusp is — the vehicle stops and goes the other way from exactly
    /// where it stopped. So a later leg is followed from where the vehicle actually is, and if that
    /// is not where the leg was drawn, the deviation says so instead of the gap being papered over.
    /// </remarks>
    public static FollowedRoute FollowLegs(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<RouteLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);
        if (legs.Count == 0) throw new ArgumentException("A manoeuvre needs at least one leg.", nameof(legs));

        var samples = new List<RouteSample>();
        VehicleState? carried = null;
        var maximumDeviation = 0.0;
        var maximumDeviationStation = 0.0;
        var sumOfSquares = 0.0;
        var measurements = 0;
        var endGap = 0.0;

        foreach (var leg in legs)
        {
            var followed = Follow(vehicle, mode, leg.Intent, leg.Direction, carried);

            // The seed sample of a later leg is the cusp itself, already the last sample of the
            // previous one, so it is dropped rather than repeated.
            samples.AddRange(carried is null ? followed.Samples : followed.Samples.Skip(1));
            if (followed.MaximumDeviationMetres > maximumDeviation)
            {
                maximumDeviation = followed.MaximumDeviationMetres;
                maximumDeviationStation = followed.MaximumDeviationStationMetres;
            }

            var count = Math.Max(1, followed.Samples.Count);
            sumOfSquares += followed.RootMeanSquareDeviationMetres * followed.RootMeanSquareDeviationMetres * count;
            measurements += count;
            endGap = followed.DeviationAtEndMetres;

            var last = samples[^1];
            carried = new VehicleState(
                last.PositionMetres,
                Geometry2D.NormalizeAngle(
                    last.PathHeadingRadians + (leg.Direction == TravelDirection.Reverse ? Math.PI : 0.0)),
                Math.Atan(last.SignedCurvaturePerMetre * vehicle.WheelbaseMetres * (double)leg.Direction),
                leg.Direction,
                last.StationMetres);
        }

        return new FollowedRoute(
            samples,
            maximumDeviation,
            Math.Sqrt(sumOfSquares / Math.Max(1, measurements)),
            endGap)
        {
            MaximumDeviationStationMetres = maximumDeviationStation
        };
    }

    public static FollowedRoute Follow(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IntentPath intent,
        TravelDirection direction = TravelDirection.Forward,
        VehicleState? startState = null)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(intent);

        var startHeading = intent.Headings[0] + (direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var state = startState is null
            ? new VehicleState(intent.Points[0], startHeading, 0.0, direction, 0.0)
            : startState with { Direction = direction };
        var samples = new List<RouteSample> { startState is null ? Seed(intent, direction) : Cusp(state, vehicle) };

        var maximumDeviation = 0.0;
        var maximumDeviationStation = 0.0;
        var sumOfSquares = 0.0;
        var measurements = 0;
        var index = 0;

        // The vehicle is driven at most a little further than the line is long. A line it cannot
        // follow leaves it lagging, not looping, so there is no case where finishing needs more.
        var travelLimit = (intent.LengthMetres * 1.5) + 20.0;

        while (true)
        {
            index = intent.NearestIndex(state.RearAxleCentreMetres.XY, index, NearestAdvanceMetres);
            var deviation = intent.Points[index].XY.DistanceTo(state.RearAxleCentreMetres.XY);
            if (deviation > maximumDeviation)
            {
                maximumDeviation = deviation;
                maximumDeviationStation = state.StationMetres;
            }

            sumOfSquares += deviation * deviation;
            measurements++;

            if (index >= intent.Count - 1) break;
            if (state.StationMetres > travelLimit) break;

            var lineHeading = intent.Headings[index];
            var offset = state.RearAxleCentreMetres.XY - intent.Points[index].XY;
            var crossTrack = (-offset.X * Math.Sin(lineHeading)) + (offset.Y * Math.Cos(lineHeading));

            // Compared against the direction of travel, not the direction the vehicle faces: when
            // reversing along a line those differ by half a turn, and the error being corrected is
            // between the line and where the rear axle is actually going.
            var movementHeading = state.VehicleHeadingRadians +
                (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
            var headingError = Geometry2D.NormalizeAngle(lineHeading - movementHeading);
            var preview = intent.IndexAt(intent.Stations[index] + PreviewMetres);
            var feedForward = Math.Atan(vehicle.WheelbaseMetres * intent.Curvatures[preview]);

            // Every term is a correction to the rear axle's path. Reversing inverts the wheel that
            // achieves it — the same reason backing a trailer feels inverted — so the whole command
            // turns with the direction of travel.
            var command = (feedForward
                + (HeadingGain * headingError)
                - Math.Atan(CrossTrackGain * crossTrack)) * (double)state.Direction;

            var advanced = RateLimitedTrajectoryGenerator.Advance(vehicle, mode, state, command, StepMetres);
            state = advanced.State;
            samples.Add(advanced.Sample);
        }

        return new FollowedRoute(
            samples,
            maximumDeviation,
            Math.Sqrt(sumOfSquares / Math.Max(1, measurements)),
            intent.Points[^1].XY.DistanceTo(state.RearAxleCentreMetres.XY))
        {
            MaximumDeviationStationMetres = maximumDeviationStation
        };
    }

    /// <summary>The sample at a cusp: the vehicle where it stopped, now facing the other way.</summary>
    /// <remarks>
    /// The wheel is left where it was, so the curvature flips sign with the direction while the
    /// steering angle does not. That is the same wheel position, described from the other end, and
    /// it keeps the steering-rate check from seeing a jump that never happened.
    /// </remarks>
    private static RouteSample Cusp(VehicleState state, VehicleDefinition vehicle) => new(
        state.StationMetres,
        state.RearAxleCentreMetres,
        Geometry2D.NormalizeAngle(
            state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0)),
        (double)state.Direction * Math.Tan(state.SteeringAngleRadians) / vehicle.WheelbaseMetres,
        state.Direction);

    private static RouteSample Seed(IntentPath intent, TravelDirection direction) => new(
        0.0,
        intent.Points[0],
        Geometry2D.NormalizeAngle(intent.Headings[0]),
        0.0,
        direction);
}
