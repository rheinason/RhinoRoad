using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the manoeuvre that ends the vehicle travelling in a chosen direction, wheel centred.
/// </summary>
/// <remarks>
/// The property being defended is that it lands on the direction asked for. Running the wheel out
/// over a picked distance instead — which is what this replaced — leaves the finishing direction to
/// fall out of the arithmetic, so the vehicle overshoots the line it was meant to leave along and
/// the correction back is the S through the exit of a turn.
/// </remarks>
public sealed class HeadingLegTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(
        string id = "PV",
        string mode = "B")
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    private static VehicleState Start(
        double steeringDegrees = 0.0,
        double headingDegrees = 0.0,
        TravelDirection direction = TravelDirection.Forward) =>
        new(
            new Point3(0, 0, 0),
            headingDegrees * Math.PI / 180.0,
            steeringDegrees * Math.PI / 180.0,
            direction,
            0.0);

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    [Theory]
    [InlineData(0.0, 15.0)]
    [InlineData(0.0, 45.0)]
    [InlineData(0.0, 90.0)]
    [InlineData(0.0, -30.0)]
    [InlineData(10.0, 0.0)]
    [InlineData(20.0, 45.0)]
    [InlineData(29.0, 90.0)]
    [InlineData(-20.0, 15.0)]
    public void TheLegEndsOnTheChosenDirectionWithTheWheelCentred(double steeringDegrees, double targetDegrees)
    {
        var (vehicle, mode) = Preset();

        var leg = HeadingLegGenerator.ToHeading(
            vehicle,
            mode,
            Start(steeringDegrees),
            targetDegrees * Math.PI / 180.0,
            0.05);

        Assert.Equal(targetDegrees, Degrees(leg.EndState.VehicleHeadingRadians), 2);
        Assert.Equal(0.0, Degrees(leg.EndState.SteeringAngleRadians), 2);
    }

    /// <summary>
    /// With the wheel already turned further than the chosen direction needs, unwinding alone would
    /// carry the vehicle past it. The wheel has to come back through centre and load the other way
    /// first, which is what a driver does, and the leg still finishes where it was asked to.
    /// </summary>
    [Fact]
    public void AWheelTurnedTooFarCounterSteersRatherThanOvershooting()
    {
        var (vehicle, mode) = Preset();
        var start = Start(steeringDegrees: 30.0);

        // Unwinding from here turns the vehicle further than this, so it must counter-steer.
        var reachableByUnwinding = HeadingLegGenerator.HeadingChangeFromUnwinding(vehicle, mode, start);
        var target = reachableByUnwinding * 0.4;

        var leg = HeadingLegGenerator.ToHeading(vehicle, mode, start, target, 0.05);

        Assert.Equal(Degrees(target), Degrees(leg.EndState.VehicleHeadingRadians), 2);
        Assert.Equal(0.0, Degrees(leg.EndState.SteeringAngleRadians), 2);
        Assert.Contains(leg.Samples, sample => sample.SignedCurvaturePerMetre < -1e-6);
    }

    [Fact]
    public void AVehicleAlreadyStraightAndPointingTheRightWayDoesNotMove()
    {
        var (vehicle, mode) = Preset();

        var leg = HeadingLegGenerator.ToHeading(vehicle, mode, Start(), 0.0, 0.05);

        Assert.Equal(0.0, leg.EndState.StationMetres, 9);
    }

    [Theory]
    [InlineData(170.0)]
    [InlineData(200.0)]
    public void ReversingEndsTravellingInTheChosenDirection(double targetDegrees)
    {
        var (vehicle, mode) = Preset();
        var start = Start(steeringDegrees: 15.0, direction: TravelDirection.Reverse);

        var leg = HeadingLegGenerator.ToHeading(
            vehicle,
            mode,
            start,
            targetDegrees * Math.PI / 180.0,
            0.05);

        // The direction of travel, which when reversing is opposite the way the vehicle faces.
        var travelHeading = Geometry2D.NormalizeAngle(leg.EndState.VehicleHeadingRadians + Math.PI);
        var expected = Geometry2D.NormalizeAngle(targetDegrees * Math.PI / 180.0);
        Assert.Equal(0.0, Degrees(Geometry2D.NormalizeAngle(travelHeading - expected)), 2);
        Assert.Equal(0.0, Degrees(leg.EndState.SteeringAngleRadians), 2);
    }

    [Theory]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    [InlineData("REN", "A")]
    [InlineData("BUS12", "B")]
    public void TheModeLimitsHoldThroughout(string vehicleId, string modeId)
    {
        var (vehicle, mode) = Preset(vehicleId, modeId);
        var leg = HeadingLegGenerator.ToHeading(vehicle, mode, Start(), Math.PI / 2.0, 0.05);
        var secondsPerStep = 0.05 / mode.SpeedMetresPerSecond;
        var previous = 0.0;

        foreach (var sample in leg.Samples)
        {
            var steering = Math.Atan(
                sample.SignedCurvaturePerMetre * vehicle.WheelbaseMetres * (double)sample.Direction);
            Assert.True(Math.Abs(steering) <= mode.MaximumWheelAngleRadians + 1e-9);
            Assert.True(
                Math.Abs(steering - previous) / secondsPerStep <= mode.MaximumSteeringRateRadiansPerSecond + 1e-9);
            previous = steering;
        }
    }

    /// <summary>
    /// The closed form the whole manoeuvre pivots on: how much the vehicle still turns while the
    /// wheel runs back to centre. If this were wrong, every leg would stop turning at the wrong
    /// moment, so it is checked against actually driving the unwind.
    /// </summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(25.0)]
    [InlineData(35.0)]
    public void TheUnwindingFormulaMatchesDrivingTheUnwind(double steeringDegrees)
    {
        var (vehicle, mode) = Preset();
        var start = Start(steeringDegrees);

        var predicted = HeadingLegGenerator.HeadingChangeFromUnwinding(vehicle, mode, start);
        var driven = new RateLimitedTrajectoryGenerator()
            .GenerateLeg(vehicle, mode, start, 0.0, 200.0, 0.005);

        Assert.Equal(Degrees(predicted), Degrees(driven.EndState.VehicleHeadingRadians), 2);
    }

    [Fact]
    public void ASensibleStepIsRequired()
    {
        var (vehicle, mode) = Preset();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HeadingLegGenerator.ToHeading(vehicle, mode, Start(), 1.0, 0.0));
    }
}
