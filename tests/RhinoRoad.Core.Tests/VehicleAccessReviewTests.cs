using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class VehicleAccessReviewTests
{
    [Fact]
    public void Consecutive_failures_become_one_event_at_the_worst_station()
    {
        var samples = Enumerable.Range(0, 7).Select(index => new VehicleAccessMetricSample(
            index, new Point3(index, 0, 0), index, 0, 0, null, TravelDirection.Forward)).ToArray();
        var events = VehicleAccessReviewAnalyzer.GroupFailures(
            ReviewCheckKind.SteeringAngle,
            samples,
            sample => sample.SteeringAngleRadians,
            sample => sample.StationMetres is >= 2 and <= 4,
            1.0,
            "too much");

        var item = Assert.Single(events);
        Assert.Equal(2, item.StartStationMetres);
        Assert.Equal(4, item.EndStationMetres);
        Assert.Equal(4, item.WorstStationMetres);
    }

    [Fact]
    public void Fallback_localizes_a_between_sample_envelope_failure_to_nearest_station()
    {
        var samples = new[]
        {
            new VehicleAccessMetricSample(0, new Point3(0, 0, 0), 0, 0, 0, null, TravelDirection.Forward),
            new VehicleAccessMetricSample(10, new Point3(10, 0, 0), 0, 0, 0, null, TravelDirection.Forward)
        };
        var item = VehicleAccessReviewAnalyzer.FallbackEvent(
            ReviewCheckKind.AllowedAreaFit, samples, new Point3(8, 1, 0), null, 0, "outside");
        Assert.Equal(10, item.WorstStationMetres);
        Assert.Equal(new Point3(8, 1, 0), item.WorldPointMetres);
    }

    [Fact]
    public void Indexed_grouping_reads_aligned_metrics_in_linear_time()
    {
        const int count = 10_000;
        var samples = Enumerable.Range(0, count).Select(index => new VehicleAccessMetricSample(
            index, new Point3(index, 0, 0), 0, 0, 0, null, TravelDirection.Forward)).ToArray();
        var actualCalls = 0;
        var predicateCalls = 0;

        var events = VehicleAccessReviewAnalyzer.GroupFailuresIndexed(
            ReviewCheckKind.ObstacleClearance,
            samples,
            (_, index) => { actualCalls++; return index; },
            (_, index) => { predicateCalls++; return index is >= 100 and < 200; },
            0,
            "failure");

        Assert.Single(events);
        Assert.Equal(count, predicateCalls);
        Assert.True(actualCalls < count * 3);
    }
}
