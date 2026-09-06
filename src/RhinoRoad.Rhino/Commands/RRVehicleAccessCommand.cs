using System.Drawing;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Services;
using RhinoRoad.Rhino.UI;

namespace RhinoRoad.Rhino.Commands;

[Guid("14708FCE-5B4E-484C-A540-6A0F3861B12A")]
public sealed class RRVehicleAccessCommand : Command
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    public override string EnglishName => "RRRoad";

    protected override Result RunCommand(RhinoDoc document, RunMode mode) => Execute(document, mode);

    internal static Result Execute(RhinoDoc document, RunMode mode)
    {
        // Settings persist for the session so a second run starts where the last one left off.
        _settings.Reconcile(Catalog);
        // Read before the dialog: showing a modal window can clear the document selection.
        var preselectedObject = PreselectedObject(document);
        var storedSource = preselectedObject is null ? null : AccessDefinitionStore.ResolveSource(document, preselectedObject);
        VehicleAccessDefinition? storedDefinition = null;
        if (storedSource is not null && AccessDefinitionStore.TryRead(storedSource, out var readDefinition, out _))
        {
            storedDefinition = readDefinition;
            if (storedDefinition is not null) Apply(storedDefinition, _settings);
        }
        if (mode == RunMode.Interactive)
        {
            if (!VehicleAccessDialog.Show(document, Catalog, _settings, storedDefinition is not null)) return Result.Cancel;
        }
        else
        {
            // A scripted run cannot raise a modal dialog, so the option prompt remains the
            // scripting interface for -Road.
            var configure = CommandLinePrompt.Configure(_settings, Catalog);
            if (configure != Result.Success) return configure;
        }

        var settings = _settings;

        // A saved source is the Configure-and-rerun path: the dialog starts from its complete setup,
        // and the accepted changes are replayed without asking for the path again.
        if (storedSource is not null && storedDefinition is not null && settings.Source == storedDefinition.SourceKind)
        {
            var configured = WithSettings(storedDefinition, settings);
            var obstacleReferencesNeedSelection = configured.ObstacleObjectIds.Count == 0 ||
                configured.ObstacleObjectIds.Any(id => document.Objects.FindId(id) is null);
            if (settings.CheckObstacles && (settings.ReselectReferences || obstacleReferencesNeedSelection))
            {
                var selected = SelectCurves("Select replacement obstacle curves; Enter for none");
                if (selected is null) return Result.Cancel;
                configured = configured with { ObstacleObjectIds = selected.ObjectIds };
            }
            var boundaryReferencesNeedSelection = configured.AllowedBoundaryObjectIds.Count == 0 ||
                configured.AllowedBoundaryObjectIds.Any(id => document.Objects.FindId(id) is null);
            if (settings.CheckAllowedArea && (settings.ReselectReferences || boundaryReferencesNeedSelection))
            {
                var selected = SelectCurves("Select replacement closed allowed-area boundaries; Enter for none", closedOnly: true);
                if (selected is null) return Result.Cancel;
                configured = configured with { AllowedBoundaryObjectIds = selected.ObjectIds };
            }
            configured = configured with
            {
                ReferenceFingerprints = configured.ObstacleObjectIds.Concat(configured.AllowedBoundaryObjectIds)
                    .Distinct().Where(id => document.Objects.FindId(id) is not null)
                    .ToDictionary(id => id, id => AccessDefinitionStore.Fingerprint(document.Objects.FindId(id)!.Geometry))
            };
            settings.ReselectReferences = false;
            if (!VehicleAccessRunService.TryPrepareUpdate(document, storedSource, out var configuredRun, out var error, configured) || configuredRun is null)
            {
                RhinoApp.WriteLine(error);
                return Result.Failure;
            }
            ShowReport(
                configuredRun.Analysis,
                configuredRun.Violations,
                configuredRun.Geometry.Warnings,
                configuredRun.Route,
                configuredRun.Definition.SourceKind, configuredRun.Obstacles.Count + configuredRun.AllowedBoundaries.Count > 0);
            if (settings.PreviewBeforeBaking &&
                !PreviewAndConfirm(document, configuredRun.Geometry, configuredRun.Violations))
                return Result.Cancel;
            try
            {
                var refreshed = VehicleAccessRunService.Commit(document, configuredRun, settings.ReplaceExisting);
                RhinoApp.WriteLine($"RhinoRoad refreshed {refreshed.Count} objects. Analysis {configuredRun.Definition.AnalysisId[..8]}.");
                return Result.Success;
            }
            catch (Exception exception)
            {
                RhinoApp.WriteLine($"Vehicle access update failed: {exception.Message}");
                return Result.Failure;
            }
        }

        var vehicle = Catalog.Get(settings.VehicleId);
        var drivingMode = vehicle.DrivingModes[settings.ModeId];
        // Two ways to get a route. Driving legs interactively builds one the vehicle can certainly
        // drive, because each leg starts from where the vehicle actually is. A selected curve is
        // taken as the rear-axle path directly, and the checks report where the vehicle could not
        // actually follow it -- so a line with corners no vehicle can turn is reported as such
        // rather than quietly redrawn into something else.
        IReadOnlyList<RouteSample> route;
        Curve? controlCurve = null;
        ManoeuvreDefinition? manoeuvre = null;
        RhinoObject sourceObject;
        Guid sourceId;

        if (settings.Source == PathSourceKind.ExistingCurve)
        {
            ObjRef objectReference;
            if (preselectedObject is not null && preselectedObject.Geometry is Curve)
            {
                objectReference = new ObjRef(preselectedObject);
                document.Objects.UnselectAll();
                document.Views.Redraw();
            }
            else
            {
                using var getter = new GetObject();
                getter.SetCommandPrompt("Select rear-axle midpoint path curve");
                getter.GeometryFilter = ObjectType.Curve;
                getter.SubObjectSelect = false;
                getter.Get();
                if (getter.CommandResult() != Result.Success) return getter.CommandResult();
                objectReference = getter.Object(0);
            }

            var curve = objectReference.Curve();
            if (curve is null) return Result.Failure;

            // Re-running against a line RhinoRoad already analysed replaces that line's output
            // instead of stacking another set beside it.
            sourceId = IntentCurveIdentity(objectReference.Object());
            sourceObject = objectReference.Object();
            try
            {
                route = RhinoRouteSampler.Sample(curve, document.ModelUnitSystem, settings.Direction);
            }
            catch (Exception exception)
            {
                RhinoApp.WriteLine($"Path sampling failed: {exception.Message}");
                return Result.Failure;
            }
        }
        else
        {
            var interactive = InteractiveRouteBuilder.TryBuild(
                document, vehicle, drivingMode, settings.Direction, settings.ClearanceMetres, out route, out manoeuvre, out controlCurve);
            if (interactive != Result.Success) return interactive;
            sourceId = Guid.NewGuid();
            sourceObject = null!;
        }

        var obstacleSelection = settings.CheckObstacles ? SelectCurves("Select obstacle curves; Enter to skip") : CurveSelection.Empty;
        if (obstacleSelection is null) return Result.Cancel;
        var boundarySelection = settings.CheckAllowedArea ? SelectCurves("Select closed allowed-area boundaries; Enter to skip", closedOnly: true) : CurveSelection.Empty;
        if (boundarySelection is null) return Result.Cancel;
        settings.ReselectReferences = false;

        VehicleAccessResult analysis;
        try
        {
            analysis = new VehicleAccessAnalyzer().Analyze(
                vehicle,
                drivingMode,
                route,
                settings.CheckMaximumGrade ? settings.MaximumGradePercent / 100.0 : null);
        }
        catch (Exception exception)
        {
            RhinoApp.WriteLine($"Vehicle analysis failed: {exception.Message}");
            return Result.Failure;
        }

        var createFixedEdges = settings.CreateFixedEdges;
        var geometry = RhinoGeometryBuilder.Build(
            analysis,
            route,
            document,
            settings.ClearanceMetres,
            settings.LeftWidthMetres,
            settings.RightWidthMetres,
            createFixedEdges,
            settings.Footprints == FootprintMode.AtInterval ? settings.FootprintIntervalMetres : 0.0,
            settings.Footprints == FootprintMode.EndsOnly);
        var clearance = RhinoGeometryBuilder.CheckClearance(
            geometry,
            createFixedEdges ? geometry.FixedRoadBoundary : null,
            obstacleSelection.Curves,
            boundarySelection.Curves,
            settings.ClearanceMetres,
            document.ModelUnitSystem,
            document.ModelAbsoluteTolerance);
        analysis.MinimumClearanceMetres = clearance.MinimumClearanceMetres;
        var allViolations = analysis.Violations.Concat(clearance.Violations).ToArray();

        ShowReport(analysis, allViolations, geometry.Warnings, route, settings.Source, obstacleSelection.Curves.Count + boundarySelection.Curves.Count > 0);
        if (settings.PreviewBeforeBaking && !PreviewAndConfirm(document, geometry, allViolations))
        {
            return Result.Cancel;
        }

        var analysisId = Guid.NewGuid().ToString("N");
        if (settings.Source == PathSourceKind.Interactive)
        {
            if (controlCurve is null || manoeuvre is null) return Result.Failure;
            var sourceObjectId = AccessDefinitionStore.AddInteractiveSource(document, controlCurve, sourceId);
            sourceObject = document.Objects.FindId(sourceObjectId)
                ?? throw new InvalidOperationException("The editable control route could not be created.");
        }

        var definition = Definition(
            document,
            settings,
            sourceId,
            sourceObject,
            manoeuvre,
            obstacleSelection.ObjectIds,
            boundarySelection.ObjectIds,
            analysisId);
        var prepared = new PreparedVehicleAccess(definition, sourceObject, route, analysis, geometry, allViolations,
            obstacleSelection.Curves, boundarySelection.Curves);
        IReadOnlyList<Guid> baked;
        try
        {
            baked = VehicleAccessRunService.Commit(document, prepared, settings.ReplaceExisting);
        }
        catch (Exception exception)
        {
            RhinoApp.WriteLine($"Vehicle access output failed: {exception.Message}");
            return Result.Failure;
        }
        RhinoApp.WriteLine($"RhinoRoad created {baked.Count} objects. Analysis {analysisId[..8]}.");
        return Result.Success;

    }

    /// <summary>
    /// The single curve selected before the command started, if there is exactly one.
    /// </summary>
    /// <remarks>
    /// Rhino's own commands honour a pre-selection, and the common case here is rerunning against
    /// a path already highlighted from the last run. Exactly one, because two selected curves give
    /// no way to say which is the route.
    /// </remarks>
    private static RhinoObject? PreselectedObject(RhinoDoc document)
    {
        var selected = document.Objects
            .GetSelectedObjects(includeLights: false, includeGrips: false)
            .Take(2)
            .ToArray();
        return selected.Length == 1 ? selected[0] : null;
    }

    /// <summary>
    /// The identity a line's output is replaced through on a re-run.
    /// </summary>
    /// <remarks>
    /// A line that RhinoRoad baked carries the id its outputs were tagged with; re-running against
    /// it must reuse that id or the previous envelopes are orphaned rather than replaced. Any other
    /// curve is identified by itself.
    /// </remarks>
    private static Guid IntentCurveIdentity(RhinoObject rhinoObject) =>
        Guid.TryParse(rhinoObject.Attributes.GetUserString("RhinoRoad.SourceId"), out var stored)
            ? stored
            : rhinoObject.Id;

    private sealed record CurveSelection(IReadOnlyList<Guid> ObjectIds, IReadOnlyList<Curve> Curves)
    {
        public static readonly CurveSelection Empty = new(Array.Empty<Guid>(), Array.Empty<Curve>());
    }

    private static CurveSelection? SelectCurves(string prompt, bool closedOnly = false)
    {
        using var getter = new GetObject();
        getter.SetCommandPrompt(prompt);
        getter.GeometryFilter = ObjectType.Curve;
        getter.SubObjectSelect = false;
        getter.AcceptNothing(true);
        getter.GetMultiple(1, 0);
        if (getter.CommandResult() == Result.Cancel) return null;
        if (getter.Result() == GetResult.Nothing) return CurveSelection.Empty;
        var accepted = Enumerable.Range(0, getter.ObjectCount)
            .Select(index => getter.Object(index))
            .Where(reference => reference.Curve() is { } curve && (!closedOnly || curve.IsClosed))
            .ToArray();
        if (closedOnly && accepted.Length < getter.ObjectCount)
        {
            RhinoApp.WriteLine("Open curves were ignored; allowed-area boundaries must be closed.");
        }
        return new CurveSelection(
            accepted.Select(reference => reference.ObjectId).ToArray(),
            accepted.Select(reference => reference.Curve()!.DuplicateCurve()).ToArray());
    }

    private static bool PreviewAndConfirm(
        RhinoDoc document,
        RhinoAnalysisGeometry geometry,
        IReadOnlyList<AnalysisViolation> violations)
    {
        var display = new CustomDisplay(true);
        try
        {
            display.AddCurve(geometry.RearAxleTrack, Color.Blue, 2);
            display.AddCurve(geometry.FrontAxleTrack, Color.CornflowerBlue, 2);
            if (geometry.BodyEnvelope is not null) display.AddCurve(geometry.BodyEnvelope, Color.DarkOrange, 2);
            if (geometry.ClearanceEnvelope is not null) display.AddCurve(geometry.ClearanceEnvelope, Color.OrangeRed, 2);
            foreach (var edge in geometry.FixedRoadEdges) display.AddCurve(edge, Color.ForestGreen, 2);
            var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
            foreach (var violation in violations)
            {
                display.AddPoint(new Point3d(
                    violation.PositionMetres.X * scale,
                    violation.PositionMetres.Y * scale,
                    violation.PositionMetres.Z * scale), Color.Red, PointStyle.RoundActivePoint, 5);
            }
            document.Views.Redraw();
            using var confirmation = new GetOption();
            confirmation.SetCommandPrompt("Preview: Enter to bake; Esc to cancel");
            confirmation.AcceptNothing(true);
            var result = confirmation.Get();
            return result == GetResult.Nothing;
        }
        finally
        {
            display.Enabled = false;
            display.Dispose();
            document.Views.Redraw();
        }
    }

    private static void ShowReport(
        VehicleAccessResult analysis,
        IReadOnlyList<AnalysisViolation> violations,
        IReadOnlyList<string> warnings,
        IReadOnlyList<RouteSample> route,
        PathSourceKind source, bool siteChecked)
    {
        var mode = analysis.DrivingMode;
        RhinoApp.WriteLine(string.Empty);
        RhinoApp.WriteLine($"RHINOROAD VEHICLE ACCESS — {MovementFit.Describe(warnings.Count == 0, violations.Count == 0, siteChecked)}");
        RhinoApp.WriteLine($"Vehicle: {analysis.Vehicle.Name} ({analysis.Vehicle.ValidationStatus})");
        RhinoApp.WriteLine($"Mode: {mode.Name}, {mode.SpeedKilometresPerHour:0.#} km/h");
        RhinoApp.WriteLine($"Max wheel angle: {Degrees(analysis.MaximumSteeringAngleRadians):0.0}° / {mode.MaximumWheelAngleDegrees:0.0}°");
        RhinoApp.WriteLine($"Max steering rate: {Degrees(analysis.MaximumSteeringRateRadiansPerSecond):0.0}°/s / {Degrees(mode.MaximumSteeringRateRadiansPerSecond):0.0}°/s");
        RhinoApp.WriteLine(source == PathSourceKind.Interactive ? "Grade unavailable — planar journey" : $"Max absolute grade: {analysis.MaximumAbsoluteGrade * 100.0:0.00}%");
        if (analysis.MinimumClearanceMetres.HasValue) RhinoApp.WriteLine($"Minimum obstacle clearance: {analysis.MinimumClearanceMetres.Value:0.00} m");

        // A selected alignment of arcs joined onto tangents asks the wheel to move instantly at
        // every join, and saying so once per join tells the designer nothing they can act on. What
        // they can act on is how long each transition has to be, and whether the stretch it has to
        // fit into is long enough. A driven route is drivable by construction and needs none of it.
        if (source == PathSourceKind.ExistingCurve)
        {
            ReportTransitions(analysis, route);
        }

        foreach (var warning in warnings) RhinoApp.WriteLine($"WARNING: {warning}");
        foreach (var violation in violations) RhinoApp.WriteLine($"{violation.Kind} @ {violation.StationMetres:0.00} m: {violation.Message}");
        RhinoApp.WriteLine(string.Empty);
    }

    /// <summary>
    /// What the drawn alignment costs the steering wheel, stretch by stretch.
    /// </summary>
    private static void ReportTransitions(VehicleAccessResult analysis, IReadOnlyList<RouteSample> route)
    {
        var runs = AlignmentTransitions.Runs(analysis.Vehicle, analysis.DrivingMode, route);
        if (runs.Count < 2) return;

        var tight = runs.Where(run => !run.Fits).ToArray();
        if (tight.Length == 0)
        {
            var worst = runs.MaxBy(run => run.RequiredTransitionMetres);
            RhinoApp.WriteLine(
                $"Transitions: every stretch has room. The tightest is {worst!.RequiredTransitionMetres:0.00} m " +
                $"needed in {worst.LengthMetres:0.00} m at {worst.StartStationMetres:0.0} m.");
            return;
        }

        RhinoApp.WriteLine(
            $"Transitions: {tight.Length} stretch(es) are too short for {analysis.Vehicle.Id} in mode " +
            $"{analysis.DrivingMode.Id}. The wheel cannot reach the curvature drawn within them:");
        foreach (var run in tight)
        {
            var shape = double.IsPositiveInfinity(run.RadiusMetres)
                ? "straight"
                : $"R={run.RadiusMetres:0.0} m";
            RhinoApp.WriteLine(
                $"  {run.StartStationMetres,8:0.0} to {run.EndStationMetres,8:0.0} m ({shape}): " +
                $"{run.LengthMetres:0.00} m available, {run.RequiredTransitionMetres:0.00} m needed.");
        }

        RhinoApp.WriteLine(
            "  Lengthen those stretches, ease the radius either side of them, or use a slower " +
            "driving mode: the wheel moves at the same rate but the vehicle covers less ground.");
    }

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    private static void Apply(VehicleAccessDefinition definition, VehicleAccessSettings settings)
    {
        settings.Source = definition.SourceKind;
        settings.VehicleId = definition.VehicleId;
        settings.ModeId = definition.ModeId;
        settings.Direction = definition.SourceKind == PathSourceKind.Interactive
            ? definition.Manoeuvre?.StartDirection ?? definition.ExistingCurveDirection
            : definition.ExistingCurveDirection;
        settings.EdgeMethod = definition.RoadEdgeMethod;
        settings.ClearanceMetres = definition.ClearanceMetres;
        settings.LeftWidthMetres = definition.LeftWidthMetres;
        settings.RightWidthMetres = definition.RightWidthMetres;
        settings.CheckMaximumGrade = definition.CheckMaximumGrade;
        settings.MaximumGradePercent = definition.MaximumGradePercent;
        settings.CheckObstacles = definition.CheckObstacles;
        settings.CheckAllowedArea = definition.CheckAllowedArea;
        settings.Footprints = definition.FootprintMode;
        settings.FootprintIntervalMetres = definition.FootprintIntervalMetres;
        settings.PreviewBeforeBaking = definition.PreviewBeforeBaking;
        settings.ReplaceExisting = definition.ReplaceExisting;
    }

    private static VehicleAccessDefinition WithSettings(
        VehicleAccessDefinition definition,
        VehicleAccessSettings settings)
    {
        var manoeuvre = definition.Manoeuvre is null
            ? null
            : ManoeuvreControlReconciler.WithStartDirection(definition.Manoeuvre, settings.Direction);
        return definition with
        {
            VehicleId = settings.VehicleId,
            ModeId = settings.ModeId,
            ExistingCurveDirection = settings.Direction,
            Manoeuvre = manoeuvre,
            ClearanceMetres = settings.ClearanceMetres,
            RoadEdgeMethod = settings.EdgeMethod,
            LeftWidthMetres = settings.LeftWidthMetres,
            RightWidthMetres = settings.RightWidthMetres,
            CheckMaximumGrade = settings.CheckMaximumGrade,
            MaximumGradePercent = settings.MaximumGradePercent,
            CheckObstacles = settings.CheckObstacles,
            CheckAllowedArea = settings.CheckAllowedArea,
            ObstacleObjectIds = settings.CheckObstacles ? definition.ObstacleObjectIds : Array.Empty<Guid>(),
            AllowedBoundaryObjectIds = settings.CheckAllowedArea ? definition.AllowedBoundaryObjectIds : Array.Empty<Guid>(),
            FootprintMode = settings.Footprints,
            FootprintIntervalMetres = settings.FootprintIntervalMetres,
            PreviewBeforeBaking = settings.PreviewBeforeBaking,
            ReplaceExisting = settings.ReplaceExisting
        };
    }

    private static VehicleAccessDefinition Definition(
        RhinoDoc document,
        VehicleAccessSettings settings,
        Guid sourceId,
        RhinoObject source,
        ManoeuvreDefinition? manoeuvre,
        IReadOnlyList<Guid> obstacles,
        IReadOnlyList<Guid> boundaries,
        string analysisId) => new(
        VehicleAccessDefinition.CurrentSchemaVersion,
        sourceId,
        source.Id,
        settings.Source,
        settings.VehicleId,
        settings.ModeId,
        settings.Direction,
        manoeuvre,
        settings.ClearanceMetres,
        settings.EdgeMethod,
        settings.LeftWidthMetres,
        settings.RightWidthMetres,
        settings.CheckMaximumGrade,
        settings.MaximumGradePercent,
        settings.CheckObstacles,
        settings.CheckAllowedArea,
        settings.Footprints,
        settings.FootprintIntervalMetres,
        settings.PreviewBeforeBaking,
        settings.ReplaceExisting,
        obstacles,
        boundaries,
        obstacles.Concat(boundaries).Distinct().ToDictionary(
            id => id,
            id => AccessDefinitionStore.Fingerprint(document.Objects.FindId(id)!.Geometry)),
        AccessDefinitionStore.Fingerprint(source),
        analysisId);

    private static readonly VehicleAccessSettings _settings = new();

    /// <summary>
    /// The scripted interface. <c>-Road</c> in a macro or script cannot raise a modal
    /// dialog, so the option prompt stays — it configures the same settings object the dialog does.
    /// </summary>
    private static class CommandLinePrompt
    {
        public static Result Configure(VehicleAccessSettings settings, VehicleCatalog catalog)
        {
            var vehicleIds = catalog.Vehicles.Select(vehicle => vehicle.Id).ToArray();
            var modeIds = catalog.Get(settings.VehicleId).DrivingModes.Keys.OrderBy(key => key).ToArray();
            var clearance = new OptionDouble(settings.ClearanceMetres, setLowerLimit: true, limit: 0.0);
            var leftWidth = new OptionDouble(settings.LeftWidthMetres, setLowerLimit: true, limit: 0.0);
            var rightWidth = new OptionDouble(settings.RightWidthMetres, setLowerLimit: true, limit: 0.0);
            var maximumGrade = new OptionDouble(settings.MaximumGradePercent, setLowerLimit: true, limit: 0.0);
            var footprintInterval = new OptionDouble(settings.FootprintIntervalMetres, setLowerLimit: true, limit: 0.0);
            var gradeToggle = new OptionToggle(settings.CheckMaximumGrade, "ReportOnly", "CheckLimit");
            var obstacleToggle = new OptionToggle(settings.CheckObstacles, "No", "Yes");
            var boundaryToggle = new OptionToggle(settings.CheckAllowedArea, "No", "Yes");
            var reselectToggle = new OptionToggle(settings.ReselectReferences, "No", "Yes");
            var replaceToggle = new OptionToggle(settings.ReplaceExisting, "No", "Yes");
            var previewToggle = new OptionToggle(settings.PreviewBeforeBaking, "No", "Yes");

            using var getter = new GetOption();
            getter.SetCommandPrompt("Road settings; Enter to continue");
            getter.AcceptNothing(true);
            var sourceIndex = getter.AddOptionList("Source", ["ExistingCurve", "Interactive"], (int)settings.Source);
            var vehicleIndex = getter.AddOptionList("Vehicle", vehicleIds, Math.Max(0, Array.IndexOf(vehicleIds, settings.VehicleId)));
            var modeIndex = getter.AddOptionList("Mode", modeIds, Math.Max(0, Array.IndexOf(modeIds, settings.ModeId)));
            var directionIndex = getter.AddOptionList("Direction", ["Forward", "Reverse"], settings.Direction == TravelDirection.Forward ? 0 : 1);
            var edgeIndex = getter.AddOptionList("RoadEdges", Enum.GetNames<RoadEdgeMethod>(), (int)settings.EdgeMethod);
            getter.AddOptionDouble("Clearance", ref clearance);
            getter.AddOptionDouble("LeftWidth", ref leftWidth);
            getter.AddOptionDouble("RightWidth", ref rightWidth);
            getter.AddOptionToggle("Grade", ref gradeToggle);
            getter.AddOptionDouble("MaxGradePercent", ref maximumGrade);
            getter.AddOptionToggle("Obstacles", ref obstacleToggle);
            getter.AddOptionToggle("AllowedArea", ref boundaryToggle);
            getter.AddOptionToggle("ReselectReferences", ref reselectToggle);
            getter.AddOptionToggle("ReplaceExisting", ref replaceToggle);
            getter.AddOptionToggle("Preview", ref previewToggle);
            getter.AddOptionDouble("FootprintInterval", ref footprintInterval);

            while (true)
            {
                var result = getter.Get();
                if (result == GetResult.Cancel) return Result.Cancel;
                if (result == GetResult.Nothing) break;
                if (result != GetResult.Option) continue;
                var option = getter.Option();
                if (option.Index == sourceIndex) settings.Source = (PathSourceKind)option.CurrentListOptionIndex;
                else if (option.Index == vehicleIndex) settings.VehicleId = vehicleIds[option.CurrentListOptionIndex];
                else if (option.Index == modeIndex) settings.ModeId = modeIds[option.CurrentListOptionIndex];
                else if (option.Index == directionIndex) settings.Direction = option.CurrentListOptionIndex == 0 ? TravelDirection.Forward : TravelDirection.Reverse;
                else if (option.Index == edgeIndex) settings.EdgeMethod = (RoadEdgeMethod)option.CurrentListOptionIndex;
            }

            settings.ClearanceMetres = clearance.CurrentValue;
            settings.LeftWidthMetres = leftWidth.CurrentValue;
            settings.RightWidthMetres = rightWidth.CurrentValue;
            settings.MaximumGradePercent = maximumGrade.CurrentValue;
            settings.FootprintIntervalMetres = footprintInterval.CurrentValue;
            settings.CheckMaximumGrade = gradeToggle.CurrentValue;
            settings.CheckObstacles = obstacleToggle.CurrentValue;
            settings.CheckAllowedArea = boundaryToggle.CurrentValue;
            settings.ReselectReferences = reselectToggle.CurrentValue;
            settings.ReplaceExisting = replaceToggle.CurrentValue;
            settings.PreviewBeforeBaking = previewToggle.CurrentValue;
            settings.Reconcile(catalog);
            return Result.Success;
        }
    }
}
