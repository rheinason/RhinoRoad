using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class TrajectoryAndGeometryTests
{
    [Fact]
    public void GeneratedTrajectoryRespectsSteeringRateAndAngleLimits()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("REN");
        var mode = vehicle.DrivingModes["A"];
        var start = new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var generator = new RateLimitedTrajectoryGenerator();

        var generated = generator.GenerateLeg(vehicle, mode, start, mode.MaximumWheelAngleRadians * 2.0, 12.0, 0.05);
        var analyzed = new VehicleAccessAnalyzer().Analyze(vehicle, mode, generated.Samples);

        Assert.True(Math.Abs(generated.EndState.SteeringAngleRadians) <= mode.MaximumWheelAngleRadians + 1e-9);
        Assert.DoesNotContain(analyzed.Violations, violation => violation.Kind == ViolationKind.SteeringRate);
    }

    [Fact]
    public void ReverseTrajectoryMovesOppositeVehicleHeading()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);

        var generated = new RateLimitedTrajectoryGenerator().GenerateLeg(vehicle, mode, start, 0.0, 2.0);

        Assert.True(generated.EndState.RearAxleCentreMetres.X < -1.99);
        Assert.Equal(0.0, generated.EndState.VehicleHeadingRadians, 6);
    }

    [Fact]
    public void GeometryDetectsIntersectionContainmentAndClearance()
    {
        Point2[] square = [new(0, 0), new(4, 0), new(4, 4), new(0, 4)];
        Point2[] crossing = [new(3, -1), new(5, -1), new(5, 1), new(3, 1)];
        Point2[] distant = [new(6, 0), new(7, 0), new(7, 1), new(6, 1)];

        Assert.True(Geometry2D.Contains(square, new Point2(2, 2)));
        Assert.Equal(0.0, Geometry2D.MinimumDistance(square, crossing), 6);
        Assert.Equal(2.0, Geometry2D.MinimumDistance(square, distant), 6);
    }
}
