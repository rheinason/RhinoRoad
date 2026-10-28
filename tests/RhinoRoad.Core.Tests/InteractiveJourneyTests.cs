using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class InteractiveJourneyTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    private static (InteractiveJourney Journey, DrivingModeDefinition Mode) Start(string vehicleId = "PV")
    {
        var vehicle = Catalog.Get(vehicleId);
        var start = new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        return (new InteractiveJourney(vehicle, start), vehicle.DrivingModes["B"]);
    }

    private static ManoeuvreControl Drive(InteractiveJourney journey, DrivingModeDefinition mode, ManoeuvreControl control)
    {
        var leg = ManoeuvreReplayService.PlanControl(
            journey.Vehicle, mode, journey.Route, journey.LegStartIndex, journey.State, control);
        journey.Commit(leg, [control]);
        return control;
    }

    [Fact]
    public void Committing_a_finish_completes_the_journey_so_no_finish_is_left_mid_route()
    {
        var (journey, mode) = Start();
        Drive(journey, mode, new ManoeuvreControl(new Point3(12, 4, 0), TravelDirection.Forward));
        journey.ToggleFinish();
        Assert.True(journey.Finishing);
        Assert.False(journey.IsComplete);

        Drive(journey, mode, new ManoeuvreControl(new Point3(25, 10, 0), TravelDirection.Forward, journey.KindFor(locked: false)));

        Assert.True(journey.IsComplete);
        Assert.False(journey.Finishing);
        Assert.Equal(ManoeuvreControlKind.Finish, journey.Controls[^1].Kind);
        Assert.Equal(1, journey.Controls.Count(control => control.Kind == ManoeuvreControlKind.Finish));
    }

    [Fact]
    public void Undo_restores_the_route_controls_and_state_of_the_previous_commit()
    {
        var (journey, mode) = Start();
        Drive(journey, mode, new ManoeuvreControl(new Point3(12, 4, 0), TravelDirection.Forward));
        var route = journey.Route.ToArray();
        var state = journey.State;
        var legStart = journey.LegStartIndex;
        Drive(journey, mode, new ManoeuvreControl(new Point3(25, -6, 0), TravelDirection.Forward));
        Assert.Equal(2, journey.LegCount);

        Assert.True(journey.Undo());

        Assert.Equal(1, journey.LegCount);
        Assert.Equal(route, journey.Route);
        Assert.Equal(state, journey.State);
        Assert.Equal(legStart, journey.LegStartIndex);
        Assert.Single(journey.Controls);
        Assert.True(journey.Undo());
        Assert.False(journey.HasRoute);
        Assert.False(journey.Undo());
    }

    [Fact]
    public void Kept_journey_replays_to_the_route_that_was_driven()
    {
        var (journey, mode) = Start();
        Drive(journey, mode, new ManoeuvreControl(new Point3(12, 4, 0), TravelDirection.Forward));
        Drive(journey, mode, new ManoeuvreControl(new Point3(25, -6, 0), TravelDirection.Forward));
        journey.ToggleDirection();
        Drive(journey, mode, new ManoeuvreControl(new Point3(15, -2, 0), TravelDirection.Reverse));

        var replay = ManoeuvreReplayService.Replay(journey.Vehicle, mode, journey.Definition());

        Assert.Equal(journey.Route, replay.Samples);
        Assert.Equal(journey.State, replay.EndState);
    }

    [Fact]
    public void Turn_finish_and_plan_reverse_exclude_each_other()
    {
        var (journey, _) = Start("SVT");
        journey.ToggleFinish();
        journey.ToggleTurn();
        Assert.True(journey.LockedTurn);
        Assert.False(journey.Finishing);
        Assert.Equal(ManoeuvreControlKind.Turn, journey.KindFor(locked: journey.LockedTurn));

        journey.ToggleFinish();
        Assert.False(journey.LockedTurn);

        journey.TogglePlanReverse();
        Assert.True(journey.PlanReverseNext);
        Assert.False(journey.Finishing);
        Assert.True(journey.PickingBayRear);
    }

    [Fact]
    public void Changing_gear_seals_the_leg_and_drops_an_armed_plan_reverse()
    {
        var (journey, mode) = Start("SVT");
        Drive(journey, mode, new ManoeuvreControl(new Point3(15, 0, 0), TravelDirection.Forward));
        journey.TogglePlanReverse();

        journey.ToggleDirection();

        Assert.Equal(TravelDirection.Reverse, journey.State.Direction);
        Assert.Equal(journey.Route.Count - 1, journey.LegStartIndex);
        Assert.False(journey.PlanReverseNext);
    }

    /// <summary>A chain of three joints: nothing in the catalogue, and nothing that can be docked.</summary>
    private static VehicleDefinition ThreeJoints
    {
        get
        {
            var pvt = Catalog.Get("PVT");
            return pvt with
            {
                Id = "PVT3",
                TowedUnits = [.. pvt.TowedUnits, pvt.TowedUnits[^1] with { Id = "SECOND", HitchOffsetMetres = -1.15 }]
            };
        }
    }

    [Theory]
    [InlineData("PV")]
    [InlineData("REN")]
    [InlineData("LV12")]
    [InlineData("BUS12")]
    [InlineData("BUS13")]
    [InlineData("BUS15")]
    [InlineData("SVT")]
    [InlineData("PVT")]
    public void Plan_reverse_and_bay_docking_are_offered_to_every_preset_travelling_forward(string id)
    {
        var vehicle = Catalog.Get(id);
        var forward = new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);

        Assert.True(InteractiveCapabilities.PlanReverseAvailable(vehicle, forward));
        Assert.False(InteractiveCapabilities.PlanReverseAvailable(vehicle, forward with { Direction = TravelDirection.Reverse }));
        Assert.True(InteractiveCapabilities.DockingAvailable(vehicle));
        // A rigid reverse point is its own rear axle; only a combination's point moves to the trailer.
        Assert.Equal(vehicle.IsArticulated, InteractiveCapabilities.PointPlacesTrailer(vehicle, TravelDirection.Reverse));
        Assert.False(InteractiveCapabilities.PointPlacesTrailer(vehicle, TravelDirection.Forward));
    }

    [Fact]
    public void A_chain_that_cannot_be_docked_plans_to_its_trailer_axle_rather_than_by_a_bay_rear()
    {
        var vehicle = ThreeJoints;
        var journey = new InteractiveJourney(vehicle, new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0));

        journey.TogglePlanReverse();

        Assert.True(journey.PlanReverseNext);
        Assert.False(InteractiveCapabilities.DockingAvailable(vehicle));
        Assert.False(journey.PickingBayRear);
    }

    [Fact]
    public void A_rigid_vehicle_plans_a_reverse_into_a_bay_picked_by_its_rear()
    {
        var (journey, _) = Start("REN");
        journey.TogglePlanReverse();
        Assert.True(journey.PlanReverseNext);
        Assert.True(journey.PickingBayRear);
        journey.TogglePlanReverse();
        Assert.False(journey.PickingBayRear);

        // Reversing with Direction armed is a bay too, picked by the rear of the vehicle.
        journey.ToggleDirection();
        journey.TogglePointExit();
        Assert.True(journey.PickingBayRear);
    }

    /// <summary>
    /// The gate that says a control docks agrees with what replay actually does with it: a docked
    /// rigid reverse arrives on its point and square, and does not report the heading as unavailable.
    /// </summary>
    [Fact]
    public void A_control_said_to_dock_is_docked_by_replay()
    {
        var vehicle = Catalog.Get("REN");
        var mode = vehicle.DrivingModes["B"];
        var state = new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(state, vehicle) };
        var bay = new ManoeuvreControl(new Point3(-25, 0, 0), TravelDirection.Reverse, ManoeuvreControlKind.Aim, Math.PI);

        Assert.True(InteractiveCapabilities.Docks(vehicle, bay));
        Assert.True(InteractiveCapabilities.PlacesDockingBody(vehicle, bay));
        var leg = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, state, bay);
        Assert.False(leg.ExitHeadingUnavailable);
        Assert.False(leg.RequestedAngleExceeded);
        var miss = ReverseBay.Miss(vehicle, route.Concat(leg.Samples.Skip(1)).ToArray(), leg.EndState,
            bay.PositionMetres.XY, bay.ExitHeadingRadians);
        Assert.True(miss.PositionMetres <= ReversePlanner.ArrivalToleranceMetres);
        Assert.True(miss.HeadingRadians!.Value <= ReversePlanner.ArrivalToleranceDegrees * Math.PI / 180.0);
    }

    [Fact]
    public void Only_an_aimed_reverse_with_a_direction_docks_and_a_rigid_aim_without_one_steers()
    {
        var rigid = Catalog.Get("REN");
        var svt = Catalog.Get("SVT");
        var point = new Point3(-20, 0, 0);
        var aim = new ManoeuvreControl(point, TravelDirection.Reverse);
        var finish = new ManoeuvreControl(point, TravelDirection.Reverse, ManoeuvreControlKind.Finish, Math.PI);
        var forward = new ManoeuvreControl(point, TravelDirection.Forward, ExitHeadingRadians: Math.PI);

        Assert.False(InteractiveCapabilities.Docks(rigid, aim));
        Assert.False(InteractiveCapabilities.Docks(rigid, finish));
        Assert.False(InteractiveCapabilities.Docks(rigid, forward));
        Assert.False(InteractiveCapabilities.PlacesDockingBody(rigid, aim));
        // A combination's reverse places its trailer whether or not it has a direction.
        Assert.True(InteractiveCapabilities.PlacesDockingBody(svt, aim));
        Assert.False(InteractiveCapabilities.Docks(ThreeJoints, aim with { ExitHeadingRadians = Math.PI }));
        Assert.True(InteractiveCapabilities.PlacesDockingBody(ThreeJoints, aim with { ExitHeadingRadians = Math.PI }));
    }

    [Fact]
    public void Sealed_commit_does_not_let_the_next_leg_rewind_into_it()
    {
        var (journey, mode) = Start();
        var control = new ManoeuvreControl(new Point3(12, 4, 0), TravelDirection.Forward, SealsLeg: true);
        var leg = ManoeuvreReplayService.PlanControl(journey.Vehicle, mode, journey.Route, 0, journey.State, control);

        journey.Commit(leg, [control]);

        Assert.Equal(journey.Route.Count - 1, journey.LegStartIndex);
    }

    /// <summary>
    /// A planned reverse followed by another reverse, driven click by click and then replayed from
    /// the saved controls. Before the seal was saved on the control, the reverse Finish after the
    /// planned dock eased back into it on replay and ended 15 to 42 m from where it was driven.
    /// </summary>
    [Theory]
    [InlineData("SVT", "B")]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    [InlineData("PVT", "B")]
    [InlineData("BUS12", "A")]
    [InlineData("BUS12", "B")]
    public void A_journey_after_a_planned_reverse_replays_to_the_route_that_was_driven(string vehicleId, string modeId)
    {
        var vehicle = Catalog.Get(vehicleId);
        var mode = vehicle.DrivingModes[modeId];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var start = new VehicleState(new Point3(-4.0 * scale, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var journey = new InteractiveJourney(vehicle, start);
        Drive(journey, mode, new ManoeuvreControl(new Point3(1.5 * scale, 0.4 * scale, 0), TravelDirection.Forward));
        var bay = ReverseBay.AxleFromRear(vehicle, new Point3(-2.0 * scale, -0.6 * scale, 0), Math.PI);
        var proposal = ReversePlanner.Find(vehicle, mode, journey.Route, journey.LegStartIndex, journey.State, bay, Math.PI);
        Assert.True(proposal.Controls[^1].SealsLeg);
        journey.Commit(proposal.Leg, proposal.Controls);

        foreach (var kind in new[] { ManoeuvreControlKind.Finish, ManoeuvreControlKind.Aim })
        {
            var facing = journey.State.VehicleHeadingRadians + Math.PI;
            var point = journey.State.RearAxleCentreMetres.XY
                + (new Point2(Math.Cos(facing + 0.4), Math.Sin(facing + 0.4)) * (0.8 * scale));
            var next = new ManoeuvreControl(new Point3(point.X, point.Y, 0), TravelDirection.Reverse, kind,
                kind == ManoeuvreControlKind.Finish ? facing + 0.3 : null);
            var leg = ManoeuvreReplayService.PlanControl(
                vehicle, mode, journey.Route, journey.LegStartIndex, journey.State, next);
            var driven = journey.Route.Take(leg.FromIndex + 1).Concat(leg.Samples.Skip(1)).ToArray();
            var saved = journey.Definition() with { Controls = journey.Controls.Append(next).ToArray() };

            var replayed = ManoeuvreReplayService.Replay(vehicle, mode, saved);

            Assert.Equal(journey.Route.Count - 1, leg.FromIndex);
            Assert.Equal(driven, replayed.Samples);
            Assert.Equal(leg.EndState, replayed.EndState);
        }
    }
}
