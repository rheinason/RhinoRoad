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
    IReadOnlyList<string> Warnings)
{
    /// <summary>Vehicle outlines stamped along the route; empty when the interval is off.</summary>
    public IReadOnlyList<Curve> Footprints { get; init; } = [];

    /// <summary>Enclosed holes in the body envelope, e.g. the island of a roundabout circulation.</summary>
    public IReadOnlyList<Curve> BodyEnvelopeHoles { get; init; } = [];

    /// <summary>Enclosed holes in the clearance envelope.</summary>
    public IReadOnlyList<Curve> ClearanceEnvelopeHoles { get; init; } = [];

    /// <summary>
    /// The regions behind the envelope curves, in metres. Clearance checks read these directly:
    /// re-sampling the baked curve and calling Rhino's point-in-curve test per sample costs
    /// thousands of interop calls on a long route, for vertices Core already has.
    /// </summary>
    public SweptRegion? BodyRegion { get; init; }

    public SweptRegion? ClearanceRegion { get; init; }

    /// <summary>The fixed-width corridor, once self-overlap on tight inside curves is resolved.</summary>
    public SweptRegion? RoadCorridorRegion { get; init; }

    /// <summary>
    /// False when the swept footprints did not collapse into a single closed region. The largest
    /// surviving fragment is still returned, so callers that compare envelopes MUST check this --
    /// a fragment measures as a plausible but wrong envelope.
    /// </summary>
    public bool BodyEnvelopeMerged { get; init; } = true;
}

internal sealed record ClearanceOutcome(
    double? MinimumClearanceMetres,
    IReadOnlyList<AnalysisViolation> Violations);

internal static class RhinoGeometryBuilder
{
    /// <summary>Builds using the active document's units and tolerance.</summary>
    public static RhinoAnalysisGeometry Build(
        VehicleAccessResult result,
        IReadOnlyList<RouteSample> route,
        RhinoDoc document,
        double clearanceMetres,
        double leftRoadWidthMetres,
        double rightRoadWidthMetres,
        bool createFixedEdges,
        double footprintIntervalMetres = 0.0,
        bool footprintEndsOnly = false)
    {
        var modelUnitsPerMetre = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        return Build(
            result,
            route,
            document.ModelUnitSystem,
            Math.Max(document.ModelAbsoluteTolerance, 0.001 * modelUnitsPerMetre),
            clearanceMetres,
            leftRoadWidthMetres,
            rightRoadWidthMetres,
            createFixedEdges,
            footprintIntervalMetres,
            footprintEndsOnly);
    }

    /// <summary>
    /// Builds against an explicit units/tolerance context. Certification uses this so its verdicts
    /// do not depend on whatever document the command happens to run in.
    /// </summary>
    public static RhinoAnalysisGeometry Build(
        VehicleAccessResult result,
        IReadOnlyList<RouteSample> route,
        UnitSystem modelUnits,
        double toleranceModelUnits,
        double clearanceMetres,
        double leftRoadWidthMetres,
        double rightRoadWidthMetres,
        bool createFixedEdges,
        double footprintIntervalMetres = 0.0,
        bool footprintEndsOnly = false)
    {
        var modelUnitsPerMetre = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var elevationMetres = route[0].PositionMetres.Z;
        var elevationModel = elevationMetres * modelUnitsPerMetre;
        var tolerance = toleranceModelUnits;
        var warnings = new List<string>();

        var rearTrack = TrackCurve(result.RearAxleTrackMetres, elevationModel, modelUnitsPerMetre);
        var frontTrack = TrackCurve(result.FrontAxleTrackMetres, elevationModel, modelUnitsPerMetre);
        // Envelope construction is a polygon union in Core: deterministic, tolerance independent,
        // and able to carry holes. Rhino only converts the resulting loops back into curves.
        var body = SweptRegionBuilder.FromPoses(result.Poses);
        if (!body.IsSuccess) warnings.Add($"Swept-envelope union failed. {body.Message}");
        var bodyEnvelope = body.Region is null ? null : LoopCurve(body.Region.OuterBoundary, elevationModel, modelUnitsPerMetre);
        var bodyHoles = HoleCurves(body.Region, elevationModel, modelUnitsPerMetre);

        var clearance = clearanceMetres <= 1e-9 || body.Region is null
            ? body
            : SweptRegionBuilder.Inflate(body.Region, clearanceMetres);
        if (!clearance.IsSuccess && body.Region is not null && clearanceMetres > 1e-9)
        {
            warnings.Add($"Clearance-envelope offset failed. {clearance.Message}");
        }
        var clearanceEnvelope = clearance.Region is null ? null : LoopCurve(clearance.Region.OuterBoundary, elevationModel, modelUnitsPerMetre);
        var clearanceHoles = HoleCurves(clearance.Region, elevationModel, modelUnitsPerMetre);

        // Corridor offsets are computed in Core from the per-sample headings; Rhino's curve offset
        // is superlinear in vertex count and dominated the post-path step on long routes.
        var corridor = createFixedEdges
            ? RoadCorridorBuilder.Build(route, leftRoadWidthMetres, rightRoadWidthMetres)
            : null;
        var fixedEdges = corridor is null || corridor.LeftEdge.Count < 2
            ? []
            : new Curve[]
            {
                OpenCurve(corridor.LeftEdge, elevationModel, modelUnitsPerMetre),
                OpenCurve(corridor.RightEdge, elevationModel, modelUnitsPerMetre)
            };
        var roadBoundary = corridor?.Region is null
            ? null
            : LoopCurve(corridor.Region.OuterBoundary, elevationModel, modelUnitsPerMetre);
        if (createFixedEdges && roadBoundary is null) warnings.Add("Fixed-width road offsets could not be generated.");

        return new RhinoAnalysisGeometry(
            rearTrack,
            frontTrack,
            bodyEnvelope,
            clearanceEnvelope,
            fixedEdges,
            roadBoundary,
            warnings)
        {
            BodyEnvelopeMerged = body.IsSuccess,
            BodyEnvelopeHoles = bodyHoles,
            ClearanceEnvelopeHoles = clearanceHoles,
            BodyRegion = body.Region,
            ClearanceRegion = clearance.Region,
            RoadCorridorRegion = corridor?.Region,
            Footprints = (footprintEndsOnly
                    ? PoseSampler.EndsOnly(result.Poses)
                    : PoseSampler.AtStationInterval(result.Poses, footprintIntervalMetres))
                .Select(pose => FootprintCurve(pose, elevationModel, modelUnitsPerMetre))
                .ToArray()
        };
    }

    public static ClearanceOutcome CheckClearance(
        RhinoAnalysisGeometry geometry,
        Curve? fixedRoadBoundary,
        IEnumerable<Curve> obstacles,
        IEnumerable<Curve> allowedBoundaries,
        double clearanceMetres,
        UnitSystem modelUnits,
        double tolerance)
    {
        var violations = new List<AnalysisViolation>();
        var bodyEnvelope = geometry.BodyEnvelope;
        var clearanceEnvelope = geometry.ClearanceEnvelope;
        if (bodyEnvelope is null || clearanceEnvelope is null || geometry.BodyRegion is null || geometry.ClearanceRegion is null)
        {
            return new ClearanceOutcome(null, violations);
        }

        var metresPerModelUnit = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        var minimum = double.PositiveInfinity;
        var bodyPoints = geometry.BodyRegion.OuterBoundary;
        var clearancePoints = geometry.ClearanceRegion.OuterBoundary;

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

        var closedBoundaries = allowedBoundaries.Where(curve => curve.IsClosed).ToArray();
        var boundaryPolygons = closedBoundaries.Select(boundary => SampleCurve(boundary, modelUnits, 0.10)).ToArray();
        foreach (var boundaryPoints in boundaryPolygons)
        {
            minimum = Math.Min(minimum, Geometry2D.MinimumDistance(bodyPoints, boundaryPoints));
        }
        if (boundaryPolygons.Length > 0 &&
            TryFindOutside(clearancePoints, point => boundaryPolygons.Any(polygon => Geometry2D.Contains(polygon, point)), out var outsidePoint))
        {
            violations.Add(new AnalysisViolation(
                ViolationKind.OutsideAllowedArea,
                0.0,
                new Point3(outsidePoint.X, outsidePoint.Y, clearanceEnvelope.PointAtStart.Z * metresPerModelUnit),
                "The clearance envelope extends outside the allowed area."));
        }

        if (fixedRoadBoundary is not null)
        {
            var roadPolygon = geometry.RoadCorridorRegion?.OuterBoundary ?? SampleCurve(fixedRoadBoundary, modelUnits, 0.10);
            if (TryFindOutside(clearancePoints, candidate => Geometry2D.Contains(roadPolygon, candidate), out var point))
            {
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

    /// <summary>Closed outline of one vehicle pose, in model units.</summary>
    private static Curve FootprintCurve(VehiclePose pose, double elevationModel, double modelUnitsPerMetre)
    {
        var points = pose.BodyOutlineWorldMetres
            .Select(point => new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, elevationModel))
            .ToList();
        points.Add(points[0]);
        return new PolylineCurve(points);
    }

    /// <summary>Closed model-unit curve from a Core loop in metres.</summary>
    /// <summary>
    /// First envelope vertex that fails <paramref name="isInside"/>. One violation is all the report
    /// needs, so this stops at the first failure rather than classifying every vertex.
    /// </summary>
    private static bool TryFindOutside(
        IReadOnlyList<Point2> envelope,
        Func<Point2, bool> isInside,
        out Point2 outside)
    {
        foreach (var point in envelope)
        {
            if (isInside(point)) continue;
            outside = point;
            return true;
        }

        outside = default;
        return false;
    }

    /// <summary>Open model-unit polyline from a Core point list in metres.</summary>
    private static Curve OpenCurve(IReadOnlyList<Point2> points, double elevationModel, double modelUnitsPerMetre) =>
        new PolylineCurve(points.Select(point =>
            new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, elevationModel)));

    private static Curve LoopCurve(IReadOnlyList<Point2> loop, double elevationModel, double modelUnitsPerMetre)
    {
        var points = loop
            .Select(point => new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, elevationModel))
            .ToList();
        points.Add(points[0]);
        return new PolylineCurve(points);
    }

    private static IReadOnlyList<Curve> HoleCurves(SweptRegion? region, double elevationModel, double modelUnitsPerMetre) =>
        region is null
            ? []
            : region.Holes.Select(hole => LoopCurve(hole, elevationModel, modelUnitsPerMetre)).ToArray();


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
