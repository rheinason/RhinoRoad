namespace RhinoRoad.Core;

/// <summary>Where on a curve a journey starts, as a length along it, and which way it sets off.</summary>
/// <param name="LengthFromStartMetres">Arc length from the curve's start to the start of the journey.</param>
/// <param name="AlongCurve">
/// Whether the journey travels the way the curve runs. False is against it, off the curve's start.
/// </param>
public sealed record TangentStartPlacement(double LengthFromStartMetres, bool AlongCurve);

/// <summary>
/// Starting a journey tangent to existing geometry, which is the usual case when it continues a road
/// centreline into a site.
/// </summary>
/// <remarks>
/// The pick says which end is meant, so the journey leaves the curve at the end nearer the pick and
/// carries on past it -- off a road's end and into whatever the road was leading to. Picked part-way
/// along with <c>atEnd</c> off, the vehicle stands where the curve was picked and heads towards that
/// same end, which is the way the designer was pointing by choosing it. A closed curve has no ends,
/// so it is started where picked and driven the way it runs.
/// </remarks>
public static class TangentStart
{
    public static TangentStartPlacement Place(double pickedLengthMetres, double curveLengthMetres, bool closed, bool atEnd)
    {
        if (!double.IsFinite(curveLengthMetres) || curveLengthMetres <= 0.0)
            throw new ArgumentException("A curve with length is required.", nameof(curveLengthMetres));
        var picked = Math.Clamp(double.IsFinite(pickedLengthMetres) ? pickedLengthMetres : 0.0, 0.0, curveLengthMetres);
        if (closed) return new TangentStartPlacement(picked, true);
        var nearEnd = picked > curveLengthMetres * 0.5;
        return new TangentStartPlacement(atEnd ? (nearEnd ? curveLengthMetres : 0.0) : picked, nearEnd);
    }

    /// <summary>
    /// The vehicle heading for a journey whose travel follows <paramref name="travelHeadingRadians"/>.
    /// Starting in reverse, the vehicle faces back along the road it is backing out of.
    /// </summary>
    public static double VehicleHeading(double travelHeadingRadians, TravelDirection direction) =>
        Geometry2D.NormalizeAngle(travelHeadingRadians + (direction == TravelDirection.Reverse ? Math.PI : 0.0));
}
