using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the driving model behind interactive route authoring.
/// </summary>
/// <remarks>
/// The properties worth defending are precision and honesty: a waypoint is reached to within
/// millimetres or the segment says it was not reached, the mode's limits are never exceeded on the
/// way, and replaying stored waypoints reproduces the recorded route exactly rather than closely.
/// </remarks>
public sealed class PursuitDriverTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(
        string id = "PV",
        string mode = "A")
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    private static VehicleState Start(TravelDirection direction = TravelDirection.Forward) =>
        new(new Point3(0, 0, 0), 0.0, 0.0, direction, 0.0);

    [Theory]
    [InlineData(20.0, 0.0)]
    [InlineData(20.0, 10.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(-10.0, 0.0)]
    [InlineData(200.0, 50.0)]
    public void ADrivenSegmentEndsOnItsTarget(double x, double y)
    {
        var (vehicle, mode) = Preset();
        var target = new Point2(x, y);

        var segment = PursuitDriver.Drive(vehicle, mode, Start(), target, TravelDirection.Forward);

        Assert.Equal(SegmentOutcome.Arrived, segment.Outcome);
        Assert.True(
            segment.EndState.RearAxleCentreMetres.XY.DistanceTo(target) <= PursuitDriver.ArrivalToleranceMetres,
            $"ended {segment.EndState.RearAxleCentreMetres.XY.DistanceTo(target):0.0000} m from the target");
    }

    /// <summary>
    /// A target dead astern lies on the vehicle's own axis, where the steering solve degenerates to
    /// "drive straight" — away from it, forever. The driver has to break that symmetry itself.
    /// </summary>
    [Fact]
    public void ATargetDirectlyBehindIsReachedByTurningRatherThanDrivingAway()
    {
        var (vehicle, mode) = Preset();
        var target = new Point2(-10.0, 0.0);

        var segment = PursuitDriver.Drive(vehicle, mode, Start(), target, TravelDirection.Forward);

        Assert.Equal(SegmentOutcome.Arrived, segment.Outcome);
        Assert.True(segment.EndState.StationMetres < 100.0, $"took {segment.EndState.StationMetres:0} m to turn around");
    }

    [Fact]
    public void AnUnreachableTargetIsReportedRatherThanMissedQuietly()
    {
        var (vehicle, mode) = Preset();
        // Inside the swept region the vehicle would need to turn through to get there.
        var target = new Point2(8.0, 8.0);

        var segment = PursuitDriver.Drive(vehicle, mode, Start(), target, TravelDirection.Forward);

        Assert.NotEqual(SegmentOutcome.Arrived, segment.Outcome);
    }

    [Theory]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    [InlineData("REN", "A")]
    [InlineData("BUS12", "B")]
    public void TheModeLimitsAreNeverExceeded(string vehicleId, string modeId)
    {
        var (vehicle, mode) = Preset(vehicleId, modeId);
        var segment = PursuitDriver.Drive(vehicle, mode, Start(), new Point2(12.0, 9.0), TravelDirection.Forward);
        var secondsPerStep = PursuitDriver.StepMetres / mode.SpeedMetresPerSecond;
        var previous = 0.0;

        foreach (var sample in segment.Samples)
        {
            var steering = Math.Atan(
                sample.SignedCurvaturePerMetre * vehicle.WheelbaseMetres * (double)sample.Direction);
            Assert.True(
                Math.Abs(steering) <= mode.MaximumWheelAngleRadians + 1e-9,
                $"wheel reached {steering * 180.0 / Math.PI:0.00}deg of {mode.MaximumWheelAngleDegrees:0.0}");
            Assert.True(
                Math.Abs(steering - previous) / secondsPerStep <= mode.MaximumSteeringRateRadiansPerSecond + 1e-9,
                "steering rate exceeded the mode limit");
            previous = steering;
        }
    }

    [Fact]
    public void ReversingDrivesBackwardsTowardsTheTarget()
    {
        var (vehicle, mode) = Preset();
        var target = new Point2(-15.0, -4.0);

        var segment = PursuitDriver.Drive(vehicle, mode, Start(), target, TravelDirection.Reverse);

        Assert.Equal(SegmentOutcome.Arrived, segment.Outcome);
        Assert.All(segment.Samples, sample => Assert.Equal(TravelDirection.Reverse, sample.Direction));
        Assert.True(segment.EndState.RearAxleCentreMetres.XY.DistanceTo(target) <= PursuitDriver.ArrivalToleranceMetres);
    }

    /// <summary>
    /// The property the whole editing story rests on: a stored manoeuvre replays into the route it
    /// was recorded from, sample for sample, so moving a waypoint cannot change anything else.
    /// </summary>
    [Fact]
    public void ReplayingAManoeuvreReproducesTheRecordedRouteExactly()
    {
        var (vehicle, mode) = Preset();
        var recorder = new RouteRecorder(vehicle, mode, new Point3(0, 0, 0), 0.0);
        recorder.Commit(new Point2(20.0, 6.0));
        recorder.Commit(new Point2(40.0, 20.0));
        recorder.Direction = TravelDirection.Reverse;
        recorder.Commit(new Point2(30.0, 14.0));

        var replay = PursuitDriver.Replay(vehicle, mode, recorder.ToManoeuvre());

        Assert.Equal(recorder.Samples.Count, replay.Samples.Count);
        Assert.Equal(recorder.Samples, replay.Samples);
    }

    [Fact]
    public void AManoeuvreCarriesTheDirectionEachWaypointWasDrivenIn()
    {
        var (vehicle, mode) = Preset();
        var recorder = new RouteRecorder(vehicle, mode, new Point3(0, 0, 0), 0.0);
        recorder.Commit(new Point2(20.0, 0.0));
        recorder.Direction = TravelDirection.Reverse;
        recorder.Commit(new Point2(10.0, 0.0));

        var manoeuvre = recorder.ToManoeuvre();

        Assert.Equal(TravelDirection.Forward, manoeuvre.Waypoints[0].Direction);
        Assert.Equal(TravelDirection.Reverse, manoeuvre.Waypoints[1].Direction);
    }

    [Fact]
    public void UndoRestoresTheRouteAndTheStateExactly()
    {
        var (vehicle, mode) = Preset();
        var recorder = new RouteRecorder(vehicle, mode, new Point3(0, 0, 0), 0.0);
        recorder.Commit(new Point2(20.0, 5.0));
        var sampleCount = recorder.Samples.Count;
        var state = recorder.State;
        recorder.Direction = TravelDirection.Reverse;
        recorder.Commit(new Point2(10.0, 2.0));

        Assert.True(recorder.Undo());

        Assert.Equal(sampleCount, recorder.Samples.Count);
        Assert.Equal(state, recorder.State);
        Assert.Equal(TravelDirection.Forward, recorder.Direction);
        Assert.Single(recorder.Waypoints);
    }

    [Fact]
    public void ThereIsNothingToUndoOnAFreshRecorder()
    {
        var (vehicle, mode) = Preset();
        var recorder = new RouteRecorder(vehicle, mode, new Point3(0, 0, 0), 0.0);

        Assert.False(recorder.CanUndo);
        Assert.False(recorder.Undo());
    }

    /// <summary>A click on the vehicle itself must not push an empty waypoint onto the route.</summary>
    [Fact]
    public void ATargetAlreadyUnderTheAxleCommitsNothing()
    {
        var (vehicle, mode) = Preset();
        var recorder = new RouteRecorder(vehicle, mode, new Point3(0, 0, 0), 0.0);

        recorder.Commit(new Point2(0.0, 0.0));

        Assert.Empty(recorder.Waypoints);
        Assert.False(recorder.CanUndo);
    }

    [Fact]
    public void SamplesAreSpacedAtTheStepAndStationsIncreaseMonotonically()
    {
        var (vehicle, mode) = Preset();
        var segment = PursuitDriver.Drive(vehicle, mode, Start(), new Point2(30.0, 8.0), TravelDirection.Forward);
        var previous = 0.0;

        foreach (var sample in segment.Samples)
        {
            Assert.True(sample.StationMetres > previous);
            Assert.True(sample.StationMetres - previous <= PursuitDriver.StepMetres + 1e-12);
            previous = sample.StationMetres;
        }
    }
}
