namespace RhinoRoad.Core;

/// <summary>
/// Puts a route's elevations onto a terrain whose height was measured below each sample.
/// </summary>
/// <remarks>
/// The projection itself needs Rhino; what is decided here is what a station that missed the
/// terrain should do. It keeps the height of the last station that hit, and one before the first
/// hit takes the first height, so a short gap at the edge of a surface reads as level rather than
/// as a cliff the grade check would then report.
/// </remarks>
public static class TerrainDrape
{
    /// <summary>
    /// Replaces each sample's Z with the matching terrain height, in metres. A null height is a
    /// miss. If every station missed, the route is returned unchanged.
    /// </summary>
    public static (IReadOnlyList<RouteSample> Route, int MissedStations) Apply(
        IReadOnlyList<RouteSample> route,
        IReadOnlyList<double?> terrainHeightsMetres)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(terrainHeightsMetres);
        if (terrainHeightsMetres.Count != route.Count)
            throw new ArgumentException("There must be one terrain height per route sample.", nameof(terrainHeightsMetres));

        var missed = terrainHeightsMetres.Count(height => !height.HasValue);
        if (missed == route.Count) return (route, missed);

        var last = terrainHeightsMetres.First(height => height.HasValue)!.Value;
        var draped = new RouteSample[route.Count];
        for (var index = 0; index < route.Count; index++)
        {
            if (terrainHeightsMetres[index] is { } height) last = height;
            var position = route[index].PositionMetres;
            draped[index] = route[index] with { PositionMetres = position with { Z = last } };
        }

        return (draped, missed);
    }

    /// <summary>The report line for stations that missed the terrain, or null when none did.</summary>
    public static string? MissWarning(int missedStations) => missedStations <= 0
        ? null
        : $"{missedStations} station{(missedStations == 1 ? " is" : "s are")} off the terrain; the elevation of the nearest earlier station was kept.";
}
