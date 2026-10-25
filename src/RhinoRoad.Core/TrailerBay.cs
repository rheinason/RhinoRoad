namespace RhinoRoad.Core;

/// <summary>
/// Where a reversing trailer stops in a bay, said the way a site plan says it: by the trailer's
/// rearmost point and the direction it backs in along. Journeys store the trailer axle, which is what
/// the kinematics steer; this converts between the two.
/// </summary>
/// <remarks>
/// The articulated view of <see cref="ReverseBay"/>, which also places a rigid vehicle's bay by the
/// rear of its body. This one keeps refusing a vehicle with no trailer, for the callers that rely on
/// that to tell the two apart.
/// </remarks>
public static class TrailerBay
{
    /// <summary>Distance from the last towed unit's axle back to the rear of its body.</summary>
    public static double RearOverhangMetres(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (!vehicle.IsArticulated) throw new ArgumentException("A towed unit is required.", nameof(vehicle));
        return ReverseBay.RearOverhangMetres(vehicle);
    }

    /// <summary>
    /// The axle position for a trailer whose rear stops at <paramref name="rearMetres"/> after
    /// reversing along <paramref name="travelHeadingRadians"/>. The rear leads while reversing, so the
    /// axle sits behind it along the travel direction.
    /// </summary>
    public static Point3 AxleFromRear(VehicleDefinition vehicle, Point3 rearMetres, double travelHeadingRadians)
    {
        RearOverhangMetres(vehicle);
        return ReverseBay.AxleFromRear(vehicle, rearMetres, travelHeadingRadians);
    }

    /// <summary>The rearmost point of a trailer whose axle stops at <paramref name="axleMetres"/>.</summary>
    public static Point3 RearFromAxle(VehicleDefinition vehicle, Point3 axleMetres, double travelHeadingRadians)
    {
        RearOverhangMetres(vehicle);
        return ReverseBay.RearFromAxle(vehicle, axleMetres, travelHeadingRadians);
    }

    /// <summary>The trailer body outline as it stands docked, for drawing the bay being picked.</summary>
    public static IReadOnlyList<Point2> DockedOutline(
        VehicleDefinition vehicle, Point3 axleMetres, double travelHeadingRadians)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (!vehicle.IsArticulated) return [];
        return ReverseBay.DockedOutline(vehicle, axleMetres, travelHeadingRadians);
    }
}
