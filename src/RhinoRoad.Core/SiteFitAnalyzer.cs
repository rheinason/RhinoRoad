using Clipper2Lib;

namespace RhinoRoad.Core;

/// <summary>A site curve in plan metres: an obstacle edge, a kerb line, a yard boundary.</summary>
public sealed record SiteCurve(IReadOnlyList<Point2> Points, bool IsClosed);

/// <summary>One stretch of the route over which the vehicle reaches a site conflict.</summary>
/// <param name="WorstMarginMetres">
/// The smallest margin in the stretch, clearance already taken off: below zero the clearance
/// allowance does not fit, and below minus the clearance the body itself is in conflict.
/// </param>
public sealed record SiteConflictRange(
    double StartStationMetres,
    double EndStationMetres,
    double WorstStationMetres,
    Point3 WorstPointMetres,
    double WorstMarginMetres);

/// <summary>
/// Where the clearance envelope meets one site constraint, and how much of it there is.
/// </summary>
/// <param name="SourceIndex">The obstacle's index in the input; -1 for the allowed area and the road.</param>
/// <param name="BodyConflict">True when the body sweep itself, not only its clearance, is in conflict.</param>
/// <param name="Regions">
/// The conflict as area: clearance envelope inside an obstacle, or outside the allowed area or the
/// road corridor. Empty for an open obstacle, which has no inside.
/// </param>
/// <param name="OpenPieces">The parts of an open obstacle that lie inside the clearance envelope.</param>
public sealed record SiteConflict(
    ViolationKind Kind,
    int SourceIndex,
    bool BodyConflict,
    IReadOnlyList<SweptRegion> Regions,
    IReadOnlyList<IReadOnlyList<Point2>> OpenPieces,
    double AreaSquareMetres,
    double LengthMetres,
    IReadOnlyList<SiteConflictRange> Ranges);

/// <param name="MinimumClearanceMetres">
/// Smallest distance from the body sweep to any obstacle or allowed-area edge, zero where the body is
/// in conflict. Null when there is no body sweep or nothing to measure against.
/// </param>
/// <param name="ObstacleMargins">
/// Per pose, the distance from the vehicle to the nearest obstacle less the clearance; negative
/// where the clearance allowance does not fit. Null when no obstacles were given.
/// </param>
/// <param name="AllowedAreaMargins">Per pose, the same margin to the edge of the allowed area.</param>
/// <param name="RoadMargins">Per pose, the same margin to the edge of the fixed-width road.</param>
public sealed record SiteFitOutcome(
    double? MinimumClearanceMetres,
    IReadOnlyList<SiteConflict> Conflicts,
    IReadOnlyList<AnalysisViolation> Violations,
    IReadOnlyList<double>? ObstacleMargins,
    IReadOnlyList<double>? AllowedAreaMargins,
    IReadOnlyList<double>? RoadMargins);

/// <summary>
/// Checks a swept path against the site: obstacles it must stay clear of, and the allowed area and
/// fixed-width road it must stay inside.
/// </summary>
/// <remarks>
/// Two views of the same question are answered here, and they have to agree. The envelopes say
/// whether anything conflicts at all and how much: a Clipper intersection or difference, exact on
/// the polygons, and the area a designer can act on. The poses say where: every pose measures its
/// own outlines against each constraint, and the stretch of stations whose poses come within the
/// clearance is where the conflict is on the road. That per-pose margin is also the profile the
/// inspector draws.
///
/// <para>
/// A margin is a signed distance. Measuring only between outlines misses the cases that matter most
/// on a real site -- a bollard or column wholly under a trailer never meets its outline -- so each
/// measure tests containment too, and an overlap reads as how deep it goes, negated.
/// </para>
/// </remarks>
public static class SiteFitAnalyzer
{
    private const int Precision = 5;

    /// <summary>Areas below this are Clipper rounding, not a conflict.</summary>
    private const double AreaToleranceSquareMetres = 1e-8;

    private const double LengthToleranceMetres = 1e-6;

    /// <summary>
    /// The least an overlap can read as. Two outlines crossing like a plus sign have no vertex inside
    /// each other, so their measured depth is zero; they still overlap, and must not read as touching.
    /// </summary>
    private const double MinimumOverlapMetres = 1e-6;

    public static SiteFitOutcome Check(
        IReadOnlyList<VehiclePose> poses,
        SweptRegion? body,
        SweptRegion? clearance,
        IReadOnlyList<SiteCurve> obstacles,
        IReadOnlyList<IReadOnlyList<Point2>> allowedBoundaries,
        SweptRegion? road,
        double clearanceMetres)
    {
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentNullException.ThrowIfNull(obstacles);
        ArgumentNullException.ThrowIfNull(allowedBoundaries);
        var shapes = poses.Select(PoseShape.Of).ToArray();
        var usableObstacles = obstacles
            .Select((curve, index) => (Curve: curve, Index: index))
            .Where(item => item.Curve.Points.Count >= (item.Curve.IsClosed ? 3 : 2))
            .Select(item => new Obstacle(item.Index, item.Curve, Box2.Of(item.Curve.Points)))
            .ToArray();
        var closedBoundaries = allowedBoundaries.Where(loop => loop.Count >= 3).ToArray();
        var allowedArea = closedBoundaries.Length == 0 ? null : Area.Of(UnionPaths(closedBoundaries));
        var roadArea = road is null ? null : Area.Of(AccessFootprint.Paths(road));
        var conflicts = new List<SiteConflict>();

        // Region-level conflicts first: they decide which constraints need a station range at all.
        var conflictingObstacles = new Dictionary<int, (ObstacleOverlap Overlap, bool Body)>();
        if (clearance is not null)
        {
            foreach (var obstacle in usableObstacles)
            {
                var overlap = Overlap(clearance, obstacle.Curve);
                if (!overlap.Any) continue;
                var bodyOverlap = body is not null && Overlap(body, obstacle.Curve).Any;
                conflictingObstacles[obstacle.Index] = (overlap, bodyOverlap);
            }
        }

        Measure[]? obstacleMargins = null;
        var perObstacle = new Dictionary<int, Measure[]>();
        if (usableObstacles.Length > 0)
            obstacleMargins = ObstacleMargins(shapes, usableObstacles, clearanceMetres, conflictingObstacles.Keys.ToHashSet(), out perObstacle);
        foreach (var obstacle in usableObstacles)
        {
            if (!conflictingObstacles.TryGetValue(obstacle.Index, out var found)) continue;
            var ranges = Ranges(poses, perObstacle[obstacle.Index]);
            conflicts.Add(new SiteConflict(
                ViolationKind.ObstacleClearance,
                obstacle.Index,
                found.Body,
                found.Overlap.Regions,
                found.Overlap.OpenPieces,
                found.Overlap.AreaSquareMetres,
                found.Overlap.LengthMetres,
                ranges));
        }

        var allowedMargins = allowedArea is null ? null : InsideMargins(shapes, allowedArea, clearanceMetres);
        if (allowedArea is not null && clearance is not null &&
            Outside(clearance, allowedArea) is { Regions.Count: > 0 } outsideAllowed)
        {
            conflicts.Add(new SiteConflict(
                ViolationKind.OutsideAllowedArea,
                -1,
                body is not null && Outside(body, allowedArea).Regions.Count > 0,
                outsideAllowed.Regions,
                [],
                outsideAllowed.AreaSquareMetres,
                0.0,
                Ranges(poses, allowedMargins!)));
        }

        var roadMargins = roadArea is null ? null : InsideMargins(shapes, roadArea, clearanceMetres);
        if (roadArea is not null && clearance is not null &&
            Outside(clearance, roadArea) is { Regions.Count: > 0 } outsideRoad)
        {
            conflicts.Add(new SiteConflict(
                ViolationKind.FixedWidthRoad,
                -1,
                body is not null && Outside(body, roadArea).Regions.Count > 0,
                outsideRoad.Regions,
                [],
                outsideRoad.AreaSquareMetres,
                0.0,
                Ranges(poses, roadMargins!)));
        }

        return new SiteFitOutcome(
            MinimumClearance(body, usableObstacles, allowedArea),
            conflicts,
            conflicts
                .SelectMany(conflict => conflict.Ranges.Select(range => Violation(conflict, range, clearanceMetres)))
                .OrderBy(violation => violation.StationMetres)
                .ThenBy(violation => violation.Kind)
                .ToArray(),
            obstacleMargins?.Select(measure => measure.Margin).ToArray(),
            allowedMargins?.Select(measure => measure.Margin).ToArray(),
            roadMargins?.Select(measure => measure.Margin).ToArray());
    }

    /// <summary>
    /// The minimum clearance, measured to the body sweep's outer boundary and every hole in it. An
    /// obstacle on a roundabout island sits in a hole, and measuring it to the outside edge instead
    /// reads metres more generous than the island kerb actually gets.
    /// </summary>
    private static double? MinimumClearance(SweptRegion? body, IReadOnlyList<Obstacle> obstacles, Area? allowed)
    {
        if (body is null) return null;
        var loops = Loops(body);
        var minimum = double.PositiveInfinity;
        foreach (var obstacle in obstacles)
        {
            minimum = Overlap(body, obstacle.Curve).Any
                ? 0.0
                : Math.Min(minimum, loops.Min(loop => Geometry2D.MinimumDistance(loop, obstacle.Curve.Points, true, obstacle.Curve.IsClosed)));
        }

        if (allowed is not null)
        {
            minimum = Outside(body, allowed).Regions.Count > 0
                ? 0.0
                : Math.Min(minimum, loops.Min(loop => allowed.Loops.Min(edge => Geometry2D.MinimumDistance(loop, edge.Points))));
        }

        return double.IsPositiveInfinity(minimum) ? null : minimum;
    }

    /// <summary>
    /// Per pose, the margin to the nearest obstacle; and for each conflicting obstacle, the margin to
    /// that obstacle alone, which is what locates its conflict on the road.
    /// </summary>
    private static Measure[] ObstacleMargins(
        IReadOnlyList<PoseShape> shapes,
        IReadOnlyList<Obstacle> obstacles,
        double clearanceMetres,
        IReadOnlySet<int> conflicting,
        out Dictionary<int, Measure[]> perObstacle)
    {
        var nearest = new Measure[shapes.Count];
        perObstacle = conflicting.ToDictionary(index => index, _ => new Measure[shapes.Count]);
        for (var pose = 0; pose < shapes.Count; pose++)
        {
            var shape = shapes[pose];
            var best = new Measure(double.PositiveInfinity, shape.Reference);

            // Nearest box first, so the exact measure of a far obstacle is skipped once a nearer one
            // has already set the margin: a box gap is a lower bound on the true distance.
            foreach (var (obstacle, gap) in obstacles
                         .Select(obstacle => (Obstacle: obstacle, Gap: shape.Box.Gap(obstacle.Box)))
                         .OrderBy(item => item.Gap))
            {
                var lowerBound = gap - clearanceMetres;
                var tracked = perObstacle.TryGetValue(obstacle.Index, out var own);
                if (lowerBound >= best.Margin && !tracked) continue;

                // A conflicting obstacle needs its own margin only where it can be negative. Further
                // away, the bound already says the pose is clear of it.
                var measure = lowerBound >= Math.Max(best.Margin, 0.0) && tracked
                    ? new Measure(lowerBound, shape.Reference)
                    : Separation(shape, obstacle, clearanceMetres);
                if (tracked) own![pose] = measure;
                if (measure.Margin < best.Margin) best = measure;
            }

            nearest[pose] = best;
        }

        return nearest;
    }

    private static Measure[] InsideMargins(IReadOnlyList<PoseShape> shapes, Area area, double clearanceMetres) =>
        shapes.Select(shape => shape.Outlines
                .Select((outline, index) => Inside(outline, shape.Boxes[index], area, clearanceMetres))
                .MinBy(measure => measure.Margin))
            .ToArray();

    /// <summary>
    /// Signed distance from a pose to an obstacle, less the clearance: the gap where they are apart,
    /// minus the depth where they overlap.
    /// </summary>
    private static Measure Separation(PoseShape shape, Obstacle obstacle, double clearanceMetres)
    {
        var best = new Measure(double.PositiveInfinity, shape.Reference);
        for (var index = 0; index < shape.Outlines.Count; index++)
        {
            var outline = shape.Outlines[index];
            var box = shape.Boxes[index];
            if (box.Gap(obstacle.Box) - clearanceMetres >= best.Margin) continue;
            var contact = Geometry2D.Closest(outline, obstacle.Curve.Points, true, obstacle.Curve.IsClosed);
            var depth = -1.0;
            var witness = contact.OnSecond;
            if (box.Gap(obstacle.Box) <= 0.0)
            {
                // An obstacle vertex inside the outline: a bollard under the body.
                foreach (var point in obstacle.Curve.Points)
                {
                    if (!box.Contains(point) || !Geometry2D.Contains(outline, point)) continue;
                    var inside = Geometry2D.ClosestOnPolyline(point, outline).Distance;
                    if (inside > depth) (depth, witness) = (inside, point);
                }

                // An outline vertex inside a closed obstacle: the body driven into a building.
                if (obstacle.Curve.IsClosed)
                {
                    foreach (var point in outline)
                    {
                        if (!obstacle.Box.Contains(point) || !Geometry2D.Contains(obstacle.Curve.Points, point)) continue;
                        var inside = Geometry2D.ClosestOnPolyline(point, obstacle.Curve.Points).Distance;
                        if (inside > depth) (depth, witness) = (inside, point);
                    }
                }
            }

            var measure = contact.Crosses || depth >= 0.0
                ? new Measure(-Math.Max(depth, MinimumOverlapMetres) - clearanceMetres, witness)
                : new Measure(contact.Distance - clearanceMetres, contact.OnSecond);
            if (measure.Margin < best.Margin) best = measure;
        }

        return best;
    }

    /// <summary>
    /// Signed distance from one outline to the edge of an area it must stay inside, less the
    /// clearance: the gap to the edge where it is inside, minus how far it pokes out where it is not.
    /// </summary>
    /// <remarks>
    /// Testing vertices alone is not enough. A vehicle corner can sit inside a yard while the edge
    /// between two corners cuts across a notch in it, so the outline is also tested for crossing the
    /// area's edges, and for an edge vertex poking into it.
    /// </remarks>
    private static Measure Inside(IReadOnlyList<Point2> outline, Box2 box, Area area, double clearanceMetres)
    {
        var depth = -1.0;
        var witness = outline[0];
        foreach (var point in outline)
        {
            if (area.Contains(point)) continue;
            var outside = area.DistanceToEdge(point);
            if (outside > depth) (depth, witness) = (outside, point);
        }

        var contact = new SegmentContact(double.PositiveInfinity, outline[0], outline[0], false);
        foreach (var edge in area.Loops)
        {
            var gap = edge.Box.Gap(box);
            if (gap <= 0.0)
            {
                foreach (var point in edge.Points)
                {
                    if (!box.Contains(point) || !Geometry2D.Contains(outline, point)) continue;
                    var intrusion = Geometry2D.ClosestOnPolyline(point, outline).Distance;
                    if (intrusion > depth) (depth, witness) = (intrusion, point);
                }
            }

            if (contact.Crosses || gap >= contact.Distance) continue;
            var candidate = Geometry2D.Closest(outline, edge.Points);
            if (candidate.Crosses || candidate.Distance < contact.Distance) contact = candidate;
        }

        return contact.Crosses || depth >= 0.0
            ? new Measure(-Math.Max(depth, MinimumOverlapMetres) - clearanceMetres, depth >= 0.0 ? witness : contact.OnSecond)
            : new Measure(contact.Distance - clearanceMetres, contact.OnSecond);
    }

    /// <summary>
    /// Groups the poses whose margin is negative into station ranges, each with its worst pose.
    /// </summary>
    /// <remarks>
    /// The region test and the pose margins are two measures of one thing, so a conflict normally has
    /// at least one failing pose. Right at the limit they can disagree by Clipper's rounding; the
    /// conflict is still real, so it is placed at the pose that comes closest rather than dropped.
    /// </remarks>
    private static IReadOnlyList<SiteConflictRange> Ranges(IReadOnlyList<VehiclePose> poses, IReadOnlyList<Measure> margins)
    {
        var ranges = new List<SiteConflictRange>();
        var start = -1;
        for (var index = 0; index <= poses.Count; index++)
        {
            var failing = index < poses.Count && margins[index].Margin < 0.0;
            if (failing && start < 0) start = index;
            if (failing || start < 0) continue;
            ranges.Add(Range(poses, margins, start, index - 1));
            start = -1;
        }

        if (ranges.Count == 0 && poses.Count > 0)
        {
            var closest = Enumerable.Range(0, poses.Count).MinBy(index => margins[index].Margin);
            ranges.Add(Range(poses, margins, closest, closest));
        }

        return ranges;
    }

    private static SiteConflictRange Range(IReadOnlyList<VehiclePose> poses, IReadOnlyList<Measure> margins, int start, int end)
    {
        var worst = start;
        for (var index = start + 1; index <= end; index++)
            if (margins[index].Margin < margins[worst].Margin) worst = index;
        var point = margins[worst].Point;
        return new SiteConflictRange(
            poses[start].StationMetres,
            poses[end].StationMetres,
            poses[worst].StationMetres,
            new Point3(point.X, point.Y, poses[worst].RearAxleCentreMetres.Z),
            margins[worst].Margin);
    }

    private static AnalysisViolation Violation(SiteConflict conflict, SiteConflictRange range, double clearanceMetres)
    {
        // The body is in conflict over this stretch only if its own outline is, not merely its clearance.
        var body = range.WorstMarginMetres < -clearanceMetres - 1e-9;
        var where = range.EndStationMetres - range.StartStationMetres < 0.005
            ? $"at station {range.StartStationMetres:0.0} m"
            : $"from station {range.StartStationMetres:0.0} to {range.EndStationMetres:0.0} m";
        var amount = conflict.Regions.Count > 0 ? $"{conflict.AreaSquareMetres:0.00} m²" : $"{conflict.LengthMetres:0.00} m";
        var message = conflict.Kind switch
        {
            ViolationKind.ObstacleClearance => (body
                    ? $"The vehicle body sweep intersects an obstacle {where}."
                    : $"The body clears this obstacle {where}, but the {clearanceMetres:0.00} m clearance allowance does not fit.") +
                (conflict.Regions.Count > 0
                    ? $" The clearance envelope overlaps it by {amount}."
                    : $" The clearance envelope crosses it along {amount}."),
            ViolationKind.OutsideAllowedArea => (body
                    ? $"The vehicle body extends outside the allowed area {where}."
                    : $"The clearance envelope extends outside the allowed area {where}.") +
                $" {amount} of the clearance envelope lies outside it.",
            _ => (body
                    ? $"The vehicle body does not fit inside the fixed-width road edges {where}."
                    : $"The clearance envelope does not fit inside the fixed-width road edges {where}.") +
                $" {amount} of the clearance envelope lies outside them."
        };
        return new AnalysisViolation(conflict.Kind, range.StartStationMetres, range.WorstPointMetres, message)
        {
            EndStationMetres = range.EndStationMetres
        };
    }

    /// <summary>The part of a region inside an obstacle: area for a closed one, length for an open one.</summary>
    private static ObstacleOverlap Overlap(SweptRegion region, SiteCurve obstacle)
    {
        if (obstacle.IsClosed)
        {
            var inside = Clipper.Intersect(
                AccessFootprint.Paths(region),
                new PathsD { AccessFootprint.Path(obstacle.Points, true) },
                FillRule.NonZero,
                Precision);
            var area = Math.Abs(inside.Sum(Clipper.Area));
            return area <= AreaToleranceSquareMetres
                ? ObstacleOverlap.None
                : new ObstacleOverlap(ToRegions(inside), [], area, 0.0);
        }

        var clipper = new ClipperD(Precision);
        var open = new PathD(obstacle.Points.Select(point => new PointD(point.X, point.Y)));
        clipper.AddOpenSubject(new PathsD { open });
        clipper.AddClip(AccessFootprint.Paths(region));
        var closedSolution = new PathsD();
        var openSolution = new PathsD();
        clipper.Execute(ClipType.Intersection, FillRule.NonZero, closedSolution, openSolution);
        var pieces = openSolution
            .Select(path => (IReadOnlyList<Point2>)path.Select(point => new Point2(point.x, point.y)).ToArray())
            .Where(piece => piece.Count >= 2)
            .ToArray();
        var length = pieces.Sum(Length);
        return length <= LengthToleranceMetres
            ? ObstacleOverlap.None
            : new ObstacleOverlap([], pieces, 0.0, length);
    }

    /// <summary>The part of a region that lies outside an area it must stay within.</summary>
    private static (IReadOnlyList<SweptRegion> Regions, double AreaSquareMetres) Outside(SweptRegion region, Area area)
    {
        var outside = Clipper.Difference(AccessFootprint.Paths(region), area.Paths, FillRule.NonZero, Precision);
        var total = Math.Abs(outside.Sum(Clipper.Area));
        return total <= AreaToleranceSquareMetres ? ([], 0.0) : (ToRegions(outside), total);
    }

    private static IReadOnlyList<SweptRegion> ToRegions(PathsD paths) =>
        SweptRegionBuilder.Nest(paths.Select(SweptRegionBuilder.ToLoop));

    /// <summary>
    /// Allowed boundaries as one area. They may overlap -- a yard drawn as two rectangles -- and a
    /// vehicle crossing the edge of one into the other has not left the allowed area.
    /// </summary>
    private static PathsD UnionPaths(IEnumerable<IReadOnlyList<Point2>> loops) =>
        Clipper.Union(new PathsD(loops.Select(loop => AccessFootprint.Path(loop, true))), new PathsD(), FillRule.NonZero, Precision);

    private static IReadOnlyList<IReadOnlyList<Point2>> Loops(SweptRegion region) =>
        [region.OuterBoundary, .. region.Holes];

    private static double Length(IReadOnlyList<Point2> polyline)
    {
        var total = 0.0;
        for (var index = 1; index < polyline.Count; index++) total += polyline[index - 1].DistanceTo(polyline[index]);
        return total;
    }

    private readonly record struct Measure(double Margin, Point2 Point);

    private sealed record Obstacle(int Index, SiteCurve Curve, Box2 Box);

    private sealed record ObstacleOverlap(
        IReadOnlyList<SweptRegion> Regions,
        IReadOnlyList<IReadOnlyList<Point2>> OpenPieces,
        double AreaSquareMetres,
        double LengthMetres)
    {
        public static readonly ObstacleOverlap None = new([], [], 0.0, 0.0);
        public bool Any => AreaSquareMetres > 0.0 || LengthMetres > 0.0;
    }

    /// <summary>A pose's occupied outlines with their boxes, computed once per pose.</summary>
    private sealed record PoseShape(IReadOnlyList<IReadOnlyList<Point2>> Outlines, IReadOnlyList<Box2> Boxes, Box2 Box, Point2 Reference)
    {
        public static PoseShape Of(VehiclePose pose)
        {
            var outlines = pose.OccupiedOutlinesWorldMetres.Where(outline => outline.Count >= 3).ToArray();
            var boxes = outlines.Select(Box2.Of).ToArray();
            return new PoseShape(outlines, boxes, Box2.Of(outlines.SelectMany(outline => outline)), pose.RearAxleCentreMetres.XY);
        }
    }

    private sealed record EdgeLoop(IReadOnlyList<Point2> Points, Box2 Box);

    /// <summary>An area to stay inside, as Clipper paths for the envelope test and loops for the poses.</summary>
    private sealed record Area(PathsD Paths, IReadOnlyList<SweptRegion> Regions, IReadOnlyList<EdgeLoop> Loops, IReadOnlyList<Box2> RegionBoxes)
    {
        public static Area? Of(PathsD paths)
        {
            var regions = ToRegions(paths);
            if (regions.Count == 0) return null;
            var loops = regions
                .SelectMany(region => new[] { region.OuterBoundary }.Concat(region.Holes))
                .Select(loop => new EdgeLoop(loop, Box2.Of(loop)))
                .ToArray();
            return new Area(paths, regions, loops, regions.Select(region => Box2.Of(region.OuterBoundary)).ToArray());
        }

        public bool Contains(Point2 point)
        {
            for (var index = 0; index < Regions.Count; index++)
            {
                if (!RegionBoxes[index].Contains(point)) continue;
                var region = Regions[index];
                if (Geometry2D.Contains(region.OuterBoundary, point) &&
                    !region.Holes.Any(hole => Geometry2D.Contains(hole, point))) return true;
            }

            return false;
        }

        public double DistanceToEdge(Point2 point)
        {
            var best = double.PositiveInfinity;
            var probe = Box2.Of(point, point);
            foreach (var loop in Loops)
            {
                if (loop.Box.Gap(probe) >= best) continue;
                best = Math.Min(best, Geometry2D.ClosestOnPolyline(point, loop.Points).Distance);
            }

            return best;
        }
    }
}
