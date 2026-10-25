using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class ManoeuvreControlEditsTests
{
    private static ManoeuvreDefinition Saved() => new(1, new(0, 0, 0), 0, TravelDirection.Forward,
        [new(new(0, 24, 0), TravelDirection.Forward), new(new(-35, 24, 0), TravelDirection.Forward),
         new(new(-40, 8, 0), TravelDirection.Reverse, ExitHeadingRadians: -Math.PI / 2),
         new(new(-40, -5, 0), TravelDirection.Reverse, ManoeuvreControlKind.Finish)]);

    private static Point3[] Points(ManoeuvreDefinition journey) =>
        new[] { journey.StartPositionMetres }.Concat(journey.Controls.Select(c => c.PositionMetres)).ToArray();

    [Fact]
    public void Inserting_between_controls_halves_the_leg_and_drives_it_the_same_way()
    {
        var saved = Saved();
        var edited = ManoeuvreControlEdits.Insert(saved, 3);

        Assert.Equal(5, edited.Controls.Count);
        Assert.Equal(new Point3(-40, 1.5, 0), edited.Controls[3].PositionMetres);
        Assert.Equal(TravelDirection.Reverse, edited.Controls[3].Direction);
        Assert.Equal(ManoeuvreControlKind.Aim, edited.Controls[3].Kind);
        Assert.Null(edited.Controls[3].ExitHeadingRadians);
        Assert.Equal(saved.Controls[2], edited.Controls[2]);
        Assert.Equal(saved.Controls[3], edited.Controls[4]);
    }

    [Fact]
    public void Inserting_before_the_first_control_splits_the_leg_from_the_start()
    {
        var edited = ManoeuvreControlEdits.Insert(Saved(), 0);

        Assert.Equal(new Point3(0, 12, 0), edited.Controls[0].PositionMetres);
        Assert.Equal(TravelDirection.Forward, edited.Controls[0].Direction);
        Assert.Equal(Saved().StartPositionMetres, edited.StartPositionMetres);
    }

    [Fact]
    public void Appending_after_a_finish_continues_the_last_leg_and_moves_the_finish_to_the_end()
    {
        var edited = ManoeuvreControlEdits.Insert(Saved(), 4);

        Assert.Equal(5, edited.Controls.Count);
        Assert.Equal(ManoeuvreControlKind.Aim, edited.Controls[3].Kind);
        Assert.Equal(ManoeuvreControlKind.Finish, edited.Controls[^1].Kind);
        Assert.Equal(TravelDirection.Reverse, edited.Controls[^1].Direction);
        var end = edited.Controls[^1].PositionMetres;
        Assert.Equal(-40, end.X, 9);
        Assert.Equal(-5 - ManoeuvreControlEdits.AppendedControlMetres, end.Y, 9);
        Assert.Single(edited.Controls, control => control.Kind == ManoeuvreControlKind.Finish);
    }

    [Fact]
    public void Deleting_the_finish_passes_it_to_the_new_last_aim_and_deleting_elsewhere_keeps_the_rest()
    {
        var saved = Saved();
        var withoutEnd = ManoeuvreControlEdits.Delete(saved, 3);
        Assert.Equal(3, withoutEnd.Controls.Count);
        Assert.Equal(ManoeuvreControlKind.Finish, withoutEnd.Controls[^1].Kind);
        Assert.Equal(saved.Controls[2].ExitHeadingRadians, withoutEnd.Controls[^1].ExitHeadingRadians);

        var withoutMiddle = ManoeuvreControlEdits.Delete(saved, 1);
        Assert.Equal(new[] { saved.Controls[0], saved.Controls[2], saved.Controls[3] }, withoutMiddle.Controls);
    }

    [Fact]
    public void A_turn_left_last_by_a_deleted_finish_stays_a_turn()
    {
        var journey = new ManoeuvreDefinition(1, new(0, 0, 0), 0, TravelDirection.Forward,
            [new(new(0, 10, 0), TravelDirection.Forward, ManoeuvreControlKind.Turn),
             new(new(5, 15, 0), TravelDirection.Forward, ManoeuvreControlKind.Finish)]);

        var edited = ManoeuvreControlEdits.Delete(journey, 1);

        Assert.Equal(ManoeuvreControlKind.Turn, Assert.Single(edited.Controls).Kind);
    }

    [Fact]
    public void The_last_control_cannot_be_deleted()
    {
        var journey = new ManoeuvreDefinition(1, new(0, 0, 0), 0, TravelDirection.Forward,
            [new(new(0, 10, 0), TravelDirection.Forward)]);

        Assert.Throws<InvalidOperationException>(() => ManoeuvreControlEdits.Delete(journey, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManoeuvreControlEdits.Insert(journey, 2));
    }

    [Fact]
    public void Edited_controls_reconcile_one_for_one_with_the_written_control_line()
    {
        // The palette writes the edited journey's positions as the polyline; replay, the preview and
        // Save all reconcile that polyline against the stored controls, which must change nothing.
        foreach (var edited in new[]
                 {
                     ManoeuvreControlEdits.Insert(Saved(), 2),
                     ManoeuvreControlEdits.Insert(Saved(), 4),
                     ManoeuvreControlEdits.Delete(Saved(), 0),
                     ManoeuvreControlEdits.Delete(Saved(), 3)
                 })
        {
            var vertices = Points(edited);
            Assert.True(JourneyEditPreview.PositionsMatch(edited, vertices));
            var reconciled = ManoeuvreControlReconciler.Reconcile(edited, vertices);
            Assert.Equal(edited.Controls, reconciled.Controls);
        }
    }

    [Fact]
    public void An_edited_journey_still_replays()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("REN");
        var mode = vehicle.DrivingModes["B"];
        var edited = ManoeuvreControlEdits.Delete(ManoeuvreControlEdits.Insert(Saved(), 1), 4);

        var replay = ManoeuvreReplayService.Replay(vehicle, mode, edited);

        Assert.NotEmpty(replay.Samples);
    }
}
