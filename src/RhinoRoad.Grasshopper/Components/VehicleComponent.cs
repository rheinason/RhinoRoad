using Grasshopper.Kernel;
using RhinoRoad.Core;
using RhinoRoad.GrasshopperPlugin.Conversion;

namespace RhinoRoad.GrasshopperPlugin.Components;

/// <summary>Reports a catalogue vehicle: its dimensions, validation status and turning geometry.</summary>
public sealed class VehicleComponent : GH_Component
{
    public VehicleComponent()
        : base("Vehicle", "Vehicle", "Look up a vehicle preset and its turning geometry at full lock.", "RhinoRoad", "Analysis")
    {
    }

    public override Guid ComponentGuid => new("c0d3a1e2-4b1a-4d2e-8f10-2a6d6a0e1b01");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddTextParameter("Vehicle", "V", "Vehicle preset id (PV, REN, LV 12, BUS 12, BUS 13,7, BUS 15, PVT, SVT).", GH_ParamAccess.item, "PV");
        pManager.AddTextParameter("Mode", "M", "Driving mode, A or B.", GH_ParamAccess.item, "A");
        pManager[0].Optional = true;
        pManager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter("Name", "N", "Vehicle name.", GH_ParamAccess.item);
        pManager.AddTextParameter("Validation", "S", "Validation status of the preset.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Length", "L", "Overall length, bumper to tail with any trailer straight.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Width", "W", "Overall body width.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Wheelbase", "WB", "Wheelbase of the lead unit.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Min Radius", "R", "Minimum rear-axle radius at full lock.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Swept Width", "SW", "Width of the annulus swept by the body at full lock.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Outline", "O", "Lead body outline at the origin, rear axle at 0,0 and heading +X.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        string? vehicleId = "PV";
        string? modeId = "A";
        da.GetData(0, ref vehicleId);
        da.GetData(1, ref modeId);
        if (!AccessRunner.TryResolve(vehicleId, modeId, out var vehicle, out var mode, out var error))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
            return;
        }

        var scale = ModelUnits.ModelUnitsPerMetre;
        var turning = TurningGeometryCalculator.AtFullLock(vehicle, mode);
        da.SetData(0, vehicle.Name);
        da.SetData(1, vehicle.ValidationStatus.ToString());
        da.SetData(2, vehicle.OverallLengthMetres * scale);
        da.SetData(3, vehicle.WidthMetres * scale);
        da.SetData(4, vehicle.WheelbaseMetres * scale);
        da.SetData(5, turning.RearAxleRadiusMetres * scale);
        da.SetData(6, turning.SweptWidthMetres * scale);
        da.SetData(7, GeometryConverter.ToPolyline(vehicle.BodyOutline, scale, closed: true));
        if (!turning.SteadyStateAttainable)
        {
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                $"The trailer cannot hold full lock continuously; the tightest sustainable rear-axle radius is {turning.MinimumSustainableRearAxleRadiusMetres * scale:0.00} model units.");
        }
    }
}
