using Rhino;
using Rhino.DocObjects;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Finds and removes RhinoRoad's own output regardless of how it is being displayed. The default
/// object list and <c>Delete(id, quiet)</c> both honour object modes, and an object on a hidden or
/// locked layer counts as hidden or locked: a refresh then left the previous footprints behind in a
/// layer the user had merely switched off, and they reappeared, doubled, when it was turned back on.
/// </summary>
internal static class DocumentObjects
{
    public static IEnumerable<RhinoObject> All(RhinoDoc document) =>
        document.Objects.GetObjectList(new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            HiddenObjects = true,
            LockedObjects = true,
            ActiveObjects = true,
            ReferenceObjects = true,
            DeletedObjects = false,
            IncludeGrips = false,
            IncludeLights = true,
            IncludePhantoms = true
        });

    /// <summary>Deletes an object even when it, or its layer, is hidden or locked.</summary>
    public static bool Delete(RhinoDoc document, Guid id) =>
        document.Objects.FindId(id) is { } item && document.Objects.Delete(item, quiet: true, ignoreModes: true);
}
