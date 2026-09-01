namespace RhinoRoad.Core;

public sealed record ReferenceCertificationThresholds(
    double MaximumEnvelopeDeviationMetres,
    double MaximumInwardUnderpredictionMetres);

public sealed record ReferenceCertificationCase(
    string VehicleId,
    string ModeId,
    int AngleGon,
    string SourceFile,
    string SourceSha256,
    double? MaximumEnvelopeDeviationMetres,
    double? MaximumInwardUnderpredictionMetres,
    double? InferredRearTrackWidthMetres,
    int RouteSampleCount,
    bool Passed,
    string Notes);

public sealed record ReferenceCertificationReport(
    string SchemaVersion,
    string GeneratedUtc,
    string RhinoVersion,
    string Method,
    ReferenceCertificationThresholds Thresholds,
    IReadOnlyList<ReferenceCertificationCase> Cases)
{
    public static readonly string[] RequiredVehicleIds = ["PV", "REN", "BUS12"];
    public static readonly string[] RequiredModeIds = ["A", "B"];
    public static readonly int[] RequiredAnglesGon = [40, 100, 180];

    public bool IsCompleteFor(string vehicleId) => RequiredModeIds.All(modeId =>
        RequiredAnglesGon.All(angle => Cases.Any(item =>
            item.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase) &&
            item.ModeId.Equals(modeId, StringComparison.OrdinalIgnoreCase) &&
            item.AngleGon == angle)));

    public bool IsValidated(string vehicleId) => IsCompleteFor(vehicleId) && Cases
        .Where(item => item.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase))
        .All(item => item.Passed);
}
