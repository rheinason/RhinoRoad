using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the behaviour of aiming the vehicle at a point, and of unwinding the wheel instead.
/// </summary>
public sealed class CursorSteeringTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(string id = "PV", string mode = "B")
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    private static VehicleState Start(double headingRadians = 0.0, double steeringRadians = 0.0) =>
        new(new Point3(0, 0, 0), headingRadians, steeringRadians, TravelDirection.Forward, 0.0);

    [Theory]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(30.0)]
    public void AimingAtAPointTurnsTheVehicleTwiceTheBearingPicked(double bearingDegrees)
    {
        // An arc tangent to the current heading and passing through the cursor ends rotated by twice
        // the bearing of that cursor. It is the reason a path aimed straight at the exit line
        // overshoots and has to be corrected back, producing an S.
        var (vehicle, mode) = Preset();
        var bearing = bearingDegrees * Math.PI / 180.0;
        var target = new Point2(20.0 * Math.Cos(bearing), 20.0 * Math.Sin(bearing));

        var controls = RateLimitedTrajectoryGenerator.ControlsFromCursor(vehicle, mode, Start(), target);
        var leg = new RateLimitedTrajectoryGenerator()
            .GenerateLeg(vehicle, mode, Start(), controls.TargetSteeringRadians, controls.TravelDistanceMetres, 0.05);

        var endDegrees = leg.EndState.VehicleHeadingRadians * 180.0 / Math.PI;
        Assert.InRange(endDegrees / bearingDegrees, 1.9, 2.0);
    }

    [Fact]
    public void UnwindingTheWheelKeepsTurningWhileItCentres()
    {
        // The straightening move: hold no target bearing, just run the wheel back to centre. The
        // vehicle keeps turning as it does, which is the gradual exit an aimed arc cannot make.
        var (vehicle, mode) = Preset();
        var turning = Start(0.0, mode.MaximumWheelAngleRadians);

        var leg = new RateLimitedTrajectoryGenerator().GenerateLeg(vehicle, mode, turning, 0.0, 10.0, 0.05);

        Assert.Equal(0.0, leg.EndState.SteeringAngleRadians, 6);
        Assert.True(
            leg.EndState.VehicleHeadingRadians > 0.0,
            "the vehicle must keep turning while the wheel returns to centre");
    }

    [Fact]
    public void AStraighteningLegEndsWithNoCurvature()
    {
        var (vehicle, mode) = Preset();
        var turning = Start(0.5, mode.MaximumWheelAngleRadians);

        var leg = new RateLimitedTrajectoryGenerator().GenerateLeg(vehicle, mode, turning, 0.0, 12.0, 0.05);

        Assert.Equal(0.0, leg.Samples[^1].SignedCurvaturePerMetre, 6);
        // And the last stretch is genuinely straight, not merely ending straight.
        var tail = leg.Samples.TakeLast(20).ToArray();
        Assert.All(tail, sample => Assert.Equal(0.0, sample.SignedCurvaturePerMetre, 4));
    }

    [Fact]
    public void TooShortAStraighteningRunLeavesLockOn()
    {
        // Lock-to-lock time is a real constraint: the wheel cannot centre in no distance, so a short
        // run leaves the vehicle still turning. The command has to let the user run it out further.
        var (vehicle, mode) = Preset();
        var turning = Start(0.0, mode.MaximumWheelAngleRadians);

        var brief = new RateLimitedTrajectoryGenerator().GenerateLeg(vehicle, mode, turning, 0.0, 1.0, 0.05);

        Assert.True(Math.Abs(brief.EndState.SteeringAngleRadians) > 0.0);
        Assert.True(Math.Abs(brief.EndState.SteeringAngleRadians) < mode.MaximumWheelAngleRadians);
    }
}
