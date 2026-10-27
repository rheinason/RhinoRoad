using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.GrasshopperPlugin.Conversion;

/// <summary>
/// Core metre geometry to Rhino model-unit curves, in a horizontal plane at <c>elevation</c> (model
/// units). The components pass the route's start elevation, as the plugin bakes its envelopes, so
/// the plan outputs sit level with the start of the Rear Path rather than at Z = 0.
/// </summary>
public static class GeometryConverter
{
    public static Point3 ToMetres(Point3d point, double metresPerModelUnit) =>
        new(point.X * metresPerModelUnit, point.Y * metresPerModelUnit, point.Z * metresPerModelUnit);

    public static PolylineCurve? ToPolyline(IReadOnlyList<Point2> points, double modelUnitsPerMetre, bool closed, double elevation = 0.0)
    {
        if (points.Count < 2) return null;
        var converted = points.Select(point => new Point3d(point.X * modelUnitsPerMetre, point.Y * modelUnitsPerMetre, elevation)).ToList();
        if (closed && converted[0].DistanceTo(converted[^1]) > 1e-12) converted.Add(converted[0]);
        return new PolylineCurve(converted);
    }

    public static PolylineCurve? ToPolyline(SweptRegion region, double modelUnitsPerMetre, double elevation = 0.0) =>
        ToPolyline(region.OuterBoundary, modelUnitsPerMetre, closed: true, elevation);

    public static IEnumerable<PolylineCurve> HolesOf(SweptRegion region, double modelUnitsPerMetre, double elevation = 0.0) =>
        region.Holes
            .Select(hole => ToPolyline(hole, modelUnitsPerMetre, closed: true, elevation))
            .OfType<PolylineCurve>();
}
