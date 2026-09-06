using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

[Guid("3DE4DA98-BE95-4BBD-9753-4CB0D92E83AB")]
public sealed class RRInspectVehicleAccessCommand : Command
{
    public override string EnglishName => "RRInspectRoad";
    protected override Result RunCommand(RhinoDoc document, RunMode mode) => VehicleAccessInspectorService.Start(document);
}
