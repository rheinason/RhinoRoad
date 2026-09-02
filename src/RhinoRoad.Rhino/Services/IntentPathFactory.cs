using Rhino;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

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
    /// Interpolated rather than joined: a polyline's corners have no radius, and asking a vehicle
    /// to follow one only ever reports how far it misses them by. The picked points are the through
    /// points, so the curve passes through exactly where it was clicked.
    /// </remarks>
    public static Curve? InterpolateThrough(IReadOnlyList<Point3d> points) =>
        points.Count < 2
            ? null
            : points.Count == 2
                ? new LineCurve(points[0], points[1])
                : Curve.CreateInterpolatedCurve(points, 3);
}
