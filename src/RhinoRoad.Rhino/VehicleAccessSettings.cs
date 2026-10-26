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

    /// <summary>Whether the route's elevations are taken from a selected terrain.</summary>
    public bool DrapeTerrain { get; set; }
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

    /// <summary>
    /// Loads the last-used values saved by <see cref="Save"/>. Unset keys keep the defaults, and
    /// unknown ids are repaired by <see cref="Reconcile"/> afterwards.
    /// </summary>
    /// <remarks>ReselectReferences is deliberately not persisted: it is a one-run request.</remarks>
    public void Load()
    {
        if (RhinoRoadPlugin.Instance?.Settings is not { } store) return;
        try
        {
            Source = ParseEnum(store.GetString("Road.Source", Source.ToString()), Source);
            VehicleId = store.GetString("Road.VehicleId", VehicleId);
            ModeId = store.GetString("Road.ModeId", ModeId);
            Direction = ParseEnum(store.GetString("Road.Direction", Direction.ToString()), Direction);
            EdgeMethod = ParseEnum(store.GetString("Road.EdgeMethod", EdgeMethod.ToString()), EdgeMethod);
            ClearanceMetres = store.GetDouble("Road.ClearanceMetres", ClearanceMetres);
            LeftWidthMetres = store.GetDouble("Road.LeftWidthMetres", LeftWidthMetres);
            RightWidthMetres = store.GetDouble("Road.RightWidthMetres", RightWidthMetres);
            CheckMaximumGrade = store.GetBool("Road.CheckMaximumGrade", CheckMaximumGrade);
            MaximumGradePercent = store.GetDouble("Road.MaximumGradePercent", MaximumGradePercent);
            CheckObstacles = store.GetBool("Road.CheckObstacles", CheckObstacles);
            CheckAllowedArea = store.GetBool("Road.CheckAllowedArea", CheckAllowedArea);
            DrapeTerrain = store.GetBool("Road.DrapeTerrain", DrapeTerrain);
            ReplaceExisting = store.GetBool("Road.ReplaceExisting", ReplaceExisting);
            PreviewBeforeBaking = store.GetBool("Road.PreviewBeforeBaking", PreviewBeforeBaking);
            Footprints = ParseEnum(store.GetString("Road.Footprints", Footprints.ToString()), Footprints);
            FootprintIntervalMetres = store.GetDouble("Road.FootprintIntervalMetres", FootprintIntervalMetres);
        }
        catch (Exception)
        {
            // Remembered values are a convenience; a damaged settings file must not stop the command.
        }
    }

    /// <summary>Remembers the current values for the next Rhino session.</summary>
    public void Save()
    {
        if (RhinoRoadPlugin.Instance?.Settings is not { } store) return;
        try
        {
            store.SetString("Road.Source", Source.ToString());
            store.SetString("Road.VehicleId", VehicleId);
            store.SetString("Road.ModeId", ModeId);
            store.SetString("Road.Direction", Direction.ToString());
            store.SetString("Road.EdgeMethod", EdgeMethod.ToString());
            store.SetDouble("Road.ClearanceMetres", ClearanceMetres);
            store.SetDouble("Road.LeftWidthMetres", LeftWidthMetres);
            store.SetDouble("Road.RightWidthMetres", RightWidthMetres);
            store.SetBool("Road.CheckMaximumGrade", CheckMaximumGrade);
            store.SetDouble("Road.MaximumGradePercent", MaximumGradePercent);
            store.SetBool("Road.CheckObstacles", CheckObstacles);
            store.SetBool("Road.CheckAllowedArea", CheckAllowedArea);
            store.SetBool("Road.DrapeTerrain", DrapeTerrain);
            store.SetBool("Road.ReplaceExisting", ReplaceExisting);
            store.SetBool("Road.PreviewBeforeBaking", PreviewBeforeBaking);
            store.SetString("Road.Footprints", Footprints.ToString());
            store.SetDouble("Road.FootprintIntervalMetres", FootprintIntervalMetres);
        }
        catch (Exception)
        {
            // As for Load: failing to remember must not fail the run.
        }
    }

    private static T ParseEnum<T>(string text, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) ? value : fallback;

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
