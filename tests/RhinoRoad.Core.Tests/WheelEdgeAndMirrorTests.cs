using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class WheelEdgeAndMirrorTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();
    private readonly VehicleAccessAnalyzer _analyzer = new();

    private static IReadOnlyList<RouteSample> Straight(double length)
    {
        var samples = new List<RouteSample>();
        for (var station = 0.0; station <= length + 1e-9; station += 0.25)
            samples.Add(new RouteSample(station, new Point3(station, 0.0, 0.0), 0.0, 0.0, TravelDirection.Forward));
        return samples;
    }

    /// <summary>A left-hand circle through the origin, centred at (0, radius).</summary>
    private static IReadOnlyList<RouteSample> Circle(double radius, double sweep)
    {
        var samples = new List<RouteSample>();
        for (var station = 0.0; station <= (radius * sweep) + 1e-9; station += 0.25)
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

    private VehicleAccessResult Run(VehicleDefinition vehicle, IReadOnlyList<RouteSample> route) =>
        _analyzer.Analyze(vehicle, vehicle.DrivingModes.Values.First(), route);

    private static WheelEdgeTrace Trace(IReadOnlyList<WheelEdgeTrace> traces, string name) =>
        traces.Single(trace => trace.Name == name);

    [Fact]
    public void Rigid_vehicle_wheel_edges_sit_beside_the_axle_tracks_on_a_straight()
    {
        var vehicle = Catalog.Get("PV");
        var result = Run(vehicle, Straight(10.0));

        var traces = WheelEdgeTracks.Compute(result);

        Assert.Equal(4, traces.Count);
        var frontLeft = Trace(traces, "Front wheel outer edge (left)").PointsMetres;
        var rearRight = Trace(traces, "Rear wheel inner edge (right)").PointsMetres;
        Assert.Equal(result.Poses.Count, frontLeft.Count);
        for (var i = 0; i < frontLeft.Count; i++)
        {
            Assert.Equal(result.FrontAxleTrackMetres[i].Y + vehicle.WheelOuterEdgeOffsetMetres, frontLeft[i].Y, 9);
            Assert.Equal(result.FrontAxleTrackMetres[i].X, frontLeft[i].X, 9);
            Assert.Equal(result.RearAxleTrackMetres[i].Y - vehicle.RearWheelInnerEdgeOffsetMetres, rearRight[i].Y, 9);
        }
    }

    [Fact]
    public void Rigid_vehicle_wheel_edges_follow_the_circle_radii()
    {
        var vehicle = Catalog.Get("PV");
        const double radius = 12.0;
        var result = Run(vehicle, Circle(radius, Math.PI / 2.0));
        var traces = WheelEdgeTracks.Compute(result);
        var centre = new Point2(0.0, radius);

        // The rear axle stays perpendicular to the radius, so its wheel edges sit exactly an offset
        // inside and outside the rear-axle circle; the front axle is a wheelbase out along the heading.
        foreach (var point in Trace(traces, "Rear wheel inner edge (left)").PointsMetres)
            Assert.Equal(radius - vehicle.RearWheelInnerEdgeOffsetMetres, point.DistanceTo(centre), 6);
        foreach (var point in Trace(traces, "Rear wheel inner edge (right)").PointsMetres)
            Assert.Equal(radius + vehicle.RearWheelInnerEdgeOffsetMetres, point.DistanceTo(centre), 6);

        var leftFront = Math.Sqrt((vehicle.WheelbaseMetres * vehicle.WheelbaseMetres) +
            Math.Pow(radius - vehicle.WheelOuterEdgeOffsetMetres, 2));
        foreach (var point in Trace(traces, "Front wheel outer edge (left)").PointsMetres)
            Assert.Equal(leftFront, point.DistanceTo(centre), 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Semitrailer_axle_wheel_edges_are_perpendicular_offsets_from_its_axle(bool circle)
    {
        var vehicle = Catalog.Get("SVT");
        var result = Run(vehicle, circle ? Circle(15.0, Math.PI / 2.0) : Straight(30.0));
        var traces = WheelEdgeTracks.Compute(result);
        var unit = vehicle.TowedUnits[0];

        Assert.Equal(8, traces.Count);
        var outerLeft = Trace(traces, $"{unit.Name} wheel outer edge (left)").PointsMetres;
        var innerRight = Trace(traces, $"{unit.Name} wheel inner edge (right)").PointsMetres;
        for (var i = 0; i < result.Poses.Count; i++)
        {
            var towed = result.Poses[i].TowedUnits[0];
            var left = new Point2(-Math.Sin(towed.HeadingRadians), Math.Cos(towed.HeadingRadians));
            var outer = outerLeft[i] - towed.AxleCentreMetres;
            var inner = innerRight[i] - towed.AxleCentreMetres;
            Assert.Equal(unit.WheelOuterEdgeOffsetMetres, (outer.X * left.X) + (outer.Y * left.Y), 9);
            Assert.Equal(-unit.RearWheelInnerEdgeOffsetMetres, (inner.X * left.X) + (inner.Y * left.Y), 9);
            Assert.Equal(unit.WheelOuterEdgeOffsetMetres, Math.Sqrt((outer.X * outer.X) + (outer.Y * outer.Y)), 9);
        }
    }

    [Theory]
    [InlineData("PV")]
    [InlineData("SVT")]
    public void Mirror_allowance_widens_the_swept_envelope_by_the_published_width_on_a_straight(string id)
    {
        var vehicle = Catalog.Get(id);
        var route = Straight(20.0);

        var plain = SweptRegionBuilder.FromPoses(Run(vehicle, route).Poses).Region!;
        var withMirrors = SweptRegionBuilder.FromPoses(Run(MirrorAllowance.Apply(vehicle), route).Poses).Region!;

        double Width(SweptRegion region) =>
            region.OuterBoundary.Max(point => point.Y) - region.OuterBoundary.Min(point => point.Y);
        // The lead body is the widest unit for these presets, so each side gains the full allowance.
        var leadHalf = vehicle.BodyOutline.Max(point => Math.Abs(point.Y));
        var widest = Math.Max(leadHalf, vehicle.TowedUnits.Select(unit => unit.BodyOutline.Count == 0 ? 0.0 : unit.BodyOutline.Max(p => Math.Abs(p.Y))).DefaultIfEmpty(0.0).Max());
        var expected = Width(plain) + (2.0 * Math.Max(0.0, leadHalf + MirrorAllowance.WidthMetres - widest));
        Assert.Equal(expected, Width(withMirrors), 6);
        Assert.True(withMirrors.AreaSquareMetres > plain.AreaSquareMetres);
    }

    [Fact]
    public void Mirror_allowance_adds_exactly_the_strip_beyond_the_body_on_a_straight()
    {
        var vehicle = Catalog.Get("PV");
        var route = Straight(20.0);
        var plain = SweptRegionBuilder.FromPoses(Run(vehicle, route).Poses).Region!;
        var withMirrors = SweptRegionBuilder.FromPoses(Run(MirrorAllowance.Apply(vehicle), route).Poses).Region!;

        // Each side: a 0.30 m strip, as long as the run plus the 1.0 m the allowance covers.
        var added = 2.0 * MirrorAllowance.WidthMetres * (20.0 + MirrorAllowance.FrontLengthMetres);
        // Loose: the envelope is simplified to a small tolerance, which shaves the stepped corners.
        Assert.InRange(withMirrors.AreaSquareMetres - plain.AreaSquareMetres, added - 0.1, added + 0.1);
    }

    [Fact]
    public void Mirror_allowance_leaves_the_original_vehicle_unchanged_and_default_has_none()
    {
        var vehicle = Catalog.Get("PV");
        var with = MirrorAllowance.Apply(vehicle);

        Assert.Empty(vehicle.Protrusions);
        Assert.Equal(2, with.Protrusions.Count);
        Assert.Empty(Run(vehicle, Straight(5.0)).Poses[0].ProtrusionOutlinesWorldMetres);
        Assert.Equal(3, Run(with, Straight(5.0)).Poses[0].OccupiedOutlinesWorldMetres.Count);
    }

    [Fact]
    public void Catalog_reads_optional_protrusions_for_a_unit()
    {
        const string json = """
            {"vehicles":[{"id":"T","name":"T","version":"1","widthMetres":2,"wheelbaseMetres":3,
             "frontOverhangMetres":1,"rearOverhangMetres":1,"axleTrackMetres":1.6,"tyreWidthMetres":0.3,
             "bodyOutline":[[-1,-1],[4,-1],[4,1],[-1,1]],
             "protrusions":[{"id":"m","name":"Mirror","outline":[[3,1],[3.2,1],[3.2,1.3],[3,1.3]],
               "minHeightMetres":1.5,"maxHeightMetres":2.2}],
             "drivingModes":{"A":{"name":"A","speedKilometresPerHour":10,"maximumWheelAngleDegrees":30,
               "lockToLockSeconds":5,"defaultClearanceMetres":0.3}},
             "validationNotes":""}]}
            """;
        var catalog = VehicleCatalog.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));

        var protrusion = Assert.Single(catalog.Get("T").Protrusions);
        Assert.Equal(1.5, protrusion.MinimumHeightMetres);
        Assert.Equal(2.2, protrusion.MaximumHeightMetres);
        var pose = Run(catalog.Get("T"), Straight(3.0)).Poses[0];
        Assert.Equal(2, pose.OccupiedOutlinesWorldMetres.Count);
    }
}
