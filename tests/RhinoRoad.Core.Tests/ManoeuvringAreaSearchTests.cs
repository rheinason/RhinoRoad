using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class ManoeuvringAreaSearchTests
{
    private static readonly VehicleDefinition Vehicle = VehicleCatalog.LoadEmbedded().Get("SVT");
    private static readonly DrivingModeDefinition Mode = Vehicle.DrivingModes["B"];

    private static ManoeuvreDefinition Journey
    {
        get
        {
            var start = new VehicleState(new(0, 0, 0), 0, 0, TravelDirection.Forward, 0);
            var turn = new ManoeuvreControl(new(24, 7, 0), TravelDirection.Forward);
            var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, Vehicle) };
            var forward = ManoeuvreReplayService.PlanControl(Vehicle, Mode, route, 0, start, turn);
            route.AddRange(forward.Samples.Skip(1));
            var reverse = TrailerReversePlanner.Find(Vehicle, Mode, route, 0,
                forward.EndState, new Point3(5, -6, 0), Math.PI);
            return new ManoeuvreDefinition(1, start.RearAxleCentreMetres, 0,
                TravelDirection.Forward, new[] { turn }.Concat(reverse.Controls).ToArray());
        }
    }

    private static Point2[] Yard =>
        [new(-100, -100), new(100, -100), new(100, 100), new(-100, 100)];

    [Fact]
    public void SuggestionsReachTheFixedBayAndReplayAsOrdinaryControls()
    {
        var result = ManoeuvringAreaSearch.Find(Vehicle, Mode, Journey, 0, Yard, [],
            0.30, TimeSpan.FromSeconds(3));

        Assert.NotEmpty(result.Suggestions);
        Assert.True(result.Suggestions.Count <= 3);
        foreach (var candidate in result.Suggestions)
        {
            Assert.True(candidate.PositionErrorMetres <= 0.25);
            Assert.True(candidate.HeadingErrorDegrees <= 2.0);
            Assert.True(candidate.ClearAreaSquareMetres > 0.0);
            var replay = ManoeuvreReplayService.Replay(Vehicle, Mode, candidate.Manoeuvre);
            Assert.Equal(candidate.Route, replay.Samples);
            Assert.Equal(Journey.Controls[^1].PositionMetres, candidate.Manoeuvre.Controls[^1].PositionMetres);
            Assert.Equal(Journey.Controls[^1].ExitHeadingRadians,
                candidate.Manoeuvre.Controls[^1].ExitHeadingRadians);
        }
    }

    [Fact]
    public void APassingCurrentJourneyIsNeverReplacedByOneWorseInArea()
    {
        var initial = ManoeuvringAreaSearch.Find(Vehicle, Mode, Journey, 0, Yard, [],
            0.30, TimeSpan.FromSeconds(3));
        var saved = initial.Suggestions[0].Manoeuvre;

        var result = ManoeuvringAreaSearch.Find(Vehicle, Mode, saved, 0, Yard, [],
            0.30, TimeSpan.FromSeconds(2));

        Assert.NotNull(result.Current);
        Assert.NotEmpty(result.Suggestions);
        Assert.True(result.Suggestions[0].ClearAreaSquareMetres
            <= result.Current.ClearAreaSquareMetres + 1e-6);
    }

    [Fact]
    public void FixedObstacleCanRuleOutEveryCandidate()
    {
        var obstacle = new Point2[]
        {
            new(-100, -100), new(100, -100), new(100, 100), new(-100, 100)
        };
        var result = ManoeuvringAreaSearch.Find(Vehicle, Mode, Journey, 0, Yard,
            [obstacle], 0.30, TimeSpan.FromSeconds(3));

        Assert.Empty(result.Suggestions);
        Assert.True(result.ObstacleConflicts > 0);
    }

    [Fact]
    public void YardBoundaryCanRuleOutEveryCandidate()
    {
        var tinyYard = new Point2[]
        {
            new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)
        };
        var result = ManoeuvringAreaSearch.Find(Vehicle, Mode, Journey, 0,
            tinyYard, [], 0.30, TimeSpan.FromSeconds(3));

        Assert.Empty(result.Suggestions);
        Assert.True(result.OutsideYard > 0);
    }

    /// <summary>
    /// A rigid vehicle is searched the same way: a car turning away from a perpendicular bay and
    /// backing in, moved around a yard for the least clear area, every suggestion still arriving in
    /// the fixed bay and replaying as ordinary controls.
    /// </summary>
    [Fact]
    public void ARigidVehicleIsSearchedLikeACombination()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
        var mode = vehicle.DrivingModes["B"];
        var start = new VehicleState(new(-15, 0, 0), 0, 0, TravelDirection.Forward, 0);
        var turn = new ManoeuvreControl(new(5.5, 1.5, 0), TravelDirection.Forward);
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(start, vehicle) };
        var forward = ManoeuvreReplayService.PlanControl(vehicle, mode, route, 0, start, turn);
        route.AddRange(forward.Samples.Skip(1));
        var bay = ReverseBay.AxleFromRear(vehicle, new Point3(0, -8.8, 0), -Math.PI / 2.0);
        var reverse = ReversePlanner.Find(vehicle, mode, route, 0, forward.EndState, bay, -Math.PI / 2.0);
        Assert.True(reverse.ReachedTarget);
        var journey = new ManoeuvreDefinition(1, start.RearAxleCentreMetres, 0,
            TravelDirection.Forward, new[] { turn }.Concat(reverse.Controls).ToArray());
        Point2[] yard = [new(-30, -15), new(30, -15), new(30, 15), new(-30, 15)];

        var result = ManoeuvringAreaSearch.Find(vehicle, mode, journey, 0, yard, [],
            0.30, TimeSpan.FromSeconds(3));

        Assert.NotNull(result.Current);
        Assert.NotEmpty(result.Suggestions);
        Assert.True(result.Suggestions[0].ClearAreaSquareMetres <= result.Current.ClearAreaSquareMetres + 1e-6);
        foreach (var candidate in result.Suggestions)
        {
            Assert.True(candidate.PositionErrorMetres <= ReversePlanner.ArrivalToleranceMetres);
            Assert.True(candidate.HeadingErrorDegrees <= ReversePlanner.ArrivalToleranceDegrees);
            Assert.Equal(candidate.Route, ManoeuvreReplayService.Replay(vehicle, mode, candidate.Manoeuvre).Samples);
            Assert.Equal(bay, candidate.Manoeuvre.Controls[^1].PositionMetres);
        }
    }

    [Fact]
    public void AreaMeasureClipsTheFootprintToTheSelectedYard()
    {
        var region = new SweptRegion(
            [new(0, 0), new(10, 0), new(10, 10), new(0, 10)], []);
        var yard = new Point2[] { new(5, 0), new(15, 0), new(15, 10), new(5, 10) };

        Assert.Equal(50.0, AccessFootprint.IntersectionArea(region, yard), 6);
        var withHole = region with
        {
            Holes = [new Point2[] { new(6, 4), new(8, 4), new(8, 6), new(6, 6) }]
        };
        Assert.Equal(46.0, AccessFootprint.IntersectionArea(withHole, yard), 6);
    }
}
