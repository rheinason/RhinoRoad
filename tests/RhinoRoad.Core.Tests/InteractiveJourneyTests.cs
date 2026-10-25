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
        Assert.True(journey.PickingTrailerRear);
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

    [Fact]
    public void Plan_reverse_is_offered_to_a_single_trailer_travelling_forward_only()
    {
        var forward = new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        Assert.True(InteractiveCapabilities.PlanReverseAvailable(Catalog.Get("SVT"), forward));
        Assert.False(InteractiveCapabilities.PlanReverseAvailable(Catalog.Get("SVT"), forward with { Direction = TravelDirection.Reverse }));
        Assert.False(InteractiveCapabilities.PlanReverseAvailable(Catalog.Get("PV"), forward));
        Assert.False(InteractiveCapabilities.PlanReverseAvailable(Catalog.Get("PVT"), forward));
        Assert.True(InteractiveCapabilities.DockingAvailable(Catalog.Get("PVT")));
        Assert.False(InteractiveCapabilities.DockingAvailable(Catalog.Get("REN")));
    }

    [Fact]
    public void Rigid_vehicle_arming_plan_reverse_has_no_effect()
    {
        var (journey, _) = Start("PV");
        journey.TogglePlanReverse();
        Assert.False(journey.PlanReverseNext);
        Assert.False(journey.PickingTrailerRear);
    }

    [Fact]
    public void Sealed_commit_does_not_let_the_next_leg_rewind_into_it()
    {
        var (journey, mode) = Start();
        var control = new ManoeuvreControl(new Point3(12, 4, 0), TravelDirection.Forward);
        var leg = ManoeuvreReplayService.PlanControl(journey.Vehicle, mode, journey.Route, 0, journey.State, control);

        journey.Commit(leg, [control], sealLeg: true);

        Assert.Equal(journey.Route.Count - 1, journey.LegStartIndex);
    }
}
