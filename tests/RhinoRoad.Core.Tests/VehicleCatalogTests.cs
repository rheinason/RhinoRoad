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
            Assert.Equal(ValidationStatus.SourceTranscribed, vehicle.ValidationStatus);
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

    private static double TotalLength(VehicleDefinition vehicle) =>
        vehicle.FrontOverhangMetres + vehicle.WheelbaseMetres + vehicle.RearOverhangMetres;
}
