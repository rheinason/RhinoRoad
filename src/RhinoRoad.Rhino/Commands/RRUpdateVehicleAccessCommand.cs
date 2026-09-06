using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

[Guid("7EBE812C-DC74-4524-A657-D763640B1B37")]
public sealed class RRUpdateVehicleAccessCommand : Command
{
    public override string EnglishName => "RRUpdateRoad";

    protected override Result RunCommand(RhinoDoc document, RunMode mode) => Execute(document, mode);

    internal static Result Execute(RhinoDoc document, RunMode mode)
    {
        var selected = Selected(document) ?? Pick();
        if (selected is null) return Result.Cancel;
        return Update(document, selected);
    }

    internal static Result Update(RhinoDoc document, RhinoObject selected)
    {
        if (AccessDerivedService.Anchor(document, selected) is { } anchor)
        {
            try
            {
                AccessDerivedService.Update(document, anchor);
                return Result.Success;
            }
            catch (Exception sizingError)
            {
                RhinoApp.WriteLine($"Sizing update stopped: {sizingError.Message} Previous output is unchanged.");
                return Result.Failure;
            }
        }
        if (!VehicleAccessRunService.TryPrepareUpdate(document, selected, out var prepared, out var error) || prepared is null)
        {
            RhinoApp.WriteLine(error);
            return Result.Failure;
        }

        try
        {
            var ids = VehicleAccessRunService.Commit(document, prepared);
            RhinoApp.WriteLine($"RhinoRoad refreshed {ids.Count} objects. Analysis {prepared.Definition.AnalysisId[..8]}.");
            return Result.Success;
        }
        catch (Exception exception)
        {
            RhinoApp.WriteLine($"Update failed: {exception.Message}");
            return Result.Failure;
        }
    }

    private static RhinoObject? Selected(RhinoDoc document)
    {
        var selected = document.Objects.GetSelectedObjects(includeLights: false, includeGrips: false).Take(2).ToArray();
        return selected.Length == 1 ? selected[0] : null;
    }

    private static RhinoObject? Pick()
    {
        using var getter = new GetObject();
        getter.SetCommandPrompt("Select road or sizing result to update");
        getter.GeometryFilter = ObjectType.AnyObject;
        getter.SubObjectSelect = false;
        getter.Get();
        return getter.CommandResult() == Result.Success ? getter.Object(0).Object() : null;
    }
}

