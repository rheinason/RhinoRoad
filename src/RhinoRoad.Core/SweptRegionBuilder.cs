using Clipper2Lib;

namespace RhinoRoad.Core;

/// <summary>
/// The plan area a vehicle body occupies over a manoeuvre: an outer boundary plus any enclosed
/// holes.
/// </summary>
/// <remarks>
/// Holes are not an edge case. A vehicle circulating a roundabout sweeps an annulus, and the island
/// it drives around is a genuine hole in the occupied area. Collapsing that to a single closed
/// outline reports the island as occupied, which is exactly backwards for clearance work.
/// </remarks>
public sealed record SweptRegion(
    IReadOnlyList<Point2> OuterBoundary,
    IReadOnlyList<IReadOnlyList<Point2>> Holes)
{
    public double AreaSquareMetres =>
        Math.Abs(SweptRegionBuilder.SignedArea(OuterBoundary)) -
        Holes.Sum(hole => Math.Abs(SweptRegionBuilder.SignedArea(hole)));
}

public enum SweptRegionStatus
{
    Success,
    NoInput,
    /// <summary>The footprints did not form one connected area, so the sweep is discontinuous.</summary>
    Disjoint
}

public sealed record SweptRegionResult(
    SweptRegionStatus Status,
    SweptRegion? Region,
    int RegionCount,
    string Message)
{
    private readonly IReadOnlyList<SweptRegion>? _regions;

    public bool IsSuccess => Status == SweptRegionStatus.Success && Region is not null;

    /// <summary>
    /// Every separate area the union produced, largest first, each with the holes it encloses.
    /// <see cref="Region"/> stays the largest of them for callers that only want one.
    /// </summary>
    /// <remarks>
    /// A disjoint sweep is a failure, but its pieces are still real occupied ground. Reporting only
    /// the largest hides the rest, and pinning every hole onto it misplaces the holes of the others.
    /// </remarks>
    public IReadOnlyList<SweptRegion> Regions
    {
        get => _regions ?? (Region is null ? [] : [Region]);
        init => _regions = value;
    }
}

/// <summary>
/// Builds swept regions by polygon union rather than curve Boolean operations.
/// </summary>
/// <remarks>
/// Unioning ~1500 near-identical overlapping footprints with a curve Boolean is fragile precisely
/// where the turn is tightest — the tight-turn certification cases failed with 129 and 225 disjoint
/// fragments surviving, and the largest fragment then measured as a plausible but wrong envelope.
/// Clipper works on polygons at a fixed precision, so this is deterministic and independent of any
/// document tolerance, and it lives in Core where it can be tested without Rhino.
/// </remarks>
public static class SweptRegionBuilder
{
    /// <summary>Decimal places Clipper quantises to. 1e-5 m is far finer than any survey input.</summary>
    private const int Precision = 5;

    /// <summary>Collapses vertices closer than this when cleaning output loops.</summary>
    private const double WeldToleranceMetres = 1e-4;

    /// <summary>
    /// Douglas-Peucker epsilon applied to output loops. Unioning footprints every few centimetres
    /// leaves boundaries with tens of thousands of near-collinear vertices, and everything
    /// downstream pays for them: baking, viewport drawing, resampling and point-in-curve tests.
    /// One millimetre is two orders of magnitude inside the 0.10 m certification tolerance and the
    /// 0.30 m default clearance, so the shape is unchanged for any purpose the tool serves.
    /// </summary>
    private const double SimplifyToleranceMetres = 1e-3;

    public static SweptRegionResult FromPoses(IReadOnlyList<VehiclePose> poses)
    {
        ArgumentNullException.ThrowIfNull(poses);
        return FromOutlines(poses.SelectMany(pose => pose.OccupiedOutlinesWorldMetres).ToArray());
    }

    public static SweptRegionResult FromOutlines(IReadOnlyList<IReadOnlyList<Point2>> outlines)
    {
        ArgumentNullException.ThrowIfNull(outlines);
        var subject = new PathsD(outlines.Count);
        foreach (var outline in outlines)
        {
            if (outline.Count < 3) continue;
            var path = new PathD(outline.Count);
            foreach (var point in outline) path.Add(new PointD(point.X, point.Y));
            subject.Add(path);
        }

        if (subject.Count == 0)
        {
            return new SweptRegionResult(SweptRegionStatus.NoInput, null, 0, "No usable footprints were supplied.");
        }

        var solution = Clipper.Union(subject, new PathsD(), FillRule.NonZero, Precision);
        return Assemble(Clipper.SimplifyPaths(solution, SimplifyToleranceMetres, isClosedPath: true));
    }

    /// <summary>
    /// Grows a region outward by <paramref name="distanceMetres"/>, the clearance envelope. Rounded
    /// joins match how a body corner actually sweeps around a pivot.
    /// </summary>
    public static SweptRegionResult Inflate(SweptRegion region, double distanceMetres)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (distanceMetres <= 0.0)
        {
            return new SweptRegionResult(SweptRegionStatus.Success, region, 1, "No clearance requested.");
        }

        var subject = new PathsD { ToPath(region.OuterBoundary) };
        foreach (var hole in region.Holes) subject.Add(ToPath(hole));

        var inflated = Clipper.InflatePaths(
            subject,
            distanceMetres,
            JoinType.Round,
            EndType.Polygon,
            miterLimit: 2.0,
            precision: Precision,
            arcTolerance: 0.0);
        return Assemble(Clipper.SimplifyPaths(inflated, SimplifyToleranceMetres, isClosedPath: true));
    }

    /// <summary>
    /// Grows every piece of a sweep by <paramref name="distanceMetres"/> together, so pieces that
    /// come within twice the distance of each other merge as their clearance envelopes do.
    /// </summary>
    public static SweptRegionResult Inflate(IReadOnlyList<SweptRegion> regions, double distanceMetres)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 1) return Inflate(regions[0], distanceMetres);
        if (regions.Count == 0)
            return new SweptRegionResult(SweptRegionStatus.NoInput, null, 0, "No region to offset.");
        var subject = new PathsD();
        foreach (var region in regions)
        {
            subject.Add(ToPath(region.OuterBoundary));
            foreach (var hole in region.Holes) subject.Add(ToPath(hole));
        }

        if (distanceMetres <= 0.0) return Assemble(subject);
        var inflated = Clipper.InflatePaths(
            subject,
            distanceMetres,
            JoinType.Round,
            EndType.Polygon,
            miterLimit: 2.0,
            precision: Precision,
            arcTolerance: 0.0);
        return Assemble(Clipper.SimplifyPaths(inflated, SimplifyToleranceMetres, isClosedPath: true));
    }

    private static SweptRegionResult Assemble(PathsD solution)
    {
        var regions = Nest(solution);
        if (regions.Count == 0)
        {
            return new SweptRegionResult(SweptRegionStatus.NoInput, null, 0, "The union produced no closed area.");
        }

        var region = regions[0];
        if (regions.Count > 1)
        {
            return new SweptRegionResult(
                SweptRegionStatus.Disjoint,
                region,
                regions.Count,
                FormattableString.Invariant($"The footprints formed {regions.Count} disconnected areas rather than one continuous sweep."))
            {
                Regions = regions
            };
        }

        var holeNote = region.Holes.Count == 0 ? string.Empty : $" with {region.Holes.Count} enclosed hole(s)";
        return new SweptRegionResult(
            SweptRegionStatus.Success,
            region,
            1,
            FormattableString.Invariant($"Swept region of {region.AreaSquareMetres:0.00} m²{holeNote}."))
        {
            Regions = regions
        };
    }

    /// <summary>Loops smaller than this are rounding left by a polygon operation, not area.</summary>
    private const double MinimumLoopAreaSquareMetres = 1e-8;

    /// <summary>
    /// Sorts the closed paths of a polygon operation into regions: each outer boundary with the
    /// holes it directly encloses, largest region first. Every caller that turns Clipper output into
    /// regions goes through here.
    /// </summary>
    /// <remarks>
    /// The nesting is Clipper's own, read from a <see cref="PolyTreeD"/>, not reconstructed from the
    /// loops afterwards. Testing which outer contains a hole's first vertex fails exactly where it
    /// matters: Clipper may start a hole on the outer boundary it touches -- a triangle cut from a
    /// slanted edge -- and a ray cast from a boundary point says inside or outside by rounding, so
    /// the hole went to the wrong region or to none. In the tree, a hole is a child of
    /// the outer it belongs to, and an island standing in a hole is a child of that hole, so it
    /// becomes a region of its own rather than part of the region around it.
    /// </remarks>
    internal static IReadOnlyList<SweptRegion> Nest(PathsD paths, int precision = Precision)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var tree = new PolyTreeD();
        Clipper.BooleanOp(ClipType.Union, paths, new PathsD(), tree, FillRule.NonZero, precision);
        var regions = new List<(SweptRegion Region, double Area)>();
        AddOuters(tree, regions);
        return regions.OrderByDescending(item => item.Area).Select(item => item.Region).ToArray();

        static void AddOuters(PolyPathD parent, List<(SweptRegion Region, double Area)> regions)
        {
            for (var index = 0; index < parent.Count; index++)
            {
                var outer = parent[index];
                var holes = new List<IReadOnlyList<Point2>>();
                for (var child = 0; child < outer.Count; child++)
                {
                    var hole = outer[child];
                    if (Usable(hole.Polygon) is { } holeLoop) holes.Add(holeLoop);
                    AddOuters(hole, regions);
                }

                if (Usable(outer.Polygon) is { } loop) regions.Add((new SweptRegion(loop, holes), Math.Abs(SignedArea(loop))));
            }
        }

        static IReadOnlyList<Point2>? Usable(PathD? path)
        {
            if (path is null) return null;
            var loop = ToLoop(path);
            return loop.Count >= 3 && Math.Abs(SignedArea(loop)) > MinimumLoopAreaSquareMetres ? loop : null;
        }
    }

    private static PathD ToPath(IReadOnlyList<Point2> loop)
    {
        var path = new PathD(loop.Count);
        foreach (var point in loop) path.Add(new PointD(point.X, point.Y));
        return path;
    }

    internal static IReadOnlyList<Point2> ToLoop(PathD path)
    {
        var loop = new List<Point2>(path.Count);
        foreach (var point in path)
        {
            var candidate = new Point2(point.x, point.y);
            if (loop.Count > 0 && loop[^1].DistanceTo(candidate) <= WeldToleranceMetres) continue;
            loop.Add(candidate);
        }

        // Clipper returns implicitly closed loops; drop a repeated closing vertex if one appears.
        while (loop.Count >= 2 && loop[0].DistanceTo(loop[^1]) <= WeldToleranceMetres) loop.RemoveAt(loop.Count - 1);
        return loop;
    }

    internal static double SignedArea(IReadOnlyList<Point2> loop)
    {
        if (loop.Count < 3) return 0.0;
        var total = 0.0;
        for (var index = 0; index < loop.Count; index++)
        {
            var current = loop[index];
            var next = loop[(index + 1) % loop.Count];
            total += (current.X * next.Y) - (next.X * current.Y);
        }

        return total * 0.5;
    }
}
