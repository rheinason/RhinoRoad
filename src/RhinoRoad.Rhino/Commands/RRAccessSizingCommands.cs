using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

[Guid("7A31EED6-D37A-4F37-8E19-5B5CF8ED5B07")]
public sealed class RRCombineVehicleAccessCommand : Command
{
    public override string EnglishName => "RRCombineRoads";
    protected override Result RunCommand(RhinoDoc document, RunMode mode) => AccessSizingCommands.Run(document, false);
}

[Guid("C31FC773-A505-4621-A576-2D02FD3DA932")]
public sealed class RRMeasureVehicleAccessCommand : Command
{
    public override string EnglishName => "RRMeasureRoad";
    protected override Result RunCommand(RhinoDoc document, RunMode mode) => AccessSizingCommands.Run(document, true);
}

internal static class AccessSizingCommands
{
    public static Result Run(RhinoDoc document, bool section)
    {
        using var pick = new GetObject();
        pick.SetCommandPrompt(section ? "Select roads or a combined footprint to measure" : "Select roads to combine (separate movements)");
        pick.GeometryFilter = ObjectType.AnyObject;
        pick.SubObjectSelect = false;
        pick.GetMultiple(1, 0);
        if (pick.CommandResult() != Result.Success) return pick.CommandResult();
        try
        {
            var ids = AccessDerivedService.SourceIds(document, pick.Objects().Select(o => o.Object()));
            if (!section && ids.Length < 2)
                throw new InvalidOperationException("Select at least two different saved journeys to combine.");
            Point2? start = null;
            Point2? end = null;
            if (section)
            {
                using var first = new GetPoint();
                first.SetCommandPrompt("Section start outside the clearance footprint (World XY)");
                if (first.Get() != GetResult.Point) return Result.Cancel;
                using var second = new GetPoint();
                second.SetCommandPrompt("Section end beyond the clearance footprint");
                second.SetBasePoint(first.Point(), true);
                second.DrawLineFromPoint(first.Point(), true);
                if (second.Get() != GetResult.Point) return Result.Cancel;
                var metres = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);
                start = new Point2(first.Point().X * metres, first.Point().Y * metres);
                end = new Point2(second.Point().X * metres, second.Point().Y * metres);
            }
            AccessDerivedService.Create(document, ids, start, end);
            return Result.Success;
        }
        catch (Exception error)
        {
            RhinoApp.WriteLine($"Sizing stopped: {error.Message} Previous output is unchanged.");
            return Result.Failure;
        }
    }
}
