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
        return new ManoeuvreDefinition(1, start.RearAxleCentreMetres, 0,
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
    public void A_rigid_vehicle_has_no_bay_and_no_fold_even_when_it_ends_reversing_with_a_direction()
    {
        // The palette once synthesised an SVT trailer on any road and reported an infinite heading
        // error for a car; a rigid vehicle has nothing to measure against a bay.
        var vehicle = Catalog.Get("PV");
        var mode = vehicle.DrivingModes.Values.First();
        var journey = new ManoeuvreDefinition(1, new(0, 0, 0), Math.PI / 2, TravelDirection.Forward,
            [new(new(0, 20, 0), TravelDirection.Forward),
             new(new(0, 10, 0), TravelDirection.Reverse, ExitHeadingRadians: -Math.PI / 2)]);
        var preview = JourneyEditPreview.Build(vehicle, mode, journey, Points(journey), 0.3);

        var readout = ClearAreaReadout.Measure(vehicle, preview, Yard, []);

        Assert.Null(readout.Bay);
        Assert.False(readout.FoldLimitReached);
        Assert.Null(BayArrival.Measure(vehicle, journey, preview.Journey));
    }

    [Fact]
    public void A_trailer_whose_last_control_is_forward_has_no_bay()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];
        var journey = new ManoeuvreDefinition(1, new(0, 0, 0), 0, TravelDirection.Forward,
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
