using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class StationPlaybackTests
{
    private static readonly double[] Stations = [0.0, 1.0, 2.0, 4.0, 8.0];

    [Theory]
    [InlineData(-5.0, 0)]
    [InlineData(0.4, 0)]
    [InlineData(0.6, 1)]
    [InlineData(3.0, 2)] // equidistant: earlier entry wins
    [InlineData(3.1, 3)]
    [InlineData(7.0, 4)]
    [InlineData(99.0, 4)]
    public void NearestIndexFindsClosestStation(double station, int expected)
        => Assert.Equal(expected, StationPlayback.NearestIndex(Stations, s => s, station));

    [Fact]
    public void NearestIndexOfEmptyListIsMinusOne()
        => Assert.Equal(-1, StationPlayback.NearestIndex(Array.Empty<double>(), s => s, 1.0));

    [Fact]
    public void NearestIndexAgreesWithLinearSearch()
    {
        var stations = Enumerable.Range(0, 200).Select(i => i * 0.37 + (i % 3) * 0.01).ToArray();
        for (var probe = -1.0; probe < 80.0; probe += 0.173)
        {
            var linear = Enumerable.Range(0, stations.Length).MinBy(i => Math.Abs(stations[i] - probe));
            Assert.Equal(linear, StationPlayback.NearestIndex(stations, s => s, probe));
        }
    }

    [Fact]
    public void AdvanceMovesByWallTimeSpeedAndMultiplier()
    {
        var (station, finished) = StationPlayback.Advance(2.0, 100.0, 0.5, 2.0, 5.0);
        Assert.Equal(7.0, station, 9);
        Assert.False(finished);
    }

    [Fact]
    public void AdvanceStopsAtRouteEnd()
    {
        var (station, finished) = StationPlayback.Advance(99.0, 100.0, 1.0, 10.0, 1.0);
        Assert.Equal(100.0, station);
        Assert.True(finished);
    }

    [Fact]
    public void AdvanceIgnoresNegativeInputs()
    {
        var (station, _) = StationPlayback.Advance(5.0, 100.0, -1.0, 10.0, 1.0);
        Assert.Equal(5.0, station);
    }

    [Fact]
    public void ClampKeepsStationOnRoute()
    {
        Assert.Equal(0.0, StationPlayback.Clamp(-3.0, 10.0));
        Assert.Equal(10.0, StationPlayback.Clamp(13.0, 10.0));
        Assert.Equal(0.0, StationPlayback.Clamp(double.NaN, 10.0));
    }

    [Fact]
    public void TrailKeepsEveryPoseWhenFewEnough()
        => Assert.Equal([0, 1, 2, 3], StationPlayback.TrailIndices(10, 3, 50));

    [Fact]
    public void TrailIsBoundedAndKeepsFirstAndLast()
    {
        var trail = StationPlayback.TrailIndices(5000, 4000, 40);
        Assert.Equal(40, trail.Count);
        Assert.Equal(0, trail[0]);
        Assert.Equal(4000, trail[^1]);
        Assert.True(trail.Zip(trail.Skip(1), (a, b) => b > a).All(x => x));
    }

    [Fact]
    public void TrailClampsLastIndexAndHandlesDegenerateInput()
    {
        Assert.Equal([0, 1, 2], StationPlayback.TrailIndices(3, 99, 10));
        Assert.Empty(StationPlayback.TrailIndices(0, 0, 10));
        Assert.Empty(StationPlayback.TrailIndices(5, 2, 0));
        Assert.Equal([2], StationPlayback.TrailIndices(5, 2, 1));
    }
}
