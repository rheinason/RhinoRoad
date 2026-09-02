namespace RhinoRoad.Core;

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
    private const double CrossTrackGain = 0.5;

    public static FollowedRoute Follow(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IntentPath intent,
        TravelDirection direction = TravelDirection.Forward)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(intent);

        var startHeading = intent.Headings[0] + (direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var state = new VehicleState(intent.Points[0], startHeading, 0.0, direction, 0.0);
        var samples = new List<RouteSample> { Seed(intent, direction) };

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
            index = intent.NearestIndex(state.RearAxleCentreMetres.XY, index);
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

    private static RouteSample Seed(IntentPath intent, TravelDirection direction) => new(
        0.0,
        intent.Points[0],
        Geometry2D.NormalizeAngle(intent.Headings[0]),
        0.0,
        direction);
}
