using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class VehicleCatalogTests
{
    [Fact]
    public void LoadsTheTraceableDanishPresets()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        Assert.Equal(8, catalog.Vehicles.Count);
        Assert.Equal(4.80, catalog.Get("PV").OverallLengthMetres, 6);
        Assert.Equal(9.62, catalog.Get("REN").OverallLengthMetres, 6);
        Assert.Equal(12.00, catalog.Get("BUS12").OverallLengthMetres, 6);
        Assert.Equal(16.50, catalog.Get("SVT").OverallLengthMetres, 6);
        Assert.Equal(18.75, catalog.Get("PVT").OverallLengthMetres, 6);
        Assert.Equal(12.00, catalog.Get("LV12").OverallLengthMetres, 6);
        Assert.Equal(13.70, catalog.Get("BUS13").OverallLengthMetres, 6);
        Assert.Equal(15.00, catalog.Get("BUS15").OverallLengthMetres, 6);
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
        Assert.All(catalog.Vehicles, vehicle =>
            Assert.Equal(0.30, vehicle.DrivingModes["A"].DefaultClearanceMetres, 6));
    }

    [Fact]
    public void CarriesDwgMeasuredWheelGeometryForReferenceReconstruction()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        AssertWheelGeometry(catalog.Get("PV"), 1.579, 0.171, 0.704, 0.875);
        AssertWheelGeometry(catalog.Get("REN"), 2.246, 0.254, 0.996, 1.250);
        AssertWheelGeometry(catalog.Get("BUS12"), 2.196, 0.254, 0.971, 1.225);
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
