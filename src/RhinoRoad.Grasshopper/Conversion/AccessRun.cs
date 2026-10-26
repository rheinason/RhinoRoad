using RhinoRoad.Core;

namespace RhinoRoad.GrasshopperPlugin.Conversion;

/// <summary>Everything a component publishes from one analysis, still in metres.</summary>
public sealed record AccessRun(
    VehicleAccessResult Result,
    SweptRegionResult Body,
    SweptRegionResult? Clearance,
    IReadOnlyList<VehiclePose> Footprints,
    IReadOnlyList<string> Messages);

/// <summary>The shared analysis pipeline behind the manoeuvre and path components.</summary>
public static class AccessRunner
{
    // Same Rhino plug-in GUID as RhinoRoad.Rhino's AssemblyInfo; this assembly cannot reference that project.
    private static readonly Guid RhinoPluginId = new("E5473B43-CAB5-469E-9F58-0D90AD315F5B");
    private static readonly object CatalogGate = new();
    private static VehicleCatalog? _catalog;

    private static VehicleCatalog Catalog
    {
        get
        {
            lock (CatalogGate) return _catalog ??= VehicleCatalog.LoadWithUserDirectory(UserVehicleFolder()).Catalog;
        }
    }

    /// <summary>Drops the cached catalog so the next analysis re-reads the user vehicles folder.</summary>
    public static void ReloadCatalog()
    {
        lock (CatalogGate) _catalog = null;
    }

    /// <summary>
    /// The folder the Rhino plug-in reads user vehicles from: its settings directory when loaded,
    /// otherwise the equivalent %APPDATA% location.
    /// </summary>
    private static string UserVehicleFolder()
    {
        var settings = Rhino.PlugIns.PlugIn.Find(RhinoPluginId)?.SettingsDirectory;
        if (!string.IsNullOrWhiteSpace(settings)) return Path.Combine(settings, "vehicles");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "McNeel", "Rhinoceros", "8.0", "Plug-ins", "RhinoRoad", "vehicles");
    }

    public static bool TryResolve(
        string? vehicleId,
        string? modeId,
        out VehicleDefinition vehicle,
        out DrivingModeDefinition mode,
        out string error)
    {
        vehicle = null!;
        mode = null!;
        var id = string.IsNullOrWhiteSpace(vehicleId) ? "PV" : vehicleId.Trim();
        var found = Catalog.Vehicles.FirstOrDefault(
            candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            error = $"Unknown vehicle '{id}'. Available: {string.Join(", ", Catalog.Vehicles.Select(item => item.Id))}.";
            return false;
        }

        var wanted = string.IsNullOrWhiteSpace(modeId) ? "A" : modeId.Trim();
        var foundMode = found.DrivingModes.Values.FirstOrDefault(
            candidate => string.Equals(candidate.Id, wanted, StringComparison.OrdinalIgnoreCase));
        if (foundMode is null)
        {
            error = $"Vehicle {found.Id} has no driving mode '{wanted}'. Available: {string.Join(", ", found.DrivingModes.Values.Select(item => item.Id))}.";
            return false;
        }

        vehicle = found;
        mode = foundMode;
        error = string.Empty;
        return true;
    }

    /// <param name="clearanceMetres">Null uses the mode's default clearance.</param>
    /// <param name="footprintIntervalMetres">Zero or less produces no footprint stamps.</param>
    public static AccessRun Run(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<RouteSample> samples,
        double? clearanceMetres,
        double footprintIntervalMetres)
    {
        var result = new VehicleAccessAnalyzer().Analyze(vehicle, mode, samples);
        var messages = new List<string>();
        var body = SweptRegionBuilder.FromPoses(result.Poses);
        messages.Add(body.Message);

        SweptRegionResult? clearance = null;
        if (body.Region is not null)
        {
            clearance = SweptRegionBuilder.Inflate(body.Region, clearanceMetres ?? mode.DefaultClearanceMetres);
        }

        var stamps = new List<VehiclePose>();
        if (footprintIntervalMetres > 0.0 && result.Poses.Count > 0)
        {
            var next = result.Poses[0].StationMetres;
            foreach (var pose in result.Poses)
            {
                if (pose.StationMetres + 1e-9 < next) continue;
                stamps.Add(pose);
                next = pose.StationMetres + footprintIntervalMetres;
            }

            // The end of the route is always stamped: it is the pose a designer most wants to see.
            if (!ReferenceEquals(stamps[^1], result.Poses[^1])) stamps.Add(result.Poses[^1]);
        }

        return new AccessRun(result, body, clearance, stamps, messages);
    }
}
