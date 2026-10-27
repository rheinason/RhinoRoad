using Rhino;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

internal sealed record VehicleAccessReviewSnapshot(
    PreparedVehicleAccess Run,
    VehicleAccessReview Review);

internal static class VehicleAccessReviewService
{
    private static readonly Dictionary<(uint Document, Guid Source), VehicleAccessReviewSnapshot> Cache = new();

    static VehicleAccessReviewService() => RhinoDoc.CloseDocument += (_, args) =>
    {
        foreach (var key in Cache.Keys.Where(key => key.Document == args.Document.RuntimeSerialNumber).ToArray())
            Cache.Remove(key);
    };

    public static void Remember(RhinoDoc document, PreparedVehicleAccess run) =>
        Cache[(document.RuntimeSerialNumber, run.Definition.StableSourceId)] =
            new VehicleAccessReviewSnapshot(run, Build(document, run));

    public static VehicleAccessReviewSnapshot? Find(RhinoDoc document, Guid sourceId) =>
        Cache.TryGetValue((document.RuntimeSerialNumber, sourceId), out var snapshot) ? snapshot : null;

    public static VehicleAccessReview Build(RhinoDoc document, PreparedVehicleAccess run)
    {
        // The site check already measured every pose when the run was prepared; measure again only
        // for a run that somehow skipped it.
        var fit = run.Geometry.SiteFit ?? SiteFit(document, run);
        var obstacleMargins = Optional(run.Definition.CheckObstacles ? fit.ObstacleMargins : null);
        var allowedMargins = Optional(run.Definition.CheckAllowedArea ? fit.AllowedAreaMargins : null);
        var roadMargins = Optional(fit.RoadMargins);
        var profileMargins = Enumerable.Range(0, run.Route.Count)
            .Select(index => Minimum(obstacleMargins?[index], allowedMargins?[index], roadMargins?[index]))
            .ToArray();
        var samples = VehicleAccessReviewAnalyzer.MetricSamples(run.Route, run.Analysis, profileMargins);
        var checks = Checks(run, obstacleMargins, allowedMargins, roadMargins);
        var events = Events(run, samples, obstacleMargins, allowedMargins, roadMargins);
        var reversals = run.Route.Zip(run.Route.Skip(1), (first, second) => first.Direction != second.Direction).Count(changed => changed);
        return new VehicleAccessReview(
            checks,
            samples,
            events,
            run.Route[^1].StationMetres - run.Route[0].StationMetres,
            run.Analysis.MaximumSteeringAngleRadians,
            run.Analysis.MaximumSteeringRateRadiansPerSecond,
            run.Analysis.MaximumAbsoluteGrade,
            run.Analysis.MinimumClearanceMetres,
            reversals,
            run.Analysis.Vehicle.Name,
            run.Analysis.DrivingMode.Name,
            run.Analysis.Vehicle.ValidationStatus,
            run.Violations.Count == 0 && run.Geometry.BodyEnvelopeMerged && run.Geometry.Warnings.Count == 0);
    }

    private static IReadOnlyList<VehicleAccessReviewCheck> Checks(
        PreparedVehicleAccess run,
        IReadOnlyList<double?>? obstacle,
        IReadOnlyList<double?>? allowed,
        IReadOnlyList<double?>? road)
    {
        var mode = run.Analysis.DrivingMode;
        return new[]
        {
            Check(ReviewCheckKind.EnvelopeGeneration,
                run.Geometry.BodyEnvelope is not null && run.Geometry.ClearanceEnvelope is not null && run.Geometry.BodyEnvelopeMerged,
                true, null, null, "Swept and clearance envelopes"),
            Check(ReviewCheckKind.SteeringAngle,
                !Has(run, ViolationKind.SteeringAngle), true,
                Degrees(run.Analysis.MaximumSteeringAngleRadians), mode.MaximumWheelAngleDegrees, "Maximum wheel angle"),
            Check(ReviewCheckKind.SteeringRate,
                !Has(run, ViolationKind.SteeringRate), true,
                Degrees(run.Analysis.MaximumSteeringRateRadiansPerSecond), Degrees(mode.MaximumSteeringRateRadiansPerSecond), "Maximum steering rate"),
            Check(ReviewCheckKind.TangentContinuity,
                !Has(run, ViolationKind.TangentDiscontinuity), true, null, null, "Route tangent continuity"),
            ArticulationCheck(run),
            GradeCheck(run),
            MarginCheck(ReviewCheckKind.FixedWidthRoadFit, run, road,
                run.Definition.RoadEdgeMethod is RoadEdgeMethod.Both or RoadEdgeMethod.FixedWidth,
                ViolationKind.FixedWidthRoad, "Fixed-width road fit"),
            MarginCheck(ReviewCheckKind.AllowedAreaFit, run, allowed, run.Definition.CheckAllowedArea,
                ViolationKind.OutsideAllowedArea, "Allowed-area fit"),
            MarginCheck(ReviewCheckKind.ObstacleClearance, run, obstacle, run.Definition.CheckObstacles,
                ViolationKind.ObstacleClearance, "Obstacle clearance")
        }.Where(check => check is not null).Select(check => check!).ToArray();
    }

    /// <summary>
    /// The fold of an articulated combination against its fold limit. A rigid vehicle has no
    /// joint, so it gets no row rather than an "Off" one it could never turn on.
    /// </summary>
    private static VehicleAccessReviewCheck? ArticulationCheck(PreparedVehicleAccess run)
    {
        if (run.Analysis.Vehicle.TowedUnits.Count == 0) return null;
        var fold = run.Analysis.MaximumArticulationAnglesRadians.DefaultIfEmpty(0.0).Max();
        var limit = VehicleAccessReviewAnalyzer.FoldLimitRadians(run.Analysis.Vehicle);
        return Check(ReviewCheckKind.ArticulationAngle,
            !Has(run, ViolationKind.ArticulationAngle) && fold <= limit + 1e-8, true,
            Degrees(fold), Degrees(limit), "Maximum fold angle");
    }

    private static VehicleAccessReviewCheck MarginCheck(
        ReviewCheckKind kind,
        PreparedVehicleAccess run,
        IReadOnlyList<double?>? margins,
        bool enabled,
        ViolationKind violation,
        string message)
    {
        if (!enabled) return new VehicleAccessReviewCheck(kind, ReviewCheckState.Off, null, null, message);
        if (margins is null || margins.All(value => !value.HasValue))
            return new VehicleAccessReviewCheck(kind, ReviewCheckState.Unavailable, null, 0.0, message);
        var minimum = margins.Where(value => value.HasValue).Min(value => value!.Value);
        var actual = kind == ReviewCheckKind.ObstacleClearance
            ? minimum + run.Definition.ClearanceMetres
            : minimum;
        var limit = kind == ReviewCheckKind.ObstacleClearance
            ? run.Definition.ClearanceMetres
            : 0.0;
        return new VehicleAccessReviewCheck(kind,
            Has(run, violation) ? ReviewCheckState.Fail : ReviewCheckState.Pass,
            actual,
            limit,
            message);
    }

    private static VehicleAccessReviewCheck Check(
        ReviewCheckKind kind, bool passed, bool enabled, double? actual, double? limit, string message) =>
        new(kind, enabled ? passed ? ReviewCheckState.Pass : ReviewCheckState.Fail : ReviewCheckState.Off,
            actual, limit, message);

    private static VehicleAccessReviewCheck GradeCheck(PreparedVehicleAccess run)
    {
        if (!run.Definition.CheckMaximumGrade)
            return new VehicleAccessReviewCheck(ReviewCheckKind.Grade, ReviewCheckState.Off, null, null, "Maximum grade");
        if (!run.HasElevation)
            return new VehicleAccessReviewCheck(
                ReviewCheckKind.Grade,
                ReviewCheckState.Unavailable,
                null,
                run.Definition.MaximumGradePercent,
                "Interactive routes are planar unless draped onto terrain.");
        return Check(
            ReviewCheckKind.Grade,
            !Has(run, ViolationKind.Grade),
            true,
            run.Analysis.MaximumAbsoluteGrade * 100.0,
            run.Definition.MaximumGradePercent,
            "Maximum grade");
    }

    private static IReadOnlyList<VehicleAccessProblemEvent> Events(
        PreparedVehicleAccess run,
        IReadOnlyList<VehicleAccessMetricSample> samples,
        IReadOnlyList<double?>? obstacle,
        IReadOnlyList<double?>? allowed,
        IReadOnlyList<double?>? road)
    {
        var result = new List<VehicleAccessProblemEvent>();
        var mode = run.Analysis.DrivingMode;
        if (run.Geometry.BodyEnvelope is null || run.Geometry.ClearanceEnvelope is null || !run.Geometry.BodyEnvelopeMerged)
        {
            var point = run.Route[^1].PositionMetres;
            result.Add(VehicleAccessReviewAnalyzer.FallbackEvent(
                ReviewCheckKind.EnvelopeGeneration,
                samples,
                point,
                null,
                null,
                run.Geometry.Warnings.FirstOrDefault() ?? "The complete swept envelope could not be generated."));
        }
        result.AddRange(VehicleAccessReviewAnalyzer.GroupFailures(
            ReviewCheckKind.SteeringAngle, samples, sample => Degrees(sample.SteeringAngleRadians),
            sample => Math.Abs(sample.SteeringAngleRadians) > mode.MaximumWheelAngleRadians + 1e-8,
            mode.MaximumWheelAngleDegrees, "Wheel angle exceeds the selected driving mode."));
        result.AddRange(VehicleAccessReviewAnalyzer.GroupFailures(
            ReviewCheckKind.SteeringRate, samples, sample => Degrees(sample.SteeringRateRadiansPerSecond),
            sample => sample.SteeringRateRadiansPerSecond > mode.MaximumSteeringRateRadiansPerSecond + 1e-8,
            Degrees(mode.MaximumSteeringRateRadiansPerSecond), "Steering changes faster than the selected driving mode."));
        if (run.Definition.CheckMaximumGrade && run.HasElevation)
            result.AddRange(VehicleAccessReviewAnalyzer.GroupFailures(
                ReviewCheckKind.Grade, samples, sample => Math.Abs(sample.Grade) * 100.0,
                sample => Math.Abs(sample.Grade) * 100.0 > run.Definition.MaximumGradePercent + 1e-8,
                run.Definition.MaximumGradePercent, "Grade exceeds the configured limit."));
        result.AddRange(VehicleAccessReviewAnalyzer.GroupFailuresIndexed(
            ReviewCheckKind.TangentContinuity, samples, (_, _) => 1.0,
            (_, index) => run.Route[index].IsTangentDiscontinuous,
            null, "The route is not tangent-continuous here."));
        if (run.Analysis.Vehicle.TowedUnits.Count > 0)
        {
            var fold = VehicleAccessReviewAnalyzer.FoldLimitRadians(run.Analysis.Vehicle);
            result.AddRange(VehicleAccessReviewAnalyzer.GroupFailures(
                ReviewCheckKind.ArticulationAngle, samples, sample => Degrees(sample.FoldAngleRadians ?? 0.0),
                sample => (sample.FoldAngleRadians ?? 0.0) > fold + 1e-8,
                Degrees(fold), "The combination folds past its fold limit."));
        }
        AddMarginEvents(result, ReviewCheckKind.ObstacleClearance, samples, obstacle, "Clearance envelope reaches an obstacle.");
        AddMarginEvents(result, ReviewCheckKind.AllowedAreaFit, samples, allowed, "Vehicle clearance reaches outside the allowed area.");
        AddMarginEvents(result, ReviewCheckKind.FixedWidthRoadFit, samples, road, "Vehicle clearance does not fit the fixed-width road.");

        // A violation only needs its own event where no per-sample metric already located that
        // check. Each such violation now carries its own station range, so all of them are listed.
        var located = result.Select(item => item.Check).ToHashSet();
        foreach (var violation in run.Violations)
        {
            var check = VehicleAccessReviewAnalyzer.CheckFor(violation.Kind);
            if (located.Contains(check)) continue;
            result.Add(VehicleAccessReviewAnalyzer.ViolationEvent(check, samples, violation));
        }
        return result.OrderBy(item => item.StartStationMetres).ThenBy(item => item.Check).ToArray();
    }

    private static void AddMarginEvents(
        List<VehicleAccessProblemEvent> events,
        ReviewCheckKind check,
        IReadOnlyList<VehicleAccessMetricSample> samples,
        IReadOnlyList<double?>? margins,
        string message)
    {
        if (margins is null) return;
        events.AddRange(VehicleAccessReviewAnalyzer.GroupFailuresIndexed(
            check, samples,
            (_, index) => margins[index] ?? double.PositiveInfinity,
            (_, index) => (margins[index] ?? double.PositiveInfinity) < 0.0,
            0.0, message));
    }

    /// <summary>
    /// The per-pose margins come from Core (<see cref="SiteFitAnalyzer"/>), which tests containment
    /// as well as distance: a bollard wholly under the body reads as overlap, not as clear.
    /// </summary>
    private static SiteFitOutcome SiteFit(RhinoDoc document, PreparedVehicleAccess run)
    {
        // The same road the run checked: whenever the road has fixed edges. CheckClearance measures
        // against the corridor region when there is one and falls back to the boundary curve's
        // outline when there is not; dropping the boundary with the region lost the road check.
        var fixedEdges = run.Definition.RoadEdgeMethod is RoadEdgeMethod.Both or RoadEdgeMethod.FixedWidth;
        RhinoGeometryBuilder.CheckClearance(
            run.Geometry,
            fixedEdges ? run.Geometry.FixedRoadBoundary : null,
            run.Obstacles,
            run.AllowedBoundaries,
            run.Definition.ClearanceMetres,
            document.ModelUnitSystem);
        return run.Geometry.SiteFit!;
    }

    private static IReadOnlyList<double?>? Optional(IReadOnlyList<double>? margins) =>
        margins?.Select(value => (double?)value).ToArray();

    private static double? Minimum(params double?[] values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Min();
    }

    private static bool Has(PreparedVehicleAccess run, ViolationKind kind) => run.Violations.Any(item => item.Kind == kind);
    private static double Degrees(double radians) => radians * 180.0 / Math.PI;
}
