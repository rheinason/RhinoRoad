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
    public double RearWheelInnerEdgeOffsetMetres => (AxleTrackMetres - TyreWidthMetres) * 0.5;
    public double WheelOuterEdgeOffsetMetres => (AxleTrackMetres + TyreWidthMetres) * 0.5;

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
    }
}

public sealed record RouteSample(
    double StationMetres,
    Point3 PositionMetres,
    double PathHeadingRadians,
    double SignedCurvaturePerMetre,
    TravelDirection Direction,
    bool IsTangentDiscontinuous = false);

public sealed record VehiclePose(
    double StationMetres,
    Point3 RearAxleCentreMetres,
    Point2 FrontAxleCentreMetres,
    double VehicleHeadingRadians,
    double SteeringAngleRadians,
    TravelDirection Direction,
    IReadOnlyList<Point2> BodyOutlineWorldMetres);

public enum ViolationKind
{
    SteeringAngle,
    SteeringRate,
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
