using Clipper2Lib;

namespace RhinoRoad.Core;

/// <summary>One obstacle as plan points in metres. An open one is a line the sweep must not cross.</summary>
public sealed record SiteObstacle(IReadOnlyList<Point2> Points, bool Closed);

/// <summary>
/// The site a journey is driven against: obstacles to keep clear of and the closed areas it must
/// stay inside. Empty lists mean that check is off.
/// </summary>
public sealed record SiteConstraints(
    IReadOnlyList<SiteObstacle> Obstacles,
    IReadOnlyList<IReadOnlyList<Point2>> AllowedAreas)
{
    public static readonly SiteConstraints None = new([], []);

    public bool IsEmpty => Obstacles.Count == 0 && AllowedAreas.Count == 0;
}

/// <summary>
/// Where a sweep runs into the site: the overlap with closed obstacles, the runs of open obstacle
/// lines it covers, and whatever lies outside the allowed area.
/// </summary>
public sealed record SiteClash(
    IReadOnlyList<IReadOnlyList<Point2>> ObstacleOverlaps,
    IReadOnlyList<IReadOnlyList<Point2>> ObstacleCrossings,
    IReadOnlyList<IReadOnlyList<Point2>> OutsideAllowedArea,
    int ObstaclesHit)
{
    public static readonly SiteClash None = new([], [], [], 0);

    public bool LeavesAllowedArea => OutsideAllowedArea.Count > 0;

    public bool Any => ObstaclesHit > 0 || LeavesAllowedArea;
}

/// <summary>
/// The site clash of a sweep while it is still being aimed, so a designer sees it before the click
/// rather than in the report afterwards.
/// </summary>
/// <remarks>
/// <para>
/// This is a picture, not the check. The committed analysis measures the baked envelopes against the
/// site and is what the report states; this runs on every mouse move, so it quantises to a millimetre
/// rather than the 1e-5 m the sweep is built at, and skips any obstacle whose bounding box the sweep
/// does not reach. A millimetre is far inside any clearance anyone draws, so what it shows red is red
/// in the report too.
/// </para>
/// <para>
/// An open obstacle is a wall or kerb line with no inside. Intersecting it as a polygon would close
/// it with a chord that is not on the site, so it is clipped as an open path instead, and what is
/// shown is the length of line the sweep covers.
/// </para>
/// </remarks>
public static class SiteClashPreview
{
    private const int Precision = 3;

    /// <summary>Overlaps smaller than this are quantisation noise along a shared edge.</summary>
    private const double MinimumAreaSquareMetres = 1e-4;

    private const double MinimumLengthMetres = 1e-3;

    public static SiteClash Find(SweptRegion region, SiteConstraints site)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(site);
        if (site.IsEmpty || region.OuterBoundary.Count < 3) return SiteClash.None;

        var subject = Paths(region);
        var bounds = Bounds(region.OuterBoundary);
        var overlaps = new List<IReadOnlyList<Point2>>();
        var crossings = new List<IReadOnlyList<Point2>>();
        var hit = 0;
        foreach (var obstacle in site.Obstacles)
        {
            var closed = obstacle.Closed && obstacle.Points.Count >= 3;
            if (obstacle.Points.Count < 2 || !bounds.Overlaps(Bounds(obstacle.Points))) continue;
            var clashed = false;
            if (closed)
            {
                foreach (var path in Clipper.Intersect(subject, new PathsD { Path(obstacle.Points, true) }, FillRule.NonZero, Precision))
                {
                    if (Math.Abs(Clipper.Area(path)) < MinimumAreaSquareMetres) continue;
                    overlaps.Add(Loop(path));
                    clashed = true;
                }
            }
            else
            {
                var clipper = new ClipperD(Precision);
                clipper.AddOpenSubject(new PathsD { Path(obstacle.Points, null) });
                clipper.AddClip(subject);
                var closedSolution = new PathsD();
                var openSolution = new PathsD();
                clipper.Execute(ClipType.Intersection, FillRule.NonZero, closedSolution, openSolution);
                foreach (var path in openSolution)
                {
                    if (path.Count < 2 || Length(path) < MinimumLengthMetres) continue;
                    crossings.Add(Loop(path));
                    clashed = true;
                }
            }

            if (clashed) hit++;
        }

        var outside = new List<IReadOnlyList<Point2>>();
        var allowed = site.AllowedAreas.Where(area => area.Count >= 3).ToArray();
        if (allowed.Length > 0)
        {
            var clip = new PathsD(allowed.Select(area => Path(area, true)));
            foreach (var path in Clipper.Difference(subject, clip, FillRule.NonZero, Precision))
            {
                if (Math.Abs(Clipper.Area(path)) >= MinimumAreaSquareMetres) outside.Add(Loop(path));
            }
        }

        return hit == 0 && outside.Count == 0
            ? SiteClash.None
            : new SiteClash(overlaps, crossings, outside, hit);
    }

    private static PathsD Paths(SweptRegion region) =>
        new(new[] { Path(region.OuterBoundary, true) }.Concat(region.Holes.Select(hole => Path(hole, false))));

    /// <summary>A Clipper path, turned to the orientation asked for; null leaves an open path as drawn.</summary>
    private static PathD Path(IReadOnlyList<Point2> points, bool? positive)
    {
        var path = new PathD(points.Select(point => new PointD(point.X, point.Y)));
        if (positive is bool wanted && (Clipper.Area(path) > 0) != wanted) path.Reverse();
        return path;
    }

    private static IReadOnlyList<Point2> Loop(PathD path) => path.Select(point => new Point2(point.x, point.y)).ToArray();

    private static double Length(PathD path)
    {
        var length = 0.0;
        for (var index = 1; index < path.Count; index++)
        {
            length += Math.Sqrt(
                Math.Pow(path[index].x - path[index - 1].x, 2) + Math.Pow(path[index].y - path[index - 1].y, 2));
        }

        return length;
    }

    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool Overlaps(Box other) =>
            MinX <= other.MaxX && other.MinX <= MaxX && MinY <= other.MaxY && other.MinY <= MaxY;
    }

    private static Box Bounds(IReadOnlyList<Point2> points) => new(
        points.Min(point => point.X), points.Min(point => point.Y),
        points.Max(point => point.X), points.Max(point => point.Y));
}
