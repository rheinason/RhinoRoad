using Rhino.Commands;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Keeps the modeless palettes out of a running command. A palette button still works while another
/// command is waiting at its prompt, and what it does -- an undo record, a point pick, a nested
/// RunScript -- would then land inside that command's undo step or interrupt its get. Each palette
/// action that writes to the document or asks for input checks this first.
/// </summary>
internal static class CommandGuard
{
    public const string BusyMessage = "Finish the running command first.";

    /// <summary>True, after reporting <see cref="BusyMessage"/>, when a Rhino command is running.</summary>
    public static bool Busy(Action<string> report)
    {
        if (!Command.InCommand()) return false;
        report(BusyMessage);
        return true;
    }
}
