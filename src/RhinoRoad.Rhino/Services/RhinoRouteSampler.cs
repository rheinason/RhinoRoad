using Rhino;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

internal static class RhinoRouteSampler
{
    public static IReadOnlyList<RouteSample> Sample(
        Curve curve,
        UnitSystem modelUnits,
        TravelDirection direction,
        double targetSpacingMetres = 0.10)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var metresPerModelUnit = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        var lengthMetres = curve.GetLength() * metresPerModelUnit;
        if (lengthMetres <= 1e-6) throw new ArgumentException("The selected path is too short.", nameof(curve));
        var divisionCount = Math.Max(2, (int)Math.Ceiling(lengthMetres / targetSpacingMetres));
        var parameters = curve.DivideByCount(divisionCount, includeEnds: true);
        if (parameters is null || parameters.Length < 2) throw new InvalidOperationException("The path could not be sampled.");

        var raw = new List<(double Parameter, Point3 Point, double Heading)>(parameters.Length);
        foreach (var parameter in parameters)
        {
            var point = curve.PointAt(parameter);
            var tangent = curve.TangentAt(parameter);
            var planLength = Math.Sqrt((tangent.X * tangent.X) + (tangent.Y * tangent.Y));
            if (planLength <= 1e-10) throw new InvalidOperationException("The path has a vertical or undefined tangent in plan.");
            raw.Add((
                parameter,
                new Point3(point.X * metresPerModelUnit, point.Y * metresPerModelUnit, point.Z * metresPerModelUnit),
                Math.Atan2(tangent.Y, tangent.X)));
        }

        var stations = new double[raw.Count];
        for (var index = 1; index < raw.Count; index++)
        {
            stations[index] = stations[index - 1] + raw[index].Point.XY.DistanceTo(raw[index - 1].Point.XY);
        }

        var result = new List<RouteSample>(raw.Count);
        for (var index = 0; index < raw.Count; index++)
        {
            var previous = Math.Max(0, index - 1);
            var next = Math.Min(raw.Count - 1, index + 1);
            var span = Math.Max(stations[next] - stations[previous], 1e-9);
            var headingChange = Geometry2D.NormalizeAngle(raw[next].Heading - raw[previous].Heading);
            var curvature = headingChange / span;
            var adjacentHeadingChange = index == 0
                ? Geometry2D.NormalizeAngle(raw[1].Heading - raw[0].Heading)
                : Geometry2D.NormalizeAngle(raw[index].Heading - raw[index - 1].Heading);
            var tangentDiscontinuity = Math.Abs(adjacentHeadingChange) > Math.PI / 18.0;
            result.Add(new RouteSample(
                stations[index],
                raw[index].Point,
                raw[index].Heading,
                curvature,
                direction,
                tangentDiscontinuity));
        }

        return result;
    }

    public static PolylineCurve ToPlanCurve(IReadOnlyList<RouteSample> samples, UnitSystem modelUnits, double elevationMetres)
    {
        var modelUnitsPerMetre = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var points = samples.Select(sample => new Point3d(
            sample.PositionMetres.X * modelUnitsPerMetre,
            sample.PositionMetres.Y * modelUnitsPerMetre,
            elevationMetres * modelUnitsPerMetre));
        return new PolylineCurve(points);
    }
}
