using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class PoseSamplerTests
{
    private static IReadOnlyList<VehiclePose> Route(double lengthMetres, double spacingMetres)
    {
        var poses = new List<VehiclePose>();
        for (var station = 0.0; station <= lengthMetres + 1e-9; station += spacingMetres)
        {
            poses.Add(new VehiclePose(
                station,
                new Point3(station, 0.0, 0.0),
                new Point2(station + 3.0, 0.0),
                0.0,
                0.0,
                TravelDirection.Forward,
                [new Point2(station, -1.0), new Point2(station + 4.0, -1.0), new Point2(station + 4.0, 1.0), new Point2(station, 1.0)]));
        }

        return poses;
    }

    [Fact]
    public void StampsAtTheRequestedIntervalAndKeepsBothEnds()
    {
        var poses = Route(20.0, 0.025);

        var selected = PoseSampler.AtStationInterval(poses, 2.0);

        Assert.Equal(poses[0].StationMetres, selected[0].StationMetres, 6);
        Assert.Equal(poses[^1].StationMetres, selected[^1].StationMetres, 6);
        for (var index = 1; index < selected.Count; index++)
        {
            var step = selected[index].StationMetres - selected[index - 1].StationMetres;
            Assert.True(step > 0.0, "stations must advance");
            // Stamps land on the first pose at or past the interval, so a gap may overshoot by
            // up to one sample spacing -- but never by a whole interval.
            Assert.True(step <= 2.0 + 0.025 + 1e-6, $"gap {step:0.000} m overshoots by more than one sample");
        }
    }

    [Fact]
    public void DoesNotStackTheFinalStampOnItsNeighbour()
    {
        // 20.05 m at a 2 m interval leaves a 0.05 m tail; the last stamp must not double up.
        var selected = PoseSampler.AtStationInterval(Route(20.05, 0.05), 2.0);

        var finalGap = selected[^1].StationMetres - selected[^2].StationMetres;
        Assert.True(finalGap > 2.0 * 0.25, $"final gap {finalGap:0.000} m is a near-duplicate stamp");
        Assert.Equal(20.05, selected[^1].StationMetres, 6);
    }

    [Fact]
    public void AnIntervalWiderThanTheRouteGivesJustTheEnds()
    {
        var selected = PoseSampler.AtStationInterval(Route(5.0, 0.05), 100.0);

        Assert.Equal(2, selected.Count);
        Assert.Equal(0.0, selected[0].StationMetres, 6);
        Assert.Equal(5.0, selected[^1].StationMetres, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void ANonPositiveIntervalTurnsFootprintsOff(double interval) =>
        Assert.Empty(PoseSampler.AtStationInterval(Route(10.0, 0.05), interval));

    [Fact]
    public void HandlesDegenerateRoutes()
    {
        Assert.Empty(PoseSampler.AtStationInterval([], 2.0));
        Assert.Single(PoseSampler.AtStationInterval([Route(1.0, 1.0)[0]], 2.0));
    }
}
