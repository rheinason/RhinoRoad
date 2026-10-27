using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

[Guid("E144A148-8DD4-4141-AB6A-885F9CD6C56B")]
[CommandStyle(Style.Hidden)]
public sealed class RRVehicleAccessSampleCommand : Command
{
    public override string EnglishName => "RRVehicleAccessSample";

    protected override Result RunCommand(RhinoDoc document, RunMode mode)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var start = new Point3d(0.0, 0.0, 0.0);
        var middle = new Point3d(7.0710678119 * scale, 2.9289321881 * scale, 0.0);
        var end = new Point3d(10.0 * scale, 10.0 * scale, 0.0);
        var routeCurve = new ArcCurve(new Arc(start, middle, end));

        // The defaults RRRoad itself starts from (not the session's saved choices, so the sample is
        // repeatable). Geometry and bake options all come from these settings, so the sample shows
        // whatever the current output is: wheel-edge paths, footprints, the mirror allowance.
        var settings = new VehicleAccessSettings
        {
            Source = PathSourceKind.ExistingCurve,
            VehicleId = "PV",
            ModeId = "B",
            EdgeMethod = RoadEdgeMethod.Both
        };
        var vehicle = RhinoRoadCatalog.Current.Get(settings.VehicleId);
        var drivingMode = vehicle.DrivingModes[settings.ModeId];
        var route = RhinoRouteSampler.Sample(routeCurve, document.ModelUnitSystem, settings.Direction, 0.10);
        var result = new VehicleAccessAnalyzer().Analyze(
            settings.IncludeMirrorAllowance ? MirrorAllowance.Apply(vehicle) : vehicle,
            drivingMode,
            route,
            settings.CheckMaximumGrade ? settings.MaximumGradePercent / 100.0 : null);
        var geometry = RhinoGeometryBuilder.Build(
            result,
            route,
            document,
            settings.ClearanceMetres,
            settings.LeftWidthMetres,
            settings.RightWidthMetres,
            settings.CreateFixedEdges,
            settings.Footprints == FootprintMode.AtInterval ? settings.FootprintIntervalMetres : 0.0,
            settings.Footprints == FootprintMode.EndsOnly,
            settings.BakeWheelEdgePaths,
            settings.IncludeMirrorAllowance);
        var sourceId = Guid.NewGuid();
        var analysisId = Guid.NewGuid().ToString("N");
        var sourceObjectId = AccessDefinitionStore.AddSampleSource(document, routeCurve);
        var source = document.Objects.FindId(sourceObjectId);
        if (source is null) return Result.Failure;
        var definition = new VehicleAccessDefinition(
            VehicleAccessDefinition.CurrentSchemaVersion,
            sourceId,
            sourceObjectId,
            settings.Source,
            vehicle.Id,
            drivingMode.Id,
            settings.Direction,
            null,
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
            [],
            [],
            new Dictionary<Guid, string>(),
            AccessDefinitionStore.Fingerprint(source),
            analysisId,
            null,
            settings.BakeWheelEdgePaths,
            settings.IncludeMirrorAllowance);
        var prepared = new PreparedVehicleAccess(definition, source, route, result, geometry, result.Violations, [], []);
        var baked = VehicleAccessRunService.Commit(document, prepared, replaceExisting: false);
        RhinoApp.WriteLine($"RhinoRoad sample created {baked.Count} objects using {vehicle.Id} mode {drivingMode.Id}.");
        return baked.Count > 0 ? Result.Success : Result.Failure;
    }
}
