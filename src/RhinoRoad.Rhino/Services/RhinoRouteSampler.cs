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
        var divisions = curve.DivideByCount(divisionCount, includeEnds: true);
        if (divisions is null || divisions.Length < 2) throw new InvalidOperationException("The path could not be sampled.");

        // Kinks are found on the curve rather than inferred from neighbouring headings, which miss
        // anything gentler than the threshold and leave the curvature spike on the wrong sample.
        var parameters = SamplingParameters.MergeBreaks(
            divisions,
            FindTangentBreaks(curve),
            (divisions[^1] - divisions[0]) / divisionCount * 0.01);

        var raw = new List<(Point3 Point, double Heading, double Curvature, bool IsBreak)>(parameters.Count);
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index].Parameter;
            var point = curve.PointAt(parameter);
            var tangent = curve.TangentAt(parameter);
            var planLength = Math.Sqrt((tangent.X * tangent.X) + (tangent.Y * tangent.Y));
            if (planLength <= 1e-10) throw new InvalidOperationException("The path has a vertical or undefined tangent in plan.");

            // The ends of the curve are never reported as a kink.
            var isBreak = parameters[index].IsTangentBreak && index > 0 && index < parameters.Count - 1;

            // At a kink curvature is not defined, and the break is reported in its own right.
            var curvature = 0.0;
            if (!isBreak)
            {
                var curvatureVector = curve.CurvatureAt(parameter);
                if (curvatureVector.IsValid)
                {
                    // Curvature of the plan projection, signed positive to the left: the turn of the
                    // heading per plan distance. The cross product of tangent and curvature vector,
                    // over the plan length cubed, gives it directly and stays finite for a sloping curve.
                    var cross = (tangent.X * curvatureVector.Y) - (tangent.Y * curvatureVector.X);
                    curvature = cross / (planLength * planLength * planLength) / metresPerModelUnit;
                }
            }

            raw.Add((
                new Point3(point.X * metresPerModelUnit, point.Y * metresPerModelUnit, point.Z * metresPerModelUnit),
                Math.Atan2(tangent.Y, tangent.X),
                curvature,
                isBreak));
        }

        var stations = new double[raw.Count];
        for (var index = 1; index < raw.Count; index++)
        {
            stations[index] = stations[index - 1] + raw[index].Point.XY.DistanceTo(raw[index - 1].Point.XY);
        }

        var result = new List<RouteSample>(raw.Count);
        for (var index = 0; index < raw.Count; index++)
        {
            result.Add(new RouteSample(
                stations[index],
                raw[index].Point,
                raw[index].Heading,
                raw[index].Curvature,
                direction,
                raw[index].IsBreak));
        }

        return result;
    }

    private static IReadOnlyList<double> FindTangentBreaks(Curve curve)
    {
        var breaks = new List<double>();
        var domain = curve.Domain;
        var from = domain.Min;

        // Half a degree: finer than any kink worth a driver's attention, coarse enough to ignore the
        // noise of a fitted NURBS.
        var cosAngleTolerance = Math.Cos(Math.PI / 360.0);
        while (curve.GetNextDiscontinuity(Continuity.G1_continuous, from, domain.Max, cosAngleTolerance, 0.0, out var t))
        {
            // A search that does not advance would never end.
            if (t <= from) break;
            breaks.Add(t);
            from = t;
        }

        return breaks;
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
