namespace RhinoRoad.Core;

/// <summary>How far a reversed docking body -- the trailer, or a rigid vehicle -- stopped from its bay.</summary>
public sealed record BayArrival(double PositionErrorMetres, double HeadingErrorDegrees)
{
    /// <summary>Within the tolerance the reverse planner and the area search accept as arrived.</summary>
    public bool Arrived =>
        PositionErrorMetres <= ReversePlanner.ArrivalToleranceMetres &&
        HeadingErrorDegrees <= ReversePlanner.ArrivalToleranceDegrees;

    /// <summary>
    /// Measures the docking body against the final control, or returns null when the journey has no
    /// bay to measure: a last control that is not an aimed reverse with a direction (see
    /// <see cref="ReverseBay.FinalBay"/>). The palette once measured a synthesised SVT on whatever road
    /// was open and printed an infinite heading error for a car; a car reversing into a bay is now
    /// measured by its own rear axle, through the same <see cref="ReverseBay.Miss"/> the planner uses.
    /// </summary>
    public static BayArrival? Measure(VehicleDefinition vehicle, ManoeuvreDefinition journey, ReplayedManoeuvre replay)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(journey);
        ArgumentNullException.ThrowIfNull(replay);
        if (ReverseBay.FinalBay(journey) is not { } final) return null;
        var miss = ReverseBay.Miss(vehicle, replay.Samples, replay.EndState, final.PositionMetres.XY,
            final.ExitHeadingRadians);
        return new BayArrival(miss.PositionMetres, miss.HeadingRadians!.Value * 180.0 / Math.PI);
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
        return new ClearAreaReadout(
            AccessFootprint.IntersectionArea(footprint, yard),
            AccessFootprint.Outside(footprint, new[] { yard }) is not null,
            obstacles.Any(polygon => AccessFootprint.ConflictPoint(footprint, polygon, true) is not null),
            BayArrival.Measure(vehicle, preview.Definition, preview.Journey),
            // Every joint against its own unit's limit, as the search and the planner judge it.
            FoldLimit.IsReached(vehicle, preview.Journey.Samples),
            preview.Journey.RequestedAngleExceeded);
    }
}
