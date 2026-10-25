using Clipper2Lib;

namespace RhinoRoad.Core;

/// <summary>Set differences between two clearance footprints, with the regions and their areas (m²).</summary>
public sealed record EnvelopeComparisonResult(
    IReadOnlyList<SweptRegion> OnlyA,
    IReadOnlyList<SweptRegion> OnlyB,
    IReadOnlyList<SweptRegion> Both,
    double AreaA,
    double AreaB,
    double OnlyAArea,
    double OnlyBArea,
    double BothArea)
{
    /// <summary>Positive when B needs more space than A.</summary>
    public double NetDifference => AreaB - AreaA;
}

/// <summary>Compares the space two designs occupy: what only A needs, what only B needs, and what both need.</summary>
public static class EnvelopeComparison
{
    private const int Precision = 5;

    public static EnvelopeComparisonResult Compare(IReadOnlyList<SweptRegion> a, IReadOnlyList<SweptRegion> b)
    {
        // Union each side first so overlapping regions within one list are never double counted.
        var pathsA = Clipper.Union(Paths(a), new PathsD(), FillRule.NonZero, Precision);
        var pathsB = Clipper.Union(Paths(b), new PathsD(), FillRule.NonZero, Precision);
        var onlyA = Clipper.Difference(pathsA, pathsB, FillRule.NonZero, Precision);
        var onlyB = Clipper.Difference(pathsB, pathsA, FillRule.NonZero, Precision);
        var both = Clipper.Intersect(pathsA, pathsB, FillRule.NonZero, Precision);
        return new EnvelopeComparisonResult(
            Regions(onlyA), Regions(onlyB), Regions(both),
            NetArea(pathsA), NetArea(pathsB), NetArea(onlyA), NetArea(onlyB), NetArea(both));
    }

    // Outers are positive and holes negative in Clipper output, so the signed sum is the occupied area.
    private static double NetArea(PathsD paths) => Math.Max(0, paths.Sum(Clipper.Area));

    private static PathsD Paths(IEnumerable<SweptRegion> regions)
    {
        var paths = new PathsD();
        foreach (var region in regions)
        {
            paths.Add(Path(region.OuterBoundary, true));
            foreach (var hole in region.Holes) paths.Add(Path(hole, false));
        }
        return paths;
    }

    private static PathD Path(IReadOnlyList<Point2> loop, bool positive)
    {
        var path = new PathD(loop.Select(p => new PointD(p.X, p.Y)));
        if ((Clipper.Area(path) > 0) != positive) path.Reverse();
        return path;
    }

    private static IReadOnlyList<SweptRegion> Regions(PathsD paths)
    {
        var loops = paths.Where(path => Math.Abs(Clipper.Area(path)) > 1e-8)
            .Select(path => (IReadOnlyList<Point2>)path.Select(p => new Point2(p.x, p.y)).ToArray()).ToArray();
        var outers = loops.Where(loop => SweptRegionBuilder.SignedArea(loop) > 0).ToArray();
        var holes = loops.Where(loop => SweptRegionBuilder.SignedArea(loop) < 0).ToArray();
        return outers.Select(outer => new SweptRegion(outer, holes.Where(hole =>
            outers.Where(candidate => Geometry2D.Contains(candidate, hole[0]))
                .MinBy(candidate => Math.Abs(SweptRegionBuilder.SignedArea(candidate))) == outer).ToArray())).ToArray();
    }
}
