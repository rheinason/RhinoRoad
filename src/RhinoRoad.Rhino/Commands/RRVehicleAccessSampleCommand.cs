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
        var vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
        var drivingMode = vehicle.DrivingModes["B"];
        var route = RhinoRouteSampler.Sample(routeCurve, document.ModelUnitSystem, TravelDirection.Forward, 0.10);
        var result = new VehicleAccessAnalyzer().Analyze(vehicle, drivingMode, route, maximumAbsoluteGrade: 0.08);
        var geometry = RhinoGeometryBuilder.Build(result, route, document, 0.30, 3.25, 3.25, createFixedEdges: true);
        var sourceId = Guid.NewGuid();
        var analysisId = Guid.NewGuid().ToString("N");
        var sourceObjectId = AccessDefinitionStore.AddSampleSource(document, routeCurve);
        var source = document.Objects.FindId(sourceObjectId);
        if (source is null) return Result.Failure;
        var definition = new VehicleAccessDefinition(
            VehicleAccessDefinition.CurrentSchemaVersion,
            sourceId,
            sourceObjectId,
            PathSourceKind.ExistingCurve,
            vehicle.Id,
            drivingMode.Id,
            TravelDirection.Forward,
            null,
            0.30,
            RoadEdgeMethod.Both,
            3.25,
            3.25,
            true,
            8.0,
            false,
            false,
            FootprintMode.None,
            2.0,
            false,
            false,
            [],
            [],
            new Dictionary<Guid, string>(),
            AccessDefinitionStore.Fingerprint(source),
            analysisId);
        var prepared = new PreparedVehicleAccess(definition, source, route, result, geometry, result.Violations, [], []);
        var baked = VehicleAccessRunService.Commit(document, prepared, replaceExisting: false);
        RhinoApp.WriteLine($"RhinoRoad sample created {baked.Count} objects using PV mode B.");
        return baked.Count > 0 ? Result.Success : Result.Failure;
    }
}
