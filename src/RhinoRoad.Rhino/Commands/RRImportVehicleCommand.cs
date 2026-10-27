using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

[Guid("7C0B5E52-3D6A-4B38-9F0E-5A1D2E8C4B71")]
public sealed class RRImportVehicleCommand : Command
{
    public override string EnglishName => "RRImportVehicle";

    protected override Result RunCommand(RhinoDoc document, RunMode mode)
    {
        var path = mode == RunMode.Scripted ? ScriptedPath(document) : DialogPath();
        if (string.IsNullOrWhiteSpace(path)) return Result.Cancel;
        if (!File.Exists(path))
        {
            RhinoApp.WriteLine($"RhinoRoad: vehicle not imported. {path} does not exist.");
            return Result.Failure;
        }

        var imported = RhinoRoadCatalog.TryImport(path, out var message);
        RhinoApp.WriteLine(imported ? $"RhinoRoad: {message}" : $"RhinoRoad: vehicle not imported.{Environment.NewLine}{message}");
        if (!imported) return Result.Failure;

        RhinoApp.WriteLine("User vehicles are marked UserDefined: not from the Vejregler source and not validated. Run RRVehicles to list all vehicles.");
        return Result.Success;
    }

    private static string? DialogPath()
    {
        var dialog = new OpenFileDialog { Title = "Import RhinoRoad vehicle", Filter = "Vehicle JSON (*.json)|*.json" };
        return dialog.ShowOpenDialog() ? dialog.FileName : null;
    }

    /// <summary>
    /// <c>-RRImportVehicle</c> in a macro cannot answer a dialog, so it reads the path from the command
    /// line, as a literal string so that a path with spaces is taken whole. A relative path is taken
    /// from the document's folder, not from wherever the Rhino process happens to be running.
    /// </summary>
    private static string? ScriptedPath(RhinoDoc document)
    {
        using var getter = new GetString();
        getter.SetCommandPrompt("Vehicle JSON file to import");
        if (getter.GetLiteralString() != GetResult.String) return null;
        var text = getter.StringResult().Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(text)) return null;
        var folder = !string.IsNullOrEmpty(document.Path) && Path.GetDirectoryName(document.Path) is { Length: > 0 } saved
            ? saved
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        return Path.GetFullPath(text, folder);
    }
}
