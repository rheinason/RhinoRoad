namespace RhinoRoad.Core;

public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 point, double scale) => new(point.X * scale, point.Y * scale);
    [System.Text.Json.Serialization.JsonIgnore]
    public double Length => Math.Sqrt((X * X) + (Y * Y));
    public double DistanceTo(Point2 other) => (this - other).Length;
}

public readonly record struct Point3(double X, double Y, double Z)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public Point2 XY => new(X, Y);
}

public static class Geometry2D
{
    public const double Epsilon = 1e-9;

    public static double NormalizeAngle(double angle)
    {
        while (angle > Math.PI) angle -= Math.PI * 2.0;
        while (angle <= -Math.PI) angle += Math.PI * 2.0;
        return angle;
    }

    public static Point2 Transform(Point2 local, Point2 origin, double heading)
    {
        var cosine = Math.Cos(heading);
        var sine = Math.Sin(heading);
        return new Point2(
            origin.X + (local.X * cosine) - (local.Y * sine),
            origin.Y + (local.X * sine) + (local.Y * cosine));
    }

    public static double DistancePointToSegment(Point2 point, Point2 start, Point2 end)
    {
        var segment = end - start;
        var lengthSquared = (segment.X * segment.X) + (segment.Y * segment.Y);
        if (lengthSquared <= Epsilon) return point.DistanceTo(start);
        var t = Math.Clamp((((point.X - start.X) * segment.X) + ((point.Y - start.Y) * segment.Y)) / lengthSquared, 0.0, 1.0);
        return point.DistanceTo(start + (segment * t));
    }

    public static bool SegmentsIntersect(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        static double Cross(Point2 p, Point2 q, Point2 r) =>
            ((q.X - p.X) * (r.Y - p.Y)) - ((q.Y - p.Y) * (r.X - p.X));

        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        return ((abC > Epsilon && abD < -Epsilon) || (abC < -Epsilon && abD > Epsilon)) &&
               ((cdA > Epsilon && cdB < -Epsilon) || (cdA < -Epsilon && cdB > Epsilon));
    }

    public static bool Contains(IReadOnlyList<Point2> polygon, Point2 point)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var j = i == 0 ? polygon.Count - 1 : i - 1;
            var a = polygon[i];
            var b = polygon[j];
            if (((a.Y > point.Y) != (b.Y > point.Y)) &&
                point.X < (((b.X - a.X) * (point.Y - a.Y) / ((b.Y - a.Y) + Epsilon)) + a.X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Smallest distance between two polylines; zero where they cross or touch.
    /// </summary>
    /// <remarks>
    /// This only looks at the outlines. A polygon lying wholly inside another never meets it, so it
    /// measures as apart: callers that need overlap to read as overlap test containment as well,
    /// as <see cref="SiteFitAnalyzer"/> does.
    /// </remarks>
    public static double MinimumDistance(IReadOnlyList<Point2> first, IReadOnlyList<Point2> second, bool closedFirst = true, bool closedSecond = true) =>
        Closest(first, second, closedFirst, closedSecond).Distance;

    /// <summary>
    /// The nearest pair of points on two polylines, and whether any of their segments properly cross.
    /// </summary>
    /// <remarks>
    /// Every segment pair is a candidate, so the work is the product of the vertex counts. The review
    /// runs this per pose against every site curve, which is thousands of calls against a site
    /// outline that may itself have thousands of vertices. Two cheap rejects keep that bearable: a
    /// segment whose box is already further from the other polyline's box than the best distance so
    /// far skips the inner loop entirely, and a segment pair whose boxes are that far apart skips the
    /// exact distance. Both are exact lower bounds, so the answer is unchanged.
    /// </remarks>
    public static SegmentContact Closest(IReadOnlyList<Point2> first, IReadOnlyList<Point2> second, bool closedFirst = true, bool closedSecond = true)
    {
        if (first.Count == 0 || second.Count == 0)
            return new SegmentContact(double.PositiveInfinity, default, default, false);
        if (first.Count == 1 || second.Count == 1)
        {
            // A single point has no segments; measure it against the other polyline directly.
            var single = first.Count == 1 ? first[0] : second[0];
            var other = first.Count == 1 ? second : first;
            var (distance, nearest) = ClosestOnPolyline(single, other, first.Count == 1 ? closedSecond : closedFirst);
            return first.Count == 1
                ? new SegmentContact(distance, single, nearest, false)
                : new SegmentContact(distance, nearest, single, false);
        }

        var best = new SegmentContact(double.PositiveInfinity, first[0], second[0], false);
        var firstSegments = closedFirst ? first.Count : first.Count - 1;
        var secondSegments = closedSecond ? second.Count : second.Count - 1;
        var secondBox = Box2.Of(second);
        for (var i = 0; i < firstSegments; i++)
        {
            var a = first[i];
            var b = first[(i + 1) % first.Count];
            var segmentBox = Box2.Of(a, b);
            if (segmentBox.Gap(secondBox) >= best.Distance) continue;
            for (var j = 0; j < secondSegments; j++)
            {
                var c = second[j];
                var d = second[(j + 1) % second.Count];
                if (segmentBox.Gap(Box2.Of(c, d)) >= best.Distance) continue;
                if (SegmentsIntersect(a, b, c, d))
                {
                    var crossing = Intersection(a, b, c, d);
                    return new SegmentContact(0.0, crossing, crossing, true);
                }
                best = Nearer(best, a, c, d, pointOnFirst: true);
                best = Nearer(best, b, c, d, pointOnFirst: true);
                best = Nearer(best, c, a, b, pointOnFirst: false);
                best = Nearer(best, d, a, b, pointOnFirst: false);
            }
        }

        return best;
    }

    /// <summary>Distance from a point to the nearest edge of a polyline, and the point it lands on.</summary>
    public static (double Distance, Point2 Nearest) ClosestOnPolyline(Point2 point, IReadOnlyList<Point2> polyline, bool closed = true)
    {
        if (polyline.Count == 0) return (double.PositiveInfinity, point);
        if (polyline.Count == 1) return (point.DistanceTo(polyline[0]), polyline[0]);
        var bestDistance = double.PositiveInfinity;
        var bestPoint = polyline[0];
        var segments = closed ? polyline.Count : polyline.Count - 1;
        for (var i = 0; i < segments; i++)
        {
            var nearest = ClosestOnSegment(point, polyline[i], polyline[(i + 1) % polyline.Count]);
            var distance = point.DistanceTo(nearest);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestPoint = nearest;
        }

        return (bestDistance, bestPoint);
    }

    public static Point2 ClosestOnSegment(Point2 point, Point2 start, Point2 end)
    {
        var segment = end - start;
        var lengthSquared = (segment.X * segment.X) + (segment.Y * segment.Y);
        if (lengthSquared <= Epsilon) return start;
        var t = Math.Clamp((((point.X - start.X) * segment.X) + ((point.Y - start.Y) * segment.Y)) / lengthSquared, 0.0, 1.0);
        return start + (segment * t);
    }

    private static SegmentContact Nearer(SegmentContact best, Point2 point, Point2 start, Point2 end, bool pointOnFirst)
    {
        var nearest = ClosestOnSegment(point, start, end);
        var distance = point.DistanceTo(nearest);
        if (distance >= best.Distance) return best;
        return pointOnFirst
            ? new SegmentContact(distance, point, nearest, false)
            : new SegmentContact(distance, nearest, point, false);
    }

    /// <summary>Where two segments already known to cross meet.</summary>
    private static Point2 Intersection(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        var r = b - a;
        var s = d - c;
        var denominator = (r.X * s.Y) - (r.Y * s.X);
        if (Math.Abs(denominator) <= Epsilon) return a;
        var t = (((c.X - a.X) * s.Y) - ((c.Y - a.Y) * s.X)) / denominator;
        return a + (r * t);
    }
}

/// <summary>The nearest pair of points between two polylines.</summary>
/// <param name="Crosses">True where two segments properly cross; both points are then the crossing.</param>
public readonly record struct SegmentContact(double Distance, Point2 OnFirst, Point2 OnSecond, bool Crosses);

/// <summary>An axis-aligned plan box, for rejecting far-apart geometry before the exact tests.</summary>
public readonly record struct Box2(double MinX, double MinY, double MaxX, double MaxY)
{
    public static Box2 Of(Point2 a, Point2 b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public static Box2 Of(IEnumerable<Point2> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return new Box2(minX, minY, maxX, maxY);
    }

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;

    public bool Contains(Point2 point) =>
        point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;

    /// <summary>
    /// The distance between the two boxes, zero where they overlap: a lower bound on the distance
    /// between anything inside one and anything inside the other.
    /// </summary>
    public double Gap(Box2 other)
    {
        if (IsEmpty || other.IsEmpty) return double.PositiveInfinity;
        var dx = Math.Max(0.0, Math.Max(other.MinX - MaxX, MinX - other.MaxX));
        var dy = Math.Max(0.0, Math.Max(other.MinY - MaxY, MinY - other.MaxY));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
