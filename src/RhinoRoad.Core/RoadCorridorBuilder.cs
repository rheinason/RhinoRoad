using Clipper2Lib;

namespace RhinoRoad.Core;

/// <summary>
/// A fixed-width road corridor: the two edge lines as drawn, and the region between them once
/// self-overlap has been resolved.
/// </summary>
public sealed record RoadCorridor(
    IReadOnlyList<Point2> LeftEdge,
    IReadOnlyList<Point2> RightEdge,
    SweptRegion? Region);

/// <summary>
/// Builds the preliminary road corridor by offsetting the route to each side.
/// </summary>
/// <remarks>
/// Offsetting the centreline is a per-sample operation — every route sample already carries its
/// heading, so each edge point is one sine and cosine away. Handing the job to a curve-offset
/// routine instead costs far more than the geometry warrants and scales badly: measured in Rhino,
/// offsetting a route polyline to both sides took 59 ms at 500 points but 615 ms at 5,000, and a
/// route sampled every 0.10 m reaches 5,000 points at 500 m.
///
/// The naive offset self-intersects wherever the inside radius is tighter than the offset distance.
/// Rather than special-case that, the raw corridor is closed into a polygon and cleaned by a
/// non-zero union, which discards the inverted lobes and leaves the region actually enclosed.
/// </remarks>
public static class RoadCorridorBuilder
{
    public static RoadCorridor Build(
        IReadOnlyList<RouteSample> route,
        double leftWidthMetres,
        double rightWidthMetres)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Count < 2) return new RoadCorridor([], [], null);

        var left = new List<Point2>(route.Count);
        var right = new List<Point2>(route.Count);
        foreach (var sample in route)
        {
            // Edge offsets follow the direction of travel, so a reversing leg keeps "left" on the
            // driver's left rather than flipping sides mid-route.
            var heading = Geometry2D.NormalizeAngle(
                sample.PathHeadingRadians + (sample.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
            var normal = new Point2(-Math.Sin(heading), Math.Cos(heading));
            var centre = sample.PositionMetres.XY;
            left.Add(centre + (normal * leftWidthMetres));
            right.Add(centre - (normal * rightWidthMetres));
        }

        return new RoadCorridor(left, right, CleanRegion(left, right));
    }

    /// <summary>Closes the two edges into a polygon and removes any self-overlap.</summary>
    private static SweptRegion? CleanRegion(IReadOnlyList<Point2> left, IReadOnlyList<Point2> right)
    {
        var loop = new List<Point2>(left.Count + right.Count);
        loop.AddRange(left);
        for (var index = right.Count - 1; index >= 0; index--) loop.Add(right[index]);

        var result = SweptRegionBuilder.FromOutlines([loop]);
        return result.Region;
    }
}
