namespace RhinoRoad.Core;

public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 point, double scale) => new(point.X * scale, point.Y * scale);
    public double Length => Math.Sqrt((X * X) + (Y * Y));
    public double DistanceTo(Point2 other) => (this - other).Length;
}

public readonly record struct Point3(double X, double Y, double Z)
{
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

    public static double MinimumDistance(IReadOnlyList<Point2> first, IReadOnlyList<Point2> second, bool closedFirst = true, bool closedSecond = true)
    {
        if (first.Count == 0 || second.Count == 0) return double.PositiveInfinity;
        var minimum = double.PositiveInfinity;
        var firstSegments = closedFirst ? first.Count : first.Count - 1;
        var secondSegments = closedSecond ? second.Count : second.Count - 1;
        for (var i = 0; i < firstSegments; i++)
        {
            var a = first[i];
            var b = first[(i + 1) % first.Count];
            for (var j = 0; j < secondSegments; j++)
            {
                var c = second[j];
                var d = second[(j + 1) % second.Count];
                if (SegmentsIntersect(a, b, c, d)) return 0.0;
                minimum = Math.Min(minimum, DistancePointToSegment(a, c, d));
                minimum = Math.Min(minimum, DistancePointToSegment(b, c, d));
                minimum = Math.Min(minimum, DistancePointToSegment(c, a, b));
                minimum = Math.Min(minimum, DistancePointToSegment(d, a, b));
            }
        }

        return minimum;
    }
}
