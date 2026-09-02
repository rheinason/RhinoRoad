namespace RhinoRoad.Core;

/// <summary>
/// The line the designer meant, sampled densely, with heading and curvature along it.
/// </summary>
/// <remarks>
/// This is intent, not a route. Nothing here is required to be drivable — a drawn curve can turn
/// tighter than any vehicle can, and saying so is one of the things this tool exists to do. The
/// route is what a vehicle makes of it, and the gap between the two is the answer.
/// </remarks>
public sealed class IntentPath
{
    private readonly double[] _stations;

    private IntentPath(
        IReadOnlyList<Point3> points,
        double[] stations,
        double[] headings,
        double[] curvatures)
    {
        Points = points;
        _stations = stations;
        Headings = headings;
        Curvatures = curvatures;
    }

    public IReadOnlyList<Point3> Points { get; }

    public IReadOnlyList<double> Stations => _stations;

    public IReadOnlyList<double> Headings { get; }

    /// <summary>Signed curvature, left positive.</summary>
    public IReadOnlyList<double> Curvatures { get; }

    public double LengthMetres => _stations[^1];

    public int Count => Points.Count;

    /// <summary>Builds an intent path from points already spaced along the line, in metres.</summary>
    public static IntentPath FromPoints(IReadOnlyList<Point3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) throw new ArgumentException("An intended line needs at least two points.", nameof(points));

        var stations = new double[points.Count];
        for (var index = 1; index < points.Count; index++)
        {
            stations[index] = stations[index - 1] + points[index].XY.DistanceTo(points[index - 1].XY);
        }

        if (stations[^1] <= Geometry2D.Epsilon)
        {
            throw new ArgumentException("The intended line has no length in plan.", nameof(points));
        }

        // Central differences: heading from the neighbours' chord, curvature from how fast that
        // heading turns. Both are taken over the span the neighbours actually cover rather than
        // over an assumed spacing, so an unevenly sampled curve is not misread as a curving one.
        var headings = new double[points.Count];
        var curvatures = new double[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            var before = Math.Max(0, index - 1);
            var after = Math.Min(points.Count - 1, index + 1);
            headings[index] = Math.Atan2(
                points[after].Y - points[before].Y,
                points[after].X - points[before].X);
        }

        for (var index = 0; index < points.Count; index++)
        {
            var before = Math.Max(0, index - 1);
            var after = Math.Min(points.Count - 1, index + 1);
            var span = Math.Max(stations[after] - stations[before], Geometry2D.Epsilon);
            curvatures[index] = Geometry2D.NormalizeAngle(headings[after] - headings[before]) / span;
        }

        return new IntentPath(points, stations, headings, curvatures);
    }

    /// <summary>
    /// The sample nearest <paramref name="point"/>, searched only a little way ahead of
    /// <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A vehicle travels the line once, so its place on the line may only creep forward. Searching
    /// the whole line instead lets that place teleport: where a line loops back near itself, the
    /// closest point to a vehicle at the start of the loop is on the far side of it, and the
    /// follower would steer for the wrong part of the curve or decide it had already finished.
    /// </para>
    /// <para>
    /// <paramref name="maximumAdvanceMetres"/> is the leash. It has to exceed a single step, so the
    /// place can keep up, without being long enough to skip a feature of the line.
    /// </para>
    /// </remarks>
    public int NearestIndex(Point2 point, int from, double maximumAdvanceMetres)
    {
        var start = Math.Clamp(from, 0, Points.Count - 1);
        var limit = _stations[start] + maximumAdvanceMetres;
        var best = Points[start].XY.DistanceTo(point);
        var bestIndex = start;
        for (var index = start + 1; index < Points.Count && _stations[index] <= limit; index++)
        {
            var distance = Points[index].XY.DistanceTo(point);
            if (distance >= best) continue;
            best = distance;
            bestIndex = index;
        }

        return bestIndex;
    }

    /// <summary>The index at a station along the line.</summary>
    public int IndexAt(double stationMetres)
    {
        var index = Array.BinarySearch(_stations, Math.Clamp(stationMetres, 0.0, LengthMetres));
        if (index < 0) index = ~index;
        return Math.Clamp(index, 0, Points.Count - 1);
    }

}
