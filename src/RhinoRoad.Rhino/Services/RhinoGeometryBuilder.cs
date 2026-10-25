using Rhino;
using Rhino.Geometry;
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

    /// <summary>Axle-centre track of each towed unit, nearest first. Empty for a rigid vehicle.</summary>
    public IReadOnlyList<Curve> TowedAxleTracks { get; init; } = [];

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

    /// <summary>The poses the envelopes were swept from; the site check locates conflicts by them.</summary>
    public IReadOnlyList<VehiclePose> Poses { get; init; } = [];

    /// <summary>
    /// False when the swept footprints did not collapse into a single closed region. The largest
    /// surviving fragment is still returned, so callers that compare envelopes MUST check this --
    /// a fragment measures as a plausible but wrong envelope.
    /// </summary>
    public bool BodyEnvelopeMerged { get; init; } = true;

    /// <summary>
    /// The last site check run against this geometry: conflict areas, their stations and the
    /// per-pose margins. Recorded by <see cref="RhinoGeometryBuilder.CheckClearance"/>, the way
    /// the callers record its minimum clearance on the analysis, so the bake can draw the conflicts
    /// and the inspector can draw the margins without measuring the site a second time.
    /// </summary>
    public SiteFitOutcome? SiteFit { get; set; }
}

internal sealed record ClearanceOutcome(
    double? MinimumClearanceMetres,
    IReadOnlyList<AnalysisViolation> Violations);

internal static class RhinoGeometryBuilder
{
    /// <summary>Builds in the active document's units.</summary>
    public static RhinoAnalysisGeometry Build(
        VehicleAccessResult result,
        IReadOnlyList<RouteSample> route,
        RhinoDoc document,
        double clearanceMetres,
        double leftRoadWidthMetres,
        double rightRoadWidthMetres,
        bool createFixedEdges,
        double footprintIntervalMetres = 0.0,
        bool footprintEndsOnly = false) =>
        Build(
            result,
            route,
            document.ModelUnitSystem,
            clearanceMetres,
            leftRoadWidthMetres,
            rightRoadWidthMetres,
            createFixedEdges,
            footprintIntervalMetres,
            footprintEndsOnly);

    /// <summary>
    /// Builds against explicit model units. Certification uses this so its verdicts do not depend on
    /// whatever document the command happens to run in. No tolerance is needed: the envelopes are
    /// Clipper polygons at a fixed precision, and Rhino only turns their loops into curves.
    /// </summary>
    public static RhinoAnalysisGeometry Build(
        VehicleAccessResult result,
        IReadOnlyList<RouteSample> route,
        UnitSystem modelUnits,
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
        var warnings = new List<string>();

        var rearTrack = TrackCurve(result.RearAxleTrackMetres, elevationModel, modelUnitsPerMetre);
        var frontTrack = TrackCurve(result.FrontAxleTrackMetres, elevationModel, modelUnitsPerMetre);
        var towedTracks = result.TowedAxleTracksMetres
            .Where(track => track.Count > 1)
            .Select(track => TrackCurve(track, elevationModel, modelUnitsPerMetre))
            .ToArray();
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
            Poses = result.Poses,
            TowedAxleTracks = towedTracks,
            Footprints = (footprintEndsOnly
                    ? PoseSampler.EndsOnly(result.Poses)
                    : PoseSampler.AtStationInterval(result.Poses, footprintIntervalMetres))
                .SelectMany(pose => FootprintCurves(pose, elevationModel, modelUnitsPerMetre))
                .ToArray()
        };
    }

    /// <summary>
    /// Checks the sweep against the selected site curves. The geometry is all in Core
    /// (<see cref="SiteFitAnalyzer"/>); this only turns the curves into metre polylines.
    /// </summary>
    /// <remarks>The full outcome is recorded on <paramref name="geometry"/> as <see cref="RhinoAnalysisGeometry.SiteFit"/>.</remarks>
    public static ClearanceOutcome CheckClearance(
        RhinoAnalysisGeometry geometry,
        Curve? fixedRoadBoundary,
        IEnumerable<Curve> obstacles,
        IEnumerable<Curve> allowedBoundaries,
        double clearanceMetres,
        UnitSystem modelUnits)
    {
        var envelopes = geometry.BodyEnvelope is not null && geometry.ClearanceEnvelope is not null;
        var road = fixedRoadBoundary is null
            ? null
            : geometry.RoadCorridorRegion ?? new SweptRegion(CurveOutline.Points(fixedRoadBoundary, modelUnits), []);
        var outcome = SiteFitAnalyzer.Check(
            geometry.Poses,
            envelopes ? geometry.BodyRegion : null,
            envelopes ? geometry.ClearanceRegion : null,
            obstacles.Select(curve => new SiteCurve(CurveOutline.Points(curve, modelUnits), curve.IsClosed)).ToArray(),
            allowedBoundaries.Where(curve => curve.IsClosed).Select(curve => (IReadOnlyList<Point2>)CurveOutline.Points(curve, modelUnits)).ToArray(),
            road,
            clearanceMetres);
        geometry.SiteFit = outcome;
        return new ClearanceOutcome(outcome.MinimumClearanceMetres, outcome.Violations);
    }

    private static Curve TrackCurve(IReadOnlyList<Point2> points, double elevationModel, double modelUnitsPerMetre) =>
        new PolylineCurve(points.Select(point => new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, elevationModel)));

    /// <summary>
    /// Closed outlines of one vehicle pose, in model units — one per rigid unit, so an articulated
    /// stamp reads as a tractor and its trailer folded against each other rather than as a lorry.
    /// </summary>
    private static IReadOnlyList<Curve> FootprintCurves(VehiclePose pose, double elevationModel, double modelUnitsPerMetre) =>
        pose.OccupiedOutlinesWorldMetres.Select(outline =>
        {
            var points = outline
                .Select(point => new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, elevationModel))
                .ToList();
            points.Add(points[0]);
            return (Curve)new PolylineCurve(points);
        }).ToArray();

    /// <summary>Open model-unit polyline from a Core point list in metres.</summary>
    internal static Curve OpenCurve(IReadOnlyList<Point2> points, double elevationModel, double modelUnitsPerMetre) =>
        new PolylineCurve(points.Select(point =>
            new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, elevationModel)));

    /// <summary>Closed model-unit curve from a Core loop in metres.</summary>
    internal static Curve LoopCurve(IReadOnlyList<Point2> loop, double elevationModel, double modelUnitsPerMetre)
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
}
