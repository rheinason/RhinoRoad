using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class WheelTrackRouteReconstructorTests
{
    private const double Wheelbase = 3.0;
    private const double OuterOffset = 0.9;

    /// <summary>
    /// The forward model, written independently of the production inverse so that a sign or
    /// side error in <see cref="WheelTrackRouteReconstructor"/> shows up as a failure rather
    /// than cancelling out: the rear trace sits at -offset on the body normal, the front trace
    /// one wheelbase ahead at +offset.
    /// </summary>
    private static (Point2[] Front, Point2[] Rear) WheelEdges(
        IReadOnlyList<(Point2 Centre, double Heading)> route,
        double wheelbase = Wheelbase,
        double offset = OuterOffset)
    {
        var front = new Point2[route.Count];
        var rear = new Point2[route.Count];
        for (var index = 0; index < route.Count; index++)
        {
            var (centre, heading) = route[index];
            var normal = new Point2(-Math.Sin(heading), Math.Cos(heading));
            var forward = new Point2(Math.Cos(heading), Math.Sin(heading));
            rear[index] = centre - (normal * offset);
            front[index] = centre + (forward * wheelbase) + (normal * offset);
        }

        return (front, rear);
    }

    private static (Point2 Centre, double Heading)[] StraightRoute(double lengthMetres, double heading, int vertices)
    {
        var route = new (Point2, double)[vertices];
        var forward = new Point2(Math.Cos(heading), Math.Sin(heading));
        for (var index = 0; index < vertices; index++)
        {
            route[index] = (forward * (lengthMetres * index / (vertices - 1)), heading);
        }

        return route;
    }

    private static (Point2 Centre, double Heading)[] ArcRoute(double radiusMetres, double sweepRadians, int vertices)
    {
        var route = new (Point2, double)[vertices];
        for (var index = 0; index < vertices; index++)
        {
            // Left-hand turn about (0, radius), starting at the origin heading +X.
            var t = sweepRadians * index / (vertices - 1);
            route[index] = (
                new Point2(radiusMetres * Math.Sin(t), radiusMetres * (1.0 - Math.Cos(t))),
                t);
        }

        return route;
    }

    [Fact]
    public void RecoversRearAxleCentresAndHeadingsFromAStraightRun()
    {
        var route = StraightRoute(40.0, Math.PI / 6.0, 41);
        var (front, rear) = WheelEdges(route);

        var result = WheelTrackRouteReconstructor.Reconstruct(front, rear, Wheelbase, OuterOffset);

        Assert.Equal(WheelTrackReconstructionStatus.Success, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Equal(0.0, result.MeasuredSeparationErrorMetres, 9);
        Assert.Equal(0.0, result.Samples[0].PositionMetres.X, 6);
        Assert.Equal(0.0, result.Samples[0].PositionMetres.Y, 6);
        Assert.Equal(40.0, result.Samples[^1].StationMetres, 6);
        foreach (var sample in result.Samples)
        {
            Assert.Equal(Math.PI / 6.0, sample.PathHeadingRadians, 9);
            Assert.Equal(0.0, sample.SignedCurvaturePerMetre, 9);
        }
    }

    [Fact]
    public void RecoversSignedCurvatureFromAConstantRadiusArc()
    {
        const double radius = 12.0;
        var route = ArcRoute(radius, Math.PI / 2.0, 200);
        var (front, rear) = WheelEdges(route);

        var result = WheelTrackRouteReconstructor.Reconstruct(front, rear, Wheelbase, OuterOffset);

        Assert.Equal(WheelTrackReconstructionStatus.Success, result.Status);

        // The chords of a polygonised arc cut inside the true arc, so the reconstructed centres
        // sit fractionally in; check the interior where the central difference is well defined.
        var interior = result.Samples.Skip(50).SkipLast(50).ToArray();
        Assert.All(interior, sample =>
            Assert.InRange(sample.SignedCurvaturePerMetre, 1.0 / radius * 0.98, 1.0 / radius * 1.02));

        // A left turn is positive curvature, and the recovered centres stay on the design arc.
        Assert.All(interior, sample =>
            Assert.InRange(
                new Point2(sample.PositionMetres.X, sample.PositionMetres.Y - radius).Length,
                radius - 0.01,
                radius + 0.01));
    }

    [Fact]
    public void ResamplesCoarseSourcePolylinesToTheRequestedSpacing()
    {
        var route = StraightRoute(30.0, 0.0, 4);
        var (front, rear) = WheelEdges(route);
        var options = new WheelTrackReconstructionOptions { SampleSpacingMetres = 0.05, MinimumSampleCount = 1 };

        var result = WheelTrackRouteReconstructor.Reconstruct(front, rear, Wheelbase, OuterOffset, options);

        Assert.Equal(WheelTrackReconstructionStatus.Success, result.Status);
        Assert.Equal(601, result.Samples.Count);
        for (var index = 1; index < result.Samples.Count; index++)
        {
            Assert.Equal(
                0.05,
                result.Samples[index].StationMetres - result.Samples[index - 1].StationMetres,
                6);
        }
    }

    [Fact]
    public void RoundTripsATrajectoryProducedByTheKinematicGenerator()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("REN");
        var mode = vehicle.DrivingModes["A"];
        var start = new VehicleState(new Point3(5.0, -2.0, 0.0), 0.4, 0.0, TravelDirection.Forward, 0.0);
        var generated = new RateLimitedTrajectoryGenerator()
            .GenerateLeg(vehicle, mode, start, mode.MaximumWheelAngleRadians * 0.6, 25.0, 0.02);
        var poses = generated.Samples
            .Select(sample => (new Point2(sample.PositionMetres.X, sample.PositionMetres.Y), sample.PathHeadingRadians))
            .ToArray();
        var (front, rear) = WheelEdges(poses, vehicle.WheelbaseMetres, vehicle.WheelOuterEdgeOffsetMetres);
        // Spacing wider than any source segment keeps the reconstruction vertex-for-vertex, so the
        // recovered route can be compared against the generated one sample by sample.
        var options = new WheelTrackReconstructionOptions { SampleSpacingMetres = 1.0, MinimumSampleCount = 1 };

        var result = WheelTrackRouteReconstructor.Reconstruct(
            front, rear, vehicle.WheelbaseMetres, vehicle.WheelOuterEdgeOffsetMetres, options);

        Assert.Equal(WheelTrackReconstructionStatus.Success, result.Status);
        Assert.Equal(generated.Samples.Count, result.Samples.Count);
        for (var index = 0; index < generated.Samples.Count; index++)
        {
            var expected = generated.Samples[index];
            var actual = result.Samples[index];
            Assert.Equal(expected.PositionMetres.X, actual.PositionMetres.X, 6);
            Assert.Equal(expected.PositionMetres.Y, actual.PositionMetres.Y, 6);
            Assert.Equal(expected.PathHeadingRadians, actual.PathHeadingRadians, 6);
            Assert.Equal(expected.StationMetres, actual.StationMetres, 6);
        }
    }

    [Fact]
    public void ReconstructedRouteReanalysesWithinTheGeneratorSteeringLimits()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("BUS12");
        var mode = vehicle.DrivingModes["A"];
        var start = new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var generated = new RateLimitedTrajectoryGenerator()
            .GenerateLeg(vehicle, mode, start, mode.MaximumWheelAngleRadians, 30.0, 0.02);
        var poses = generated.Samples
            .Select(sample => (new Point2(sample.PositionMetres.X, sample.PositionMetres.Y), sample.PathHeadingRadians))
            .ToArray();
        var (front, rear) = WheelEdges(poses, vehicle.WheelbaseMetres, vehicle.WheelOuterEdgeOffsetMetres);
        var options = new WheelTrackReconstructionOptions { SampleSpacingMetres = 0.02, MinimumSampleCount = 1 };

        var result = WheelTrackRouteReconstructor.Reconstruct(
            front, rear, vehicle.WheelbaseMetres, vehicle.WheelOuterEdgeOffsetMetres, options);
        var analysis = new VehicleAccessAnalyzer().Analyze(vehicle, mode, result.Samples);

        Assert.Equal(WheelTrackReconstructionStatus.Success, result.Status);
        Assert.True(
            analysis.MaximumSteeringAngleRadians <= mode.MaximumWheelAngleRadians + 1e-6,
            $"Reconstructed wheel angle {analysis.MaximumSteeringAngleRadians:0.0000} rad exceeded the {mode.MaximumWheelAngleRadians:0.0000} rad limit.");
    }

    [Fact]
    public void RejectsTracesWhoseChordContradictsThePreset()
    {
        var route = StraightRoute(40.0, 0.0, 41);
        // Traces drawn for a vehicle with a materially longer wheelbase than the preset claims.
        var (front, rear) = WheelEdges(route, Wheelbase + 1.0);

        var result = WheelTrackRouteReconstructor.Reconstruct(front, rear, Wheelbase, OuterOffset);

        Assert.Equal(WheelTrackReconstructionStatus.SeparationOutOfTolerance, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Samples);
        Assert.Equal(
            Math.Sqrt((4.0 * 4.0) + (4.0 * OuterOffset * OuterOffset)) -
            Math.Sqrt((Wheelbase * Wheelbase) + (4.0 * OuterOffset * OuterOffset)),
            result.MeasuredSeparationErrorMetres,
            6);
        Assert.Contains("mismatched", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptsChordDriftInsideTolerance()
    {
        var route = StraightRoute(40.0, 0.0, 41);
        var (front, rear) = WheelEdges(route, Wheelbase + 0.10);

        var result = WheelTrackRouteReconstructor.Reconstruct(front, rear, Wheelbase, OuterOffset);

        Assert.Equal(WheelTrackReconstructionStatus.Success, result.Status);
        Assert.InRange(result.MeasuredSeparationErrorMetres, 0.05, 0.15);
    }

    [Fact]
    public void ResolvesTracesTessellatedAtDifferentDensities()
    {
        // Same underlying pass, but the front edge is drawn with three times the vertices --
        // the shape of the four REN cases the official DWGs actually ship.
        var coarse = ArcRoute(14.0, 1.2, 60);
        var dense = ArcRoute(14.0, 1.2, 180);
        var rear = WheelEdges(coarse).Rear;
        var front = WheelEdges(dense).Front;
        var options = new WheelTrackReconstructionOptions { MinimumSampleCount = 1 };

        var mixed = WheelTrackRouteReconstructor.Reconstruct(front, rear, Wheelbase, OuterOffset, options);
        var paired = WheelTrackRouteReconstructor.Reconstruct(
            WheelEdges(coarse).Front, rear, Wheelbase, OuterOffset, options);

        Assert.Equal(WheelTrackReconstructionStatus.Success, mixed.Status);
        Assert.Contains("Paired", mixed.Message);

        // The recovered route must agree with the vertex-paired reconstruction of the same pass.
        // The residual is the discretisation gap between the two tessellations, and must stay far
        // below the 0.10 m certification tolerance it feeds.
        // Sample counts may differ slightly: resolving the chord against a denser trace shifts where
        // the end points land, changing step counts. Compare the routes geometrically instead, by
        // matching each sample to the nearest station on the other route.
        Assert.InRange(mixed.Samples.Count, paired.Samples.Count * 0.98, paired.Samples.Count * 1.02);
        var length = mixed.Samples[^1].StationMetres;
        var worstOverall = 0.0;
        var worstInterior = 0.0;
        var worstHeadingInterior = 0.0;
        foreach (var b in mixed.Samples)
        {
            var a = paired.Samples.MinBy(sample => Math.Abs(sample.StationMetres - b.StationMetres))!;
            var offset = a.PositionMetres.XY.DistanceTo(b.PositionMetres.XY);
            worstOverall = Math.Max(worstOverall, offset);
            if (b.StationMetres < length * 0.05 || b.StationMetres > length * 0.95) continue;
            worstInterior = Math.Max(worstInterior, offset);
            worstHeadingInterior = Math.Max(
                worstHeadingInterior,
                Math.Abs(Geometry2D.NormalizeAngle(a.PathHeadingRadians - b.PathHeadingRadians)));
        }

        // Through the body of the manoeuvre the two reconstructions must agree closely.
        Assert.True(worstInterior < 0.005, $"interior disagreement {worstInterior:0.00000} m");
        Assert.True(worstHeadingInterior < 0.005, $"interior heading disagreement {worstHeadingInterior:0.00000} rad");

        // The ends may differ more, because the chord tolerance decides where the first and last
        // paired points land. It must still stay far inside the 0.10 m certification tolerance.
        Assert.True(worstOverall < 0.025, $"worst disagreement {worstOverall:0.00000} m");
    }

    [Fact]
    public void RejectsTracesThatDescribeDifferentPasses()
    {
        // A front trace lifted from an unrelated case: no forward chord solution exists.
        var rear = WheelEdges(ArcRoute(14.0, 1.2, 60)).Rear;
        var foreign = WheelEdges(StraightRoute(40.0, Math.PI, 91)).Front
            .Select(point => point + new Point2(400.0, 400.0))
            .ToArray();

        var result = WheelTrackRouteReconstructor.Reconstruct(foreign, rear, Wheelbase, OuterOffset);

        Assert.Equal(WheelTrackReconstructionStatus.PairingFailed, result.Status);
        Assert.Empty(result.Samples);
        Assert.Contains("not vertex-paired", result.Message);
    }

    [Fact]
    public void RejectsTracesThatAreChordConsistentButStartAtDifferentMoments()
    {
        // The published REN mode A cases ship exactly this: two long curved traces that admit a
        // chord-consistent walk, but whose starts sit 12-20 m apart rather than one chord apart.
        // Without a shared-start check the walk invents a correspondence and the sweep lands metres
        // away from the reference.
        var route = ArcRoute(14.0, 2.2, 120);
        var rear = WheelEdges(route).Rear;
        var front = WheelEdges(route).Front;
        var truncatedRear = rear.Skip(rear.Length / 2).ToArray();

        var result = WheelTrackRouteReconstructor.Reconstruct(front, truncatedRear, Wheelbase, OuterOffset);

        Assert.Equal(WheelTrackReconstructionStatus.PairingFailed, result.Status);
        Assert.Contains("begin at different points", result.Message);
        Assert.Empty(result.Samples);
    }

    [Fact]
    public void PairingReportsCoverageAndRejectsAStubFrontTrace()
    {
        var route = ArcRoute(14.0, 1.2, 60);
        var rear = WheelEdges(route).Rear;
        var full = WheelEdges(route).Front;
        var separation = Math.Sqrt((Wheelbase * Wheelbase) + (4.0 * OuterOffset * OuterOffset));

        var good = WheelTrackPairing.Resolve(full, rear, separation);
        Assert.True(good.IsSuccess);
        Assert.InRange(good.FrontLengthConsumedFraction, WheelTrackPairing.MinimumFrontCoverageFraction, 1.0);
        Assert.Equal(rear.Length, good.PairedFrontVertices.Count);

        // A front trace continuing far past the rear pass is not a correspondence for it.
        var overrun = full.Concat(Enumerable.Range(1, 400).Select(i => full[^1] + new Point2(i * 0.5, 0.0))).ToArray();
        var bad = WheelTrackPairing.Resolve(overrun, rear, separation);
        Assert.False(bad.IsSuccess);
        Assert.Contains("consumed only", bad.Message);
    }

    [Fact]
    public void RejectsEmptyOrDegenerateTraces()
    {
        var (front, rear) = WheelEdges(StraightRoute(40.0, 0.0, 41));

        Assert.Equal(
            WheelTrackReconstructionStatus.InsufficientVertices,
            WheelTrackRouteReconstructor.Reconstruct([], [], Wheelbase, OuterOffset).Status);
        Assert.Equal(
            WheelTrackReconstructionStatus.InsufficientVertices,
            WheelTrackRouteReconstructor.Reconstruct(front.Take(1).ToArray(), rear.Take(1).ToArray(), Wheelbase, OuterOffset).Status);
    }

    [Theory]
    [InlineData(0.0, OuterOffset)]
    [InlineData(-1.0, OuterOffset)]
    [InlineData(Wheelbase, 0.0)]
    [InlineData(Wheelbase, -0.5)]
    public void RejectsNonPhysicalVehicleGeometry(double wheelbase, double offset)
    {
        var (front, rear) = WheelEdges(StraightRoute(40.0, 0.0, 41));

        var result = WheelTrackRouteReconstructor.Reconstruct(front, rear, wheelbase, offset);

        Assert.Equal(WheelTrackReconstructionStatus.InvalidVehicleGeometry, result.Status);
        Assert.Empty(result.Samples);
    }

    [Fact]
    public void ShortRoutesFailTheCertificationSampleFloor()
    {
        // Two metres at 0.025 m spacing is 81 samples, far below the certification floor of 500.
        var (front, rear) = WheelEdges(StraightRoute(2.0, 0.0, 3));

        var result = WheelTrackRouteReconstructor.Reconstruct(front, rear, Wheelbase, OuterOffset);

        Assert.Equal(WheelTrackReconstructionStatus.InsufficientSamples, result.Status);
        Assert.Empty(result.Samples);
        Assert.Contains("500", result.Message);
    }
}
