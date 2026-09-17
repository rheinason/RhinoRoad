using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>A bay is picked by the trailer's rear and stored by its axle; the two must agree.</summary>
public sealed class TrailerBayTests
{
    private static readonly VehicleDefinition Svt = VehicleCatalog.LoadEmbedded().Get("SVT");

    [Fact]
    public void TheSvtTrailerRearIsItsPublishedOverhangBehindTheAxle()
    {
        Assert.Equal(3.2, TrailerBay.RearOverhangMetres(Svt), 9);
    }

    [Fact]
    public void ReversingSouthPutsTheAxleNorthOfTheRear()
    {
        var axle = TrailerBay.AxleFromRear(Svt, new Point3(50, -29, 0), -Math.PI / 2.0);

        Assert.Equal(50.0, axle.X, 9);
        Assert.Equal(-25.8, axle.Y, 9);
        var rear = TrailerBay.RearFromAxle(Svt, axle, -Math.PI / 2.0);
        Assert.Equal(0.0, new Point3(50, -29, 0).XY.DistanceTo(rear.XY), 9);
    }

    [Fact]
    public void TheDockedOutlineEndsAtThePickedRear()
    {
        var heading = 0.7;
        var rear = new Point3(12, 4, 0);
        var outline = TrailerBay.DockedOutline(Svt, TrailerBay.AxleFromRear(Svt, rear, heading), heading);
        var travel = new Point2(Math.Cos(heading), Math.Sin(heading));

        // The rear edge is the part of the body furthest along the reversing direction.
        var furthest = outline.Max(point => (point.X * travel.X) + (point.Y * travel.Y));
        Assert.Equal((rear.X * travel.X) + (rear.Y * travel.Y), furthest, 9);
    }

    [Fact]
    public void ASingleUnitVehicleHasNoTrailerBay()
    {
        var bus = VehicleCatalog.LoadEmbedded().Get("BUS12");

        Assert.Throws<ArgumentException>(() => TrailerBay.RearOverhangMetres(bus));
        Assert.Empty(TrailerBay.DockedOutline(bus, new Point3(0, 0, 0), 0.0));
    }
}
