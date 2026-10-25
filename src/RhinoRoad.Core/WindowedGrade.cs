namespace RhinoRoad.Core;

/// <summary>
/// Grade measured over a distance rather than between neighbouring samples.
/// </summary>
/// <remarks>
/// A route is sampled about every 0.10 m, so a rise of a millimetre of noise in Z between two
/// samples reads as a 1 % grade, and a sampled NURBS can be noisy enough to look like a wall. A
/// vehicle does not climb 0.10 m of road; it climbs a wheelbase of it, and the limit that matters is
/// the one the whole vehicle sees.
/// </remarks>
public static class WindowedGrade
{
    /// <summary>The shortest window used, however short the vehicle.</summary>
    public const double MinimumWindowMetres = 5.0;

    public static double WindowMetres(double wheelbaseMetres) => Math.Max(MinimumWindowMetres, wheelbaseMetres);

    /// <summary>
    /// The signed grade over the trailing window ending at each sample, by sample index. A sample
    /// earlier than one full window along the route has none, so is omitted. A route shorter than
    /// the window in total is judged once, end to end, rather than never.
    /// </summary>
    public static IReadOnlyList<(int Index, double Grade)> Compute(IReadOnlyList<RouteSample> samples, double windowMetres)
    {
        var result = new List<(int, double)>();
        if (samples.Count < 2) return result;

        var distance = new double[samples.Count];
        for (var index = 1; index < samples.Count; index++)
        {
            distance[index] = distance[index - 1] + samples[index].PositionMetres.XY.DistanceTo(samples[index - 1].PositionMetres.XY);
        }

        if (distance[^1] < windowMetres)
        {
            if (distance[^1] > Geometry2D.Epsilon)
                result.Add((samples.Count - 1, (samples[^1].PositionMetres.Z - samples[0].PositionMetres.Z) / distance[^1]));
            return result;
        }

        // The window start only ever moves forward as the end does, so this is a single pass.
        var start = 0;
        for (var end = 1; end < samples.Count; end++)
        {
            if (distance[end] < windowMetres - 1e-9) continue;
            while (start + 1 < end && distance[end] - distance[start + 1] >= windowMetres - 1e-9) start++;
            var span = distance[end] - distance[start];
            if (span <= Geometry2D.Epsilon) continue;
            result.Add((end, (samples[end].PositionMetres.Z - samples[start].PositionMetres.Z) / span));
        }

        return result;
    }
}
