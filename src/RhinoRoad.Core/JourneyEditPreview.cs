namespace RhinoRoad.Core;

public sealed record JourneyEditPreviewResult(
    ManoeuvreDefinition Definition,
    ReplayedManoeuvre Journey,
    SweptRegionResult Body,
    SweptRegionResult Clearance,
    IReadOnlyList<RouteSample> PreviousRoute,
    int UnchangedSampleCount);

/// <summary>Pure preview of grip positions. Never modifies the saved driving intent.</summary>
public static class JourneyEditPreview
{
    public static bool PositionsMatch(ManoeuvreDefinition saved, IReadOnlyList<Point3> vertices) =>
        vertices.Count == saved.Controls.Count + 1 &&
        SamePoint(vertices[0], saved.StartPositionMetres) &&
        saved.Controls.Select((c, i) => SamePoint(c.PositionMetres, vertices[i + 1])).All(same => same);

    private static bool SamePoint(Point3 a, Point3 b) =>
        a.XY.DistanceTo(b.XY) <= 1e-7 && Math.Abs(a.Z - b.Z) <= 1e-7;

    public static JourneyEditPreviewResult Build(VehicleDefinition vehicle, DrivingModeDefinition mode,
        ManoeuvreDefinition saved, IReadOnlyList<Point3> vertices, double clearance,
        IReadOnlyList<RouteSample>? previousRoute = null)
    {
        var edited = ManoeuvreControlReconciler.Reconcile(saved, vertices);
        var journey = ManoeuvreReplayService.Replay(vehicle, mode, edited);
        previousRoute ??= ManoeuvreReplayService.Replay(vehicle, mode, saved).Samples;
        var unchanged = 0;
        while (unchanged < previousRoute.Count && unchanged < journey.Samples.Count &&
               previousRoute[unchanged] == journey.Samples[unchanged]) unchanged++;
        var analysis = new VehicleAccessAnalyzer().Analyze(vehicle, mode, journey.Samples);
        var body = SweptRegionBuilder.FromPoses(analysis.Poses);
        var inflated = body.IsSuccess ? SweptRegionBuilder.Inflate(body.Region!, clearance) : body;
        return new JourneyEditPreviewResult(edited, journey, body, inflated, previousRoute, unchanged);
    }
}
