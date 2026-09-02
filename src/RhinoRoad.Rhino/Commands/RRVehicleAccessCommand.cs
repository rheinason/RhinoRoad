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
        var storedManoeuvre = preselectedPath is null
            ? null
            : ManoeuvreStore.Read(preselectedPath.Object(), document.ModelUnitSystem);
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
        IReadOnlyList<RouteSample> route;
        Manoeuvre? manoeuvre = null;
        Guid sourceId;

        if (storedManoeuvre is not null)
        {
            // A control curve was selected: rebuild from its waypoints, honouring any grip edit,
            // and replace the output that curve produced last time rather than stacking a new set
            // beside it. The vehicle and mode come from the dialog, so the same route can be re-run
            // against a different vehicle by selecting the curve and changing the preset.
            manoeuvre = storedManoeuvre;
            var replay = PursuitDriver.Replay(vehicle, drivingMode, manoeuvre);
            route = replay.Samples;
            sourceId = ManoeuvreStore.SourceId(preselectedPath!.Object());
            if (!replay.Arrived)
            {
                RhinoApp.WriteLine(
                    "Some waypoints could not be reached by this vehicle in this mode; the route " +
                    "stops short of them.");
            }
        }
        else if (settings.Source == PathSourceKind.ExistingCurve)
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
            sourceId = objectReference.ObjectId;
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
            var interactiveResult = InteractiveRouteBuilder.TryBuild(
                document, vehicle, drivingMode, out route, out _, out manoeuvre);
            if (interactiveResult != Result.Success) return interactiveResult;

            // A fresh identity, stored on the control curve, so the next run against that curve
            // replaces this output instead of drawing a second copy over it.
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
            manoeuvre,
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
