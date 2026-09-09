using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Turning the wheel while the vehicle is stopped.
/// </summary>
/// <remarks>
/// The steering rate is a rate per second, and the leg generators spend it per metre travelled, so a
/// wheel movement always costs distance — 12.5 m from centre to full lock in mode A and 4.17 m in
/// mode B, the same for every preset, because the rate and the lock scale together. Standing still,
/// that cost is zero, and the manoeuvres where it matters are exactly the tight ones: a shuffle in a
/// turning area spends most of its length on the wheel rather than on the turn.
/// </remarks>
public sealed class StandstillSteeringTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(
        string id = "REN", string modeId = "B")
    {
        var vehicle = Catalog.Get(id);
        return (vehicle, vehicle.DrivingModes[modeId]);
    }

    /// <summary>
    /// The rate limit is what makes a wheel movement cost distance, and it is that cost — not the
    /// lock angle — that a standstill removes.
    /// </summary>
    [Theory]
    [InlineData("A", 12.5)]
    [InlineData("B", 4.17)]
    public void ReachingFullLockCostsAFixedDistanceThatIsTheSameForEveryPreset(
        string modeId, double expectedMetres)
    {
        foreach (var vehicle in Catalog.Vehicles)
        {
            var mode = vehicle.DrivingModes[modeId];
            var metres = mode.MaximumWheelAngleRadians
                / (mode.MaximumSteeringRateRadiansPerSecond / mode.SpeedMetresPerSecond);
            Assert.Equal(expectedMetres, metres, 2);
        }
    }

    [Fact]
    public void AWheelTurnedAtAStandstillIsAtItsAngleBeforeTheVehicleMoves()
    {
        var (vehicle, mode) = Preset();
        var state = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var lockAngle = mode.MaximumWheelAngleRadians;

        var rolling = RateLimitedTrajectoryGenerator.Advance(vehicle, mode, state, lockAngle, 0.10);
        var stopped = RateLimitedTrajectoryGenerator.Advance(
            vehicle, mode, state, lockAngle, 0.10, wheelSetAtStandstill: true);

        Assert.True(rolling.State.SteeringAngleRadians < lockAngle * 0.05, "rolling, the wheel barely moves");
        Assert.Equal(lockAngle, stopped.State.SteeringAngleRadians, 9);

        // And it therefore turns the vehicle over that step, where the rolling one hardly does.
        Assert.True(Math.Abs(stopped.HeadingChangeRadians) > Math.Abs(rolling.HeadingChangeRadians) * 10.0);
    }

    /// <summary>
    /// A change of direction is a standstill whether or not anyone asked for one, because the vehicle
    /// has to stop to make it. This is read from the route rather than the state, since callers set
    /// the new direction on the state before planning.
    /// </summary>
    [Fact]
    public void AChangeOfDirectionTurnsTheWheelForFree()
    {
        var (vehicle, mode) = Preset();
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };

        // Drive forward on full lock, so the wheel is hard over when the direction changes.
        var forward = ManoeuvreReplayService.PlanControl(
            vehicle, mode, route, 0, start,
            new ManoeuvreControl(LockedTurnGenerator.CursorForSweep(start, Math.PI / 2.0, 10.0),
                TravelDirection.Forward, ManoeuvreControlKind.Turn));
        route.AddRange(forward.Samples.Skip(1));
        Assert.True(Math.Abs(forward.EndState.SteeringAngleRadians) > mode.MaximumWheelAngleRadians * 0.9);

        // Reversing, the wheel goes the other way. Free at the stop, it is there on the first sample.
        var reversed = ManoeuvreReplayService.PlanControl(
            vehicle, mode, route, route.Count - 1,
            forward.EndState with { Direction = TravelDirection.Reverse },
            new ManoeuvreControl(
                LockedTurnGenerator.CursorForSweep(
                    forward.EndState with { Direction = TravelDirection.Reverse }, -Math.PI / 4.0, 10.0),
                TravelDirection.Reverse,
                ManoeuvreControlKind.Turn));

        var firstMoved = reversed.Samples[1];
        var curvature = Math.Abs(firstMoved.SignedCurvaturePerMetre) * vehicle.WheelbaseMetres;
        Assert.True(
            Math.Atan(curvature) > mode.MaximumWheelAngleRadians * 0.9,
            "the reversing leg should start already at lock");
    }

    /// <summary>
    /// Away from a direction change the stop has to be asked for, because inventing one shrinks the
    /// swept envelope, and an envelope smaller than the vehicle really needs is the unsafe direction
    /// to be wrong in.
    /// </summary>
    [Fact]
    public void AStopMidRouteIsOnlyTakenWhenItIsAskedFor()
    {
        var (vehicle, mode) = Preset();
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };
        var target = new Point3(18, 12, 0);

        var rolling = ManoeuvreReplayService.PlanControl(
            vehicle, mode, route, 0, start, new ManoeuvreControl(target, TravelDirection.Forward));
        var stopped = ManoeuvreReplayService.PlanControl(
            vehicle, mode, route, 0, start,
            new ManoeuvreControl(target, TravelDirection.Forward, FromStandstill: true));

        var rollingFirst = Math.Abs(rolling.Samples[1].SignedCurvaturePerMetre);
        var stoppedFirst = Math.Abs(stopped.Samples[1].SignedCurvaturePerMetre);
        Assert.True(stoppedFirst > rollingFirst * 5.0, "the asked-for stop should start already turning");

        // The route is the same shape either way at the far end; what changes is where it starts to
        // bend, so the stopped leg keeps more of its length inside a tighter band.
        Assert.True(stopped.Samples.Count > 1 && rolling.Samples.Count > 1);
    }

    /// <summary>A standstill is intent, so it has to survive being saved and re-driven.</summary>
    [Fact]
    public void AnAskedForStandstillReplaysIdentically()
    {
        var (vehicle, mode) = Preset();
        var controls = new List<ManoeuvreControl>
        {
            new(new(20, 0, 0), TravelDirection.Forward),
            new(new(30, 10, 0), TravelDirection.Forward, FromStandstill: true),
            new(new(20, 20, 0), TravelDirection.Reverse)
        };
        var journey = new ManoeuvreDefinition(
            ManoeuvreDefinition.CurrentSchemaVersion, new Point3(0, 0, 0), 0.0, TravelDirection.Forward, controls);

        var first = ManoeuvreReplayService.Replay(vehicle, mode, journey);
        var second = ManoeuvreReplayService.Replay(vehicle, mode, journey);

        Assert.Equal(first.Samples, second.Samples);
        Assert.Equal(first.EndState, second.EndState);

        // And it is not the same route as the one without the stop, or the flag would be doing nothing.
        var withoutStop = ManoeuvreReplayService.Replay(vehicle, mode, journey with
        {
            Controls = controls.Select(control => control with { FromStandstill = false }).ToArray()
        });
        Assert.NotEqual(first.Samples.Count, withoutStop.Samples.Count);
    }
}
