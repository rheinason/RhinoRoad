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
    /// earlier than one full window along its leg has none, so is omitted. A leg shorter than the
    /// window in total is judged once, end to end, rather than never.
    /// </summary>
    /// <remarks>
    /// The window never reaches back across a change of direction or a standstill. At a cusp the
    /// vehicle drives back over the ground it has just climbed, so a window spanning it measures the
    /// net rise of up-and-back, which is close to nothing: 3 m up a 10 % ramp and 3 m back down it
    /// read as 2 %. Each leg between stops is therefore windowed on its own, starting from the cusp.
    /// </remarks>
    public static IReadOnlyList<(int Index, double Grade)> Compute(IReadOnlyList<RouteSample> samples, double windowMetres)
    {
        var result = new List<(int, double)>();
        if (samples.Count < 2) return result;

        var distance = new double[samples.Count];
        for (var index = 1; index < samples.Count; index++)
        {
            distance[index] = distance[index - 1] + samples[index].PositionMetres.XY.DistanceTo(samples[index - 1].PositionMetres.XY);
        }

        // A sample whose direction differs from the one before, or that starts from a standstill,
        // begins a new leg; the sample before it, where the vehicle stopped, is that leg's first.
        var legStart = 0;
        for (var index = 1; index <= samples.Count; index++)
        {
            var legEnds = index == samples.Count ||
                          samples[index].Direction != samples[index - 1].Direction ||
                          samples[index].StartsFromStandstill;
            if (!legEnds) continue;
            AddLeg(samples, distance, legStart, index - 1, windowMetres, result);
            legStart = index - 1;
        }

        return result;
    }

    private static void AddLeg(
        IReadOnlyList<RouteSample> samples,
        IReadOnlyList<double> distance,
        int first,
        int last,
        double windowMetres,
        List<(int, double)> result)
    {
        if (last <= first) return;
        var length = distance[last] - distance[first];
        if (length < windowMetres)
        {
            if (length > Geometry2D.Epsilon)
                result.Add((last, (samples[last].PositionMetres.Z - samples[first].PositionMetres.Z) / length));
            return;
        }

        // The window start only ever moves forward as the end does, so this is a single pass.
        var start = first;
        for (var end = first + 1; end <= last; end++)
        {
            if (distance[end] - distance[first] < windowMetres - 1e-9) continue;
            while (start + 1 < end && distance[end] - distance[start + 1] >= windowMetres - 1e-9) start++;
            var span = distance[end] - distance[start];
            if (span <= Geometry2D.Epsilon) continue;
            result.Add((end, (samples[end].PositionMetres.Z - samples[start].PositionMetres.Z) / span));
        }
    }
}
