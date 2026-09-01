using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

internal sealed record RhinoAnalysisGeometry(
    Curve RearAxleTrack,
    Curve FrontAxleTrack,
    Curve? BodyEnvelope,
    Curve? ClearanceEnvelope,
    IReadOnlyList<Curve> FixedRoadEdges,
    Curve? FixedRoadBoundary,
    IReadOnlyList<string> Warnings);

internal sealed record ClearanceOutcome(
    double? MinimumClearanceMetres,
    IReadOnlyList<AnalysisViolation> Violations);

internal static class RhinoGeometryBuilder
{
    public static RhinoAnalysisGeometry Build(
        VehicleAccessResult result,
        IReadOnlyList<RouteSample> route,
        RhinoDoc document,
        double clearanceMetres,
        double leftRoadWidthMetres,
        double rightRoadWidthMetres,
        bool createFixedEdges)
    {
        var modelUnitsPerMetre = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var elevationMetres = route[0].PositionMetres.Z;
        var elevationModel = elevationMetres * modelUnitsPerMetre;
        var tolerance = Math.Max(document.ModelAbsoluteTolerance, 0.001 * modelUnitsPerMetre);
        var warnings = new List<string>();

        var rearTrack = TrackCurve(result.RearAxleTrackMetres, elevationModel, modelUnitsPerMetre);
        var frontTrack = TrackCurve(result.FrontAxleTrackMetres, elevationModel, modelUnitsPerMetre);
        var bodyEnvelope = SweptEnvelope(result.Poses, 0.0, elevationModel, modelUnitsPerMetre, tolerance);
        if (bodyEnvelope is null) warnings.Add("Swept-envelope Boolean failed.");
        var clearanceEnvelope = clearanceMetres <= 1e-9
            ? bodyEnvelope?.DuplicateCurve()
            : SweptEnvelope(result.Poses, clearanceMetres, elevationModel, modelUnitsPerMetre, tolerance);
        if (clearanceEnvelope is null) warnings.Add("Clearance-envelope Boolean failed.");

        var fixedEdges = createFixedEdges
            ? FixedWidthEdges(route, elevationMetres, document.ModelUnitSystem, leftRoadWidthMetres, rightRoadWidthMetres, tolerance)
            : [];
        var roadBoundary = fixedEdges.Count == 2 ? CloseCorridor(fixedEdges[0], fixedEdges[1], tolerance) : null;
        if (createFixedEdges && fixedEdges.Count != 2) warnings.Add("Fixed-width road offsets could not be generated.");

        return new RhinoAnalysisGeometry(
            rearTrack,
            frontTrack,
            bodyEnvelope,
            clearanceEnvelope,
            fixedEdges,
            roadBoundary,
            warnings);
    }

    public static ClearanceOutcome CheckClearance(
        Curve? bodyEnvelope,
        Curve? clearanceEnvelope,
        Curve? fixedRoadBoundary,
        IEnumerable<Curve> obstacles,
        IEnumerable<Curve> allowedBoundaries,
        double clearanceMetres,
        UnitSystem modelUnits,
        double tolerance)
    {
        var violations = new List<AnalysisViolation>();
        if (bodyEnvelope is null || clearanceEnvelope is null) return new ClearanceOutcome(null, violations);
        var metresPerModelUnit = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        var minimum = double.PositiveInfinity;
        var bodyPoints = SampleCurve(bodyEnvelope, modelUnits, 0.10);
        var clearancePoints = SampleCurve(clearanceEnvelope, modelUnits, 0.10);

        foreach (var obstacle in obstacles)
        {
            var obstaclePoints = SampleCurve(obstacle, modelUnits, 0.10);
            minimum = Math.Min(minimum, Geometry2D.MinimumDistance(bodyPoints, obstaclePoints, true, obstacle.IsClosed));
            if (IntersectsOrContains(clearanceEnvelope, obstacle, tolerance))
            {
                var location = obstacle.PointAtStart;
                violations.Add(new AnalysisViolation(
                    ViolationKind.ObstacleClearance,
                    0.0,
                    new Point3(location.X * metresPerModelUnit, location.Y * metresPerModelUnit, location.Z * metresPerModelUnit),
                    $"The {clearanceMetres:0.00} m clearance envelope intersects an obstacle."));
            }
        }

        var plane = PlaneAt(clearanceEnvelope.PointAtStart.Z);
        var closedBoundaries = allowedBoundaries.Where(curve => curve.IsClosed).ToArray();
        foreach (var boundary in closedBoundaries)
        {
            var boundaryPoints = SampleCurve(boundary, modelUnits, 0.10);
            minimum = Math.Min(minimum, Geometry2D.MinimumDistance(bodyPoints, boundaryPoints));
        }
        if (closedBoundaries.Length > 0)
        {
            var outsidePoints = clearancePoints.Where(point => !closedBoundaries.Any(boundary =>
                boundary.Contains(ToPoint3d(point, clearanceEnvelope.PointAtStart.Z, modelUnits), plane, tolerance) != PointContainment.Outside)).ToArray();
            if (outsidePoints.Length > 0)
            {
                var outsidePoint = outsidePoints[0];
                violations.Add(new AnalysisViolation(
                    ViolationKind.OutsideAllowedArea,
                    0.0,
                    new Point3(outsidePoint.X, outsidePoint.Y, clearanceEnvelope.PointAtStart.Z * metresPerModelUnit),
                    "The clearance envelope extends outside the allowed area."));
            }
        }

        if (fixedRoadBoundary is not null)
        {
            var roadPlane = PlaneAt(fixedRoadBoundary.PointAtStart.Z);
            var outsideRoadPoints = clearancePoints.Where(point =>
                fixedRoadBoundary.Contains(ToPoint3d(point, fixedRoadBoundary.PointAtStart.Z, modelUnits), roadPlane, tolerance) == PointContainment.Outside);
            var outsideRoad = outsideRoadPoints.ToArray();
            if (outsideRoad.Length > 0)
            {
                var point = outsideRoad[0];
                violations.Add(new AnalysisViolation(
                    ViolationKind.FixedWidthRoad,
                    0.0,
                    new Point3(point.X, point.Y, fixedRoadBoundary.PointAtStart.Z * metresPerModelUnit),
                    "The clearance envelope does not fit inside the fixed-width road edges."));
            }
        }

        return new ClearanceOutcome(double.IsPositiveInfinity(minimum) ? null : minimum, violations);
    }

    private static Curve TrackCurve(IReadOnlyList<Point2> points, double elevationModel, double modelUnitsPerMetre) =>
        new PolylineCurve(points.Select(point => new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, elevationModel)));

    private static Curve? SweptEnvelope(
        IReadOnlyList<VehiclePose> poses,
        double clearanceMetres,
        double elevationModel,
        double modelUnitsPerMetre,
        double tolerance)
    {
        var footprints = new List<Curve>(poses.Count);
        foreach (var pose in poses)
        {
            var outline = new PolylineCurve(pose.BodyOutlineWorldMetres
                .Select(point => new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, elevationModel))
                .Append(new Point3d(
                    pose.BodyOutlineWorldMetres[0].X * modelUnitsPerMetre,
                    pose.BodyOutlineWorldMetres[0].Y * modelUnitsPerMetre,
                    elevationModel)));
            Curve? footprint = outline;
            if (clearanceMetres > 1e-9)
            {
                footprint = OffsetOutward(outline, clearanceMetres * modelUnitsPerMetre, tolerance);
            }
            if (footprint is not null) footprints.Add(footprint);
        }

        var union = BooleanUnionBatched(footprints, tolerance);
        return LargestClosed(union);
    }

    private static IReadOnlyList<Curve> FixedWidthEdges(
        IReadOnlyList<RouteSample> route,
        double elevationMetres,
        UnitSystem modelUnits,
        double leftMetres,
        double rightMetres,
        double tolerance)
    {
        var plan = RhinoRouteSampler.ToPlanCurve(route, modelUnits, elevationMetres);
        var modelUnitsPerMetre = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var plane = PlaneAt(elevationMetres * modelUnitsPerMetre);
        var left = plan.Offset(plane, leftMetres * modelUnitsPerMetre, tolerance, CurveOffsetCornerStyle.Round);
        var right = plan.Offset(plane, -rightMetres * modelUnitsPerMetre, tolerance, CurveOffsetCornerStyle.Round);
        if (left is null || left.Length == 0 || right is null || right.Length == 0) return [];
        return [left.OrderByDescending(curve => curve.GetLength()).First(), right.OrderByDescending(curve => curve.GetLength()).First()];
    }

    private static Curve? CloseCorridor(Curve left, Curve right, double tolerance)
    {
        var rightReversed = right.DuplicateCurve();
        rightReversed.Reverse();
        var start = new LineCurve(left.PointAtEnd, rightReversed.PointAtStart);
        var end = new LineCurve(rightReversed.PointAtEnd, left.PointAtStart);
        var joined = Curve.JoinCurves([left.DuplicateCurve(), start, rightReversed, end], tolerance, preserveDirection: true);
        return joined?.Where(curve => curve.IsClosed).OrderByDescending(curve => Math.Abs(Area(curve))).FirstOrDefault();
    }

    private static Curve? OffsetOutward(Curve curve, double distance, double tolerance)
    {
        var plane = PlaneAt(curve.PointAtStart.Z);
        var positive = curve.Offset(plane, distance, tolerance, CurveOffsetCornerStyle.Round);
        var negative = curve.Offset(plane, -distance, tolerance, CurveOffsetCornerStyle.Round);
        return (positive ?? []).Concat(negative ?? [])
            .Where(item => item.IsClosed)
            .OrderByDescending(item => Math.Abs(Area(item)))
            .FirstOrDefault();
    }

    private static IReadOnlyList<Curve> BooleanUnionBatched(IReadOnlyList<Curve> curves, double tolerance, int batchSize = 32)
    {
        if (curves.Count <= 1) return curves;
        var work = curves.ToList();
        var reduced = new List<Curve>();
        for (var index = 0; index < work.Count; index += batchSize)
        {
            var chunk = work.Skip(index).Take(batchSize).ToArray();
            var union = TryUnion(chunk, tolerance);
            reduced.AddRange(union.Count > 0 ? union : chunk);
        }

        work = reduced;
        for (var pass = 0; pass < 8 && work.Count > 1; pass++)
        {
            var union = TryUnion(work, tolerance);
            if (union.Count > 0) return union;
            if (work.Count <= batchSize) break;
            var next = new List<Curve>();
            for (var index = 0; index < work.Count; index += batchSize)
            {
                var chunk = work.Skip(index).Take(batchSize).ToArray();
                var partial = TryUnion(chunk, tolerance);
                next.AddRange(partial.Count > 0 ? partial : chunk);
            }
            if (next.Count >= work.Count) break;
            work = next;
        }

        return work;
    }

    private static IReadOnlyList<Curve> TryUnion(IEnumerable<Curve> curves, double tolerance)
    {
        try
        {
            return Curve.CreateBooleanUnion(curves, tolerance) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Curve? LargestClosed(IEnumerable<Curve> curves) => curves
        .Where(curve => curve is not null && curve.IsClosed)
        .OrderByDescending(curve => Math.Abs(Area(curve)))
        .FirstOrDefault();

    private static double Area(Curve curve) => AreaMassProperties.Compute(curve)?.Area ?? 0.0;

    private static bool IntersectsOrContains(Curve envelope, Curve obstacle, double tolerance)
    {
        var intersections = Intersection.CurveCurve(envelope, obstacle, tolerance, tolerance);
        if (intersections is { Count: > 0 }) return true;
        if (!obstacle.IsClosed) return false;
        var plane = PlaneAt(envelope.PointAtStart.Z);
        return envelope.Contains(obstacle.PointAtStart, plane, tolerance) != PointContainment.Outside ||
               obstacle.Contains(envelope.PointAtStart, plane, tolerance) != PointContainment.Outside;
    }

    private static IReadOnlyList<Point2> SampleCurve(Curve curve, UnitSystem modelUnits, double spacingMetres)
    {
        var metresPerModelUnit = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        var count = Math.Max(4, (int)Math.Ceiling(curve.GetLength() * metresPerModelUnit / spacingMetres));
        var parameters = curve.DivideByCount(count, includeEnds: true) ?? [curve.Domain.T0, curve.Domain.T1];
        return parameters.Select(parameter => curve.PointAt(parameter))
            .Select(point => new Point2(point.X * metresPerModelUnit, point.Y * metresPerModelUnit))
            .ToArray();
    }

    private static Point3d ToPoint3d(Point2 pointMetres, double elevationModel, UnitSystem modelUnits)
    {
        var modelUnitsPerMetre = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        return new Point3d(pointMetres.X * modelUnitsPerMetre, pointMetres.Y * modelUnitsPerMetre, elevationModel);
    }

    private static Plane PlaneAt(double elevationModel) => new(new Point3d(0.0, 0.0, elevationModel), Vector3d.ZAxis);
}
