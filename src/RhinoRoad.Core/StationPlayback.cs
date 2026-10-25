namespace RhinoRoad.Core;

/// <summary>
/// Pure maths behind scrubbing and playing back a swept path: finding the pose for a station,
/// advancing a station over real time, and choosing a bounded subset of poses for the swept trail.
/// Kept free of UI so the inspector only has to wire it up.
/// </summary>
public static class StationPlayback
{
    /// <summary>
    /// Index of the entry whose station is nearest <paramref name="station"/>, by binary search.
    /// Stations must be ascending (as pose and review samples are). Returns -1 for an empty list.
    /// Ties go to the earlier entry.
    /// </summary>
    public static int NearestIndex<T>(IReadOnlyList<T> items, Func<T, double> stationOf, double station)
    {
        if (items.Count == 0) return -1;
        var low = 0;
        var high = items.Count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (stationOf(items[middle]) < station) low = middle + 1;
            else high = middle;
        }
        // low is the first entry at or beyond the station; the one before it may be nearer.
        if (low > 0 && station - stationOf(items[low - 1]) <= stationOf(items[low]) - station) return low - 1;
        return low;
    }

    public static double Clamp(double station, double routeLength)
        => double.IsNaN(station) ? 0.0 : Math.Clamp(station, 0.0, Math.Max(0.0, routeLength));

    /// <summary>
    /// Moves a station on by <paramref name="elapsedSeconds"/> of wall time at
    /// <paramref name="speedMetresPerSecond"/> times <paramref name="multiplier"/>. Stops at the end
    /// of the route and says so, so the caller can stop its timer.
    /// </summary>
    public static (double Station, bool Finished) Advance(
        double station, double routeLength, double elapsedSeconds, double speedMetresPerSecond, double multiplier)
    {
        var length = Math.Max(0.0, routeLength);
        var step = Math.Max(0.0, elapsedSeconds) * Math.Max(0.0, speedMetresPerSecond) * Math.Max(0.0, multiplier);
        var next = Clamp(station + step, length);
        return (next, next >= length);
    }

    /// <summary>
    /// Indices of the poses (ascending) that form the swept trail up to <paramref name="lastIndex"/>:
    /// the first pose, then an even subset of at most <paramref name="maximumPoses"/> entries ending
    /// on the last, so a long route costs a bounded amount per frame.
    /// </summary>
    public static IReadOnlyList<int> TrailIndices(int count, int lastIndex, int maximumPoses)
    {
        if (count <= 0 || lastIndex < 0 || maximumPoses <= 0) return [];
        lastIndex = Math.Min(lastIndex, count - 1);
        var available = lastIndex + 1;
        if (available <= maximumPoses) return Enumerable.Range(0, available).ToArray();
        if (maximumPoses == 1) return [lastIndex];
        var result = new int[maximumPoses];
        for (var index = 0; index < maximumPoses; index++)
            result[index] = (int)Math.Round(index * (double)lastIndex / (maximumPoses - 1));
        return result;
    }
}
