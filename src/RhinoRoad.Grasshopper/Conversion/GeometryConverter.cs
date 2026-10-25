using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.GrasshopperPlugin.Conversion;

/// <summary>Core metre geometry to Rhino model-unit curves, in the World XY plane.</summary>
public static class GeometryConverter
{
    public static Point3 ToMetres(Point3d point, double metresPerModelUnit) =>
        new(point.X * metresPerModelUnit, point.Y * metresPerModelUnit, point.Z * metresPerModelUnit);

    public static PolylineCurve? ToPolyline(IReadOnlyList<Point2> points, double modelUnitsPerMetre, bool closed)
    {
        if (points.Count < 2) return null;
        var converted = points.Select(point => new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, 0.0)).ToList();
        if (closed && converted[0].DistanceTo(converted[^1]) > 1e-12) converted.Add(converted[0]);
        return new PolylineCurve(converted);
    }

    public static PolylineCurve? ToPolyline(SweptRegion region, double modelUnitsPerMetre) =>
        ToPolyline(region.OuterBoundary, modelUnitsPerMetre, closed: true);

    public static IEnumerable<PolylineCurve> HolesOf(SweptRegion region, double modelUnitsPerMetre) =>
        region.Holes
            .Select(hole => ToPolyline(hole, modelUnitsPerMetre, closed: true))
            .OfType<PolylineCurve>();
}
