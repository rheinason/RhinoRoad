using Clipper2Lib;

namespace RhinoRoad.Core;

public sealed record AccessSectionInterval(Point2 Start, Point2 End)
{
    public double WidthMetres => Start.DistanceTo(End);
}

/// <summary>Space required by separate journeys, including disconnected regions and islands.</summary>
public static class AccessFootprint
{
    public static Point2? Outside(SweptRegion region, IEnumerable<IReadOnlyList<Point2>> allowed)
    {
        var clip = new PathsD(allowed.Select(loop => Path(loop, true)));
        var difference = Clipper.Difference(Paths(region), clip, FillRule.NonZero, 5);
        var area = difference.FirstOrDefault(path => Math.Abs(Clipper.Area(path)) > 1e-8);
        return area is null ? null : new Point2(area[0].x, area[0].y);
    }

    public static Point2? ConflictPoint(SweptRegion region, IReadOnlyList<Point2> obstacle, bool closed)
    {
        if (obstacle.Count < 2) return null;
        if (closed)
        {
            var intersection = Clipper.Intersect(Paths(region), new PathsD { Path(obstacle, true) }, FillRule.NonZero, 5);
            var area = intersection.FirstOrDefault(path => Math.Abs(Clipper.Area(path)) > 1e-8);
            if (area is not null) return new Point2(area[0].x, area[0].y);
        }
        for (var i = 0; i < (closed ? obstacle.Count : obstacle.Count - 1); i++)
        {
            var a = obstacle[i];
            var b = obstacle[(i + 1) % obstacle.Count];
            if (a.DistanceTo(b) < 1e-6) continue;
            var interval = Measure([region], a, b).FirstOrDefault();
            if (interval is not null) return interval.Start;
        }
        return null;
    }

    private static PathsD Paths(SweptRegion region) =>
        new(new[] { Path(region.OuterBoundary, true) }.Concat(region.Holes.Select(h => Path(h, false))));

    public static IReadOnlyList<SweptRegion> Combine(IEnumerable<SweptRegion> regions)
    {
        var paths = new PathsD();
        foreach (var region in regions)
        {
            paths.Add(Path(region.OuterBoundary, true));
            foreach (var hole in region.Holes) paths.Add(Path(hole, false));
        }
        var loops = Clipper.Union(paths, new PathsD(), FillRule.NonZero, 5)
            .Select(path => (IReadOnlyList<Point2>)path.Select(p => new Point2(p.x, p.y)).ToArray()).ToArray();
        var outers = loops.Where(loop => SweptRegionBuilder.SignedArea(loop) > 0).ToArray();
        var holes = loops.Where(loop => SweptRegionBuilder.SignedArea(loop) < 0).ToArray();
        return outers.Select(outer => new SweptRegion(outer, holes.Where(hole =>
            outers.Where(candidate => Geometry2D.Contains(candidate, hole[0]))
                .MinBy(candidate => Math.Abs(SweptRegionBuilder.SignedArea(candidate))) == outer).ToArray())).ToArray();
    }

    /// <summary>Intersect a finite section with occupied space; never bridge gaps or holes.</summary>
    public static IReadOnlyList<AccessSectionInterval> Measure(
        IReadOnlyList<SweptRegion> regions, Point2 start, Point2 end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = start.DistanceTo(end);
        if (!double.IsFinite(length) || length < 1e-6)
            throw new ArgumentException("Pick two distinct finite section points.");
        var cuts = new List<double> { 0, 1 };
        foreach (var loop in regions.SelectMany(r => new[] { r.OuterBoundary }.Concat(r.Holes)))
        for (var i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var cross = dx * ey - dy * ex;
            var ax = a.X - start.X;
            var ay = a.Y - start.Y;
            if (Math.Abs(cross) < 1e-12)
            {
                if (Math.Abs(ax * dy - ay * dx) > 1e-9 * length) continue;
                cuts.Add(Math.Clamp((ax * dx + ay * dy) / (length * length), 0, 1));
                cuts.Add(Math.Clamp(((b.X - start.X) * dx + (b.Y - start.Y) * dy) / (length * length), 0, 1));
                continue;
            }
            var t = (ax * ey - ay * ex) / cross;
            var u = (ax * dy - ay * dx) / cross;
            if (t >= 0 && t <= 1 && u >= 0 && u <= 1) cuts.Add(t);
        }
        var sorted = cuts.Order().Distinct().ToArray();
        var intervals = new List<AccessSectionInterval>();
        Point2 At(double t) => new(start.X + t * dx, start.Y + t * dy);
        for (var i = 1; i < sorted.Length; i++)
        {
            if ((sorted[i] - sorted[i - 1]) * length < 1e-6) continue;
            var midpoint = At((sorted[i] + sorted[i - 1]) / 2);
            if (!regions.Any(r => Geometry2D.Contains(r.OuterBoundary, midpoint) &&
                                 !r.Holes.Any(h => Geometry2D.Contains(h, midpoint)))) continue;
            var a = At(sorted[i - 1]);
            var b = At(sorted[i]);
            if (intervals.Count > 0 && intervals[^1].End.DistanceTo(a) < 1e-6)
                intervals[^1] = intervals[^1] with { End = b };
            else intervals.Add(new AccessSectionInterval(a, b));
        }
        return intervals;
    }

    private static PathD Path(IReadOnlyList<Point2> loop, bool positive)
    {
        var path = new PathD(loop.Select(p => new PointD(p.X, p.Y)));
        if ((Clipper.Area(path) > 0) != positive) path.Reverse();
        return path;
    }
}
