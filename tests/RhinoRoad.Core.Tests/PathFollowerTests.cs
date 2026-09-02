using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins what a vehicle makes of a drawn line.
/// </summary>
/// <remarks>
/// Two properties matter and they pull against each other: a line the vehicle can drive is followed
/// closely, and a line it cannot is not faked. The second is why deviation is reported rather than
/// suppressed — the route must stay inside the mode's limits even when that means leaving the line.
/// </remarks>
public sealed class PathFollowerTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(
        string id = "PV",
        string mode = "A")
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    private static List<Point3> Straight(double lengthMetres, double fromX = 0.0)
    {
        var points = new List<Point3>();
        for (var x = fromX; x <= fromX + lengthMetres; x += 0.05) points.Add(new Point3(x, 0.0, 0.0));
        return points;
    }

    /// <summary>A straight lead-in, then an arc — the way a drawn alignment actually starts.</summary>
    private static List<Point3> ArcAfterLeadIn(double radiusMetres, double sweepDegrees, double leadInMetres = 20.0)
    {
        var points = Straight(leadInMetres, -leadInMetres);
        var sweep = sweepDegrees * Math.PI / 180.0;
        var count = Math.Max(2, (int)(radiusMetres * sweep / 0.05));
        for (var index = 1; index <= count; index++)
        {
            var angle = index * sweep / count;
            points.Add(new Point3(
                radiusMetres * Math.Sin(angle),
                radiusMetres * (1.0 - Math.Cos(angle)),
                0.0));
        }

        return points;
    }

    [Fact]
    public void AStraightLineIsFollowedExactly()
    {
        var (vehicle, mode) = Preset();
        var intent = IntentPath.FromPoints(Straight(50.0));

        var route = PathFollower.Follow(vehicle, mode, intent);

        Assert.True(route.MaximumDeviationMetres < 1e-6, $"strayed {route.MaximumDeviationMetres:0.000000} m");
    }

    [Theory]
    [InlineData("PV", 40.0, 0.10)]
    [InlineData("PV", 15.0, 0.10)]
    [InlineData("REN", 40.0, 0.15)]
    [InlineData("BUS12", 40.0, 0.10)]
    public void ALineTheVehicleCanDriveIsFollowedClosely(string vehicleId, double radius, double toleranceMetres)
    {
        var (vehicle, mode) = Preset(vehicleId);
        var intent = IntentPath.FromPoints(ArcAfterLeadIn(radius, 90.0));

        var route = PathFollower.Follow(vehicle, mode, intent);

        Assert.True(
            route.MaximumDeviationMetres < toleranceMetres,
            $"{vehicleId} strayed {route.MaximumDeviationMetres:0.000} m from an R={radius} m arc");
    }

    /// <summary>
    /// A curve tighter than the vehicle can drive has to produce a route that leaves it, not a
    /// route that pretends. Reporting the gap is the point; hiding it would make the tool lie.
    /// </summary>
    [Fact]
    public void ALineTooTightToDriveIsLeftBehindAndReported()
    {
        var (vehicle, mode) = Preset();
        var minimumRadius = vehicle.WheelbaseMetres / Math.Tan(mode.MaximumWheelAngleRadians);
        var intent = IntentPath.FromPoints(ArcAfterLeadIn(minimumRadius / 2.0, 90.0));

        var route = PathFollower.Follow(vehicle, mode, intent);

        Assert.True(route.MaximumDeviationMetres > 1.0, "an undrivable curve must show a real deviation");
        Assert.True(route.MaximumDeviationStationMetres > 0.0);
    }

    [Theory]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    [InlineData("BUS12", "A")]
    public void TheRouteStaysInsideTheModeLimitsEvenOffTheLine(string vehicleId, string modeId)
    {
        var (vehicle, mode) = Preset(vehicleId, modeId);
        var minimumRadius = vehicle.WheelbaseMetres / Math.Tan(mode.MaximumWheelAngleRadians);
        var intent = IntentPath.FromPoints(ArcAfterLeadIn(minimumRadius / 3.0, 120.0));

        var route = PathFollower.Follow(vehicle, mode, intent);
        var secondsPerStep = PathFollower.StepMetres / mode.SpeedMetresPerSecond;
        var previous = 0.0;

        foreach (var sample in route.Samples)
        {
            var steering = Math.Atan(
                sample.SignedCurvaturePerMetre * vehicle.WheelbaseMetres * (double)sample.Direction);
            Assert.True(Math.Abs(steering) <= mode.MaximumWheelAngleRadians + 1e-9);
            Assert.True(
                Math.Abs(steering - previous) / secondsPerStep <= mode.MaximumSteeringRateRadiansPerSecond + 1e-9);
            previous = steering;
        }
    }

    [Fact]
    public void DeviationStatisticsAgreeWithEachOther()
    {
        var (vehicle, mode) = Preset();
        var intent = IntentPath.FromPoints(ArcAfterLeadIn(15.0, 90.0));

        var route = PathFollower.Follow(vehicle, mode, intent);

        Assert.True(route.RootMeanSquareDeviationMetres <= route.MaximumDeviationMetres);
        Assert.True(route.DeviationAtEndMetres <= route.MaximumDeviationMetres + 1e-9);
    }

    [Fact]
    public void ReversingFollowsTheLineBackwards()
    {
        var (vehicle, mode) = Preset();
        var intent = IntentPath.FromPoints(Straight(30.0));

        var route = PathFollower.Follow(vehicle, mode, intent, TravelDirection.Reverse);

        Assert.All(route.Samples, sample => Assert.Equal(TravelDirection.Reverse, sample.Direction));
        Assert.True(route.MaximumDeviationMetres < 1e-6);
    }

    [Fact]
    public void SamplesAreSpacedAtTheFollowerStep()
    {
        var (vehicle, mode) = Preset();
        var route = PathFollower.Follow(vehicle, mode, IntentPath.FromPoints(Straight(10.0)));
        var previous = 0.0;

        foreach (var sample in route.Samples.Skip(1))
        {
            Assert.Equal(PathFollower.StepMetres, sample.StationMetres - previous, 9);
            previous = sample.StationMetres;
        }
    }
}
