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

namespace RhinoRoad.Rhino.Commands;

internal enum PathSourceKind
{
    ExistingCurve,
    Interactive
}

internal enum RoadEdgeMethod
{
    Both,
    MinimumFootprint,
    FixedWidth,
    None
}

[Guid("14708FCE-5B4E-484C-A540-6A0F3861B12A")]
public sealed class RRVehicleAccessCommand : Command
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    public override string EnglishName => "RRVehicleAccess";

    protected override Result RunCommand(RhinoDoc document, RunMode mode)
    {
        var settings = new CommandSettings();
        var configure = settings.Configure(Catalog);
        if (configure != Result.Success) return configure;

        var vehicle = Catalog.Get(settings.VehicleId);
        var drivingMode = vehicle.DrivingModes[settings.ModeId];
        IReadOnlyList<RouteSample> route;
        Curve? interactivePath = null;
        Guid sourceId;

        if (settings.Source == PathSourceKind.ExistingCurve)
        {
            using var getter = new GetObject();
            getter.SetCommandPrompt("Select rear-axle midpoint path curve");
            getter.GeometryFilter = ObjectType.Curve;
            getter.SubObjectSelect = false;
            getter.Get();
            if (getter.CommandResult() != Result.Success) return getter.CommandResult();
            var objectReference = getter.Object(0);
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
            var interactiveResult = InteractiveRouteBuilder.TryBuild(document, vehicle, drivingMode, out route, out interactivePath);
            if (interactiveResult != Result.Success) return interactiveResult;
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

        var createFixedEdges = settings.EdgeMethod is RoadEdgeMethod.Both or RoadEdgeMethod.FixedWidth;
        var geometry = RhinoGeometryBuilder.Build(
            analysis,
            route,
            document,
            settings.ClearanceMetres,
            settings.LeftWidthMetres,
            settings.RightWidthMetres,
            createFixedEdges);
        var clearance = RhinoGeometryBuilder.CheckClearance(
            geometry.BodyEnvelope,
            geometry.ClearanceEnvelope,
            createFixedEdges ? geometry.FixedRoadBoundary : null,
            obstacles,
            boundaries,
            settings.ClearanceMetres,
            document.ModelUnitSystem,
            document.ModelAbsoluteTolerance);
        analysis.MinimumClearanceMetres = clearance.MinimumClearanceMetres;
        var allViolations = analysis.Violations.Concat(clearance.Violations).ToArray();

        ShowReport(analysis, allViolations, geometry.Warnings);
        if (!PreviewAndConfirm(document, geometry, allViolations)) return Result.Cancel;

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

    private sealed class CommandSettings
    {
        public PathSourceKind Source { get; private set; } = PathSourceKind.ExistingCurve;
        public string VehicleId { get; private set; } = "PV";
        public string ModeId { get; private set; } = "A";
        public TravelDirection Direction { get; private set; } = TravelDirection.Forward;
        public RoadEdgeMethod EdgeMethod { get; private set; } = RoadEdgeMethod.Both;
        public double ClearanceMetres { get; private set; } = 0.30;
        public double LeftWidthMetres { get; private set; } = 3.25;
        public double RightWidthMetres { get; private set; } = 3.25;
        public bool CheckMaximumGrade { get; private set; }
        public double MaximumGradePercent { get; private set; } = 8.0;
        public bool CheckObstacles { get; private set; } = true;
        public bool CheckAllowedArea { get; private set; }
        public bool ReplaceExisting { get; private set; } = true;

        public Result Configure(VehicleCatalog catalog)
        {
            var vehicleIds = catalog.Vehicles.Select(vehicle => vehicle.Id).ToArray();
            var clearance = new OptionDouble(ClearanceMetres, setLowerLimit: true, limit: 0.0);
            var leftWidth = new OptionDouble(LeftWidthMetres, setLowerLimit: true, limit: 0.0);
            var rightWidth = new OptionDouble(RightWidthMetres, setLowerLimit: true, limit: 0.0);
            var maximumGrade = new OptionDouble(MaximumGradePercent, setLowerLimit: true, limit: 0.0);
            var gradeToggle = new OptionToggle(CheckMaximumGrade, "ReportOnly", "CheckLimit");
            var obstacleToggle = new OptionToggle(CheckObstacles, "No", "Yes");
            var boundaryToggle = new OptionToggle(CheckAllowedArea, "No", "Yes");
            var replaceToggle = new OptionToggle(ReplaceExisting, "No", "Yes");

            using var getter = new GetOption();
            getter.SetCommandPrompt("Configure vehicle access; Enter to continue");
            getter.AcceptNothing(true);
            var sourceIndex = getter.AddOptionList("Source", ["ExistingCurve", "Interactive"], (int)Source);
            var vehicleIndex = getter.AddOptionList("Vehicle", vehicleIds, Array.IndexOf(vehicleIds, VehicleId));
            var modeIndex = getter.AddOptionList("Mode", ["A", "B"], ModeId == "A" ? 0 : 1);
            var directionIndex = getter.AddOptionList("Direction", ["Forward", "Reverse"], Direction == TravelDirection.Forward ? 0 : 1);
            var edgeIndex = getter.AddOptionList("RoadEdges", ["Both", "MinimumFootprint", "FixedWidth", "None"], (int)EdgeMethod);
            getter.AddOptionDouble("Clearance", ref clearance);
            getter.AddOptionDouble("LeftWidth", ref leftWidth);
            getter.AddOptionDouble("RightWidth", ref rightWidth);
            getter.AddOptionToggle("Grade", ref gradeToggle);
            getter.AddOptionDouble("MaxGradePercent", ref maximumGrade);
            getter.AddOptionToggle("Obstacles", ref obstacleToggle);
            getter.AddOptionToggle("AllowedArea", ref boundaryToggle);
            getter.AddOptionToggle("ReplaceExisting", ref replaceToggle);

            while (true)
            {
                var result = getter.Get();
                if (result == GetResult.Cancel) return Result.Cancel;
                if (result == GetResult.Nothing) break;
                if (result != GetResult.Option) continue;
                var option = getter.Option();
                if (option.Index == sourceIndex) Source = (PathSourceKind)option.CurrentListOptionIndex;
                else if (option.Index == vehicleIndex) VehicleId = vehicleIds[option.CurrentListOptionIndex];
                else if (option.Index == modeIndex) ModeId = option.CurrentListOptionIndex == 0 ? "A" : "B";
                else if (option.Index == directionIndex) Direction = option.CurrentListOptionIndex == 0 ? TravelDirection.Forward : TravelDirection.Reverse;
                else if (option.Index == edgeIndex) EdgeMethod = (RoadEdgeMethod)option.CurrentListOptionIndex;
            }

            ClearanceMetres = clearance.CurrentValue;
            LeftWidthMetres = leftWidth.CurrentValue;
            RightWidthMetres = rightWidth.CurrentValue;
            CheckMaximumGrade = gradeToggle.CurrentValue;
            MaximumGradePercent = maximumGrade.CurrentValue;
            CheckObstacles = obstacleToggle.CurrentValue;
            CheckAllowedArea = boundaryToggle.CurrentValue;
            ReplaceExisting = replaceToggle.CurrentValue;
            return Result.Success;
        }
    }
}
