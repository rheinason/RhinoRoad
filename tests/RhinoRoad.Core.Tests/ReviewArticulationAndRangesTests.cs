using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class ReviewArticulationAndRangesTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    private static IReadOnlyList<RouteSample> Circle(double radius, double sweep, double step)
    {
        var samples = new List<RouteSample>();
        for (var station = 0.0; station <= (radius * sweep) + 1e-9; station += step)
        {
            var theta = station / radius;
            samples.Add(new RouteSample(
                station,
                new Point3(radius * Math.Sin(theta), radius * (1.0 - Math.Cos(theta)), 0.0),
                theta,
                1.0 / radius,
                TravelDirection.Forward));
        }

        return samples;
    }

    [Fact]
    public void Jackknife_is_filed_under_its_own_check()
    {
        Assert.Equal(ReviewCheckKind.ArticulationAngle, VehicleAccessReviewAnalyzer.CheckFor(ViolationKind.ArticulationAngle));
        Assert.Equal(ReviewCheckKind.ObstacleClearance, VehicleAccessReviewAnalyzer.CheckFor(ViolationKind.ObstacleClearance));
        Assert.Equal(ReviewCheckKind.FixedWidthRoadFit, VehicleAccessReviewAnalyzer.CheckFor(ViolationKind.FixedWidthRoad));
    }

    [Fact]
    public void Fold_limit_is_the_tightest_units_own_limit()
    {
        var svt = Catalog.Get("SVT");
        Assert.Equal(svt.TowedUnits[0].MaximumArticulationRadians, VehicleAccessReviewAnalyzer.FoldLimitRadians(svt), 12);
        Assert.Equal(65.0 * Math.PI / 180.0, VehicleAccessReviewAnalyzer.FoldLimitRadians(svt), 12);

        var pvt = Catalog.Get("PVT");
        var mixed = pvt with
        {
            TowedUnits = [pvt.TowedUnits[0] with { MaximumArticulationDegrees = 50.0 }, .. pvt.TowedUnits.Skip(1)]
        };
        Assert.Equal(50.0 * Math.PI / 180.0, VehicleAccessReviewAnalyzer.FoldLimitRadians(mixed), 12);
        Assert.Equal(VehicleAccessAnalyzer.JackknifeFoldRadians, VehicleAccessReviewAnalyzer.FoldLimitRadians(Catalog.Get("PV")));
    }

    /// <summary>
    /// The inspector's fold check and the analyzer's fold-limit violation are judged by one limit, so
    /// a route past 65 degrees but short of a jackknife fails both rather than only the analysis.
    /// Both kinds of fold violation are filed under the articulation check.
    /// </summary>
    [Fact]
    public void A_fold_past_the_unit_limit_but_short_of_a_jackknife_fails_the_review_check_too()
    {
        var vehicle = Catalog.Get("SVT");
        var limit = VehicleAccessReviewAnalyzer.FoldLimitRadians(vehicle);
        RouteSample[] route = [];
        VehicleAccessResult? result = null;
        // Tighten a circle until the trailer folds past its limit but not past the jackknife floor.
        for (var radius = 12.0; radius >= 4.0; radius -= 0.25)
        {
            route = Circle(radius, Math.PI, 0.1).ToArray();
            result = new VehicleAccessAnalyzer().Analyze(vehicle, vehicle.DrivingModes["A"], route);
            var fold = result.MaximumArticulationAnglesRadians.Max();
            if (fold > limit && fold <= VehicleAccessAnalyzer.JackknifeFoldRadians) break;
            result = null;
        }

        Assert.NotNull(result);
        var folds = result!.Violations.Where(violation => violation.Kind == ViolationKind.ArticulationAngle).ToArray();
        Assert.NotEmpty(folds);
        Assert.All(folds, violation =>
            Assert.Equal(ReviewCheckKind.ArticulationAngle, VehicleAccessReviewAnalyzer.CheckFor(violation.Kind)));
        var samples = VehicleAccessReviewAnalyzer.MetricSamples(route, result);
        Assert.Contains(samples, sample => sample.FoldAngleRadians > limit);
    }

    [Fact]
    public void Articulated_samples_carry_the_largest_fold_over_their_joints()
    {
        var vehicle = Catalog.Get("PVT");
        var route = Circle(15.0, Math.PI, 0.1);
        var result = new VehicleAccessAnalyzer().Analyze(vehicle, vehicle.DrivingModes["A"], route);

        var samples = VehicleAccessReviewAnalyzer.MetricSamples(route, result);

        Assert.All(samples, sample => Assert.NotNull(sample.FoldAngleRadians));
        Assert.Equal(result.MaximumArticulationAnglesRadians.Max(), samples.Max(sample => sample.FoldAngleRadians!.Value), 9);
        var index = samples.Count / 2;
        Assert.Equal(
            result.Poses[index].TowedUnits.Max(unit => Math.Abs(unit.ArticulationAngleRadians)),
            samples[index].FoldAngleRadians!.Value,
            12);
    }

    [Fact]
    public void Rigid_samples_have_no_fold()
    {
        var vehicle = Catalog.Get("PV");
        var route = Circle(15.0, Math.PI / 2.0, 0.1);
        var result = new VehicleAccessAnalyzer().Analyze(vehicle, vehicle.DrivingModes["A"], route);

        Assert.All(VehicleAccessReviewAnalyzer.MetricSamples(route, result), sample => Assert.Null(sample.FoldAngleRadians));
    }

    [Fact]
    public void Ranged_violation_becomes_an_event_over_its_own_stations()
    {
        var samples = Enumerable.Range(0, 41).Select(index => new VehicleAccessMetricSample(
            index * 0.5, new Point3(index * 0.5, 0, 0), 0, 0, 0, null, TravelDirection.Forward)).ToArray();
        var violation = new AnalysisViolation(ViolationKind.ObstacleClearance, 6.0, new Point3(8.1, 2.0, 0.0), "obstacle")
        {
            EndStationMetres = 12.0
        };

        var item = VehicleAccessReviewAnalyzer.ViolationEvent(ReviewCheckKind.ObstacleClearance, samples, violation);

        Assert.Equal(6.0, item.StartStationMetres);
        Assert.Equal(12.0, item.EndStationMetres);
        Assert.Equal(8.0, item.WorstStationMetres);
        Assert.Equal("obstacle", item.Message);
    }

    [Fact]
    public void Consecutive_violations_bake_as_one_marker_per_run()
    {
        var stations = Enumerable.Range(0, 401).Select(index => index * 0.1).ToArray();
        static AnalysisViolation At(ViolationKind kind, double station) =>
            new(kind, station, new Point3(station, 0, 0), $"{kind} at {station:0.0}");

        // A run every 0.3 m from 2 to 5 m -- what the analyzer's quarter-metre coalescing leaves of a
        // continuous over-lock -- then a separate one at 20 m, and a ranged site conflict.
        var violations = Enumerable.Range(0, 11).Select(i => At(ViolationKind.SteeringAngle, 2.0 + (0.3 * i)))
            .Append(At(ViolationKind.SteeringAngle, 20.0))
            .Append(At(ViolationKind.Grade, 3.0))
            .Append(new AnalysisViolation(ViolationKind.ObstacleClearance, 10.0, new Point3(11, 2, 0), "obstacle") { EndStationMetres = 14.0 })
            .ToArray();

        var ranges = ViolationRanges.Group(violations, stations);

        Assert.Equal(4, ranges.Count);
        var turn = ranges.First(range => range.Kind == ViolationKind.SteeringAngle);
        Assert.Equal(2.0, turn.StartStationMetres, 9);
        Assert.Equal(5.0, turn.EndStationMetres, 9);
        Assert.Equal(11, turn.Count);
        Assert.Equal($"SteeringAngle at {2.0:0.0}", turn.Message);
        Assert.Equal("Wheel angle 2.0–5.0 m", ViolationRanges.Label(turn));
        Assert.Contains(ranges, range => range.Kind == ViolationKind.SteeringAngle && range.StartStationMetres == 20.0);
        var site = ranges.Single(range => range.Kind == ViolationKind.ObstacleClearance);
        Assert.Equal(14.0, site.EndStationMetres);
        Assert.Equal("Obstacle 10.0–14.0 m", ViolationRanges.Label(site));
    }
}
