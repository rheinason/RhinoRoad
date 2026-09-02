using Rhino;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>One leg of a manoeuvre as it exists in the document: a curve, and how it is driven.</summary>
internal sealed record IntentLeg(Curve Curve, TravelDirection Direction);

/// <summary>
/// Turns a Rhino curve into the intended line a vehicle is asked to follow.
/// </summary>
/// <remarks>
/// One entry point for both ways of getting a line: a curve drawn earlier and selected, or one
/// interpolated through points clicked in the command. They are the same thing downstream, which is
/// why editing a driven route with ordinary Rhino curve tools works at all.
/// </remarks>
internal static class IntentPathFactory
{
    /// <summary>Sampling spacing of the intended line, in metres.</summary>
    /// <remarks>
    /// Half the follower's step, so the nearest-point lookup has something to land on between
    /// integration steps. At this spacing the chord error on the tightest curve any of these
    /// vehicles can drive is under a tenth of a millimetre.
    /// </remarks>
    private const double SpacingMetres = 0.025;

    public static IntentPath FromCurve(Curve curve, UnitSystem modelUnits)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var metresPerModelUnit = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        var lengthMetres = curve.GetLength() * metresPerModelUnit;
        if (lengthMetres <= 1e-6) throw new ArgumentException("The selected path is too short.", nameof(curve));

        var divisions = Math.Max(2, (int)Math.Ceiling(lengthMetres / SpacingMetres));
        var parameters = curve.DivideByCount(divisions, includeEnds: true)
            ?? throw new InvalidOperationException("The path could not be sampled.");

        var points = parameters
            .Select(curve.PointAt)
            .Select(point => new Point3(
                point.X * metresPerModelUnit,
                point.Y * metresPerModelUnit,
                point.Z * metresPerModelUnit))
            .ToArray();
        return IntentPath.FromPoints(points);
    }

    /// <summary>
    /// The smooth line through a set of picked points.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interpolated rather than joined: a polyline's corners have no radius, and asking a vehicle
    /// to follow one only ever reports how far it misses them by. The picked points are the through
    /// points, so the line passes through exactly where it was clicked.
    /// </para>
    /// <para>
    /// Centripetal knots, not the default uniform ones. Uniform spacing assumes the points are
    /// evenly spread, and where they are not it overshoots — badly enough to throw loops into the
    /// line between closely clicked points, which the vehicle then dutifully drives. Centripetal
    /// spacing weights each span by the square root of its length and is the standard cure.
    /// </para>
    /// </remarks>
    public static Curve? InterpolateThrough(IReadOnlyList<Point3d> points)
    {
        var distinct = WithoutRepeats(points);
        return distinct.Count switch
        {
            < 2 => null,
            2 => new LineCurve(distinct[0], distinct[1]),
            _ => Curve.CreateInterpolatedCurve(distinct, 3, CurveKnotStyle.ChordSquareRoot)
        };
    }

    /// <summary>
    /// Drops points that repeat the one before them.
    /// </summary>
    /// <remarks>
    /// A double-click, or a click that snapped to the same place twice, leaves a span of no length.
    /// Interpolation through it is undefined and comes back as a kink or a loop rather than an
    /// error, so the point is discarded before it can do that.
    /// </remarks>
    private static IReadOnlyList<Point3d> WithoutRepeats(IReadOnlyList<Point3d> points)
    {
        const double minimumSpacingModelUnits = 1e-6;
        var kept = new List<Point3d>(points.Count);
        foreach (var point in points)
        {
            if (kept.Count > 0 && kept[^1].DistanceTo(point) <= minimumSpacingModelUnits) continue;
            kept.Add(point);
        }

        return kept;
    }
}
