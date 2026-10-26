using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

[Guid("2F6A91D4-8B3E-4C57-A0D2-6E49B7C13F85")]
public sealed class RRVehiclesCommand : Command
{
    public override string EnglishName => "RRVehicles";

    protected override Result RunCommand(RhinoDoc document, RunMode mode)
    {
        var load = RhinoRoadCatalog.Reload();
        RhinoApp.WriteLine("RhinoRoad vehicles:");
        foreach (var vehicle in load.Catalog.Vehicles)
        {
            var note = vehicle.ValidationStatus == ValidationStatus.UserDefined || string.IsNullOrWhiteSpace(vehicle.Source.Title)
                ? vehicle.Source.Notes
                : vehicle.Source.Title;
            RhinoApp.WriteLine($"  {vehicle.Id,-10} {vehicle.Name}  [{vehicle.ValidationStatus}]  {Shorten(note)}");
        }

        RhinoApp.WriteLine($"User vehicles folder: {RhinoRoadCatalog.UserFolder}");
        foreach (var error in load.Errors) RhinoApp.WriteLine($"  Skipped: {error}");
        return Result.Success;
    }

    private static string Shorten(string text) => text.Length <= 90 ? text : text[..87] + "...";
}
