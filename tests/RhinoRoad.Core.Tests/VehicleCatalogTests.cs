using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class VehicleCatalogTests
{
    [Fact]
    public void LoadsThreeTraceableDanishPresets()
    {
        var catalog = VehicleCatalog.LoadEmbedded();

        Assert.Equal(3, catalog.Vehicles.Count);
        Assert.Equal(4.80, TotalLength(catalog.Get("PV")), 6);
        Assert.Equal(9.62, TotalLength(catalog.Get("REN")), 6);
        Assert.Equal(12.00, TotalLength(catalog.Get("BUS12")), 6);
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

    private static double TotalLength(VehicleDefinition vehicle) =>
        vehicle.FrontOverhangMetres + vehicle.WheelbaseMetres + vehicle.RearOverhangMetres;
}
