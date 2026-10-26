using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.UI;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

[Guid("7C0B5E52-3D6A-4B38-9F0E-5A1D2E8C4B71")]
public sealed class RRImportVehicleCommand : Command
{
    public override string EnglishName => "RRImportVehicle";

    protected override Result RunCommand(RhinoDoc document, RunMode mode)
    {
        var dialog = new OpenFileDialog { Title = "Import RhinoRoad vehicle", Filter = "Vehicle JSON (*.json)|*.json" };
        if (!dialog.ShowOpenDialog()) return Result.Cancel;

        var imported = RhinoRoadCatalog.TryImport(dialog.FileName, out var message);
        RhinoApp.WriteLine(imported ? $"RhinoRoad: {message}" : $"RhinoRoad: vehicle not imported.{Environment.NewLine}{message}");
        if (!imported) return Result.Failure;

        RhinoApp.WriteLine("User vehicles are marked UserDefined: not from the Vejregler source and not validated. Run RRVehicles to list all vehicles.");
        return Result.Success;
    }
}
