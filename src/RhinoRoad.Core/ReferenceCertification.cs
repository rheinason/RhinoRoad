namespace RhinoRoad.Core;

public sealed record ReferenceCertificationThresholds(
    double MaximumEnvelopeDeviationMetres,
    double MaximumInwardUnderpredictionMetres)
{
    private const double ComparisonEpsilon = 1e-9;

    /// <summary>
    /// A case passes only when the two-sided envelope deviation and the inward underprediction
    /// both sit inside tolerance. Inward underprediction is the stricter of the two because a
    /// swept envelope that falls short of the official one understates the space the vehicle needs.
    /// </summary>
    public bool Accepts(double maximumEnvelopeDeviationMetres, double maximumInwardUnderpredictionMetres) =>
        maximumEnvelopeDeviationMetres <= MaximumEnvelopeDeviationMetres + ComparisonEpsilon &&
        maximumInwardUnderpredictionMetres <= MaximumInwardUnderpredictionMetres + ComparisonEpsilon;
}

/// <summary>
/// One case of the matrix.
/// </summary>
/// <remarks>
/// <paramref name="MeasuredChordMetres"/> is taken from the drawing's own wheel traces and compared
/// against <paramref name="ExpectedChordMetres"/>, which the preset predicts. Until schema 1.3 this
/// pair recorded the preset's wheel offset twice under two names, so the comparison — and the test
/// asserting it — could never fail. The chord is the quantity the traces actually determine.
/// </remarks>
public sealed record ReferenceCertificationCase(
    string VehicleId,
    string ModeId,
    int AngleGon,
    string SourceFile,
    string SourceSha256,
    double? MaximumEnvelopeDeviationMetres,
    double? MaximumInwardUnderpredictionMetres,
    DeviationStatistics? Deviation,
    double? MeasuredChordMetres,
    double? MeasuredChordSpreadMetres,
    double ExpectedChordMetres,
    int RouteSampleCount,
    bool Passed,
    string Notes);

/// <summary>
/// The geometry context a run was measured in. Certification verdicts depend on it — the swept
/// envelope is a Boolean union whose success is tolerance-dependent — so a report that omits it
/// cannot be reproduced or trusted.
/// </summary>
public sealed record ReferenceCertificationEnvironment(
    string ModelUnitSystem,
    double ModelAbsoluteTolerance,
    double GeometryToleranceMetres);

public sealed record ReferenceCertificationReport(
    string SchemaVersion,
    string GeneratedUtc,
    string RhinoVersion,
    string Method,
    ReferenceCertificationEnvironment Environment,
    ReferenceCertificationThresholds Thresholds,
    IReadOnlyList<ReferenceCertificationCase> Cases)
{
    public const string CurrentSchemaVersion = "1.4";

    /// <summary>
    /// True when the report covers every mode and angle the plan requires for this vehicle.
    /// The matrix comes from the plan, not from constants, so adding a vehicle is a data edit.
    /// </summary>
    public bool IsCompleteFor(CertificationPlan plan, string vehicleId)
    {
        var modeIds = plan.ModeIdsFor(vehicleId);
        return modeIds.Count > 0 && modeIds.All(modeId =>
            plan.AnglesGon.All(angle => Cases.Any(item =>
                item.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase) &&
                item.ModeId.Equals(modeId, StringComparison.OrdinalIgnoreCase) &&
                item.AngleGon == angle)));
    }

    public bool IsValidated(CertificationPlan plan, string vehicleId) =>
        IsCompleteFor(plan, vehicleId) && Cases
            .Where(item => item.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase))
            .All(item => item.Passed);
}
