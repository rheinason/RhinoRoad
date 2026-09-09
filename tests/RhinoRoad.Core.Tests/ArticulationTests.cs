using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// A trailer's position is integrated, not derived, so the checks that matter are the ones with a
/// closed form to compare against. In a steady turn every unit circles one centre, which fixes each
/// towed axle's radius exactly: <c>R' = sqrt(R² + h² − L²)</c>. Driving far enough round to settle
/// and measuring that radius tests the integrator, the sign of the hitch offset and the chaining of
/// one joint into the next all at once.
/// </summary>
public sealed class ArticulationTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();
    private static readonly VehicleAccessAnalyzer Analyzer = new();

    private static double SettledRadius(double radius, TowedUnitDefinition unit) => Math.Sqrt(
        (radius * radius) + (unit.HitchOffsetMetres * unit.HitchOffsetMetres)
        - (unit.WheelbaseMetres * unit.WheelbaseMetres));

    [Fact]
    public void SemitrailerSettlesOnTheClosedFormOffTrackingRadius()
    {
        var vehicle = Catalog.Get("SVT");
        var trailer = vehicle.TowedUnits[0];
        const double radius = 20.0;

        var result = Analyzer.Analyze(vehicle, vehicle.DrivingModes["A"], Circle(radius, 4.0 * Math.PI, 0.1));
        var track = Assert.Single(result.TowedAxleTracksMetres);

        Assert.Equal(SettledRadius(radius, trailer), Centre(radius).DistanceTo(track[^1]), 3);
    }

    [Fact]
    public void DrawbarCombinationSettlesThroughBothJoints()
    {
        var vehicle = Catalog.Get("PVT");
        const double radius = 20.0;

        var result = Analyzer.Analyze(vehicle, vehicle.DrivingModes["A"], Circle(radius, 4.0 * Math.PI, 0.1));

        Assert.Equal(2, result.TowedAxleTracksMetres.Count);
        var dolly = SettledRadius(radius, vehicle.TowedUnits[0]);
        var trailer = SettledRadius(dolly, vehicle.TowedUnits[1]);
        Assert.Equal(dolly, Centre(radius).DistanceTo(result.TowedAxleTracksMetres[0][^1]), 3);
        Assert.Equal(trailer, Centre(radius).DistanceTo(result.TowedAxleTracksMetres[1][^1]), 3);

        // Each joint takes a further bite out of the radius, which is the whole reason a drawbar
        // combination is modelled as two joints and not one long trailer.
        Assert.True(trailer < dolly && dolly < radius);
    }

    [Fact]
    public void TrailerTrailsStraightBehindOnAStraightRoute()
    {
        var vehicle = Catalog.Get("SVT");
        var trailer = vehicle.TowedUnits[0];

        var result = Analyzer.Analyze(vehicle, vehicle.DrivingModes["A"], Straight(40.0, 0.5));

        foreach (var pose in result.Poses)
        {
            var towed = Assert.Single(pose.TowedUnits);
            Assert.Equal(0.0, towed.ArticulationAngleRadians, 9);
            Assert.Equal(0.0, towed.AxleCentreMetres.Y, 9);
            Assert.Equal(
                pose.RearAxleCentreMetres.X - trailer.WheelbaseMetres + trailer.HitchOffsetMetres,
                towed.AxleCentreMetres.X,
                9);
        }
    }

    [Fact]
    public void TrailerCutsInsideTheTractorThroughATurn()
    {
        var vehicle = Catalog.Get("SVT");
        const double radius = 15.0;

        var result = Analyzer.Analyze(vehicle, vehicle.DrivingModes["A"], Circle(radius, Math.PI, 0.1));
        var track = Assert.Single(result.TowedAxleTracksMetres);

        Assert.True(Centre(radius).DistanceTo(track[^1]) < radius - 1.0);
        Assert.True(Assert.Single(result.MaximumArticulationAnglesRadians) > 0.3);
    }

    /// <summary>
    /// Forward, the fold is a stable equilibrium and settles. In reverse the same equation runs the
    /// other way and the fold runs away — which is jackknifing, and is the behaviour a reversing
    /// manoeuvre has to reproduce rather than smooth over. A sign dropped anywhere in the integrator
    /// makes reverse look like forward, so this is the check that catches it.
    /// </summary>
    [Fact]
    public void ReversingRunsTheFoldAwayInsteadOfSettlingIt()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["A"];
        const double radius = 25.0;
        // Fifteen metres of arc: long enough for the forward fold to have settled, short enough that
        // the reverse fold has not yet folded all the way back on itself.
        const double sweep = 0.6;

        var forward = Folds(Analyzer.Analyze(vehicle, mode, Circle(radius, sweep, 0.1)));
        var reverse = Folds(Analyzer.Analyze(vehicle, mode, Circle(radius, sweep, 0.1, TravelDirection.Reverse)));

        Assert.True(forward[^1] < Math.Asin(vehicle.TowedUnits[0].WheelbaseMetres / radius));
        Assert.True(reverse[^1] > forward[^1] * 3.0);

        // Not merely larger: still accelerating where the forward fold has flattened out.
        Assert.True(reverse[^1] - reverse[^21] > (forward[^1] - forward[^21]) * 5.0);
    }

    /// <summary>
    /// The trailer is still folded along a stretch where the tractor is running dead straight, and
    /// unfolds as that stretch goes on. A model reading the trailer off the current sample would
    /// have it straight from the first metre of the exit.
    /// </summary>
    [Fact]
    public void TheTrailerCarriesTheCornerIntoTheStraightThatFollowsIt()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["A"];

        var straight = Analyzer.Analyze(vehicle, mode, Straight(40.0, 0.1));
        var afterTurn = Analyzer.Analyze(vehicle, mode, TurnThenStraight(20.0, Math.PI / 2.0, 6.0, 0.1));

        Assert.Equal(0.0, straight.Poses[^1].TowedUnits[0].ArticulationAngleRadians, 9);

        var exit = Folds(afterTurn)[^61..];
        Assert.True(exit[0] > 0.15);
        Assert.True(exit[^1] < exit[0]);
        Assert.True(exit[^1] > 0.0);
        Assert.Equal(
            afterTurn.Poses[^1].VehicleHeadingRadians,
            afterTurn.Poses[^61].VehicleHeadingRadians,
            9);
    }

    /// <summary>
    /// A folded run must not be reported as feasible. It was: a reversing SVT reached a 179.9°
    /// fold — the trailer through the cab — and the analysis returned no violations at all, while
    /// the swept envelope built from those footprints went on to be baked into the drawing as a
    /// clearance envelope.
    /// </summary>
    [Fact]
    public void AJackknifedRunIsNotReportedAsFeasible()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];

        var result = Analyzer.Analyze(vehicle, mode, Circle(25.0, 40.0 / 25.0, 0.05, TravelDirection.Reverse));

        Assert.False(result.IsFeasible);
        var violation = Assert.Single(result.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
        Assert.Contains("jackknifed", violation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.MaximumArticulationAnglesRadians[0] > VehicleAccessAnalyzer.JackknifeFoldRadians);

        // The station it is first reported at is where the fold crosses the limit, not the end.
        Assert.InRange(violation.StationMetres, 10.0, 25.0);
    }

    /// <summary>
    /// The limit must not fire on manoeuvres that are merely tight. Everything at or above the
    /// radius the combination can sustain stays well inside it, forward.
    /// </summary>
    [Fact]
    public void TurnsTheCombinationCanHoldRaiseNoJackknife()
    {
        foreach (var id in new[] { "SVT", "PVT" })
        {
            var vehicle = Catalog.Get(id);
            var mode = vehicle.DrivingModes["B"];
            foreach (var radius in new[] { 12.0, 20.0, 40.0 })
            {
                var result = Analyzer.Analyze(vehicle, mode, Circle(radius, 2.0 * Math.PI, 0.05));
                Assert.DoesNotContain(result.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
            }
        }
    }

    /// <summary>Reversing dead straight is the one reverse that holds: the fold has nothing to grow from.</summary>
    [Fact]
    public void ReversingInAStraightLineKeepsTheTrailerBehindTheVehicle()
    {
        var vehicle = Catalog.Get("SVT");
        var samples = Straight(60.0, 0.1)
            .Select(sample => sample with { Direction = TravelDirection.Reverse })
            .ToArray();

        var result = Analyzer.Analyze(vehicle, vehicle.DrivingModes["B"], samples);

        Assert.Equal(0.0, result.MaximumArticulationAnglesRadians[0], 9);
        Assert.DoesNotContain(result.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
    }

    [Fact]
    public void SweptRegionCoversTheTrailerAndNotJustTheTractor()
    {
        var vehicle = Catalog.Get("SVT");
        var result = Analyzer.Analyze(vehicle, vehicle.DrivingModes["A"], Circle(15.0, Math.PI / 2.0, 0.1));

        var whole = SweptRegionBuilder.FromPoses(result.Poses);
        var tractorOnly = SweptRegionBuilder.FromOutlines(
            result.Poses.Select(pose => pose.BodyOutlineWorldMetres).ToArray());

        Assert.True(whole.IsSuccess);
        Assert.True(whole.Region!.AreaSquareMetres > tractorOnly.Region!.AreaSquareMetres * 1.5);
        Assert.All(result.Poses, pose => Assert.Equal(2, pose.OccupiedOutlinesWorldMetres.Count));
    }

    /// <summary>The drawbar has no body, so it must not contribute an outline to sweep or clip.</summary>
    [Fact]
    public void RunningGearWithoutABodySweepsNothing()
    {
        var vehicle = Catalog.Get("PVT");
        var result = Analyzer.Analyze(vehicle, vehicle.DrivingModes["A"], Circle(20.0, Math.PI / 2.0, 0.1));

        Assert.All(result.Poses, pose =>
        {
            Assert.Equal(2, pose.TowedUnits.Count);
            Assert.Equal(2, pose.OccupiedOutlinesWorldMetres.Count);
        });
    }

    [Fact]
    public void RigidPresetsAreUnchangedByArticulationSupport()
    {
        var vehicle = Catalog.Get("PV");
        var result = Analyzer.Analyze(vehicle, vehicle.DrivingModes["A"], Circle(15.0, Math.PI / 2.0, 0.1));

        Assert.Empty(result.TowedAxleTracksMetres);
        Assert.Empty(result.MaximumArticulationAnglesRadians);
        Assert.All(result.Poses, pose =>
        {
            Assert.Empty(pose.TowedUnits);
            Assert.Same(pose.BodyOutlineWorldMetres, Assert.Single(pose.OccupiedOutlinesWorldMetres));
        });
    }

    [Fact]
    public void FullLockOnASemitrailerReportsThatTheCombinationCannotHoldIt()
    {
        var vehicle = Catalog.Get("SVT");
        var trailer = vehicle.TowedUnits[0];

        var turn = TurningGeometryCalculator.AtFullLock(vehicle, vehicle.DrivingModes["B"]);

        Assert.False(turn.SteadyStateAttainable);
        Assert.Empty(turn.TowedAxleRadiiMetres);
        Assert.Equal(
            Math.Sqrt((trailer.WheelbaseMetres * trailer.WheelbaseMetres)
                - (trailer.HitchOffsetMetres * trailer.HitchOffsetMetres)),
            turn.MinimumSustainableRearAxleRadiusMetres,
            6);
        Assert.True(turn.RearAxleRadiusMetres < turn.MinimumSustainableRearAxleRadiusMetres);
    }

    [Fact]
    public void ASustainableLockReportsTheAnnulusTheWholeCombinationSweeps()
    {
        var vehicle = Catalog.Get("SVT");
        // A lock gentle enough for the trailer to follow: 3.6 m wheelbase on a 20 m rear-axle radius.
        var mode = new DrivingModeDefinition(
            "W", "Wide", 5.0, Math.Atan(vehicle.WheelbaseMetres / 20.0) * 180.0 / Math.PI, 6.0, 0.30);

        var turn = TurningGeometryCalculator.AtFullLock(vehicle, mode);

        Assert.True(turn.SteadyStateAttainable);
        Assert.Equal(20.0, turn.RearAxleRadiusMetres, 6);
        Assert.Equal(SettledRadius(20.0, vehicle.TowedUnits[0]), Assert.Single(turn.TowedAxleRadiiMetres), 6);

        // The trailer cuts well inside anything the tractor alone would justify.
        var tractorOnly = TurningGeometryCalculator.AtFullLock(
            vehicle with { TowedUnits = [] }, mode);
        Assert.True(turn.InnerRadiusMetres < tractorOnly.InnerRadiusMetres - 1.0);
        Assert.True(turn.SweptWidthMetres > tractorOnly.SweptWidthMetres + 1.0);
    }

    /// <summary>
    /// The interactive preview reuses a cached sweep for the part of the route it is not redrawing,
    /// which means it also has to hand that part's fold forward. Rewinding into a corner and
    /// replaying it is where a mishandled cache shows up as a trailer that jumps.
    /// </summary>
    [Fact]
    public void PreviewOfAnArticulatedJourneyMatchesAFullAnalysis()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["A"];
        var start = new VehicleState(new(0, 0, 0), 0, 0, TravelDirection.Forward, 0);
        var leg = new RateLimitedTrajectoryGenerator().GenerateLeg(vehicle, mode, start, 0.35, 45);
        var builder = new JourneySweepBuilder(leg.Samples, vehicle, 0.3);

        foreach (var index in new[] { leg.Samples.Count - 1, 200, 60, 200 })
        {
            var samples = leg.Samples.Take(index + 1).ToArray();
            var preview = builder.Build(new PlannedManoeuvreLeg(index, [samples[^1]], leg.EndState, false));
            var expected = SweptRegionBuilder.FromPoses(Analyzer.Analyze(vehicle, mode, samples).Poses);

            Assert.Equal(expected.Region!.AreaSquareMetres, preview.Body.Region!.AreaSquareMetres, 1);
        }
    }

    [Fact]
    public void TraceAlongARouteAgreesWithTheAnalysisAtEverySample()
    {
        var vehicle = Catalog.Get("PVT");
        var samples = Circle(18.0, Math.PI, 0.1);
        var result = Analyzer.Analyze(vehicle, vehicle.DrivingModes["A"], samples);

        var states = ArticulationTrace.Along(vehicle, samples);

        Assert.Equal(samples.Count, states.Count);
        for (var index = 0; index < samples.Count; index++)
        {
            var pose = result.Poses[index];
            var traced = states[index]!.Poses(pose.RearAxleCentreMetres.XY, pose.VehicleHeadingRadians);
            for (var unit = 0; unit < traced.Count; unit++)
            {
                Assert.Equal(pose.TowedUnits[unit].AxleCentreMetres.X, traced[unit].AxleCentreMetres.X, 9);
                Assert.Equal(pose.TowedUnits[unit].AxleCentreMetres.Y, traced[unit].AxleCentreMetres.Y, 9);
            }
        }
    }

    private static Point2 Centre(double radius) => new(0.0, radius);

    private static double[] Folds(VehicleAccessResult result) => result.Poses
        .Select(pose => Math.Abs(pose.TowedUnits[0].ArticulationAngleRadians))
        .ToArray();

    private static IReadOnlyList<RouteSample> Straight(double length, double step)
    {
        var samples = new List<RouteSample>();
        for (var station = 0.0; station <= length + 1e-9; station += step)
        {
            samples.Add(new RouteSample(station, new Point3(station, 0.0, 0.0), 0.0, 0.0, TravelDirection.Forward));
        }

        return samples;
    }

    /// <summary>A left-hand circle through the origin, centred at (0, radius).</summary>
    private static IReadOnlyList<RouteSample> Circle(
        double radius,
        double sweep,
        double step,
        TravelDirection direction = TravelDirection.Forward)
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
                direction));
        }

        return samples;
    }

    /// <summary>A quarter circle, then a straight run on the exit heading.</summary>
    private static IReadOnlyList<RouteSample> TurnThenStraight(double radius, double sweep, double straight, double step)
    {
        var samples = Circle(radius, sweep, step).ToList();
        var last = samples[^1];
        var heading = last.PathHeadingRadians;
        for (var run = step; run <= straight + 1e-9; run += step)
        {
            samples.Add(new RouteSample(
                last.StationMetres + run,
                new Point3(
                    last.PositionMetres.X + (run * Math.Cos(heading)),
                    last.PositionMetres.Y + (run * Math.Sin(heading)),
                    0.0),
                heading,
                0.0,
                TravelDirection.Forward));
        }

        return samples;
    }
}
