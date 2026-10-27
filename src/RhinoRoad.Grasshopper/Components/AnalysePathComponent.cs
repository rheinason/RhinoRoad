using Grasshopper.Kernel;
using Rhino.Geometry;
using RhinoRoad.Core;
using RhinoRoad.GrasshopperPlugin.Conversion;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.GrasshopperPlugin.Components;

/// <summary>Checks an existing curve as the rear-axle path of a vehicle.</summary>
public sealed class AnalysePathComponent : AccessComponentBase
{
    private const int VehicleInput = 0;
    private const int ModeInput = 1;
    private const int PathInput = 2;
    private const int ReverseInput = 3;
    private const int ClearanceInput = 4;
    private const int IntervalInput = 5;

    public AnalysePathComponent()
        : base("Analyse Path", "Path", "Check a curve as a vehicle's rear-axle path and report its swept envelopes, tracks and feasibility.")
    {
    }

    public override Guid ComponentGuid => new("c0d3a1e2-4b1a-4d2e-8f10-2a6d6a0e1b03");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddParameter(VehicleParameter());
        pManager.AddParameter(ModeParameter());
        pManager.AddCurveParameter("Path", "P", "Rear-axle path, in the direction of travel.", GH_ParamAccess.item);
        pManager.AddBooleanParameter("Reverse", "R", "True when the vehicle reverses along the curve. Defaults to false.", GH_ParamAccess.item, false);
        pManager.AddParameter(ClearanceParameter());
        pManager.AddParameter(IntervalParameter());
        pManager[ReverseInput].Optional = true;
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        if (!TryGetVehicle(da, VehicleInput, ModeInput, out var vehicle, out var mode)) return;

        Curve? path = null;
        if (!da.GetData(PathInput, ref path) || path is null) return;
        var reverse = false;
        da.GetData(ReverseInput, ref reverse);
        var document = RhinoDocument;
        var metresPerUnit = ModelUnits.MetresPerModelUnit(document);
        double? clearance = null;
        var clearanceValue = 0.0;
        if (da.GetData(ClearanceInput, ref clearanceValue)) clearance = Math.Max(0.0, clearanceValue * metresPerUnit);
        var interval = 0.0;
        da.GetData(IntervalInput, ref interval);

        try
        {
            var samples = RhinoRouteSampler.Sample(path, ModelUnits.Of(document), reverse ? TravelDirection.Reverse : TravelDirection.Forward);
            Publish(da, AccessRunner.Run(vehicle, mode, samples, clearance, interval * metresPerUnit));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, exception.Message);
        }
    }
}
