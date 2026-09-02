using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the steering law and the cost of the wheel's own slew rate.
/// </summary>
/// <remarks>
/// These are the facts the driving model is built around rather than features in their own right:
/// why aiming cannot be done in one arc, and why the steering rate — not the turning radius — is
/// what actually limits these vehicles.
/// </remarks>
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
        // the bearing of that cursor — an inscribed-angle result. It is why the driver re-aims every
        // step instead of committing to the arc it solves: driven to its end, a single arc always
        // overshoots the direction the point was clicked in.
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
        // Centring the wheel does not stop the turn; the vehicle keeps rotating until the wheel is
        // actually straight. That lag is what makes a corner exit gradual rather than abrupt.
        var (vehicle, mode) = Preset();
        var turning = Start(0.0, mode.MaximumWheelAngleRadians);

        var leg = new RateLimitedTrajectoryGenerator().GenerateLeg(vehicle, mode, turning, 0.0, 10.0, 0.05);

        Assert.Equal(0.0, leg.EndState.SteeringAngleRadians, 6);
        Assert.True(
            leg.EndState.VehicleHeadingRadians > 0.0,
            "the vehicle must keep turning while the wheel returns to centre");
    }

    [Fact]
    public void ALegLongEnoughToCentreTheWheelEndsWithNoCurvature()
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
    public void TooShortARunLeavesLockOn()
    {
        // Lock-to-lock time is the binding constraint on these vehicles: in mode A the wheel needs
        // 12.5 m of travel to go from centre to full lock. A short leg therefore cannot centre the
        // wheel at all, which is why paths built from arcs alone are not drivable here.
        var (vehicle, mode) = Preset();
        var turning = Start(0.0, mode.MaximumWheelAngleRadians);

        var brief = new RateLimitedTrajectoryGenerator().GenerateLeg(vehicle, mode, turning, 0.0, 1.0, 0.05);

        Assert.True(Math.Abs(brief.EndState.SteeringAngleRadians) > 0.0);
        Assert.True(Math.Abs(brief.EndState.SteeringAngleRadians) < mode.MaximumWheelAngleRadians);
    }
}
