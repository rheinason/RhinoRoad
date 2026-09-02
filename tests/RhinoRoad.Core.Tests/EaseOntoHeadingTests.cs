using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the exit from a corner: rewinding to where the wheel should have started coming back.
/// </summary>
/// <remarks>
/// A driver eases out from partway through a bend. A committed leg has usually driven past that
/// moment, so leaving the route alone forces either more turn-in or a counter-steer, and both read
/// as a correction rather than as an exit. Rewinding is what makes the corner one continuous turn.
/// </remarks>
public sealed class EaseOntoHeadingTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(
        string id = "PV",
        string mode = "A")
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    /// <summary>
    /// A left-hand corner driven at full lock, as a committed route.
    /// </summary>
    /// <remarks>
    /// Kept under half a turn on purpose. Past that, headings wrap and the vehicle passes through
    /// the same compass direction more than once, so "the exit that needs more turn than this
    /// corner has produced" stops being a thing that can be asked. The solver copes -- it takes the
    /// latest crossing, giving back as little of the corner as it can -- but a test that leans on
    /// which crossing is meant would be testing the wrap, not the exit.
    /// </remarks>
    private static IReadOnlyList<RouteSample> Corner(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        double lengthMetres = 20.0)
    {
        var start = new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        return new RateLimitedTrajectoryGenerator()
            .GenerateLeg(vehicle, mode, start, mode.MaximumWheelAngleRadians, lengthMetres, 0.10)
            .Samples;
    }

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    [Fact]
    public void AShallowerExitRewindsIntoTheCorner()
    {
        var (vehicle, mode) = Preset();
        var route = Corner(vehicle, mode);
        var atEnd = HeadingLegGenerator.StateAt(route[^1], vehicle);

        // Half the heading the corner has already reached: an exit that needed the wheel to start
        // coming back well before the end of the committed leg.
        var target = atEnd.VehicleHeadingRadians * 0.5;

        var eased = HeadingLegGenerator.EaseOntoHeading(vehicle, mode, route, 0, target, 0.05);

        Assert.True(eased.FromIndex < route.Count - 1, "the exit should have rewound into the corner");
        Assert.Equal(Degrees(target), Degrees(eased.Leg.EndState.VehicleHeadingRadians), 1);
        Assert.Equal(0.0, Degrees(eased.Leg.EndState.SteeringAngleRadians), 2);
    }

    /// <summary>
    /// Rewinding exists to avoid the correction, so the eased exit must never turn back the other
    /// way: from a left-hand corner every sample of the exit still curves left.
    /// </summary>
    [Fact]
    public void TheEasedExitNeverCounterSteers()
    {
        var (vehicle, mode) = Preset();
        var route = Corner(vehicle, mode);
        var target = HeadingLegGenerator.StateAt(route[^1], vehicle).VehicleHeadingRadians * 0.5;

        var eased = HeadingLegGenerator.EaseOntoHeading(vehicle, mode, route, 0, target, 0.05);

        Assert.All(eased.Leg.Samples, sample => Assert.True(sample.SignedCurvaturePerMetre >= -1e-9));
    }

    /// <summary>
    /// The wheel only comes back through an ease-out; it does not load up again on the way.
    /// </summary>
    /// <remarks>
    /// Allowed a sample's worth of slack. The rewind lands on a committed route sample, and the
    /// exact moment to start unwinding falls between two of them, so the exit may hold the lock for
    /// a fraction of a sample before letting it go. That is under a quarter of a degree of wheel at
    /// this mode's slew rate, and it is sampling granularity rather than a correction.
    /// </remarks>
    [Fact]
    public void TheWheelUnwindsMonotonicallyThroughTheExit()
    {
        var (vehicle, mode) = Preset();
        var route = Corner(vehicle, mode);
        var target = HeadingLegGenerator.StateAt(route[^1], vehicle).VehicleHeadingRadians * 0.5;

        var eased = HeadingLegGenerator.EaseOntoHeading(vehicle, mode, route, 0, target, 0.05);

        var slackRadians = mode.MaximumSteeringRateRadiansPerSecond * (0.10 / mode.SpeedMetresPerSecond);
        var previous = double.MaxValue;
        foreach (var sample in eased.Leg.Samples)
        {
            var steering = Math.Abs(Math.Atan(
                sample.SignedCurvaturePerMetre * vehicle.WheelbaseMetres * (double)sample.Direction));
            Assert.True(
                steering <= previous + slackRadians,
                $"the wheel loaded up by {Degrees(steering - previous):0.000} deg during the exit");
            previous = Math.Max(steering, previous == double.MaxValue ? steering : previous);
            previous = steering;
        }
    }

    [Fact]
    public void AnExitNeedingMoreTurnDoesNotRewind()
    {
        var (vehicle, mode) = Preset();
        var route = Corner(vehicle, mode);
        var atEnd = HeadingLegGenerator.StateAt(route[^1], vehicle);

        Assert.InRange(Degrees(atEnd.VehicleHeadingRadians), 1.0, 179.0);

        // Beyond where even unwinding from the end lands, so there is nothing to give back. That
        // margin has to clear the unwind itself, which from full lock in mode A is worth some 70
        // degrees on its own -- the wheel keeps the vehicle turning all the way back to centre.
        var target = atEnd.VehicleHeadingRadians
            + HeadingLegGenerator.HeadingChangeFromUnwinding(vehicle, mode, atEnd)
            + (20.0 * Math.PI / 180.0);

        var eased = HeadingLegGenerator.EaseOntoHeading(vehicle, mode, route, 0, target, 0.05);

        Assert.Equal(route.Count - 1, eased.FromIndex);

        // Compared as directions rather than as numbers: this target is past half a turn from where
        // the vehicle started, so the two name the same heading on different laps.
        Assert.Equal(
            0.0,
            Degrees(Geometry2D.NormalizeAngle(eased.Leg.EndState.VehicleHeadingRadians - target)),
            1);
    }

    /// <summary>
    /// The caller pins the earliest sample the exit may reach, so a reversing leg on the far side of
    /// a cusp cannot be silently undone by easing the corner after it.
    /// </summary>
    [Fact]
    public void TheRewindStopsAtTheEarliestSampleAllowed()
    {
        var (vehicle, mode) = Preset();
        var route = Corner(vehicle, mode);
        var target = HeadingLegGenerator.StateAt(route[^1], vehicle).VehicleHeadingRadians * 0.2;
        var earliest = route.Count - 10;

        var eased = HeadingLegGenerator.EaseOntoHeading(vehicle, mode, route, earliest, target, 0.05);

        Assert.True(eased.FromIndex >= earliest);
    }

    [Fact]
    public void RewindingRestoresTheWheelTheRouteWasCarrying()
    {
        var (vehicle, mode) = Preset();
        var route = Corner(vehicle, mode);
        var middle = route[route.Count / 2];

        var restored = HeadingLegGenerator.StateAt(middle, vehicle);

        Assert.Equal(middle.PositionMetres, restored.RearAxleCentreMetres);
        Assert.Equal(middle.StationMetres, restored.StationMetres);
        Assert.Equal(
            middle.SignedCurvaturePerMetre,
            (double)restored.Direction * Math.Tan(restored.SteeringAngleRadians) / vehicle.WheelbaseMetres,
            9);
    }

    [Fact]
    public void AnEmptyRouteIsRejected()
    {
        var (vehicle, mode) = Preset();

        Assert.Throws<ArgumentException>(() =>
            HeadingLegGenerator.EaseOntoHeading(vehicle, mode, [], 0, 1.0, 0.05));
    }
}
