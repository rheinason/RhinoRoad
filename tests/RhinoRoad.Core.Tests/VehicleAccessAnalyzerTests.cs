using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class VehicleAccessAnalyzerTests
{
    private readonly VehicleDefinition _vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
    private readonly VehicleAccessAnalyzer _analyzer = new();

    [Fact]
    public void StraightRouteProducesExpectedTracksAndEnvelope()
    {
        var samples = StraightSamples(0.0, 10.0, 1.0, 0.0);
        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["A"], samples);

        Assert.True(result.IsFeasible);
        Assert.Equal(0.0, result.MaximumSteeringAngleRadians, 8);
        Assert.Equal(0.0, result.MaximumAbsoluteGrade, 8);
        Assert.Equal(_vehicle.WheelbaseMetres, result.FrontAxleTrackMetres[0].X, 6);
        Assert.Equal(-_vehicle.RearOverhangMetres, result.Poses[0].BodyOutlineWorldMetres.Min(point => point.X), 6);
    }

    [Fact]
    public void CircularRouteComputesBicycleSteeringAngle()
    {
        const double radius = 10.0;
        var samples = CircleSamples(radius, Math.PI / 2.0, 0.2);
        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["B"], samples);

        Assert.True(result.IsFeasible);
        Assert.Equal(Math.Atan(_vehicle.WheelbaseMetres / radius), result.MaximumSteeringAngleRadians, 5);
    }

    [Fact]
    public void TightCurveReportsSteeringViolation()
    {
        var samples = CircleSamples(2.0, Math.PI / 2.0, 0.1);
        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["B"], samples);

        Assert.Contains(result.Violations, violation => violation.Kind == ViolationKind.SteeringAngle);
    }

    [Fact]
    public void AbruptCurvatureReportsSteeringRateViolation()
    {
        var samples = new[]
        {
            new RouteSample(0.0, new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Forward),
            new RouteSample(0.1, new Point3(0.1, 0, 0), 0.0, 0.2, TravelDirection.Forward),
            new RouteSample(0.2, new Point3(0.2, 0, 0), 0.02, 0.2, TravelDirection.Forward)
        };

        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["A"], samples);

        Assert.Contains(result.Violations, violation => violation.Kind == ViolationKind.SteeringRate);
    }

    [Fact]
    public void ThreeDimensionalRouteReportsAndChecksGrade()
    {
        var samples = StraightSamples(0.0, 10.0, 1.0, 0.10);
        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["A"], samples, maximumAbsoluteGrade: 0.08);

        Assert.Equal(0.10, result.MaximumAbsoluteGrade, 6);
        Assert.Contains(result.Violations, violation => violation.Kind == ViolationKind.Grade);
    }

    [Fact]
    public void ReverseRouteOrientsVehicleOppositeTravelTangent()
    {
        var samples = StraightSamples(0.0, 2.0, 1.0, 0.0)
            .Select(sample => sample with { Direction = TravelDirection.Reverse })
            .ToArray();
        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["B"], samples);

        Assert.Equal(Math.PI, Math.Abs(result.Poses[0].VehicleHeadingRadians), 6);
        Assert.True(result.FrontAxleTrackMetres[0].X < result.RearAxleTrackMetres[0].X);
    }

    [Fact]
    public void ExplicitTangentDiscontinuityIsInfeasible()
    {
        var samples = StraightSamples(0.0, 2.0, 1.0, 0.0).ToArray();
        samples[1] = samples[1] with { IsTangentDiscontinuous = true };

        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["A"], samples);

        Assert.Contains(result.Violations, violation => violation.Kind == ViolationKind.TangentDiscontinuity);
    }

    [Fact]
    public void SmoothSCurveRemainsFeasible()
    {
        const double length = 20.0;
        const double step = 0.1;
        var samples = new List<RouteSample>();
        double x = 0.0;
        double y = 0.0;
        double heading = 0.0;
        for (var station = 0.0; station <= length + 1e-9; station += step)
        {
            double curvature = 0.04 * Math.Sin((Math.PI * 2.0 * station) / length);
            samples.Add(new RouteSample(station, new Point3(x, y, 0.0), heading, curvature, TravelDirection.Forward));
            heading += curvature * step;
            x += Math.Cos(heading) * step;
            y += Math.Sin(heading) * step;
        }

        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["B"], samples);

        Assert.True(result.IsFeasible);
        Assert.True(result.FrontAxleTrackMetres.Max(point => Math.Abs(point.Y)) > 0.1);
    }

    [Fact]
    public void ClosedCircularLoopProducesCoincidentTrackEnds()
    {
        const double radius = 12.0;
        var samples = CircleSamplesByCount(radius, Math.PI * 2.0, 240);

        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["B"], samples);

        Assert.True(result.IsFeasible);
        Assert.InRange(result.RearAxleTrackMetres[0].DistanceTo(result.RearAxleTrackMetres[^1]), 0.0, 1e-8);
        Assert.InRange(result.FrontAxleTrackMetres[0].DistanceTo(result.FrontAxleTrackMetres[^1]), 0.0, 1e-8);
    }

    [Fact]
    public void RouteShorterThanVehicleStillProducesValidPoses()
    {
        var samples = StraightSamples(0.0, 1.0, 0.1, 0.0);

        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["A"], samples);

        Assert.True(result.IsFeasible);
        Assert.Equal(samples.Count, result.Poses.Count);
        Assert.True(_vehicle.FrontOverhangMetres + _vehicle.WheelbaseMetres + _vehicle.RearOverhangMetres > 1.0);
    }

    [Fact]
    public void GradeIsReportedWithoutBecomingFailureWhenNoLimitIsSupplied()
    {
        var result = _analyzer.Analyze(
            _vehicle,
            _vehicle.DrivingModes["A"],
            StraightSamples(0.0, 10.0, 1.0, 0.12));

        Assert.True(result.IsFeasible);
        Assert.Equal(0.12, result.MaximumAbsoluteGrade, 6);
        Assert.DoesNotContain(result.Violations, violation => violation.Kind == ViolationKind.Grade);
    }

    private static IReadOnlyList<RouteSample> StraightSamples(double start, double end, double step, double grade)
    {
        var result = new List<RouteSample>();
        for (var station = start; station <= end + 1e-9; station += step)
        {
            result.Add(new RouteSample(station, new Point3(station, 0.0, station * grade), 0.0, 0.0, TravelDirection.Forward));
        }
        return result;
    }

    private static IReadOnlyList<RouteSample> CircleSamples(double radius, double angle, double step)
    {
        var length = radius * angle;
        var result = new List<RouteSample>();
        for (var station = 0.0; station <= length + 1e-9; station += step)
        {
            var theta = station / radius;
            result.Add(new RouteSample(
                station,
                new Point3(radius * Math.Sin(theta), radius * (1.0 - Math.Cos(theta)), 0.0),
                theta,
                1.0 / radius,
                TravelDirection.Forward));
        }
        return result;
    }

    private static IReadOnlyList<RouteSample> CircleSamplesByCount(double radius, double angle, int count)
    {
        var result = new List<RouteSample>(count + 1);
        for (var index = 0; index <= count; index++)
        {
            double theta = angle * index / count;
            double station = radius * theta;
            result.Add(new RouteSample(
                station,
                new Point3(radius * Math.Sin(theta), radius * (1.0 - Math.Cos(theta)), 0.0),
                theta,
                1.0 / radius,
                TravelDirection.Forward));
        }
        return result;
    }
}
