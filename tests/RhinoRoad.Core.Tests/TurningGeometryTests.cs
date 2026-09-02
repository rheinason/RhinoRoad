using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class TurningGeometryTests
{
    [Theory]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    [InlineData("REN", "A")]
    [InlineData("REN", "B")]
    [InlineData("BUS12", "A")]
    [InlineData("BUS12", "B")]
    public void EveryPresetReportsAPhysicallyOrderedTurn(string vehicleId, string modeId)
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(vehicleId);
        var mode = vehicle.DrivingModes[modeId];

        var turn = TurningGeometryCalculator.AtFullLock(vehicle, mode);

        Assert.True(turn.RearAxleRadiusMetres > 0.0);
        Assert.True(
            turn.OuterRadiusMetres > turn.RearAxleRadiusMetres,
            $"{vehicleId}/{modeId}: the body must swing outside the path it steers");
        Assert.True(
            turn.InnerRadiusMetres < turn.RearAxleRadiusMetres,
            $"{vehicleId}/{modeId}: the inside of the body must cut inside the steered path");
        Assert.True(
            turn.SweptWidthMetres > vehicle.WidthMetres,
            $"{vehicleId}/{modeId}: a turn always needs more width than the vehicle is wide");
    }

    [Fact]
    public void RearAxleRadiusFollowsTheBicycleModel()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("REN");
        var mode = vehicle.DrivingModes["A"];

        var turn = TurningGeometryCalculator.AtFullLock(vehicle, mode);

        Assert.Equal(
            vehicle.WheelbaseMetres / Math.Tan(mode.MaximumWheelAngleRadians),
            turn.RearAxleRadiusMetres,
            9);
    }

    [Fact]
    public void ASharperModeTurnsTighterButSweepsWider()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("BUS12");
        var gentle = TurningGeometryCalculator.AtFullLock(vehicle, vehicle.DrivingModes["A"]);
        var sharp = TurningGeometryCalculator.AtFullLock(vehicle, vehicle.DrivingModes["B"]);

        // Mode B allows 45 degrees of lock against mode A's 29.7, so it turns in a smaller circle --
        // and pays for it with a wider swept band, which is what constrains the kerb line.
        Assert.True(sharp.RearAxleRadiusMetres < gentle.RearAxleRadiusMetres);
        Assert.True(sharp.SweptWidthMetres > gentle.SweptWidthMetres);
    }

    [Fact]
    public void OuterRadiusAccountsForTheFrontCornerNotJustTheCentreline()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("BUS12");
        var mode = vehicle.DrivingModes["A"];

        var turn = TurningGeometryCalculator.AtFullLock(vehicle, mode);

        // Ignoring corner swing would put the outer edge at radius + half width. The real corner
        // rides further out, and for a 12 m bus the difference is metres, not millimetres.
        var naive = turn.RearAxleRadiusMetres + (vehicle.WidthMetres * 0.5);
        Assert.True(
            turn.OuterRadiusMetres > naive + 1.0,
            $"corner swing {turn.OuterRadiusMetres - naive:0.00} m should dominate a naive half-width estimate");
    }
}
