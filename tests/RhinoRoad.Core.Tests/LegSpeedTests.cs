using System.Text.Json.Nodes;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// A leg driven slower than its mode: the same lock, less distance to move the wheel, and every
/// part of the program -- generators, docking, the planner, replay, the analyzer -- agreeing on it.
/// </summary>
public sealed class LegSpeedTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    private static ManoeuvreDefinition Journey(VehicleState start, params ManoeuvreControl[] controls) =>
        new(ManoeuvreDefinition.CurrentSchemaVersion, start.RearAxleCentreMetres, start.VehicleHeadingRadians,
            start.Direction, controls);

    [Fact]
    public void A_leg_speed_keeps_the_lock_and_never_exceeds_the_mode()
    {
        var mode = Catalog.Get("PV").DrivingModes["A"];

        Assert.Same(mode, mode.AtSpeed(null));
        Assert.Same(mode, mode.AtSpeed(15.0));
        var crawl = mode.AtSpeed(3.0);
        Assert.Equal(3.0, crawl.SpeedKilometresPerHour);
        Assert.Equal(mode.MaximumWheelAngleDegrees, crawl.MaximumWheelAngleDegrees);
        Assert.Equal(mode.MaximumSteeringRateRadiansPerSecond, crawl.MaximumSteeringRateRadiansPerSecond);
        Assert.Equal(mode.Id, crawl.Id);

        // Faster than the mode would understate the swept path, so it is driven at the mode's speed.
        Assert.Equal(15.0, mode.LegSpeedKilometresPerHour(40.0));
        Assert.Same(mode, mode.AtSpeed(40.0));

        Assert.Equal(12.5, mode.CentreToLockMetres(), 9);
        Assert.Equal(12.5 * 5.0 / 15.0, mode.CentreToLockMetres(5.0), 9);
        foreach (var bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => mode.LegSpeedKilometresPerHour(bad));
    }

    /// <summary>
    /// The wheel reaches its lock in a distance proportional to the leg's speed: 12.5 m at mode A's
    /// 15 km/h, a third of that at 5 km/h and a fifth at 3 km/h, with the lock the same in all three.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(5.0)]
    [InlineData(3.0)]
    public void A_slower_leg_reaches_lock_in_proportionally_less_distance(double? speed)
    {
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes["A"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };
        var turn = new ManoeuvreControl(LockedTurnGenerator.CursorForSweep(start, 5.0, 20.0), TravelDirection.Forward,
            ManoeuvreControlKind.Turn, SpeedKilometresPerHour: speed);

        var leg = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start, turn);

        var lockCurvature = Math.Tan(mode.MaximumWheelAngleRadians) / vehicle.WheelbaseMetres;
        var atLock = leg.Samples.First(sample => Math.Abs(sample.SignedCurvaturePerMetre) >= lockCurvature - 1e-9);
        var expected = 12.5 * (speed ?? 15.0) / 15.0;
        Assert.InRange(atLock.StationMetres, expected - 0.1 - 1e-9, expected + 0.1 + 1e-9);
        Assert.Equal(lockCurvature, leg.Samples.Max(sample => Math.Abs(sample.SignedCurvaturePerMetre)), 9);

        // Each sample after the first carries the speed it was driven at; a leg at the mode's speed
        // carries none, so every route saved before speeds existed replays to the same samples.
        Assert.Null(leg.Samples[0].SpeedKilometresPerHour);
        Assert.All(leg.Samples.Skip(1), sample => Assert.Equal(speed, sample.SpeedKilometresPerHour));
    }

    [Fact]
    public void A_speed_above_the_mode_drives_exactly_as_the_mode_and_records_the_mode_speed()
    {
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };
        var aim = new ManoeuvreControl(new Point3(8, 6, 0), TravelDirection.Forward);

        var atMode = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start, aim);
        var tooFast = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start,
            aim with { SpeedKilometresPerHour = 50.0 });

        Assert.Equal(atMode.EndState, tooFast.EndState);
        Assert.Equal(atMode.Samples.Select(sample => sample.PositionMetres), tooFast.Samples.Select(sample => sample.PositionMetres));
        Assert.All(tooFast.Samples.Skip(1), sample => Assert.Equal(mode.SpeedKilometresPerHour, sample.SpeedKilometresPerHour));
    }

    /// <summary>
    /// A journey whose legs run at different speeds -- including a reverse docked at walking pace --
    /// replays sample for sample to the route it was driven as, and twice to the same route.
    /// </summary>
    [Theory]
    [InlineData("SVT", "A")]
    [InlineData("SVT", "B")]
    [InlineData("LV12", "A")]
    [InlineData("PVT", "B")]
    public void A_journey_with_leg_speeds_replays_exactly(string id, string modeId)
    {
        var vehicle = Catalog.Get(id);
        var mode = vehicle.DrivingModes[modeId];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var start = new VehicleState(new(-4.0 * scale, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var bay = ReverseBay.AxleFromRear(vehicle, new Point3(-6.0 * scale, -1.0 * scale, 0), Math.PI);
        ManoeuvreControl[] controls =
        [
            new(new Point3(-1.0 * scale, 0.1 * scale, 0), TravelDirection.Forward),
            new(new Point3(1.5 * scale, 0.4 * scale, 0), TravelDirection.Forward, SpeedKilometresPerHour: 3.0),
            new(bay, TravelDirection.Reverse, ManoeuvreControlKind.Aim, Math.PI, SpeedKilometresPerHour: 1.0)
        ];

        var journey = new InteractiveJourney(vehicle, start);
        foreach (var control in controls)
        {
            if (journey.State.Direction != control.Direction) journey.ToggleDirection();
            var leg = ManoeuvreReplayService.PlanControl(vehicle, mode, journey.Route, journey.LegStartIndex,
                journey.State, control);
            journey.Commit(leg, [control]);
        }

        var first = ManoeuvreReplayService.Replay(vehicle, mode, journey.Definition());
        var second = ManoeuvreReplayService.Replay(vehicle, mode, journey.Definition());

        Assert.Equal(journey.Route, first.Samples);
        Assert.Equal(journey.State, first.EndState);
        Assert.Equal(first.Samples, second.Samples);
        Assert.Contains(first.Samples, sample => sample.SpeedKilometresPerHour == 1.0);
        Assert.Contains(first.Samples, sample => sample.SpeedKilometresPerHour == 3.0);
        var miss = ReverseBay.Miss(vehicle, first.Samples, first.EndState, bay.XY, Math.PI);
        Assert.True(miss.PositionMetres <= ReversePlanner.ArrivalToleranceMetres, $"{id} {modeId}: miss {miss.PositionMetres:0.000} m");
        Assert.True(miss.HeadingRadians <= ReversePlanner.ArrivalToleranceDegrees * Math.PI / 180.0);
    }

    /// <summary>
    /// The steering-rate check prices each interval at the speed it was driven at. A leg driven at
    /// 3 km/h moves the wheel five times as far per metre as mode A allows at 15 km/h: within the
    /// rate at 3 km/h, five times over it if those samples are read at the mode's speed.
    /// </summary>
    [Fact]
    public void The_steering_rate_check_uses_each_samples_speed()
    {
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes["A"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };
        var turn = new ManoeuvreControl(LockedTurnGenerator.CursorForSweep(start, 3.0, 20.0), TravelDirection.Forward,
            ManoeuvreControlKind.Turn, SpeedKilometresPerHour: 3.0);
        var slow = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start, turn).Samples;
        var analyzer = new VehicleAccessAnalyzer();

        var honest = analyzer.Analyze(vehicle, mode, slow);
        var readAtModeSpeed = analyzer.Analyze(vehicle, mode,
            slow.Select(sample => sample with { SpeedKilometresPerHour = null }).ToArray());

        Assert.DoesNotContain(honest.Violations, violation => violation.Kind == ViolationKind.SteeringRate);
        Assert.InRange(honest.MaximumSteeringRateRadiansPerSecond, 0.9 * mode.MaximumSteeringRateRadiansPerSecond,
            mode.MaximumSteeringRateRadiansPerSecond + 1e-8);
        Assert.Contains(readAtModeSpeed.Violations, violation => violation.Kind == ViolationKind.SteeringRate);
        Assert.Equal(5.0 * honest.MaximumSteeringRateRadiansPerSecond, readAtModeSpeed.MaximumSteeringRateRadiansPerSecond, 6);

        // The review's per-sample rate follows the same speed.
        var samples = VehicleAccessReviewAnalyzer.MetricSamples(slow, honest);
        Assert.All(samples, sample => Assert.True(
            sample.SteeringRateRadiansPerSecond <= mode.MaximumSteeringRateRadiansPerSecond + 1e-8));
    }

    [Fact]
    public void Alignment_transitions_are_priced_at_the_stretchs_speed()
    {
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes["A"];
        // Straight, a 10 m stretch at a 12 m radius, straight again: an alignment drawn rather than
        // driven, so its curvature steps and every transition it needs is priced by the formula.
        var drawn = Enumerable.Range(0, 31).Select(station => new RouteSample(
            station, new Point3(station, 0, 0), 0.0, station is > 10 and <= 20 ? 1.0 / 12.0 : 0.0,
            TravelDirection.Forward)).ToArray();
        var crawled = drawn.Select(sample => sample with { SpeedKilometresPerHour = 5.0 }).ToArray();

        var bend = AlignmentTransitions.Runs(vehicle, mode, drawn).Single(run => run.CurvaturePerMetre > 0.0);
        var slowBend = AlignmentTransitions.Runs(vehicle, mode, crawled).Single(run => run.CurvaturePerMetre > 0.0);

        Assert.Equal(2.0 * AlignmentTransitions.RequiredLengthMetres(vehicle, mode, 0.0, 1.0 / 12.0),
            bend.RequiredTransitionMetres, 9);
        Assert.Equal(bend.RequiredTransitionMetres / 3.0, slowBend.RequiredTransitionMetres, 9);
    }

    [Fact]
    public void The_planner_puts_its_speed_on_every_control_and_replays_to_its_leg()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["A"];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var start = new VehicleState(new(-2.0 * scale, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };
        var bay = ReverseBay.AxleFromRear(vehicle, new Point3(-2.0 * scale, -0.6 * scale, 0), Math.PI);

        var proposal = ReversePlanner.Find(vehicle, mode, route, 0, start, bay, Math.PI, 5.0);

        Assert.True(proposal.ReachedTarget, $"miss {proposal.MissMetres:0.000} m");
        Assert.All(proposal.Controls, control => Assert.Equal(5.0, control.SpeedKilometresPerHour));
        var replay = ManoeuvreReplayService.Replay(vehicle, mode, Journey(start, [.. proposal.Controls]));
        Assert.Equal(route.Take(proposal.Leg.FromIndex + 1).Concat(proposal.Leg.Samples.Skip(1)), replay.Samples);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReversePlanner.Find(vehicle, mode, route, 0, start, bay, Math.PI, 0.0));
    }

    [Fact]
    public void A_saved_speed_round_trips_and_a_nonsense_speed_is_refused()
    {
        var definition = SchemaMigrationTests.FixtureDefinition();
        var controls = definition.Manoeuvre!.Controls.ToArray();
        controls[2] = controls[2] with { SpeedKilometresPerHour = 3.0 };
        var withSpeed = definition with { Manoeuvre = definition.Manoeuvre with { Controls = controls } };

        var loaded = AccessDefinitionSerializer.Deserialize(AccessDefinitionSerializer.Serialize(withSpeed));
        Assert.Equal(3.0, loaded.Manoeuvre!.Controls[2].SpeedKilometresPerHour);
        Assert.Null(loaded.Manoeuvre.Controls[0].SpeedKilometresPerHour);

        var root = JsonNode.Parse(AccessDefinitionSerializer.Serialize(withSpeed))!.AsObject();
        root["manoeuvre"]!["controls"]![2]!["speedKilometresPerHour"] = -2.0;
        Assert.False(AccessDefinitionSerializer.TryDeserialize(root.ToJsonString(), out _, out var error));
        Assert.Contains("speed", error);
    }

    [Fact]
    public void An_inserted_control_is_driven_at_the_speed_of_the_leg_it_splits()
    {
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var journey = Journey(start,
            new ManoeuvreControl(new Point3(10, 0, 0), TravelDirection.Forward),
            new ManoeuvreControl(new Point3(20, 0, 0), TravelDirection.Reverse, SpeedKilometresPerHour: 3.0));

        var split = ManoeuvreControlEdits.Insert(journey, 1);
        var appended = ManoeuvreControlEdits.Insert(journey, 2);

        Assert.Equal(3.0, split.Controls[1].SpeedKilometresPerHour);
        Assert.Null(split.Controls[0].SpeedKilometresPerHour);
        Assert.Equal(3.0, appended.Controls[2].SpeedKilometresPerHour);
    }

    [Fact]
    public void The_offered_speeds_are_the_modes_and_the_crawls_below_it()
    {
        var vehicle = Catalog.Get("PV");
        var modeA = vehicle.DrivingModes["A"];
        var modeB = vehicle.DrivingModes["B"];

        Assert.Equal([null, 5.0, 3.0, 1.0], ManoeuvreControlEdits.SpeedChoices(modeA, null));
        Assert.Equal([null, 3.0, 1.0], ManoeuvreControlEdits.SpeedChoices(modeB, null));
        // A control's own speed is shown rather than replaced, whatever it is.
        Assert.Equal([null, 5.0, 3.0, 2.5, 1.0], ManoeuvreControlEdits.SpeedChoices(modeA, 2.5));
        Assert.Equal([null, 10.0, 3.0, 1.0], ManoeuvreControlEdits.SpeedChoices(modeB, 10.0));
    }

    [Fact]
    public void The_pick_loops_speed_is_latched_and_the_mode_speed_is_kept_as_none()
    {
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes["A"];
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var journey = new InteractiveJourney(vehicle, start);

        Assert.Null(journey.SpeedKilometresPerHour);
        Assert.Equal(3.0, journey.SetSpeed(mode, 3.0));
        Assert.Equal(3.0, journey.SpeedKilometresPerHour);

        var control = new ManoeuvreControl(new Point3(8, 2, 0), TravelDirection.Forward,
            SpeedKilometresPerHour: journey.SpeedKilometresPerHour);
        journey.Commit(ManoeuvreReplayService.PlanControl(vehicle, mode, journey.Route, journey.LegStartIndex,
            journey.State, control), [control]);
        Assert.True(journey.Undo());
        Assert.Equal(3.0, journey.SpeedKilometresPerHour);

        Assert.Equal(15.0, journey.SetSpeed(mode, 40.0));
        Assert.Null(journey.SpeedKilometresPerHour);
        Assert.Equal(15.0, journey.SetSpeed(mode, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => journey.SetSpeed(mode, 0.0));
    }
}
