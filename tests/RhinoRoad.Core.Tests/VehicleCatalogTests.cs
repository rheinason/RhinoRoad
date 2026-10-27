using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class VehicleCatalogTests
{
    [Fact]
    public void LoadsTheTraceableDanishPresets()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        Assert.Equal(10, catalog.Vehicles.Count);
        Assert.Equal(4.80, catalog.Get("PV").OverallLengthMetres, 6);
        Assert.Equal(9.62, catalog.Get("REN").OverallLengthMetres, 6);
        Assert.Equal(12.00, catalog.Get("BUS12").OverallLengthMetres, 6);
        Assert.Equal(16.50, catalog.Get("SVT").OverallLengthMetres, 6);
        Assert.Equal(18.75, catalog.Get("PVT").OverallLengthMetres, 6);
        Assert.Equal(12.00, catalog.Get("LV12").OverallLengthMetres, 6);
        Assert.Equal(13.70, catalog.Get("BUS13").OverallLengthMetres, 6);
        Assert.Equal(15.00, catalog.Get("BUS15").OverallLengthMetres, 6);
        Assert.Equal(22.00, catalog.Get("SK").OverallLengthMetres, 6);
        Assert.Equal(25.18, catalog.Get("MVT").OverallLengthMetres, 6);
        Assert.All(catalog.Vehicles, vehicle =>
        {
            Assert.Contains("A", vehicle.DrivingModes.Keys);
            Assert.Contains("B", vehicle.DrivingModes.Keys);
            Assert.False(string.IsNullOrWhiteSpace(vehicle.Source.Url));
            // Which presets are validated is decided by the stored certification report, not by a
            // constant here -- see PresetValidationStatusCannotOutrunTheStoredReport. This asserts
            // only that a preset never claims validation without recording why.
            Assert.NotEqual(ValidationStatus.ValidationFailed, vehicle.ValidationStatus);
            Assert.False(string.IsNullOrWhiteSpace(vehicle.ValidationNotes));
            if (vehicle.ValidationStatus == ValidationStatus.ReferenceValidated)
            {
                Assert.Contains("certification", vehicle.ValidationNotes, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public void UsesPublishedModeBSteeringLimits()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        Assert.Equal(36.0, catalog.Get("PV").DrivingModes["B"].MaximumWheelAngleDegrees);
        Assert.Equal(35.0, catalog.Get("REN").DrivingModes["B"].MaximumWheelAngleDegrees);
        Assert.Equal(45.0, catalog.Get("BUS12").DrivingModes["B"].MaximumWheelAngleDegrees);

        // Figure 6.7 prints 43 gon for both combinations, and 38 degrees beside it. The exact
        // conversion is what the official curve sheets were drawn to -- see
        // LegacyCurveAgreementTests.ThePvtLorryHoldsThePublishedFullLockArc -- so 43 gon it is.
        Assert.Equal(38.7, catalog.Get("SVT").DrivingModes["B"].MaximumWheelAngleDegrees, 6);
        Assert.Equal(38.7, catalog.Get("PVT").DrivingModes["B"].MaximumWheelAngleDegrees, 6);
        Assert.Equal(43.0, catalog.Get("SVT").DrivingModes["B"].MaximumWheelAngleDegrees / 0.9, 6);

        // The same exact-gon convention for the three added presets: 44, 46 and 59 gon.
        Assert.Equal(39.6, catalog.Get("LV12").DrivingModes["B"].MaximumWheelAngleDegrees, 6);
        Assert.Equal(41.4, catalog.Get("BUS13").DrivingModes["B"].MaximumWheelAngleDegrees, 6);
        Assert.Equal(53.1, catalog.Get("BUS15").DrivingModes["B"].MaximumWheelAngleDegrees, 6);

        // SK: Figure 6.7 prints no single lock, only "kørekurver findes for hjuldrejning 11,1/16,7
        // og 50 gon"; 50 gon is the one the official SKT B sheet for this 22 m vehicle is drawn at
        // -- see LegacyCurveAgreementTests.TheSkTractorHoldsTheFiftyGonArc.
        Assert.Equal(50.0, catalog.Get("SK").DrivingModes["B"].MaximumWheelAngleDegrees / 0.9, 6);

        // MVT: 43 gon in Figure 6.7, but the official MVT sheet states the 38 degrees it was drawn at,
        // where the SVT and PVT sheets imply the exact conversion -- see the preset notes.
        Assert.Equal(38.0, catalog.Get("MVT").DrivingModes["B"].MaximumWheelAngleDegrees, 6);

        Assert.All(catalog.Vehicles, vehicle =>
        {
            Assert.Equal(0.30, vehicle.DrivingModes["A"].DefaultClearanceMetres, 6);
            Assert.Equal(29.7, vehicle.DrivingModes["A"].MaximumWheelAngleDegrees, 6);
        });
    }

    /// <summary>
    /// Figures 6.3 and 6.5 keep special vehicles out of the 15 km/h rows "da der ønskes en lav
    /// svinghastighed for disse", so SK is the one preset whose mode A runs at 5 km/h.
    /// </summary>
    [Fact]
    public void SpecialVehiclesTurnSlowlyInModeA()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        foreach (var vehicle in catalog.Vehicles)
        {
            Assert.Equal(vehicle.Id == "SK" ? 5.0 : 15.0, vehicle.DrivingModes["A"].SpeedKilometresPerHour, 6);
            Assert.Equal(5.0, vehicle.DrivingModes["B"].SpeedKilometresPerHour, 6);
        }
    }

    [Fact]
    public void CarriesDwgMeasuredWheelGeometryForReferenceReconstruction()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        AssertWheelGeometry(catalog.Get("PV"), 1.579, 0.171, 0.704, 0.875);
        AssertWheelGeometry(catalog.Get("REN"), 2.246, 0.254, 0.996, 1.250);
        AssertWheelGeometry(catalog.Get("BUS12"), 2.196, 0.254, 0.971, 1.225);

        // SK's comes from the vector copy of its type-vehicle figure in SKT_B.dwg, every wheel the same.
        var sk = catalog.Get("SK");
        AssertWheelGeometry(sk, 2.196, 0.254, 0.971, 1.225);
        Assert.Equal(sk.AxleTrackMetres, Assert.Single(sk.TowedUnits).AxleTrackMetres, 6);
        Assert.Equal(sk.TyreWidthMetres, sk.TowedUnits[0].TyreWidthMetres, 6);
    }

    /// <summary>
    /// The published dimension chains, restated through the model that has to reproduce them. Both
    /// figures dimension a chain of axles rather than a wheelbase, so a preset that transcribes one
    /// number into the wrong link still totals the right overall length — these pin the links.
    /// </summary>
    [Fact]
    public void ArticulatedPresetsReproduceTheirPublishedDimensionChains()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        // SVT: 1,4 + 3,6 + 8,3 + 3,2 = 16,5, kingpin 0,6 ahead of the drive axle.
        var svt = catalog.Get("SVT");
        var semitrailer = Assert.Single(svt.TowedUnits);
        Assert.Equal(3.60, svt.WheelbaseMetres, 6);
        Assert.Equal(0.60, semitrailer.HitchOffsetMetres, 6);
        Assert.Equal(8.30, semitrailer.WheelbaseMetres - semitrailer.HitchOffsetMetres, 6);
        Assert.Equal(-8.30, svt.StraightAxleOffsetsMetres[1], 6);
        Assert.Equal(3.20, -semitrailer.BodyOutline.Min(point => point.X), 6);

        // PVT: 1,4 + 5,5 + 2,15, then 1,2 + 5,8 + 1,15, coupling 0,15 inside the lorry's tail.
        var pvt = catalog.Get("PVT");
        Assert.Equal(2, pvt.TowedUnits.Count);
        var drawbar = pvt.TowedUnits[0];
        var trailer = pvt.TowedUnits[1];
        Assert.Empty(drawbar.BodyOutline);
        Assert.Equal(0.15, drawbar.HitchOffsetMetres - pvt.BodyOutline.Min(point => point.X), 6);
        Assert.Equal(2.90, drawbar.WheelbaseMetres, 6);
        Assert.Equal(5.80, trailer.WheelbaseMetres, 6);
        // Trailer front axle to its nose is 1,2; that axle is the turntable the body pivots on.
        Assert.Equal(0.00, trailer.HitchOffsetMetres, 6);
        Assert.Equal(1.20, trailer.BodyOutline.Max(point => point.X) - trailer.WheelbaseMetres, 6);
        Assert.Equal(1.15, -trailer.BodyOutline.Min(point => point.X), 6);
        // The gap the figure leaves between lorry tail and trailer nose.
        Assert.Equal(
            1.55,
            pvt.BodyOutline.Min(point => point.X)
                - (pvt.StraightAxleOffsetsMetres[2] + trailer.BodyOutline.Max(point => point.X)),
            6);

        // SK: 1,2 + 4,8 + 13,4 + 2,6 = 22, kingpin 0,6 ahead of the tractor's tandem centre --
        // structurally SVT with a longer tractor and trailer.
        var sk = catalog.Get("SK");
        var skTrailer = Assert.Single(sk.TowedUnits);
        Assert.Equal(1.20, sk.BodyOutline.Max(point => point.X) - sk.WheelbaseMetres, 6);
        Assert.Equal(4.80, sk.WheelbaseMetres, 6);
        Assert.Equal(0.60, skTrailer.HitchOffsetMetres, 6);
        Assert.Equal(-13.40, sk.StraightAxleOffsetsMetres[1], 6);
        Assert.Equal(2.60, -skTrailer.BodyOutline.Min(point => point.X), 6);

        // MVT, a B-double, read off the official sheet's chain 1,42 + 3,19 + 8,48 + 7,78 + 4,31 =
        // 25,18: bumper, front axle, tractor fifth wheel, link fifth wheel, semitrailer axle, tail.
        var mvt = catalog.Get("MVT");
        Assert.Equal(2, mvt.TowedUnits.Count);
        var link = mvt.TowedUnits[0];
        var semitrailer2 = mvt.TowedUnits[1];
        var frontAxle = mvt.WheelbaseMetres;
        var tractorFifthWheel = link.HitchOffsetMetres;
        var linkFifthWheel = mvt.StraightAxleOffsetsMetres[1] + semitrailer2.HitchOffsetMetres;
        Assert.Equal(1.42, mvt.BodyOutline.Max(point => point.X) - frontAxle, 6);
        Assert.Equal(3.19, frontAxle - tractorFifthWheel, 6);
        Assert.Equal(8.48, tractorFifthWheel - linkFifthWheel, 6);
        Assert.Equal(7.78, linkFifthWheel - mvt.StraightAxleOffsetsMetres[2], 6);
        Assert.Equal(4.31, -semitrailer2.BodyOutline.Min(point => point.X), 6);
        // The figure's own links: 3,22 to the tractor tandem centre, the second fifth wheel 0,05
        // behind the rear of the link trailer's two axles, 1,82 apart.
        Assert.Equal(3.22, mvt.WheelbaseMetres, 6);
        Assert.Equal(0.05, -semitrailer2.HitchOffsetMetres - (1.82 / 2.0), 6);
        // The semitrailer nose is 1,5 ahead of its kingpin.
        Assert.Equal(1.50, semitrailer2.BodyOutline.Max(point => point.X) - semitrailer2.WheelbaseMetres, 6);
    }

    /// <summary>
    /// The official MVT curve sheet prints the radii its tractor turns on at each lock it was drawn
    /// at. They depend only on the tractor's wheelbase, front overhang and width, so they check the
    /// effective-axle choice -- the tandem centre, 3,22 m behind the front axle -- independently.
    /// </summary>
    [Theory]
    [InlineData(30.0, 8.28, 6.44)]
    [InlineData(38.0, 7.12, 5.23)]
    public void TheMvtTractorTurnsOnTheRadiiPrintedOnTheOfficialSheet(
        double lockDegrees, double frontCornerMetres, double frontAxleMetres)
    {
        var mvt = VehicleCatalog.LoadEmbedded().Get("MVT");
        var mode = new DrivingModeDefinition("sheet", "sheet", 5.0, lockDegrees, 6.0, 0.30);
        var turn = TurningGeometryCalculator.AtFullLock(mvt, mode);

        Assert.False(turn.SteadyStateAttainable, "the outer radius is then the tractor's alone");
        Assert.Equal(frontCornerMetres, turn.OuterRadiusMetres, 2);
        Assert.Equal(
            frontAxleMetres,
            Math.Sqrt((turn.RearAxleRadiusMetres * turn.RearAxleRadiusMetres) + (mvt.WheelbaseMetres * mvt.WheelbaseMetres)),
            2);
    }

    [Fact]
    public void RigidPresetsTowNothing()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        foreach (var id in new[] { "PV", "REN", "BUS12", "LV12", "BUS13", "BUS15" })
        {
            Assert.False(catalog.Get(id).IsArticulated);
            Assert.Empty(catalog.Get(id).TowedUnits);
        }
    }

    [Fact]
    public void TryGetFindsAPresetAndReportsAMissingVehicleWithoutThrowing()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        Assert.True(catalog.TryGet("pv", out var found));
        Assert.Equal("PV", found!.Id);
        Assert.False(catalog.TryGet("MY-LORRY", out var missing));
        Assert.Null(missing);
        Assert.False(catalog.TryGet(null, out _));
        // The message names the vehicle and the two ways out, since a saved road can outlive the
        // user-vehicle file it was drawn with.
        var message = VehicleCatalog.NotFoundMessage("MY-LORRY");
        Assert.Contains("MY-LORRY", message);
        Assert.Contains("RRImportVehicle", message);
        Assert.Contains("change the road's vehicle", message);
    }

    private static void AssertWheelGeometry(
        VehicleDefinition vehicle,
        double axleTrack,
        double tyreWidth,
        double innerEdge,
        double outerEdge)
    {
        Assert.Equal(axleTrack, vehicle.AxleTrackMetres, 6);
        Assert.Equal(tyreWidth, vehicle.TyreWidthMetres, 6);
        Assert.Equal(innerEdge, vehicle.RearWheelInnerEdgeOffsetMetres, 6);
        Assert.Equal(outerEdge, vehicle.WheelOuterEdgeOffsetMetres, 6);
    }

}
