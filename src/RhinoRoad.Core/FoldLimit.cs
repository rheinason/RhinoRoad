namespace RhinoRoad.Core;

/// <summary>
/// The one fold limit, read per towed unit from
/// <see cref="TowedUnitDefinition.MaximumArticulationDegrees"/>. The reverse generator stops at it,
/// the planner and the area search reject a candidate that reaches it, and the analyzer reports a
/// route that passes it, so a route one of them refuses cannot be baked as feasible by another.
/// </summary>
public static class FoldLimit
{
    /// <summary>
    /// The largest fold at any joint along <paramref name="route"/>, and whether any joint reached its
    /// unit's limit on the way. Zero and false for a rigid vehicle, which has nothing to fold.
    /// </summary>
    public static (double MaximumRadians, bool LimitReached) Measure(
        VehicleDefinition vehicle, IReadOnlyList<RouteSample> route)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(route);
        var maximum = 0.0;
        var reached = false;
        if (!vehicle.IsArticulated) return (maximum, reached);
        foreach (var step in ArticulationTrace.Follow(vehicle, route))
        {
            for (var index = 0; index < vehicle.TowedUnits.Count; index++)
            {
                var fold = Math.Abs(step.Chain!.ArticulationAngleRadians(index, step.VehicleHeadingRadians));
                maximum = Math.Max(maximum, fold);
                reached |= fold >= vehicle.TowedUnits[index].MaximumArticulationRadians;
            }
        }

        return (maximum, reached);
    }

    /// <summary>Whether any joint along <paramref name="route"/> reaches its unit's fold limit.</summary>
    public static bool IsReached(VehicleDefinition vehicle, IReadOnlyList<RouteSample> route) =>
        Measure(vehicle, route).LimitReached;

    /// <summary>
    /// Whether any joint's largest fold, as an analysis reports it in
    /// <see cref="VehicleAccessResult.MaximumArticulationAnglesRadians"/>, reaches its unit's limit.
    /// </summary>
    public static bool IsReached(VehicleDefinition vehicle, IReadOnlyList<double> maximumFoldsRadians)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(maximumFoldsRadians);
        for (var index = 0; index < Math.Min(vehicle.TowedUnits.Count, maximumFoldsRadians.Count); index++)
        {
            if (maximumFoldsRadians[index] >= vehicle.TowedUnits[index].MaximumArticulationRadians) return true;
        }

        return false;
    }
}
