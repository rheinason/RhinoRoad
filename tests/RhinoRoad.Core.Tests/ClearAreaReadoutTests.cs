using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class ClearAreaReadoutTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    private static Point2[] Yard =>
        [new(-100, -100), new(100, -100), new(100, 100), new(-100, 100)];

    private static Point3[] Points(ManoeuvreDefinition journey) =>
        new[] { journey.StartPositionMetres }.Concat(journey.Controls.Select(c => c.PositionMetres)).ToArray();

    private static ManoeuvreDefinition TrailerIntoBay(VehicleDefinition vehicle, DrivingModeDefinition mode)
    {
        var start = new VehicleState(new(0, 0, 0), 0, 0, TravelDirection.Forward, 0);
        var turn = new ManoeuvreControl(new(24, 7, 0), TravelDirection.Forward);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };
        var forward = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start, turn);
        route.AddRange(forward.Samples.Skip(1));
        var reverse = TrailerReversePlanner.Find(vehicle, mode, route, 0,
            forward.EndState, new Point3(5, -6, 0), Math.PI);
        return new ManoeuvreDefinition(ManoeuvreDefinition.CurrentSchemaVersion, start.RearAxleCentreMetres, 0,
            TravelDirection.Forward, new[] { turn }.Concat(reverse.Controls).ToArray());
    }

    [Fact]
    public void A_trailer_reversed_into_its_bay_is_measured_against_it()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];
        var journey = TrailerIntoBay(vehicle, mode);
        var preview = JourneyEditPreview.Build(vehicle, mode, journey, Points(journey), 0.3);

        var readout = ClearAreaReadout.Measure(vehicle, preview, Yard, []);

        Assert.NotNull(readout.Bay);
        Assert.True(double.IsFinite(readout.Bay!.PositionErrorMetres));
        Assert.True(double.IsFinite(readout.Bay.HeadingErrorDegrees));
        Assert.True(readout.ClearAreaSquareMetres > 0.0);
        Assert.False(readout.OutsideYard);
        Assert.False(readout.ObstacleConflict);
    }

    [Fact]
    public void A_rigid_vehicle_reversing_into_a_bay_is_measured_by_its_rear_axle_and_has_no_fold()
    {
        // The palette once synthesised an SVT trailer on any road and reported an infinite heading
        // error for a car. A rigid vehicle docks by its own rear axle, so that is what is measured.
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes.Values.First();
        var journey = new ManoeuvreDefinition(ManoeuvreDefinition.CurrentSchemaVersion, new(0, 0, 0), Math.PI / 2, TravelDirection.Forward,
            [new(new(0, 20, 0), TravelDirection.Forward),
             new(new(0, 10, 0), TravelDirection.Reverse, ExitHeadingRadians: -Math.PI / 2)]);
        var preview = JourneyEditPreview.Build(vehicle, mode, journey, Points(journey), 0.3);

        var readout = ClearAreaReadout.Measure(vehicle, preview, Yard, []);

        Assert.NotNull(readout.Bay);
        Assert.False(readout.FoldLimitReached);
        // One implementation: the readout's bay error is the planner's miss.
        var miss = ReverseBay.Miss(vehicle, preview.Journey.Samples, preview.Journey.EndState,
            new Point2(0, 10), -Math.PI / 2);
        Assert.Equal(miss.PositionMetres, readout.Bay!.PositionErrorMetres, 12);
        Assert.Equal(miss.HeadingRadians!.Value * 180.0 / Math.PI, readout.Bay.HeadingErrorDegrees, 12);
        Assert.True(readout.Bay.Arrived);
    }

    [Fact]
    public void A_trailer_bay_error_is_the_planners_miss()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];
        var journey = TrailerIntoBay(vehicle, mode);
        var replay = ManoeuvreReplayService.Replay(vehicle, mode, journey);
        var final = journey.Controls[^1];

        var arrival = BayArrival.Measure(vehicle, journey, replay)!;
        var miss = ReverseBay.Miss(vehicle, replay.Samples, replay.EndState, final.PositionMetres.XY,
            final.ExitHeadingRadians);

        Assert.Equal(miss.PositionMetres, arrival.PositionErrorMetres, 12);
        Assert.Equal(miss.HeadingRadians!.Value * 180.0 / Math.PI, arrival.HeadingErrorDegrees, 12);
    }

    [Fact]
    public void A_journey_ending_in_a_finish_or_without_a_direction_has_no_bay()
    {
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes.Values.First();
        ManoeuvreControl forward = new(new(0, 20, 0), TravelDirection.Forward);
        foreach (var final in new[]
        {
            new ManoeuvreControl(new(0, 10, 0), TravelDirection.Reverse),
            new ManoeuvreControl(new(0, 10, 0), TravelDirection.Reverse, ManoeuvreControlKind.Finish, -Math.PI / 2)
        })
        {
            var journey = new ManoeuvreDefinition(ManoeuvreDefinition.CurrentSchemaVersion, new(0, 0, 0), Math.PI / 2, TravelDirection.Forward, [forward, final]);
            Assert.Null(ReverseBay.FinalBay(journey));
            Assert.Null(BayArrival.Measure(vehicle, journey, ManoeuvreReplayService.Replay(vehicle, mode, journey)));
        }
    }

    [Fact]
    public void The_fold_limit_is_each_units_own()
    {
        // The readout once hard-coded 65 degrees; a unit given a tighter limit is judged by it.
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];
        var journey = TrailerIntoBay(vehicle, mode);
        var preview = JourneyEditPreview.Build(vehicle, mode, journey, Points(journey), 0.3);
        var fold = FoldLimit.Measure(vehicle, preview.Journey.Samples).MaximumRadians;
        Assert.True(fold > 2.0 * Math.PI / 180.0);
        var tight = vehicle with
        {
            TowedUnits = vehicle.TowedUnits
                .Select(unit => unit with { MaximumArticulationDegrees = fold * 180.0 / Math.PI / 2.0 }).ToArray()
        };

        Assert.False(ClearAreaReadout.Measure(vehicle, preview, Yard, []).FoldLimitReached);
        Assert.True(ClearAreaReadout.Measure(tight, preview, Yard, []).FoldLimitReached);
    }

    [Fact]
    public void A_trailer_whose_last_control_is_forward_has_no_bay()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];
        var journey = new ManoeuvreDefinition(ManoeuvreDefinition.CurrentSchemaVersion, new(0, 0, 0), 0, TravelDirection.Forward,
            [new(new(30, 0, 0), TravelDirection.Forward, ExitHeadingRadians: 0.0)]);
        var replay = ManoeuvreReplayService.Replay(vehicle, mode, journey);

        Assert.Null(BayArrival.Measure(vehicle, journey, replay));
    }

    [Fact]
    public void Obstacles_and_a_tight_yard_are_reported()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];
        var journey = TrailerIntoBay(vehicle, mode);
        var preview = JourneyEditPreview.Build(vehicle, mode, journey, Points(journey), 0.3);
        Point2[] tight = [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)];
        Point2[] obstacle = [new(20, 0), new(28, 0), new(28, 10), new(20, 10)];

        var readout = ClearAreaReadout.Measure(vehicle, preview, tight, [obstacle]);

        Assert.True(readout.OutsideYard);
        Assert.True(readout.ObstacleConflict);
        Assert.False(readout.Passes);
    }
}
