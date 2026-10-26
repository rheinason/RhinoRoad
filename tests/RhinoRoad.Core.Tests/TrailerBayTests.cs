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

    /// <summary>
    /// A rigid vehicle's bay is picked the same way, by the rear of its body, and stored by its rear
    /// axle — the point the rigid reverse steers. The distance between them is the published rear
    /// overhang.
    /// </summary>
    [Theory]
    [InlineData("PV")]
    [InlineData("REN")]
    [InlineData("LV12")]
    [InlineData("BUS12")]
    [InlineData("BUS13")]
    [InlineData("BUS15")]
    public void ARigidBayIsTheRearOfTheBodyAndTheRearAxleBehindIt(string id)
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        var heading = -Math.PI / 2.0;
        var rear = new Point3(7, -12, 0);

        var axle = ReverseBay.AxleFromRear(vehicle, rear, heading);
        var outline = ReverseBay.DockedOutline(vehicle, axle, heading);
        var travel = new Point2(Math.Cos(heading), Math.Sin(heading));

        Assert.Equal(vehicle.RearOverhangMetres, ReverseBay.RearOverhangMetres(vehicle), 9);
        Assert.Equal(rear.Y + vehicle.RearOverhangMetres, axle.Y, 9);
        Assert.Equal(0.0, rear.XY.DistanceTo(ReverseBay.RearFromAxle(vehicle, axle, heading).XY), 9);
        Assert.Equal((rear.X * travel.X) + (rear.Y * travel.Y),
            outline.Max(point => (point.X * travel.X) + (point.Y * travel.Y)), 9);
    }

    [Fact]
    public void ForATrailerTheGeneralBayIsTheTrailerBay()
    {
        var rear = new Point3(50, -29, 0);

        Assert.Equal(TrailerBay.AxleFromRear(Svt, rear, 0.4), ReverseBay.AxleFromRear(Svt, rear, 0.4));
        Assert.Equal(TrailerBay.DockedOutline(Svt, rear, 0.4), ReverseBay.DockedOutline(Svt, rear, 0.4));
    }

    /// <summary>
    /// Where the docking body stands at the end of a route: the trailer, placed from the route's
    /// history, or for a rigid vehicle the end state itself.
    /// </summary>
    [Fact]
    public void TheDockedPoseIsTheTrailerForACombinationAndTheBodyForARigidVehicle()
    {
        var bus = VehicleCatalog.LoadEmbedded().Get("BUS12");
        var end = new VehicleState(new(3, 4, 0), 0.5, 0.0, TravelDirection.Reverse, 0.0);
        var busRoute = new[] { ManoeuvreReplayService.StateSample(end, bus) };
        Assert.Equal((new Point2(3, 4), 0.5), ReverseBay.DockedPose(bus, busRoute, end));
        var miss = ReverseBay.Miss(bus, busRoute, end, new Point2(3, 5), 0.5 + Math.PI);
        Assert.Equal(1.0, miss.PositionMetres, 9);
        Assert.Equal(0.0, miss.HeadingRadians!.Value, 9);

        var svtRoute = new[] { ManoeuvreReplayService.StateSample(end, Svt) };
        var (axle, facing) = ReverseBay.DockedPose(Svt, svtRoute, end);
        Assert.Equal(8.9 - 0.6, end.RearAxleCentreMetres.XY.DistanceTo(axle), 9);
        Assert.Equal(0.5, facing, 9);
    }
}
