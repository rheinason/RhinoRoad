using Rhino;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// A site curve (obstacle, yard, road boundary) as plan points in metres, for the polygon checks.
/// </summary>
/// <remarks>
/// These curves used to be divided every 10 cm. A 400 m yard became 4,000 points, and the review
/// measured every one of a journey's ~1,500 poses against every segment, which froze Rhino for
/// minutes on Save. The checks that consume the points -- Clipper polygon operations, segment
/// distances and point-in-polygon -- are exact on straight segments, so a polyline is used as its own
/// vertices (a rectangle is four points, and exact rather than approximate), and anything curved is
/// flattened to a 1 cm chord tolerance.
/// </remarks>
internal static class CurveOutline
{
    private const double ChordToleranceMetres = 0.01;

    public static Point2[] Points(Curve curve, UnitSystem modelUnits)
    {
        var metres = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        Polyline? polyline = curve.TryGetPolyline(out var exact) ? exact : null;
        if (polyline is null)
        {
            var tolerance = ChordToleranceMetres / metres;
            polyline = curve.ToPolyline(tolerance, RhinoMath.ToRadians(2.0), 0.0, 0.0)?.TryGetPolyline(out var flattened) == true
                ? flattened : null;
        }
        IEnumerable<Point3d> points = polyline is { Count: >= 2 }
            ? polyline
            : (curve.DivideByCount(Math.Max(4, (int)Math.Ceiling(curve.GetLength() * metres / 0.10)), true)
                ?? [curve.Domain.T0, curve.Domain.T1]).Select(curve.PointAt);
        var plan = points.Select(point => new Point2(point.X * metres, point.Y * metres)).ToList();
        // A closed outline is treated as closed by every consumer; a repeated end point adds nothing.
        if (curve.IsClosed && plan.Count > 3 && plan[0].DistanceTo(plan[^1]) < 1e-9) plan.RemoveAt(plan.Count - 1);
        return plan.ToArray();
    }
}
