namespace RhinoRoad.Core;

/// <summary>A position and the direction the vehicle faces there.</summary>
public readonly record struct Pose2(Point2 PositionMetres, double HeadingRadians);

public enum ArcDirection
{
    Left,
    Straight,
    Right
}

/// <summary>One arc or tangent of a solved path.</summary>
public sealed record PathSegment(ArcDirection Direction, double LengthMetres)
{
    public double SignedCurvature(double radiusMetres) => Direction switch
    {
        ArcDirection.Left => 1.0 / radiusMetres,
        ArcDirection.Right => -1.0 / radiusMetres,
        _ => 0.0
    };
}

public sealed record DubinsSolution(
    string Word,
    IReadOnlyList<PathSegment> Segments,
    double RadiusMetres,
    double LengthMetres);

/// <summary>
/// The shortest bounded-curvature path between two poses: arcs at one radius joined by tangents.
/// </summary>
/// <remarks>
/// <para>
/// Geometry, not integration. The path is six candidate words — LSL, RSR, LSR, RSL, RLR, LRL — of
/// which the shortest valid one is the true optimum, and each is closed-form, so the result carries
/// no accumulated error and honours both endpoint headings exactly. That matters here: on a tight
/// stretch the question is usually decided by the angle the vehicle arrives at, and a path that
/// only approximately meets a heading cannot answer it.
/// </para>
/// <para>
/// Curvature steps instantly where an arc meets a tangent, which no steered vehicle can do. This
/// solver deliberately stops at the geometric intent; converting intent into a path a real wheel
/// can follow is a separate, rate-limited pass, and the gap between the two is the honest measure
/// of how hard the manoeuvre is.
/// </para>
/// </remarks>
public static class DubinsPath
{
    /// <summary>Solves for the shortest path, or null when the poses admit none at this radius.</summary>
    public static DubinsSolution? Solve(Pose2 start, Pose2 goal, double radiusMetres)
    {
        if (radiusMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(radiusMetres));

        var delta = goal.PositionMetres - start.PositionMetres;
        var separation = delta.Length;
        var bearing = separation <= Geometry2D.Epsilon ? 0.0 : Math.Atan2(delta.Y, delta.X);
        var d = separation / radiusMetres;
        var alpha = Modulo2Pi(start.HeadingRadians - bearing);
        var beta = Modulo2Pi(goal.HeadingRadians - bearing);

        DubinsSolution? best = null;
        foreach (var candidate in Candidates(alpha, beta, d))
        {
            if (candidate is null) continue;
            var (word, first, second, third) = candidate.Value;
            if (double.IsNaN(first) || double.IsNaN(second) || double.IsNaN(third)) continue;
            var segments = new PathSegment[]
            {
                new(Steer(word[0]), first * radiusMetres),
                new(Steer(word[1]), second * radiusMetres),
                new(Steer(word[2]), third * radiusMetres)
            };
            var length = segments.Sum(segment => segment.LengthMetres);
            if (best is not null && length >= best.LengthMetres) continue;

            // Every word here is a transcribed trigonometric identity, and a wrong one produces a
            // plausible length rather than an obvious failure. Integrating the candidate and
            // checking where it actually lands is the only way to know it solves the problem posed.
            var solution = new DubinsSolution(word, segments, radiusMetres, length);
            if (!Reaches(start, goal, solution)) continue;
            best = solution;
        }

        return best;
    }

    /// <summary>Walks a pose along one segment.</summary>
    public static Pose2 Advance(Pose2 pose, ArcDirection direction, double distanceMetres, double radiusMetres)
    {
        if (direction == ArcDirection.Straight)
        {
            return pose with
            {
                PositionMetres = new Point2(
                    pose.PositionMetres.X + (Math.Cos(pose.HeadingRadians) * distanceMetres),
                    pose.PositionMetres.Y + (Math.Sin(pose.HeadingRadians) * distanceMetres))
            };
        }

        var sign = direction == ArcDirection.Left ? 1.0 : -1.0;
        var turned = sign * distanceMetres / radiusMetres;
        var heading = pose.HeadingRadians + turned;

        // Exact arc displacement, from the centre of curvature rather than by stepping along it.
        var centre = new Point2(
            pose.PositionMetres.X - (sign * radiusMetres * Math.Sin(pose.HeadingRadians)),
            pose.PositionMetres.Y + (sign * radiusMetres * Math.Cos(pose.HeadingRadians)));
        return new Pose2(
            new Point2(
                centre.X + (sign * radiusMetres * Math.Sin(heading)),
                centre.Y - (sign * radiusMetres * Math.Cos(heading))),
            Geometry2D.NormalizeAngle(heading));
    }

    /// <summary>The pose at <paramref name="distanceMetres"/> along the solution.</summary>
    public static Pose2 PoseAt(Pose2 start, DubinsSolution solution, double distanceMetres)
    {
        var pose = start;
        var remaining = Math.Clamp(distanceMetres, 0.0, solution.LengthMetres);
        foreach (var segment in solution.Segments)
        {
            var travelled = Math.Min(remaining, segment.LengthMetres);
            if (travelled > 0.0) pose = Advance(pose, segment.Direction, travelled, solution.RadiusMetres);
            remaining -= travelled;
            if (remaining <= 0.0) break;
        }

        return pose;
    }

    /// <summary>The signed curvature at a distance along the solution.</summary>
    public static double CurvatureAt(DubinsSolution solution, double distanceMetres)
    {
        var remaining = distanceMetres;
        foreach (var segment in solution.Segments)
        {
            if (remaining <= segment.LengthMetres) return segment.SignedCurvature(solution.RadiusMetres);
            remaining -= segment.LengthMetres;
        }

        return solution.Segments[^1].SignedCurvature(solution.RadiusMetres);
    }

    private static bool Reaches(Pose2 start, Pose2 goal, DubinsSolution solution)
    {
        var end = PoseAt(start, solution, solution.LengthMetres);
        const double positionTolerance = 1e-6;
        const double headingTolerance = 1e-6;
        return end.PositionMetres.DistanceTo(goal.PositionMetres) <= positionTolerance
            && Math.Abs(Geometry2D.NormalizeAngle(end.HeadingRadians - goal.HeadingRadians)) <= headingTolerance;
    }

    private static ArcDirection Steer(char letter) => letter switch
    {
        'L' => ArcDirection.Left,
        'R' => ArcDirection.Right,
        _ => ArcDirection.Straight
    };

    private static IEnumerable<(string Word, double First, double Second, double Third)?> Candidates(
        double alpha,
        double beta,
        double d)
    {
        var sinAlpha = Math.Sin(alpha);
        var sinBeta = Math.Sin(beta);
        var cosAlpha = Math.Cos(alpha);
        var cosBeta = Math.Cos(beta);
        var cosAlphaMinusBeta = Math.Cos(alpha - beta);

        yield return LeftStraightLeft();
        yield return RightStraightRight();
        yield return LeftStraightRight();
        yield return RightStraightLeft();
        yield return RightLeftRight();
        yield return LeftRightLeft();

        (string, double, double, double)? LeftStraightLeft()
        {
            var tmp = d + sinAlpha - sinBeta;
            var squared = 2.0 + (d * d) - (2.0 * cosAlphaMinusBeta) + (2.0 * d * (sinAlpha - sinBeta));
            if (squared < 0.0) return null;
            var theta = Math.Atan2(cosBeta - cosAlpha, tmp);
            return ("LSL", Modulo2Pi(theta - alpha), Math.Sqrt(squared), Modulo2Pi(beta - theta));
        }

        (string, double, double, double)? RightStraightRight()
        {
            var tmp = d - sinAlpha + sinBeta;
            var squared = 2.0 + (d * d) - (2.0 * cosAlphaMinusBeta) + (2.0 * d * (sinBeta - sinAlpha));
            if (squared < 0.0) return null;
            var theta = Math.Atan2(cosAlpha - cosBeta, tmp);
            return ("RSR", Modulo2Pi(alpha - theta), Math.Sqrt(squared), Modulo2Pi(theta - beta));
        }

        (string, double, double, double)? LeftStraightRight()
        {
            var squared = -2.0 + (d * d) + (2.0 * cosAlphaMinusBeta) + (2.0 * d * (sinAlpha + sinBeta));
            if (squared < 0.0) return null;
            var p = Math.Sqrt(squared);
            var theta = Math.Atan2(-cosAlpha - cosBeta, d + sinAlpha + sinBeta) - Math.Atan2(-2.0, p);
            return ("LSR", Modulo2Pi(theta - alpha), p, Modulo2Pi(theta - beta));
        }

        (string, double, double, double)? RightStraightLeft()
        {
            var squared = (d * d) - 2.0 + (2.0 * cosAlphaMinusBeta) - (2.0 * d * (sinAlpha + sinBeta));
            if (squared < 0.0) return null;
            var p = Math.Sqrt(squared);
            var theta = Math.Atan2(cosAlpha + cosBeta, d - sinAlpha - sinBeta) - Math.Atan2(2.0, p);
            return ("RSL", Modulo2Pi(alpha - theta), p, Modulo2Pi(beta - theta));
        }

        (string, double, double, double)? RightLeftRight()
        {
            var tmp = (6.0 - (d * d) + (2.0 * cosAlphaMinusBeta) + (2.0 * d * (sinAlpha - sinBeta))) / 8.0;
            if (Math.Abs(tmp) > 1.0) return null;
            var p = Modulo2Pi((2.0 * Math.PI) - Math.Acos(tmp));
            var t = Modulo2Pi(alpha - Math.Atan2(cosAlpha - cosBeta, d - sinAlpha + sinBeta) + (p / 2.0));
            return ("RLR", t, p, Modulo2Pi(alpha - beta - t + p));
        }

        (string, double, double, double)? LeftRightLeft()
        {
            var tmp = (6.0 - (d * d) + (2.0 * cosAlphaMinusBeta) + (2.0 * d * (sinBeta - sinAlpha))) / 8.0;
            if (Math.Abs(tmp) > 1.0) return null;
            var p = Modulo2Pi((2.0 * Math.PI) - Math.Acos(tmp));
            var t = Modulo2Pi(Math.Atan2(cosBeta - cosAlpha, d + sinAlpha - sinBeta) - alpha + (p / 2.0));
            return ("LRL", t, p, Modulo2Pi(beta - alpha - t + p));
        }
    }

    private static double Modulo2Pi(double angle)
    {
        var value = angle % (2.0 * Math.PI);
        return value < 0.0 ? value + (2.0 * Math.PI) : value;
    }
}
