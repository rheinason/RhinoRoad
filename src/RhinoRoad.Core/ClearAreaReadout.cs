namespace RhinoRoad.Core;

/// <summary>How far a reversed trailer stopped from its bay.</summary>
public sealed record BayArrival(double PositionErrorMetres, double HeadingErrorDegrees)
{
    /// <summary>Within the tolerance the reverse planner and the area search accept as arrived.</summary>
    public bool Arrived =>
        PositionErrorMetres <= TrailerReversePlanner.ArrivalToleranceMetres &&
        HeadingErrorDegrees <= TrailerReversePlanner.ArrivalToleranceDegrees;

    /// <summary>
    /// Measures the last towed unit against the final control, or returns null when the journey
    /// has no bay to measure: a rigid vehicle, or a last control that is not a reverse with a
    /// direction. The palette once measured a synthesised SVT on whatever road was open and printed
    /// an infinite heading error for a car; a bay is only defined where the trailer reverses into one.
    /// </summary>
    public static BayArrival? Measure(VehicleDefinition vehicle, ManoeuvreDefinition journey, ReplayedManoeuvre replay)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(journey);
        ArgumentNullException.ThrowIfNull(replay);
        if (!vehicle.IsArticulated || journey.Controls.Count == 0 ||
            journey.Controls[^1] is not { Direction: TravelDirection.Reverse, ExitHeadingRadians: double heading } final ||
            ArticulationTrace.AtEndOf(vehicle, replay.Samples) is not { } chain)
            return null;
        var end = replay.EndState;
        var trailer = chain.Poses(end.RearAxleCentreMetres.XY, end.VehicleHeadingRadians)[^1];
        // The bay heading is the way the trailer travels in, so its body faces the other way.
        var headingError = Math.Abs(Geometry2D.NormalizeAngle(trailer.HeadingRadians - heading - Math.PI)) * 180.0 / Math.PI;
        return new BayArrival(trailer.AxleCentreMetres.DistanceTo(final.PositionMetres.XY), headingError);
    }
}

/// <summary>
/// The live line under the clear-area search: how much of the yard the dragged route clears, and
/// whether it would pass the search's own tests. Pure, so the palette can measure it off the UI
/// thread once per preview rather than on a timer.
/// </summary>
public sealed record ClearAreaReadout(
    double ClearAreaSquareMetres,
    bool OutsideYard,
    bool ObstacleConflict,
    BayArrival? Bay,
    bool FoldLimitReached,
    bool SteeringLimitReached)
{
    /// <summary>The fold the area search and the reverse planner reject a route at.</summary>
    public const double FoldLimitDegrees = 65.0;

    public bool Passes =>
        !OutsideYard && !ObstacleConflict && !FoldLimitReached && !SteeringLimitReached && (Bay?.Arrived ?? true);

    public static ClearAreaReadout Measure(
        VehicleDefinition vehicle,
        JourneyEditPreviewResult preview,
        IReadOnlyList<Point2> yard,
        IReadOnlyList<IReadOnlyList<Point2>> obstacles)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(yard);
        ArgumentNullException.ThrowIfNull(obstacles);
        if (!preview.Clearance.IsSuccess)
            throw new ArgumentException("The preview has no clearance region to measure.", nameof(preview));

        var footprint = preview.Clearance.Region!;
        var foldLimit = FoldLimitDegrees * Math.PI / 180.0;
        // Every joint counts: a fold anywhere in the chain is the jackknife the search rejects.
        var foldExceeded = vehicle.IsArticulated && ArticulationTrace.Follow(vehicle, preview.Journey.Samples)
            .Any(step => step.Chain is { } chain && Enumerable.Range(0, chain.Count)
                .Any(joint => Math.Abs(chain.ArticulationAngleRadians(joint, step.VehicleHeadingRadians)) >= foldLimit));
        return new ClearAreaReadout(
            AccessFootprint.IntersectionArea(footprint, yard),
            AccessFootprint.Outside(footprint, new[] { yard }) is not null,
            obstacles.Any(polygon => AccessFootprint.ConflictPoint(footprint, polygon, true) is not null),
            BayArrival.Measure(vehicle, preview.Definition, preview.Journey),
            foldExceeded,
            preview.Journey.RequestedAngleExceeded);
    }
}
