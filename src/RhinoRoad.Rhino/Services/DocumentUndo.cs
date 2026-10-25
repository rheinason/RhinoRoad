using Rhino;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Undo records for changes made outside a command. Rhino records a command's changes on its own,
/// but the Edit Road palette and the inspector are modeless: their Save, intent edits and discard
/// changed the document with nothing recording them, so Ctrl+Z after Save could not bring back the
/// previous result. Inside a command, or inside a record already open, the change simply joins it —
/// a second record there would split one user action into two undo steps.
/// </summary>
internal static class DocumentUndo
{
    public static T Record<T>(RhinoDoc document, string description, Func<T> change)
    {
        if (document.UndoRecordingIsActive || document.UndoActive || document.RedoActive) return change();
        var record = document.BeginUndoRecord(description);
        try
        {
            return change();
        }
        finally
        {
            if (record != 0) document.EndUndoRecord(record);
        }
    }

    public static void Record(RhinoDoc document, string description, Action change) =>
        Record(document, description, () =>
        {
            change();
            return true;
        });
}
