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

    public override string EnglishName => "RRVehicleAccess";

    protected override Result RunCommand(RhinoDoc document, RunMode mode)
    {
        // Settings persist for the session so a second run starts where the last one left off.
        _settings.Reconcile(Catalog);
        // Read before the dialog: showing a modal window can clear the document selection.
        var preselectedPath = PreselectedCurve(document);
        if (mode == RunMode.Interactive)
        {
            if (!VehicleAccessDialog.Show(document, Catalog, _settings)) return Result.Cancel;
        }
        else
        {
            // A scripted run cannot raise a modal dialog, so the option prompt remains the
            // scripting interface for -RRVehicleAccess.
            var configure = CommandLinePrompt.Configure(_settings, Catalog);
            if (configure != Result.Success) return configure;
        }

        var settings = _settings;

        var vehicle = Catalog.Get(settings.VehicleId);
        var drivingMode = vehicle.DrivingModes[settings.ModeId];
        // Two ways to get a route. Driving legs interactively builds one the vehicle can certainly
        // drive, because each leg starts from where the vehicle actually is. A selected curve is
        // taken as the rear-axle path directly, and the checks report where the vehicle could not
        // actually follow it -- so a line with corners no vehicle can turn is reported as such
        // rather than quietly redrawn into something else.
        IReadOnlyList<RouteSample> route;
        Curve? interactivePath = null;
        Guid sourceId;

        if (settings.Source == PathSourceKind.ExistingCurve)
        {
            ObjRef objectReference;
            if (preselectedPath is not null)
            {
                objectReference = preselectedPath;
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
                document, vehicle, drivingMode, out route, out interactivePath);
            if (interactive != Result.Success) return interactive;
            sourceId = Guid.NewGuid();
        }

        var obstacles = settings.CheckObstacles ? SelectCurves("Select obstacle curves; Enter to skip") : [];
        if (obstacles is null) return Result.Cancel;
        var boundaries = settings.CheckAllowedArea ? SelectCurves("Select closed allowed-area boundaries; Enter to skip", closedOnly: true) : [];
        if (boundaries is null) return Result.Cancel;

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
            obstacles,
            boundaries,
            settings.ClearanceMetres,
            document.ModelUnitSystem,
            document.ModelAbsoluteTolerance);
        analysis.MinimumClearanceMetres = clearance.MinimumClearanceMetres;
        var allViolations = analysis.Violations.Concat(clearance.Violations).ToArray();

        ShowReport(analysis, allViolations, geometry.Warnings);
        if (settings.PreviewBeforeBaking && !PreviewAndConfirm(document, geometry, allViolations))
        {
            return Result.Cancel;
        }

        var analysisId = Guid.NewGuid().ToString("N");
        var baked = RhinoOutputWriter.Bake(
            document,
            geometry,

            interactivePath,
            analysis,
            allViolations,
            sourceId,
            analysisId,
            settings.ClearanceMetres,
            settings.LeftWidthMetres,
            settings.RightWidthMetres,
            settings.ReplaceExisting);
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
    private static ObjRef? PreselectedCurve(RhinoDoc document)
    {
        var selected = document.Objects
            .GetSelectedObjects(includeLights: false, includeGrips: false)
            .Where(candidate => candidate.Geometry is Curve)
            .Take(2)
            .ToArray();
        return selected.Length == 1 ? new ObjRef(selected[0]) : null;
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

    private static IReadOnlyList<Curve>? SelectCurves(string prompt, bool closedOnly = false)
    {
        using var getter = new GetObject();
        getter.SetCommandPrompt(prompt);
        getter.GeometryFilter = ObjectType.Curve;
        getter.SubObjectSelect = false;
        getter.AcceptNothing(true);
        getter.GetMultiple(1, 0);
        if (getter.CommandResult() == Result.Cancel) return null;
        if (getter.Result() == GetResult.Nothing) return [];
        var curves = Enumerable.Range(0, getter.ObjectCount)
            .Select(index => getter.Object(index).Curve())
            .Where(curve => curve is not null && (!closedOnly || curve.IsClosed))
            .Select(curve => curve!.DuplicateCurve())
            .ToArray();
        if (closedOnly && curves.Length < getter.ObjectCount)
        {
            RhinoApp.WriteLine("Open curves were ignored; allowed-area boundaries must be closed.");
        }
        return curves;
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
        IReadOnlyList<string> warnings)
    {
        var mode = analysis.DrivingMode;
        RhinoApp.WriteLine(string.Empty);
        RhinoApp.WriteLine($"RHINOROAD VEHICLE ACCESS — {(violations.Count == 0 ? "PASS" : "FAIL")}");
        RhinoApp.WriteLine($"Vehicle: {analysis.Vehicle.Name} ({analysis.Vehicle.ValidationStatus})");
        RhinoApp.WriteLine($"Mode: {mode.Name}, {mode.SpeedKilometresPerHour:0.#} km/h");
        RhinoApp.WriteLine($"Max wheel angle: {Degrees(analysis.MaximumSteeringAngleRadians):0.0}° / {mode.MaximumWheelAngleDegrees:0.0}°");
        RhinoApp.WriteLine($"Max steering rate: {Degrees(analysis.MaximumSteeringRateRadiansPerSecond):0.0}°/s / {Degrees(mode.MaximumSteeringRateRadiansPerSecond):0.0}°/s");
        RhinoApp.WriteLine($"Max absolute grade: {analysis.MaximumAbsoluteGrade * 100.0:0.00}%");
        if (analysis.MinimumClearanceMetres.HasValue) RhinoApp.WriteLine($"Minimum obstacle clearance: {analysis.MinimumClearanceMetres.Value:0.00} m");

        foreach (var warning in warnings) RhinoApp.WriteLine($"WARNING: {warning}");
        foreach (var violation in violations) RhinoApp.WriteLine($"{violation.Kind} @ {violation.StationMetres:0.00} m: {violation.Message}");
        RhinoApp.WriteLine(string.Empty);
    }

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    private static readonly VehicleAccessSettings _settings = new();

    /// <summary>
    /// The scripted interface. <c>-RRVehicleAccess</c> in a macro or script cannot raise a modal
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
            var replaceToggle = new OptionToggle(settings.ReplaceExisting, "No", "Yes");
            var previewToggle = new OptionToggle(settings.PreviewBeforeBaking, "No", "Yes");

            using var getter = new GetOption();
            getter.SetCommandPrompt("Configure vehicle access; Enter to continue");
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
            settings.ReplaceExisting = replaceToggle.CurrentValue;
            settings.PreviewBeforeBaking = previewToggle.CurrentValue;
            settings.Reconcile(catalog);
            return Result.Success;
        }
    }
}
