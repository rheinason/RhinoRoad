using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class JourneyEditPreviewTests
{
    private static ManoeuvreDefinition Saved() => new(1, new(0, 0, 0), 0, TravelDirection.Forward,
        [new(new(0, 24, 0), TravelDirection.Forward), new(new(-35, 24, 0), TravelDirection.Forward),
         new(new(-40, 8, 0), TravelDirection.Reverse), new(new(-40, -5, 0), TravelDirection.Reverse, ManoeuvreControlKind.Finish)]);
    private static Point3[] Points(ManoeuvreDefinition saved) =>
        new[] { saved.StartPositionMetres }.Concat(saved.Controls.Select(c => c.PositionMetres)).ToArray();

    [Fact]
    public void Live_positions_match_update_replay_and_preserve_direction_finish_and_heading()
    {
        var saved = Saved();
        var vehicle = VehicleCatalog.LoadEmbedded().Get("REN");
        var mode = vehicle.DrivingModes["B"];
        var vertices = Points(saved);
        vertices[2] = new(-34.75, 24.15, 0);
        var preview = JourneyEditPreview.Build(vehicle, mode, saved, vertices, 0.3);
        var updated = ManoeuvreReplayService.Replay(vehicle, mode, ManoeuvreControlReconciler.Reconcile(saved, vertices));
        Assert.Equal(updated.Samples, preview.Journey.Samples);
        Assert.Equal(updated.EndState, preview.Journey.EndState);
        Assert.Equal(saved.StartHeadingRadians, preview.Definition.StartHeadingRadians);
        Assert.Equal(saved.Controls.Select(c => c.Direction), preview.Definition.Controls.Select(c => c.Direction));
        Assert.Equal(ManoeuvreControlKind.Finish, preview.Definition.Controls[^1].Kind);
        Assert.True(preview.Body.IsSuccess && preview.Clearance.IsSuccess);
        Assert.True(preview.UnchangedSampleCount < preview.PreviousRoute.Count);
        Assert.Equal(new Point3(-35, 24, 0), saved.Controls[1].PositionMetres);
    }

    [Fact]
    public void Cancelled_drag_returns_to_saved_route_without_modifying_saved_definition()
    {
        var saved = Saved();
        var vehicle = VehicleCatalog.LoadEmbedded().Get("REN");
        var mode = vehicle.DrivingModes["B"];
        var vertices = Points(saved);
        vertices[1] = new(0.2, 24.2, 0);
        var moved = JourneyEditPreview.Build(vehicle, mode, saved, vertices, 0.3);
        var cancelled = JourneyEditPreview.Build(vehicle, mode, saved, Points(saved), 0.3, moved.PreviousRoute);
        Assert.Equal(cancelled.PreviousRoute, cancelled.Journey.Samples);
        Assert.Equal(cancelled.PreviousRoute.Count, cancelled.UnchangedSampleCount);
        Assert.True(JourneyEditPreview.PositionsMatch(saved, Points(saved)));
    }

    [Fact]
    public void Unit_round_trip_noise_does_not_start_a_preview_but_a_small_real_move_does()
    {
        var saved = Saved();
        var vertices = Points(saved);
        vertices[1] = vertices[1] with { X = 1e-10 };
        Assert.True(JourneyEditPreview.PositionsMatch(saved, vertices));
        vertices[1] = vertices[1] with { X = 0.001 };
        Assert.False(JourneyEditPreview.PositionsMatch(saved, vertices));
        Assert.False(JourneyEditPreview.PositionsMatch(saved, []));
    }

    [Fact]
    public void Moving_start_and_inserting_control_uses_the_same_reconciliation_as_update()
    {
        var saved = Saved();
        var vehicle = VehicleCatalog.LoadEmbedded().Get("REN");
        var vertices = Points(saved).ToList();
        vertices[0] = new(1, 0, 0);
        vertices.Insert(2, new(-15, 25, 0));
        var preview = JourneyEditPreview.Build(vehicle, vehicle.DrivingModes["B"], saved, vertices, 0.3);
        var reconciled = ManoeuvreControlReconciler.Reconcile(saved, vertices);
        Assert.Equal(reconciled.Controls, preview.Definition.Controls);
        Assert.Equal(reconciled.StartPositionMetres, preview.Definition.StartPositionMetres);
    }
}
