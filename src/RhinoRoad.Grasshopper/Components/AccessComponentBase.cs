using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using RhinoRoad.Core;
using RhinoRoad.GrasshopperPlugin.Conversion;

namespace RhinoRoad.GrasshopperPlugin.Components;

/// <summary>
/// What Drive Manoeuvre and Analyse Path have in common: the vehicle and mode inputs, and the
/// output set an analysis publishes. Only how the route is obtained differs between them.
/// </summary>
public abstract class AccessComponentBase : GH_Component
{
    private const int RearPathOutput = 0;
    private const int BodyOutput = 1;
    private const int BodyHolesOutput = 2;
    private const int ClearanceOutput = 3;
    private const int ClearanceHolesOutput = 4;
    private const int TowedOutput = 5;
    private const int WheelAngleOutput = 6;
    private const int SteeringRateOutput = 7;
    private const int FoldOutput = 8;
    private const int ViolationsOutput = 9;
    private const int FeasibleOutput = 10;
    private const int FootprintsOutput = 11;

    protected AccessComponentBase(string name, string nickname, string description)
        : base(name, nickname, description, "RhinoRoad", "Analysis")
    {
    }

    protected static Param_String VehicleParameter()
    {
        var parameter = new Param_String
        {
            Name = "Vehicle",
            NickName = "V",
            Description = "Vehicle preset id (PV, REN, LV 12, BUS 12, BUS 13,7, BUS 15, PVT, SVT). Defaults to PV.",
            Access = GH_ParamAccess.item,
            Optional = true
        };
        parameter.SetPersistentData("PV");
        return parameter;
    }

    protected static Param_String ModeParameter()
    {
        var parameter = new Param_String
        {
            Name = "Mode",
            NickName = "M",
            Description = "Driving mode, A or B. Defaults to A.",
            Access = GH_ParamAccess.item,
            Optional = true
        };
        parameter.SetPersistentData("A");
        return parameter;
    }

    protected static Param_Number ClearanceParameter() => new()
    {
        Name = "Clearance",
        NickName = "C",
        Description = "Clearance around the body envelope in metres. Omit for the mode's default.",
        Access = GH_ParamAccess.item,
        Optional = true
    };

    protected static Param_Number IntervalParameter() => new()
    {
        Name = "Interval",
        NickName = "I",
        Description = "Footprint stamp spacing along the route in metres. Omit or 0 for no footprints.",
        Access = GH_ParamAccess.item,
        Optional = true
    };

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddCurveParameter("Rear Path", "P", "Rear-axle path of the vehicle.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Body Envelope", "B", "Outer boundary of the area the body sweeps.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Body Holes", "BH", "Enclosed holes in the body envelope.", GH_ParamAccess.list);
        pManager.AddCurveParameter("Clearance Envelope", "CE", "Outer boundary of the body envelope grown by the clearance.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Clearance Holes", "CH", "Enclosed holes in the clearance envelope.", GH_ParamAccess.list);
        pManager.AddCurveParameter("Towed Tracks", "T", "Axle-centre track of each towed unit, one branch per unit.", GH_ParamAccess.tree);
        pManager.AddNumberParameter("Max Wheel Angle", "W", "Largest wheel angle in degrees.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Max Steering Rate", "R", "Largest steering rate in degrees per second.", GH_ParamAccess.item);
        pManager.AddNumberParameter("Max Fold", "F", "Largest fold at each articulation joint in degrees.", GH_ParamAccess.list);
        pManager.AddTextParameter("Violations", "X", "What the route breaks, if anything.", GH_ParamAccess.list);
        pManager.AddBooleanParameter("Feasible", "OK", "True when the route has no violations.", GH_ParamAccess.item);
        pManager.AddCurveParameter("Footprints", "FP", "Vehicle outlines stamped at the interval, towed units included.", GH_ParamAccess.list);
    }

    /// <summary>Resolves the vehicle and mode, raising an error message if either is unknown.</summary>
    protected bool TryGetVehicle(IGH_DataAccess da, int vehicleIndex, int modeIndex, out VehicleDefinition vehicle, out DrivingModeDefinition mode)
    {
        string? vehicleId = "PV";
        string? modeId = "A";
        da.GetData(vehicleIndex, ref vehicleId);
        da.GetData(modeIndex, ref modeId);
        if (AccessRunner.TryResolve(vehicleId, modeId, out vehicle, out mode, out var error)) return true;
        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
        return false;
    }

    protected void Publish(IGH_DataAccess da, AccessRun run)
    {
        var scale = ModelUnits.ModelUnitsPerMetre;
        var result = run.Result;
        var rear = result.Poses
            .Select(pose => new Point3d(pose.RearAxleCentreMetres.X * scale, pose.RearAxleCentreMetres.Y * scale, pose.RearAxleCentreMetres.Z * scale))
            .ToList();
        if (rear.Count >= 2) da.SetData(RearPathOutput, new PolylineCurve(rear));

        if (run.Body.Region is not null)
        {
            da.SetData(BodyOutput, GeometryConverter.ToPolyline(run.Body.Region, scale));
            da.SetDataList(BodyHolesOutput, GeometryConverter.HolesOf(run.Body.Region, scale));
        }

        if (run.Clearance?.Region is not null)
        {
            da.SetData(ClearanceOutput, GeometryConverter.ToPolyline(run.Clearance.Region, scale));
            da.SetDataList(ClearanceHolesOutput, GeometryConverter.HolesOf(run.Clearance.Region, scale));
        }

        if (run.Body.Status == SweptRegionStatus.Disjoint)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, run.Body.Message + " Only the largest area is output.");
        }
        else if (run.Body.Region is null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, run.Body.Message);
        }

        var towed = new GH_Structure<GH_Curve>();
        for (var unit = 0; unit < result.TowedAxleTracksMetres.Count; unit++)
        {
            var track = GeometryConverter.ToPolyline(result.TowedAxleTracksMetres[unit], scale, closed: false);
            if (track is not null) towed.Append(new GH_Curve(track), new GH_Path(unit));
        }

        da.SetDataTree(TowedOutput, towed);
        da.SetData(WheelAngleOutput, result.MaximumSteeringAngleRadians * 180.0 / Math.PI);
        da.SetData(SteeringRateOutput, result.MaximumSteeringRateRadiansPerSecond * 180.0 / Math.PI);
        da.SetDataList(FoldOutput, result.MaximumArticulationAnglesRadians.Select(angle => angle * 180.0 / Math.PI));
        da.SetDataList(ViolationsOutput, result.Violations.Select(violation => $"{violation.Kind} at {violation.StationMetres:0.0} m: {violation.Message}"));
        da.SetData(FeasibleOutput, result.IsFeasible);

        var footprints = new List<Curve>();
        foreach (var pose in run.Footprints)
        {
            foreach (var outline in pose.OccupiedOutlinesWorldMetres)
            {
                var curve = GeometryConverter.ToPolyline(outline, scale, closed: true);
                if (curve is not null) footprints.Add(curve);
            }
        }

        da.SetDataList(FootprintsOutput, footprints);
        if (!result.IsFeasible)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"{result.Violations.Count} violation(s); see the Violations output.");
        }
    }

    /// <summary>Reads an optional list as plain objects, unwrapping Grasshopper's goo.</summary>
    protected static List<object?> ReadObjects(IGH_DataAccess da, int index)
    {
        var goos = new List<IGH_Goo>();
        da.GetDataList(index, goos);
        return goos.Select(goo => goo?.ScriptVariable()).ToList();
    }
}
