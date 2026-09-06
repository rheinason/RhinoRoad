using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

// Keep old macros working without cluttering Rhino autocomplete. Forward directly so scripted
// mode, command result, and the shared session settings behave exactly like the public commands.
[Guid("D3737389-723B-4999-B80E-3CC549805953"), CommandStyle(Style.Hidden)]
public sealed class LegacyVehicleAccessCommand : Command
{
    public override string EnglishName => "RRVehicleAccess";
    protected override Result RunCommand(RhinoDoc document, RunMode mode) => RRVehicleAccessCommand.Execute(document, mode);
}

[Guid("88654B02-4C34-48EF-9315-58900A043771"), CommandStyle(Style.Hidden)]
public sealed class LegacyEditVehicleJourneyCommand : Command
{
    public override string EnglishName => "RREditVehicleJourney";
    protected override Result RunCommand(RhinoDoc document, RunMode mode) => RREditVehicleJourneyCommand.Execute(document, mode);
}

[Guid("037A0EE7-6073-4479-A8EE-68F1FA5C3119"), CommandStyle(Style.Hidden)]
public sealed class LegacyUpdateVehicleAccessCommand : Command
{
    public override string EnglishName => "RRUpdateVehicleAccess";
    protected override Result RunCommand(RhinoDoc document, RunMode mode) => RRUpdateVehicleAccessCommand.Execute(document, mode);
}

[Guid("544F730F-C398-4D9F-9E9C-30D2DB777F10"), CommandStyle(Style.Hidden)]
public sealed class LegacyInspectVehicleAccessCommand : Command
{
    public override string EnglishName => "RRInspectVehicleAccess";
    protected override Result RunCommand(RhinoDoc document, RunMode mode) => VehicleAccessInspectorService.Start(document);
}

[Guid("B28B654D-A3A6-4FCA-A53E-C637519127DE"), CommandStyle(Style.Hidden)]
public sealed class LegacyMeasureVehicleAccessCommand : Command
{
    public override string EnglishName => "RRMeasureVehicleAccess";
    protected override Result RunCommand(RhinoDoc document, RunMode mode) => AccessSizingCommands.Run(document, true);
}

[Guid("5FF0053E-7968-4BFC-A3D7-55859D95832E"), CommandStyle(Style.Hidden)]
public sealed class LegacyCombineVehicleAccessCommand : Command
{
    public override string EnglishName => "RRCombineVehicleAccess";
    protected override Result RunCommand(RhinoDoc document, RunMode mode) => AccessSizingCommands.Run(document, false);
}
