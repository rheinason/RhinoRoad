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
    ObstacleClearance
}

public enum ReviewMetricKind
{
    SteeringAngle,
    SteeringRate,
    ClearanceOrRoadMargin,
    Grade
}

public sealed record VehicleAccessMetricSample(
    double StationMetres,
    Point3 PositionMetres,
    double SteeringAngleRadians,
    double SteeringRateRadiansPerSecond,
    double Grade,
    double? ClearanceOrRoadMarginMetres,
    TravelDirection Direction);

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
                    var elapsed = distance / result.DrivingMode.SpeedMetresPerSecond;
                    steeringRate = Math.Abs(Geometry2D.NormalizeAngle(
                        result.Poses[index].SteeringAngleRadians - result.Poses[index - 1].SteeringAngleRadians)) / elapsed;
                    grade = (route[index].PositionMetres.Z - route[index - 1].PositionMetres.Z) / distance;
                }
            }
            samples.Add(new VehicleAccessMetricSample(
                route[index].StationMetres,
                route[index].PositionMetres,
                result.Poses[index].SteeringAngleRadians,
                steeringRate,
                grade,
                clearanceOrRoadMargins?[index],
                route[index].Direction));
        }
        return samples;
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
