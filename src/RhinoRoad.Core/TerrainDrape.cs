namespace RhinoRoad.Core;

/// <summary>
/// Puts a route's elevations onto a terrain whose height was measured below each sample.
/// </summary>
/// <remarks>
/// The projection itself needs Rhino; what is decided here is what a station that missed the
/// terrain should do. A miss between two hits -- a hole in the mesh, a gap between two surfaces --
/// takes the height interpolated linearly by station between the hits either side, so the grade
/// across the hole is the grade between its edges. Holding the last height instead turned a 10 m
/// hole in a 5 % slope into a level shelf followed by a half-metre step, which the grade check read
/// as a cliff. Only the misses before the first hit and after the last, where there is nothing to
/// interpolate towards, hold the nearest hit's height, so a short gap at the edge of a surface reads
/// as level.
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

        var draped = new RouteSample[route.Count];
        var previousHit = -1;
        for (var index = 0; index < route.Count; index++)
        {
            if (terrainHeightsMetres[index] is not { } height) continue;
            draped[index] = WithZ(route[index], height);

            // Fill the misses since the previous hit: interpolated between it and this one, or,
            // before the first hit, held at this one's height.
            for (var gap = previousHit + 1; gap < index; gap++)
            {
                draped[gap] = WithZ(route[gap], previousHit < 0
                    ? height
                    : Interpolate(route, previousHit, index, gap, terrainHeightsMetres[previousHit]!.Value, height));
            }

            previousHit = index;
        }

        // After the last hit there is nothing to interpolate towards.
        var last = terrainHeightsMetres[previousHit]!.Value;
        for (var gap = previousHit + 1; gap < route.Count; gap++) draped[gap] = WithZ(route[gap], last);

        return (draped, missed);
    }

    /// <summary>The report line for stations that missed the terrain, or null when none did.</summary>
    public static string? MissWarning(int missedStations) => missedStations <= 0
        ? null
        : $"{missedStations} station{(missedStations == 1 ? " is" : "s are")} off the terrain; " +
          "their elevations were interpolated between the nearest stations on it either side, " +
          "and held level before the first and after the last.";

    private static RouteSample WithZ(RouteSample sample, double z) =>
        sample with { PositionMetres = sample.PositionMetres with { Z = z } };

    /// <summary>The height at <paramref name="index"/>, linear by station between two hits.</summary>
    private static double Interpolate(IReadOnlyList<RouteSample> route, int from, int to, int index, double fromHeight, double toHeight)
    {
        var span = route[to].StationMetres - route[from].StationMetres;
        // Stations that do not advance (a standstill, or a station-less route) fall back to the index.
        var t = Math.Abs(span) > Geometry2D.Epsilon
            ? (route[index].StationMetres - route[from].StationMetres) / span
            : (double)(index - from) / (to - from);
        return fromHeight + ((toHeight - fromHeight) * Math.Clamp(t, 0.0, 1.0));
    }
}
