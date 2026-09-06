using RhinoRoad.Core;

namespace RhinoRoad.Rhino;

/// <summary>
/// Everything <c>Road</c> needs before it starts asking for geometry.
/// </summary>
/// <remarks>
/// Held apart from both the dialog and the command-line prompt so the two entry points configure
/// the same object. The command must stay scriptable — <c>-Road</c> in a macro cannot
/// raise a modal dialog — so the prompt is not legacy, it is the scripted interface.
/// </remarks>
internal sealed class VehicleAccessSettings
{
    public PathSourceKind Source { get; set; } = PathSourceKind.Interactive;
    public string VehicleId { get; set; } = "PV";
    public string ModeId { get; set; } = "A";
    public TravelDirection Direction { get; set; } = TravelDirection.Forward;
    public RoadEdgeMethod EdgeMethod { get; set; } = RoadEdgeMethod.MinimumFootprint;
    public double ClearanceMetres { get; set; } = 0.30;
    public double LeftWidthMetres { get; set; } = 3.25;
    public double RightWidthMetres { get; set; } = 3.25;
    public bool CheckMaximumGrade { get; set; }
    public double MaximumGradePercent { get; set; } = 8.0;
    public bool CheckObstacles { get; set; }
    public bool CheckAllowedArea { get; set; }
    public bool ReselectReferences { get; set; }
    public bool ReplaceExisting { get; set; } = true;

    /// <summary>
    /// Whether to stop at a transient viewport preview before baking.
    /// </summary>
    /// <remarks>
    /// Off by default. The baked result is itself the preview — it lands on its own layers, is
    /// grouped, and a rerun replaces it — so the confirmation prompt mostly costs a keystroke per
    /// run without telling the designer anything the geometry will not.
    /// </remarks>
    public bool PreviewBeforeBaking { get; set; }

    public FootprintMode Footprints { get; set; } = FootprintMode.AtInterval;

    /// <summary>Station interval for stamped vehicle outlines when <see cref="Footprints"/> is AtInterval.</summary>
    public double FootprintIntervalMetres { get; set; } = 2.0;

    public bool CreateFixedEdges => EdgeMethod is RoadEdgeMethod.Both or RoadEdgeMethod.FixedWidth;

    /// <summary>Keeps the selection valid when a preset is renamed or a mode is not published.</summary>
    public void Reconcile(VehicleCatalog catalog)
    {
        if (catalog.Vehicles.All(vehicle => vehicle.Id != VehicleId))
        {
            VehicleId = catalog.Vehicles.First().Id;
        }

        var vehicle = catalog.Get(VehicleId);
        if (!vehicle.DrivingModes.ContainsKey(ModeId))
        {
            ModeId = vehicle.DrivingModes.Keys.OrderBy(key => key).First();
        }
    }
}
