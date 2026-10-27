using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class VehicleAccessReviewTests
{
    [Theory]
    [InlineData("PV", "A")]
    [InlineData("SVT", "B")]
    public void A_cusp_is_not_a_steering_rate_problem_when_the_check_passes(string vehicleId, string modeId)
    {
        // A change of direction is a standstill: the wheel is turned while the vehicle stands, so
        // the profile must not price that movement against the next tenth of a metre. A live run
        // listed 314 deg/s at the cusp of an SVT bay reverse while the check itself passed.
        var vehicle = VehicleCatalog.LoadEmbedded().Get(vehicleId);
        var mode = vehicle.DrivingModes[modeId];
        var journey = new ManoeuvreDefinition(
            ManoeuvreDefinition.CurrentSchemaVersion,
            new Point3(0, 0, 0),
            0.0,
            TravelDirection.Forward,
            [
                new ManoeuvreControl(new Point3(25, 0, 0), TravelDirection.Forward),
                new ManoeuvreControl(new Point3(35, 5, 0), TravelDirection.Forward),
                new ManoeuvreControl(new Point3(20, 2, 0), TravelDirection.Reverse)
            ]);
        var route = ManoeuvreReplayService.Replay(vehicle, mode, journey).Samples;
        Assert.Contains(route, sample => sample.StartsFromStandstill);
        var result = new VehicleAccessAnalyzer().Analyze(vehicle, mode, route);
        Assert.DoesNotContain(result.Violations, violation => violation.Kind == ViolationKind.SteeringRate);

        var samples = VehicleAccessReviewAnalyzer.MetricSamples(route, result);

        Assert.All(samples, sample =>
            Assert.True(sample.SteeringRateRadiansPerSecond <= mode.MaximumSteeringRateRadiansPerSecond + 1e-8,
                $"{sample.SteeringRateRadiansPerSecond * 180 / Math.PI:0.0} deg/s at {sample.StationMetres:0.0} m"));
    }

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
