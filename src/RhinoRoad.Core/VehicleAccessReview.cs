namespace RhinoRoad.Core;

public enum ReviewCheckState
{
    Pass,
    Fail,
    Off,
    Unavailable
}

public enum ReviewCheckKind
{
    EnvelopeGeneration,
    SteeringAngle,
    SteeringRate,
    TangentContinuity,
    Grade,
    FixedWidthRoadFit,
    AllowedAreaFit,
    ObstacleClearance,

    /// <summary>How far an articulated combination folds at its joints. Not reported for a rigid vehicle.</summary>
    ArticulationAngle
}

public enum ReviewMetricKind
{
    SteeringAngle,
    SteeringRate,
    ClearanceOrRoadMargin,
    Grade,

    /// <summary>The largest fold over the joints of an articulated combination.</summary>
    FoldAngle
}

public sealed record VehicleAccessMetricSample(
    double StationMetres,
    Point3 PositionMetres,
    double SteeringAngleRadians,
    double SteeringRateRadiansPerSecond,
    double Grade,
    double? ClearanceOrRoadMarginMetres,
    TravelDirection Direction)
{
    /// <summary>Largest fold over the joints at this sample, unsigned; null for a rigid vehicle.</summary>
    public double? FoldAngleRadians { get; init; }
}

public sealed record VehicleAccessProblemEvent(
    ReviewCheckKind Check,
    double StartStationMetres,
    double EndStationMetres,
    double WorstStationMetres,
    Point3 WorldPointMetres,
    double? ActualValue,
    double? Limit,
    string Message);

public sealed record VehicleAccessReviewCheck(
    ReviewCheckKind Kind,
    ReviewCheckState State,
    double? ActualValue,
    double? Limit,
    string Message);

public sealed record VehicleAccessReview(
    IReadOnlyList<VehicleAccessReviewCheck> Checks,
    IReadOnlyList<VehicleAccessMetricSample> Samples,
    IReadOnlyList<VehicleAccessProblemEvent> ProblemEvents,
    double RouteLengthMetres,
    double MaximumSteeringAngleRadians,
    double MaximumSteeringRateRadiansPerSecond,
    double MaximumAbsoluteGrade,
    double? MinimumClearanceMetres,
    int ReversalCount,
    string VehicleName,
    string ModeName,
    ValidationStatus ValidationStatus,
    bool IsFeasible);

public static class VehicleAccessReviewAnalyzer
{
    public static IReadOnlyList<VehicleAccessMetricSample> MetricSamples(
        IReadOnlyList<RouteSample> route,
        VehicleAccessResult result,
        IReadOnlyList<double?>? clearanceOrRoadMargins = null)
    {
        if (route.Count != result.Poses.Count) throw new ArgumentException("Route and pose counts must match.");
        if (clearanceOrRoadMargins is not null && clearanceOrRoadMargins.Count != route.Count)
            throw new ArgumentException("Margin and route counts must match.", nameof(clearanceOrRoadMargins));
        var samples = new List<VehicleAccessMetricSample>(route.Count);
        for (var index = 0; index < route.Count; index++)
        {
            var steeringRate = 0.0;
            var grade = 0.0;
            if (index > 0)
            {
                var distance = route[index].PositionMetres.XY.DistanceTo(route[index - 1].PositionMetres.XY);
                if (distance > Geometry2D.Epsilon)
                {
                    // A wheel turned at a standstill -- every change of direction, and an armed
                    // Stop -- moved while the vehicle covered no distance, so it has no rate to
                    // report. The analyzer skips the same samples; without this the profile showed
                    // a steering-rate problem at every cusp while the check itself passed.
                    if (!route[index].StartsFromStandstill)
                    {
                        // At the speed the interval was driven at, as the analyzer prices it.
                        var elapsed = distance
                            / (result.DrivingMode.LegSpeedKilometresPerHour(route[index].SpeedKilometresPerHour) / 3.6);
                        steeringRate = Math.Abs(Geometry2D.NormalizeAngle(
                            result.Poses[index].SteeringAngleRadians - result.Poses[index - 1].SteeringAngleRadians)) / elapsed;
                    }
                    grade = (route[index].PositionMetres.Z - route[index - 1].PositionMetres.Z) / distance;
                }
            }
            var towed = result.Poses[index].TowedUnits;
            samples.Add(new VehicleAccessMetricSample(
                route[index].StationMetres,
                route[index].PositionMetres,
                result.Poses[index].SteeringAngleRadians,
                steeringRate,
                grade,
                clearanceOrRoadMargins?[index],
                route[index].Direction)
            {
                FoldAngleRadians = towed.Count == 0 ? null : towed.Max(unit => Math.Abs(unit.ArticulationAngleRadians))
            });
        }
        return samples;
    }

    /// <summary>
    /// The fold the review judges an articulated vehicle against. The one place that limit is chosen.
    /// </summary>
    /// <remarks>
    /// The tightest of the per-unit limits (<see cref="TowedUnitDefinition.MaximumArticulationDegrees"/>),
    /// which is where the analyzer files its fold-limit violation and where the generator, the planner
    /// and the area search stop, so the inspector's check fails where the analysis does rather than
    /// only at the jackknife floor past it. The profile carries the largest fold over all joints, so
    /// one limit is drawn against it; with every unit on the same limit, as every preset is, that is
    /// exactly each unit's own. A rigid vehicle has no joint and gets the jackknife floor, which
    /// nothing it does can reach.
    /// </remarks>
    public static double FoldLimitRadians(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return vehicle.TowedUnits.Count == 0
            ? VehicleAccessAnalyzer.JackknifeFoldRadians
            : Math.Min(VehicleAccessAnalyzer.JackknifeFoldRadians,
                vehicle.TowedUnits.Min(unit => unit.MaximumArticulationRadians));
    }

    /// <summary>The inspector check a violation is filed under.</summary>
    public static ReviewCheckKind CheckFor(ViolationKind kind) => kind switch
    {
        ViolationKind.SteeringAngle => ReviewCheckKind.SteeringAngle,
        ViolationKind.SteeringRate => ReviewCheckKind.SteeringRate,
        ViolationKind.ArticulationAngle => ReviewCheckKind.ArticulationAngle,
        ViolationKind.TangentDiscontinuity => ReviewCheckKind.TangentContinuity,
        ViolationKind.Grade => ReviewCheckKind.Grade,
        ViolationKind.ObstacleClearance => ReviewCheckKind.ObstacleClearance,
        ViolationKind.OutsideAllowedArea => ReviewCheckKind.AllowedAreaFit,
        ViolationKind.FixedWidthRoad => ReviewCheckKind.FixedWidthRoadFit,
        _ => ReviewCheckKind.EnvelopeGeneration
    };

    /// <summary>
    /// A problem event for a violation that no per-sample metric already describes, spanning the
    /// violation's own station range and anchored at its worst point.
    /// </summary>
    public static VehicleAccessProblemEvent ViolationEvent(
        ReviewCheckKind check,
        IReadOnlyList<VehicleAccessMetricSample> samples,
        AnalysisViolation violation)
    {
        if (samples.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(samples));
        var start = violation.StationMetres;
        var end = Math.Max(start, violation.EndStationMetres ?? start);
        var within = samples.Where(sample => sample.StationMetres >= start - 1e-9 && sample.StationMetres <= end + 1e-9).ToArray();
        var worst = (within.Length > 0 ? within : samples).MinBy(sample => sample.PositionMetres.XY.DistanceTo(violation.PositionMetres.XY))!;
        return new VehicleAccessProblemEvent(
            check,
            start,
            end,
            worst.StationMetres,
            violation.PositionMetres,
            null,
            null,
            violation.Message);
    }

    public static IReadOnlyList<VehicleAccessProblemEvent> GroupFailures(
        ReviewCheckKind check,
        IReadOnlyList<VehicleAccessMetricSample> samples,
        Func<VehicleAccessMetricSample, double> actual,
        Func<VehicleAccessMetricSample, bool> fails,
        double? limit,
        string message) => GroupFailuresIndexed(
            check,
            samples,
            (sample, _) => actual(sample),
            (sample, _) => fails(sample),
            limit,
            message);

    /// <summary>Index-aware grouping for metric arrays already aligned one-to-one with samples.</summary>
    public static IReadOnlyList<VehicleAccessProblemEvent> GroupFailuresIndexed(
        ReviewCheckKind check,
        IReadOnlyList<VehicleAccessMetricSample> samples,
        Func<VehicleAccessMetricSample, int, double> actual,
        Func<VehicleAccessMetricSample, int, bool> fails,
        double? limit,
        string message)
    {
        var events = new List<VehicleAccessProblemEvent>();
        var start = -1;
        for (var index = 0; index <= samples.Count; index++)
        {
            var failing = index < samples.Count && fails(samples[index], index);
            if (failing && start < 0) start = index;
            if (failing || start < 0) continue;
            var end = index - 1;
            var worst = start;
            for (var candidate = start + 1; candidate <= end; candidate++)
                if (Math.Abs(actual(samples[candidate], candidate)) > Math.Abs(actual(samples[worst], worst))) worst = candidate;
            events.Add(new VehicleAccessProblemEvent(
                check,
                samples[start].StationMetres,
                samples[end].StationMetres,
                samples[worst].StationMetres,
                samples[worst].PositionMetres,
                actual(samples[worst], worst),
                limit,
                message));
            start = -1;
        }
        return events;
    }

    public static VehicleAccessProblemEvent FallbackEvent(
        ReviewCheckKind check,
        IReadOnlyList<VehicleAccessMetricSample> samples,
        Point3 point,
        double? actual,
        double? limit,
        string message)
    {
        if (samples.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(samples));
        var nearest = samples.MinBy(sample => sample.PositionMetres.XY.DistanceTo(point.XY))!;
        return new VehicleAccessProblemEvent(
            check,
            nearest.StationMetres,
            nearest.StationMetres,
            nearest.StationMetres,
            point,
            actual,
            limit,
            message);
    }
}
