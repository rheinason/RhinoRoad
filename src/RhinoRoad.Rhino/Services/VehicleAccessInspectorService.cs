using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace RhinoRoad.Rhino.Services;

internal static class VehicleAccessInspectorService
{
    // The open inspector lives on the document's session, which closes it with the document.
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

        var session = RoadDocumentSession.For(document);
        if (session.InspectorForm is { } existing)
        {
            existing.SetObject(target.Id);
            existing.BringToFront();
            return Result.Success;
        }

        var form = new VehicleAccessInspectorForm(document, target.Id);
        session.InspectorForm = form;
        form.Closed += (_, _) => Forget(session, form);
        var serial = document.RuntimeSerialNumber;
        Application.Instance.AsyncInvoke(() =>
        {
            // The document can close between the command and this callback; the session has then
            // abandoned the inspector, which must not open over a document that is gone.
            if (form.Abandoned || RhinoDoc.FromRuntimeSerialNumber(serial) is null)
            {
                Forget(session, form);
                return;
            }
            try
            {
                form.Show();
                form.PositionOverActiveView();
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine($"RhinoRoad could not open Inspect Road: {error.Message}");
                Forget(session, form);
                form.Close();
            }
        });
        return Result.Success;
    }

    public static void Refresh(RhinoDoc document, Guid sourceId) =>
        RoadDocumentSession.Find(document)?.InspectorForm?.RefreshSource(sourceId);

    private static void Forget(RoadDocumentSession session, VehicleAccessInspectorForm form)
    {
        if (session.InspectorForm == form) session.InspectorForm = null;
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
