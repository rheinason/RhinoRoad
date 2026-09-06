using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

internal sealed record PreparedVehicleAccess(
    VehicleAccessDefinition Definition,
    RhinoObject Source,
    IReadOnlyList<RouteSample> Route,
    VehicleAccessResult Analysis,
    RhinoAnalysisGeometry Geometry,
    IReadOnlyList<AnalysisViolation> Violations,
    IReadOnlyList<Curve> Obstacles,
    IReadOnlyList<Curve> AllowedBoundaries);

internal static class VehicleAccessRunService
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    public static bool TryPrepareUpdate(
        RhinoDoc document,
        RhinoObject selected,
        out PreparedVehicleAccess? prepared,
        out string error,
        VehicleAccessDefinition? configuredDefinition = null)
    {
        prepared = null;
        var source = AccessDefinitionStore.ResolveSource(document, selected);
        if (source is null)
        {
            error = "The selected object is not linked to a saved RhinoRoad access analysis. Use Road to configure it.";
            return false;
        }
        if (configuredDefinition is null &&
            (!AccessDefinitionStore.TryRead(source, out configuredDefinition, out var readError) || configuredDefinition is null))
        {
            error = $"The saved access definition cannot be read: {readError} Use Road to configure it.";
            return false;
        }
        var stored = configuredDefinition!;

        try
        {
            var requiredReferenceIds = (stored.CheckObstacles ? stored.ObstacleObjectIds : Array.Empty<Guid>())
                .Concat(stored.CheckAllowedArea ? stored.AllowedBoundaryObjectIds : Array.Empty<Guid>());
            var missing = requiredReferenceIds
                .Where(id => document.Objects.FindId(id) is null)
                .Distinct()
                .ToArray();
            if (missing.Length > 0)
            {
                error = $"Update stopped. Missing referenced object{(missing.Length == 1 ? string.Empty : "s")}: " +
                        string.Join(", ", missing.Select(id => id.ToString("D"))) +
                        ". The previous result is unchanged; use Road to Configure.";
                return false;
            }

            var vehicle = Catalog.Get(stored.VehicleId);
            if (!vehicle.DrivingModes.TryGetValue(stored.ModeId, out var mode))
                throw new InvalidDataException($"Driving mode {stored.ModeId} no longer exists for {stored.VehicleId}.");

            IReadOnlyList<RouteSample> route;
            ManoeuvreDefinition? manoeuvre = stored.Manoeuvre;
            if (stored.SourceKind == PathSourceKind.Interactive)
            {
                if (source.Geometry is not Curve controlCurve || manoeuvre is null)
                    throw new InvalidDataException("The editable control route is missing or is not a curve.");
                manoeuvre = ManoeuvreControlReconciler.Reconcile(
                    manoeuvre,
                    AccessDefinitionStore.PolylineVertices(controlCurve, document.ModelUnitSystem));
                route = ManoeuvreReplayService.Replay(vehicle, mode, manoeuvre).Samples;
            }
            else
            {
                if (source.Geometry is not Curve path)
                    throw new InvalidDataException("The saved source path is missing or is not a curve.");
                route = RhinoRouteSampler.Sample(path, document.ModelUnitSystem, stored.ExistingCurveDirection);
            }

            var obstacles = stored.CheckObstacles
                ? ResolveCurves(document, stored.ObstacleObjectIds)
                : Array.Empty<Curve>();
            var boundaries = stored.CheckAllowedArea
                ? ResolveCurves(document, stored.AllowedBoundaryObjectIds)
                : Array.Empty<Curve>();
            var analysis = new VehicleAccessAnalyzer().Analyze(
                vehicle,
                mode,
                route,
                stored.CheckMaximumGrade ? stored.MaximumGradePercent / 100.0 : null);
            var createFixedEdges = stored.RoadEdgeMethod is RoadEdgeMethod.Both or RoadEdgeMethod.FixedWidth;
            var geometry = RhinoGeometryBuilder.Build(
                analysis,
                route,
                document,
                stored.ClearanceMetres,
                stored.LeftWidthMetres,
                stored.RightWidthMetres,
                createFixedEdges,
                stored.FootprintMode == FootprintMode.AtInterval ? stored.FootprintIntervalMetres : 0.0,
                stored.FootprintMode == FootprintMode.EndsOnly);
            var clearance = RhinoGeometryBuilder.CheckClearance(
                geometry,
                createFixedEdges ? geometry.FixedRoadBoundary : null,
                obstacles,
                boundaries,
                stored.ClearanceMetres,
                document.ModelUnitSystem,
                document.ModelAbsoluteTolerance);
            analysis.MinimumClearanceMetres = clearance.MinimumClearanceMetres;
            var violations = analysis.Violations.Concat(clearance.Violations).ToArray();
            var analysisId = Guid.NewGuid().ToString("N");
            var updated = stored with
            {
                Manoeuvre = manoeuvre,
                SourceFingerprint = AccessDefinitionStore.Fingerprint(source),
                ReferenceFingerprints = requiredReferenceIds.Distinct()
                    .ToDictionary(id => id, id => AccessDefinitionStore.Fingerprint(document.Objects.FindId(id)!.Geometry)),
                AnalysisId = analysisId
            };
            prepared = new PreparedVehicleAccess(updated, source, route, analysis, geometry, violations, obstacles, boundaries);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"Update stopped: {exception.Message} The previous result is unchanged; use Road to Configure.";
            return false;
        }
    }

    public static IReadOnlyList<Guid> Commit(
        RhinoDoc document,
        PreparedVehicleAccess prepared,
        bool replaceExisting = true)
    {
        var previousAttributes = prepared.Source.Attributes.Duplicate();
        if (!AccessDefinitionStore.Write(document, prepared.Source.Id, prepared.Definition))
            throw new InvalidOperationException("The editable source metadata could not be updated.");
        IReadOnlyList<Guid> ids;
        try
        {
            ids = RhinoOutputWriter.Bake(
                document,
                prepared.Geometry,
                prepared.Analysis,
                prepared.Violations,
                prepared.Definition.StableSourceId,
                prepared.Definition.AnalysisId,
                prepared.Definition.ClearanceMetres,
                prepared.Definition.LeftWidthMetres,
                prepared.Definition.RightWidthMetres,
                replaceExisting);
        }
        catch
        {
            document.Objects.ModifyAttributes(prepared.Source.Id, previousAttributes, quiet: true);
            throw;
        }
        try
        {
            VehicleAccessReviewService.Remember(document, prepared);
            VehicleAccessInspectorService.Refresh(document, prepared.Definition.StableSourceId);
            AccessDerivedService.RefreshForSource(document, prepared.Definition.StableSourceId);
        }
        catch (Exception exception)
        {
            // Diagnostics are explanatory output. A display-only failure must never turn a valid,
            // safely committed swept path into a failed update.
            RhinoApp.WriteLine($"Vehicle access was updated, but diagnostics could not refresh: {exception.Message}");
        }
        return ids;
    }

    private static IReadOnlyList<Curve> ResolveCurves(RhinoDoc document, IEnumerable<Guid> ids) => ids
        .Select(id => document.Objects.FindId(id)?.Geometry as Curve)
        .Where(curve => curve is not null)
        .Select(curve => curve!.DuplicateCurve())
        .ToArray();
}
