using System.Globalization;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class ManoeuvreSerializerTests
{
    private static Manoeuvre Sample() => new(
        "PV",
        "A",
        new Point3(12.3456789012345, -98.7654321098765, 3.25),
        0.7853981633974483,
        [
            new RouteWaypoint(new Point2(20.1, 6.2), TravelDirection.Forward),
            new RouteWaypoint(new Point2(-40.9, 20.0), TravelDirection.Reverse)
        ]);

    [Fact]
    public void AManoeuvreSurvivesTheRoundTripBitForBit()
    {
        var original = Sample();

        var restored = ManoeuvreSerializer.FromJson(ManoeuvreSerializer.ToJson(original));

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
    }

    /// <summary>
    /// The replayed route is only the recorded one if the stored coordinates are the recorded ones,
    /// so the round trip has to preserve every digit rather than a sensible number of them.
    /// </summary>
    [Fact]
    public void CoordinatesKeepFullPrecision()
    {
        var original = Sample();

        var restored = ManoeuvreSerializer.FromJson(ManoeuvreSerializer.ToJson(original))!;

        Assert.Equal(original.StartMetres.X, restored.StartMetres.X);
        Assert.Equal(original.StartHeadingRadians, restored.StartHeadingRadians);
        Assert.Equal(original.Waypoints[0].TargetMetres.X, restored.Waypoints[0].TargetMetres.X);
    }

    [Fact]
    public void ReplayingARestoredManoeuvreGivesTheSameRoute()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
        var mode = vehicle.DrivingModes["A"];
        var recorder = new RouteRecorder(vehicle, mode, new Point3(0, 0, 0), 0.3);
        recorder.Commit(new Point2(20.0, 6.0));
        recorder.Direction = TravelDirection.Reverse;
        recorder.Commit(new Point2(8.0, 2.0));

        var restored = ManoeuvreSerializer.FromJson(ManoeuvreSerializer.ToJson(recorder.ToManoeuvre()))!;

        Assert.Equal(recorder.Samples, PursuitDriver.Replay(vehicle, mode, restored).Samples);
    }

    /// <summary>A document written where the decimal separator is a comma must open where it is not.</summary>
    [Fact]
    public void TheFormatDoesNotDependOnTheMachinesCulture()
    {
        var original = Sample();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("da-DK");
            var json = ManoeuvreSerializer.ToJson(original);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            Assert.Equal(original, ManoeuvreSerializer.FromJson(json));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"v\":\"PV\"}")]
    [InlineData("{\"v\":\"PV\",\"m\":\"A\",\"s\":[\"0\",\"0\"],\"h\":\"0\",\"w\":[]}")]
    [InlineData("{\"v\":\"PV\",\"m\":\"A\",\"s\":[\"0\",\"0\",\"0\"],\"h\":\"0\",\"w\":[[\"x\",\"1\",\"F\"]]}")]
    public void UnusableTextIsRejectedRatherThanGuessedAt(string? json)
    {
        Assert.Null(ManoeuvreSerializer.FromJson(json));
    }

    [Fact]
    public void AManoeuvreWithNoWaypointsIsStillReadable()
    {
        var empty = new Manoeuvre("PV", "A", new Point3(1, 2, 3), 0.5, []);

        var restored = ManoeuvreSerializer.FromJson(ManoeuvreSerializer.ToJson(empty));

        Assert.NotNull(restored);
        Assert.Empty(restored.Waypoints);
    }
}
