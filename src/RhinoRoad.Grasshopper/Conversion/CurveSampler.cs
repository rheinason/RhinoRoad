using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.GrasshopperPlugin.Conversion;

/// <summary>
/// Samples a Rhino curve into Core route samples. Deliberately a small copy of the plugin's
/// route sampler rather than a reference to it: that class is internal to the Rhino plugin, and this
/// assembly must load without it.
/// </summary>
public static class CurveSampler
{
    public static IReadOnlyList<RouteSample> Sample(
        Curve curve,
        double metresPerModelUnit,
        TravelDirection direction,
        double targetSpacingMetres = 0.10)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var lengthMetres = curve.GetLength() * metresPerModelUnit;
        if (lengthMetres <= 1e-6) throw new ArgumentException("The path is too short.", nameof(curve));
        var divisionCount = Math.Max(2, (int)Math.Ceiling(lengthMetres / targetSpacingMetres));
        var parameters = curve.DivideByCount(divisionCount, includeEnds: true);
        if (parameters is null || parameters.Length < 2) throw new InvalidOperationException("The path could not be sampled.");

        var raw = new List<(Point3 Point, double Heading)>(parameters.Length);
        foreach (var parameter in parameters)
        {
            var point = curve.PointAt(parameter);
            var tangent = curve.TangentAt(parameter);
            if (Math.Sqrt((tangent.X * tangent.X) + (tangent.Y * tangent.Y)) <= 1e-10)
                throw new InvalidOperationException("The path has a vertical or undefined tangent in plan.");
            raw.Add((
                new Point3(point.X * metresPerModelUnit, point.Y * metresPerModelUnit, point.Z * metresPerModelUnit),
                Math.Atan2(tangent.Y, tangent.X)));
        }

        var stations = new double[raw.Count];
        for (var index = 1; index < raw.Count; index++)
            stations[index] = stations[index - 1] + raw[index].Point.XY.DistanceTo(raw[index - 1].Point.XY);

        var result = new List<RouteSample>(raw.Count);
        for (var index = 0; index < raw.Count; index++)
        {
            var previous = Math.Max(0, index - 1);
            var next = Math.Min(raw.Count - 1, index + 1);
            var span = Math.Max(stations[next] - stations[previous], 1e-9);
            var curvature = Geometry2D.NormalizeAngle(raw[next].Heading - raw[previous].Heading) / span;
            var adjacent = index == 0
                ? Geometry2D.NormalizeAngle(raw[1].Heading - raw[0].Heading)
                : Geometry2D.NormalizeAngle(raw[index].Heading - raw[index - 1].Heading);
            result.Add(new RouteSample(
                stations[index],
                raw[index].Point,
                raw[index].Heading,
                curvature,
                direction,
                Math.Abs(adjacent) > Math.PI / 18.0));
        }

        return result;
    }
}
