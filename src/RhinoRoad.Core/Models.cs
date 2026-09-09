namespace RhinoRoad.Core;

public enum TravelDirection
{
    Forward = 1,
    Reverse = -1
}

public enum ValidationStatus
{
    SourceTranscribed,
    ReferenceValidated,
    ValidationFailed
}

public sealed record VehicleSource(
    string Title,
    string PublicationDate,
    string Url,
    string Figure,
    string Notes);

public sealed record DrivingModeDefinition(
    string Id,
    string Name,
    double SpeedKilometresPerHour,
    double MaximumWheelAngleDegrees,
    double LockToLockSeconds,
    double DefaultClearanceMetres)
{
    public double SpeedMetresPerSecond => SpeedKilometresPerHour / 3.6;
    public double MaximumWheelAngleRadians => MaximumWheelAngleDegrees * Math.PI / 180.0;
    public double MaximumSteeringRateRadiansPerSecond => (2.0 * MaximumWheelAngleRadians) / LockToLockSeconds;
}

/// <summary>
/// One towed unit in an articulation chain: a semitrailer, a drawbar dolly, a trailer body.
/// </summary>
/// <param name="HitchOffsetMetres">
/// Where this unit is coupled, measured along the towing unit from its axle reference. Positive is
/// ahead of that axle — a fifth wheel sitting over the drive axle — and negative is behind it, which
/// is where a drawbar eye hangs. The sign matters: a hitch ahead of the axle makes the towed unit
/// cut in less, one behind it makes the tail swing out, and the two are not interchangeable.
/// </param>
/// <param name="WheelbaseMetres">Hitch to this unit's own axle reference.</param>
/// <param name="BodyOutline">
/// Body corners in this unit's frame — its axle at the origin, heading +X. A running gear with no
/// body of its own, such as a drawbar dolly, carries an empty outline and sweeps nothing.
/// </param>
public sealed record TowedUnitDefinition(
    string Id,
    string Name,
    double HitchOffsetMetres,
    double WheelbaseMetres,
    double WidthMetres,
    double AxleTrackMetres,
    double TyreWidthMetres,
    IReadOnlyList<Point2> BodyOutline)
{
    public double RearWheelInnerEdgeOffsetMetres => (AxleTrackMetres - TyreWidthMetres) * 0.5;
    public double WheelOuterEdgeOffsetMetres => (AxleTrackMetres + TyreWidthMetres) * 0.5;

    public void Validate(string vehicleId)
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException($"{vehicleId}: a towed unit is missing its id.");
        if (WheelbaseMetres <= 0.0) throw new InvalidDataException($"{vehicleId}/{Id}: wheelbase must be positive.");
        if (WidthMetres <= 0.0) throw new InvalidDataException($"{vehicleId}/{Id}: width must be positive.");
        if (AxleTrackMetres <= 0.0 || AxleTrackMetres >= WidthMetres)
            throw new InvalidDataException($"{vehicleId}/{Id}: axle track must be positive and narrower than the body.");
        if (TyreWidthMetres <= 0.0 || TyreWidthMetres >= AxleTrackMetres)
            throw new InvalidDataException($"{vehicleId}/{Id}: tyre width must be positive and narrower than the axle track.");
        if (BodyOutline.Count is not 0 and < 3)
            throw new InvalidDataException($"{vehicleId}/{Id}: a body outline needs at least three points, or none at all.");
    }
}

public sealed record VehicleDefinition(
    string Id,
    string Name,
    string Version,
    double WidthMetres,
    double WheelbaseMetres,
    double FrontOverhangMetres,
    double RearOverhangMetres,
    double AxleTrackMetres,
    double TyreWidthMetres,
    IReadOnlyList<Point2> BodyOutline,
    IReadOnlyDictionary<string, DrivingModeDefinition> DrivingModes,
    VehicleSource Source,
    ValidationStatus ValidationStatus,
    string ValidationNotes)
{
    /// <summary>
    /// What this vehicle tows, nearest first. Empty for a rigid vehicle, one entry for a
    /// semitrailer, two for a drawbar combination whose dolly and body pivot separately.
    /// </summary>
    public IReadOnlyList<TowedUnitDefinition> TowedUnits { get; init; } = [];

    public bool IsArticulated => TowedUnits.Count > 0;

    public double RearWheelInnerEdgeOffsetMetres => (AxleTrackMetres - TyreWidthMetres) * 0.5;
    public double WheelOuterEdgeOffsetMetres => (AxleTrackMetres + TyreWidthMetres) * 0.5;

    /// <summary>
    /// Where each unit's axle sits along the combination when it stands straight, measured from the
    /// lead rear axle. The lead unit is always at zero; each towed unit hangs off its hitch.
    /// </summary>
    public IReadOnlyList<double> StraightAxleOffsetsMetres
    {
        get
        {
            var offsets = new double[TowedUnits.Count + 1];
            for (var index = 0; index < TowedUnits.Count; index++)
            {
                offsets[index + 1] = offsets[index] + TowedUnits[index].HitchOffsetMetres - TowedUnits[index].WheelbaseMetres;
            }

            return offsets;
        }
    }

    /// <summary>Bumper to tail with the combination straight — the length the source dimensions.</summary>
    public double OverallLengthMetres
    {
        get
        {
            var offsets = StraightAxleOffsetsMetres;
            var minimum = BodyOutline.Count == 0 ? 0.0 : BodyOutline.Min(point => point.X);
            var maximum = BodyOutline.Count == 0 ? 0.0 : BodyOutline.Max(point => point.X);
            for (var index = 0; index < TowedUnits.Count; index++)
            {
                var outline = TowedUnits[index].BodyOutline;
                if (outline.Count == 0) continue;
                minimum = Math.Min(minimum, offsets[index + 1] + outline.Min(point => point.X));
                maximum = Math.Max(maximum, offsets[index + 1] + outline.Max(point => point.X));
            }

            return maximum - minimum;
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException("Vehicle id is required.");
        if (WheelbaseMetres <= 0.0) throw new InvalidDataException($"{Id}: wheelbase must be positive.");
        if (WidthMetres <= 0.0) throw new InvalidDataException($"{Id}: width must be positive.");
        if (AxleTrackMetres <= 0.0 || AxleTrackMetres >= WidthMetres)
            throw new InvalidDataException($"{Id}: axle track must be positive and narrower than the body.");
        if (TyreWidthMetres <= 0.0 || TyreWidthMetres >= AxleTrackMetres)
            throw new InvalidDataException($"{Id}: tyre width must be positive and narrower than the axle track.");
        if (BodyOutline.Count < 3) throw new InvalidDataException($"{Id}: body outline needs at least three points.");
        if (DrivingModes.Count == 0) throw new InvalidDataException($"{Id}: at least one driving mode is required.");
        foreach (var mode in DrivingModes.Values)
        {
            if (mode.SpeedMetresPerSecond <= 0.0 || mode.MaximumWheelAngleRadians <= 0.0 || mode.LockToLockSeconds <= 0.0)
            {
                throw new InvalidDataException($"{Id}/{mode.Id}: invalid driving mode values.");
            }
        }

        foreach (var unit in TowedUnits) unit.Validate(Id);
    }
}

/// <param name="StartsFromStandstill">
/// Whether the vehicle stood still before this sample and turned its wheel where it stood. The
/// steering rate is a rate per second, so standing still buys wheel movement for no distance at all —
/// which is legitimate, and would otherwise read as an impossible steering rate across the step.
/// </param>
public sealed record RouteSample(
    double StationMetres,
    Point3 PositionMetres,
    double PathHeadingRadians,
    double SignedCurvaturePerMetre,
    TravelDirection Direction,
    bool IsTangentDiscontinuous = false,
    bool StartsFromStandstill = false);

/// <summary>Where one towed unit sits at a pose, and how far it is folded against its tower.</summary>
public sealed record TowedUnitPose(
    string UnitId,
    Point2 HitchMetres,
    Point2 AxleCentreMetres,
    double HeadingRadians,
    double ArticulationAngleRadians,
    IReadOnlyList<Point2> BodyOutlineWorldMetres);

public sealed record VehiclePose(
    double StationMetres,
    Point3 RearAxleCentreMetres,
    Point2 FrontAxleCentreMetres,
    double VehicleHeadingRadians,
    double SteeringAngleRadians,
    TravelDirection Direction,
    IReadOnlyList<Point2> BodyOutlineWorldMetres)
{
    /// <summary>Towed units at this pose, nearest first. Empty for a rigid vehicle.</summary>
    public IReadOnlyList<TowedUnitPose> TowedUnits { get; init; } = [];

    /// <summary>
    /// Every closed outline this pose occupies. Clearance, containment and envelope work all read
    /// this rather than <see cref="BodyOutlineWorldMetres"/>: a trailer that is not in the list is
    /// a trailer the check silently drives through obstacles.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Point2>> OccupiedOutlinesWorldMetres =>
        TowedUnits.Count == 0
            ? [BodyOutlineWorldMetres]
            : [BodyOutlineWorldMetres, .. TowedUnits
                .Select(unit => unit.BodyOutlineWorldMetres)
                .Where(outline => outline.Count >= 3)];
}

public enum ViolationKind
{
    SteeringAngle,
    SteeringRate,
    ArticulationAngle,
    TangentDiscontinuity,
    Grade,
    ObstacleClearance,
    OutsideAllowedArea,
    FixedWidthRoad
}

public sealed record AnalysisViolation(
    ViolationKind Kind,
    double StationMetres,
    Point3 PositionMetres,
    string Message);

public sealed class VehicleAccessResult
{
    public required VehicleDefinition Vehicle { get; init; }
    public required DrivingModeDefinition DrivingMode { get; init; }
    public required IReadOnlyList<VehiclePose> Poses { get; init; }
    public required IReadOnlyList<Point2> RearAxleTrackMetres { get; init; }
    public required IReadOnlyList<Point2> FrontAxleTrackMetres { get; init; }
    public required IReadOnlyList<AnalysisViolation> Violations { get; init; }
    public required double MaximumSteeringAngleRadians { get; init; }
    public required double MaximumSteeringRateRadiansPerSecond { get; init; }
    public required double MaximumAbsoluteGrade { get; init; }

    /// <summary>Axle-centre track of each towed unit, nearest first. Empty for a rigid vehicle.</summary>
    public IReadOnlyList<IReadOnlyList<Point2>> TowedAxleTracksMetres { get; init; } = [];

    /// <summary>
    /// Largest fold reached at each articulation joint over the route. The source publishes no
    /// per-vehicle fold limit, so any value below
    /// <see cref="VehicleAccessAnalyzer.JackknifeFoldRadians"/> is reported rather than judged.
    /// </summary>
    public IReadOnlyList<double> MaximumArticulationAnglesRadians { get; init; } = [];
    public double? MinimumClearanceMetres { get; set; }
    public bool IsFeasible => Violations.Count == 0;
}

public sealed record VehicleState(
    Point3 RearAxleCentreMetres,
    double VehicleHeadingRadians,
    double SteeringAngleRadians,
    TravelDirection Direction,
    double StationMetres);

public sealed record GeneratedTrajectory(VehicleState EndState, IReadOnlyList<RouteSample> Samples);

/// <summary>The outcome of one integration step: where it ended, what it drew, and how far it turned.</summary>
/// <remarks>
/// <paramref name="HeadingChangeRadians"/> is the raw change, not the difference of two normalised
/// headings — a caller accumulating rotation over many steps needs it to survive the wrap at pi.
/// </remarks>
public sealed record AdvanceResult(VehicleState State, RouteSample Sample, double HeadingChangeRadians);
