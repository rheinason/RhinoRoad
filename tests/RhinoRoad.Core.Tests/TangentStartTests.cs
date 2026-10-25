using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class TangentStartTests
{
    [Fact]
    public void Pick_near_the_end_starts_at_the_end_and_carries_on_past_it()
    {
        var placement = TangentStart.Place(90.0, 100.0, closed: false, atEnd: true);

        Assert.Equal(100.0, placement.LengthFromStartMetres);
        Assert.True(placement.AlongCurve);
    }

    [Fact]
    public void Pick_near_the_start_starts_there_and_leaves_against_the_curve()
    {
        var placement = TangentStart.Place(10.0, 100.0, closed: false, atEnd: true);

        Assert.Equal(0.0, placement.LengthFromStartMetres);
        Assert.False(placement.AlongCurve);
    }

    [Fact]
    public void Picked_point_option_starts_where_picked_heading_for_the_nearer_end()
    {
        Assert.Equal(new TangentStartPlacement(30.0, false), TangentStart.Place(30.0, 100.0, closed: false, atEnd: false));
        Assert.Equal(new TangentStartPlacement(70.0, true), TangentStart.Place(70.0, 100.0, closed: false, atEnd: false));
    }

    [Fact]
    public void Closed_curve_starts_where_picked_and_runs_its_own_way()
    {
        Assert.Equal(new TangentStartPlacement(10.0, true), TangentStart.Place(10.0, 100.0, closed: true, atEnd: true));
    }

    [Fact]
    public void Reverse_start_faces_back_along_the_way_it_travels()
    {
        Assert.Equal(0.5, TangentStart.VehicleHeading(0.5, TravelDirection.Forward), 12);
        Assert.Equal(0.5 - Math.PI, TangentStart.VehicleHeading(0.5, TravelDirection.Reverse), 12);
    }
}
