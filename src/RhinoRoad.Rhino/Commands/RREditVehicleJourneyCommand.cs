using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

/// <summary>
/// Opens an edit session on a saved journey. The command itself is deliberately thin: a running
/// command cannot receive grip drags, so everything that used to sit behind its option prompt now
/// lives in the modeless palette, which can stay open while the road is dragged into shape.
/// </summary>
[Guid("C3697BC4-5D35-46E9-B422-034B17D2C476")]
public sealed class RREditVehicleJourneyCommand : Command
{
    public override string EnglishName => "RREditRoad";

    protected override Result RunCommand(RhinoDoc document, RunMode mode) => Execute(document, mode);

    internal static Result Execute(RhinoDoc document, RunMode mode)
    {
        var target = Selected(document) ?? Pick(document);
        if (target is null) return Result.Cancel;
        var source = AccessDefinitionStore.ResolveSource(document, target);
        if (source is null || !AccessDefinitionStore.TryRead(source, out var saved, out _) || saved is null)
        {
            RhinoApp.WriteLine("That object is not linked to a saved RhinoRoad road. Use Road to create one.");
            return Result.Failure;
        }
        if (saved.SourceKind != PathSourceKind.Interactive || saved.Manoeuvre is null)
        {
            RhinoApp.WriteLine(
                "This road follows a curve you supplied. Edit that curve with Rhino's own tools, then run RRUpdateRoad to rerun the checks.");
            return Result.Failure;
        }
        if (source.Geometry is not Curve curve)
        {
            RhinoApp.WriteLine("The saved control line is missing or is not a curve.");
            return Result.Failure;
        }
        if (mode == RunMode.Scripted)
        {
            // A script has no palette to click, so the scripted contract stays the old one: turn the
            // grips on and leave RRUpdateRoad to commit.
            document.Objects.UnselectAll();
            document.Objects.Select(source.Id);
            source.GripsOn = true;
            document.Views.Redraw();
            return Result.Success;
        }
        return JourneyEditForm.Start(document, source, saved, curve);
    }

    private static RhinoObject? Selected(RhinoDoc document)
    {
        var selected = document.Objects.GetSelectedObjects(includeLights: false, includeGrips: false).Take(2).ToArray();
        return selected.Length == 1 ? selected[0] : null;
    }

    private static RhinoObject? Pick(RhinoDoc document)
    {
        using var getter = new GetObject();
        getter.SetCommandPrompt("Select road to edit");
        getter.GeometryFilter = ObjectType.AnyObject;
        getter.SubObjectSelect = false;
        return getter.Get() == global::Rhino.Input.GetResult.Object ? getter.Object(0).Object() : null;
    }
}
