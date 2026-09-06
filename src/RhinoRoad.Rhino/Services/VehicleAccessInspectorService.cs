using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace RhinoRoad.Rhino.Services;

internal static class VehicleAccessInspectorService
{
    private static readonly Dictionary<uint, VehicleAccessInspectorForm> OpenForms = new();

    static VehicleAccessInspectorService() => RhinoDoc.CloseDocument += (_, args) =>
    {
        if (!OpenForms.Remove(args.Document.RuntimeSerialNumber, out var form)) return;
        if (form.Visible) form.Close();
    };

    public static Result Start(RhinoDoc document)
    {
        var selected = document.Objects.GetSelectedObjects(false, false).Take(2).ToArray();
        var target = selected.Length == 1 ? selected[0] : Pick();
        if (target is null) return Result.Cancel;
        if (AccessDerivedService.Anchor(document, target) is { } anchor)
        {
            RhinoApp.WriteLine(AccessDerivedService.Describe(document, anchor));
            return Result.Success;
        }
        if (AccessDefinitionStore.ResolveSource(document, target) is null)
        {
            RhinoApp.WriteLine("That object is not linked to a saved RhinoRoad vehicle-access analysis.");
            return Result.Failure;
        }

        if (OpenForms.TryGetValue(document.RuntimeSerialNumber, out var existing))
        {
            existing.SetObject(target.Id);
            existing.BringToFront();
            return Result.Success;
        }

        var form = new VehicleAccessInspectorForm(document, target.Id);
        OpenForms[document.RuntimeSerialNumber] = form;
        form.Closed += (_, _) => OpenForms.Remove(document.RuntimeSerialNumber);
        Application.Instance.AsyncInvoke(() =>
        {
            try
            {
                form.Show();
                form.PositionOverActiveView();
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine($"RhinoRoad could not open Inspect Road: {error.Message}");
                OpenForms.Remove(document.RuntimeSerialNumber);
                form.Close();
            }
        });
        return Result.Success;
    }

    public static void Refresh(RhinoDoc document, Guid sourceId)
    {
        if (OpenForms.TryGetValue(document.RuntimeSerialNumber, out var form)) form.RefreshSource(sourceId);
    }

    private static RhinoObject? Pick()
    {
        using var getter = new GetObject();
        getter.SetCommandPrompt("Select road or sizing result to inspect");
        getter.GeometryFilter = ObjectType.AnyObject;
        getter.SubObjectSelect = false;
        return getter.Get() == GetResult.Object ? getter.Object(0).Object() : null;
    }
}
