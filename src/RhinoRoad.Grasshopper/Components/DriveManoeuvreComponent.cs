using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Rhino.Geometry;
using RhinoRoad.Core;
using RhinoRoad.GrasshopperPlugin.Conversion;

namespace RhinoRoad.GrasshopperPlugin.Components;

/// <summary>Replays a click-by-click manoeuvre, exactly as the Rhino plugin's interactive command does.</summary>
public sealed class DriveManoeuvreComponent : AccessComponentBase
{
    private const int VehicleInput = 0;
    private const int ModeInput = 1;
    private const int StartInput = 2;
    private const int HeadingInput = 3;
    private const int PointsInput = 4;
    private const int DirectionsInput = 5;
    private const int KindsInput = 6;
    private const int ExitsInput = 7;
    private const int ClearanceInput = 8;
    private const int IntervalInput = 9;

    // Last, so a definition saved before it existed still wires its other inputs by position.
    private const int SpeedsInput = 10;

    public DriveManoeuvreComponent()
        : base("Drive Manoeuvre", "Drive", "Drive a vehicle through control points and report its swept envelopes, tracks and feasibility.")
    {
    }

    public override Guid ComponentGuid => new("c0d3a1e2-4b1a-4d2e-8f10-2a6d6a0e1b02");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddParameter(VehicleParameter());
        pManager.AddParameter(ModeParameter());
        pManager.AddPointParameter("Start", "S", "Rear-axle start position.", GH_ParamAccess.item);
        var heading = new Param_Vector
        {
            Name = "Heading",
            NickName = "H",
            Description = "Start heading in plan. Defaults to +X.",
            Access = GH_ParamAccess.item,
            Optional = true
        };
        heading.SetPersistentData(new Vector3d(1.0, 0.0, 0.0));
        pManager.AddParameter(heading);
        pManager.AddPointParameter("Points", "P", "Control points the vehicle is driven to, in order.", GH_ParamAccess.list);
        pManager.AddGenericParameter("Directions", "D", "Per control: Forward/Reverse text or a Boolean (true is forward). A short list repeats its last value.", GH_ParamAccess.list);
        pManager.AddTextParameter("Kinds", "K", "Per control: Aim, Turn or Finish. Defaults to Aim.", GH_ParamAccess.list);
        pManager.AddVectorParameter("Exit Headings", "E", "Per control: the heading to leave the point on. Zero or missing for none.", GH_ParamAccess.list);
        pManager.AddParameter(ClearanceParameter());
        pManager.AddParameter(IntervalParameter());
        pManager.AddNumberParameter("Speeds", "V",
            "Per control: the speed in km/h to drive that leg at, slower than the mode's for a reverse or a shuffle "
            + "at walking pace. Zero or missing for the mode's speed; a speed above the mode's is driven at the mode's. "
            + "The lock stays the mode's; a slower leg only moves the wheel in less distance.",
            GH_ParamAccess.list);
        foreach (var index in new[] { DirectionsInput, KindsInput, ExitsInput, SpeedsInput }) pManager[index].Optional = true;
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        if (!TryGetVehicle(da, VehicleInput, ModeInput, out var vehicle, out var mode)) return;

        var start = Point3d.Unset;
        var heading = new Vector3d(1.0, 0.0, 0.0);
        var points = new List<Point3d>();
        if (!da.GetData(StartInput, ref start)) return;
        da.GetData(HeadingInput, ref heading);
        if (!da.GetDataList(PointsInput, points) || points.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least one control point is required.");
            return;
        }

        var startHeading = ManoeuvreInputParser.HeadingOf(heading.X, heading.Y);
        if (startHeading is null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The start heading has no plan direction.");
            return;
        }

        var exits = new List<Vector3d>();
        da.GetDataList(ExitsInput, exits);
        var speeds = new List<double>();
        da.GetDataList(SpeedsInput, speeds);
        double? clearance = null;
        var clearanceValue = 0.0;
        if (da.GetData(ClearanceInput, ref clearanceValue)) clearance = Math.Max(0.0, clearanceValue * ModelUnits.MetresPerModelUnit);
        var interval = 0.0;
        da.GetData(IntervalInput, ref interval);

        var metresPerUnit = ModelUnits.MetresPerModelUnit;
        var problems = new List<string>();
        var controls = ManoeuvreInputParser.BuildControls(
            points.Select(point => GeometryConverter.ToMetres(point, metresPerUnit)).ToList(),
            ReadObjects(da, DirectionsInput),
            ReadObjects(da, KindsInput),
            exits.Select(vector => ManoeuvreInputParser.HeadingOf(vector.X, vector.Y)).ToList(),
            problems,
            speeds.Select(speed => (double?)speed).ToList());
        foreach (var problem in problems) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, problem);
        if (controls.Any(control => control.SpeedKilometresPerHour > mode.SpeedKilometresPerHour))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"A speed above the mode's {mode.SpeedKilometresPerHour:0.#} km/h is driven at {mode.SpeedKilometresPerHour:0.#} km/h: "
                + "the mode's lock and steering rate are stated for its speed, and driving faster would understate the swept path.");
        }

        try
        {
            var definition = new ManoeuvreDefinition(
                ManoeuvreDefinition.CurrentSchemaVersion,
                GeometryConverter.ToMetres(start, metresPerUnit),
                startHeading.Value,
                controls[0].Direction,
                controls);
            var replayed = ManoeuvreReplayService.Replay(vehicle, mode, definition);
            if (replayed.RequestedAngleExceeded)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "A control asked for more wheel angle than the mode allows; the wheel was held at its limit.");
            }

            Publish(da, AccessRunner.Run(vehicle, mode, replayed.Samples, clearance, interval * metresPerUnit));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, exception.Message);
        }
    }
}
