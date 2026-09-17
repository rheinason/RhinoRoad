namespace RhinoRoad.Core;

/// <summary>
/// Where a reversing trailer stops in a bay, said the way a site plan says it: by the trailer's
/// rearmost point and the direction it backs in along. Journeys store the trailer axle, which is what
/// the kinematics steer; this converts between the two.
/// </summary>
public static class TrailerBay
{
    /// <summary>Distance from the last towed unit's axle back to the rear of its body.</summary>
    public static double RearOverhangMetres(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (!vehicle.IsArticulated) throw new ArgumentException("A towed unit is required.", nameof(vehicle));
        var outline = vehicle.TowedUnits[^1].BodyOutline;
        return outline.Count == 0 ? 0.0 : Math.Max(0.0, -outline.Min(point => point.X));
    }

    /// <summary>
    /// The axle position for a trailer whose rear stops at <paramref name="rearMetres"/> after
    /// reversing along <paramref name="travelHeadingRadians"/>. The rear leads while reversing, so the
    /// axle sits behind it along the travel direction.
    /// </summary>
    public static Point3 AxleFromRear(VehicleDefinition vehicle, Point3 rearMetres, double travelHeadingRadians)
    {
        var overhang = RearOverhangMetres(vehicle);
        return new Point3(
            rearMetres.X - (Math.Cos(travelHeadingRadians) * overhang),
            rearMetres.Y - (Math.Sin(travelHeadingRadians) * overhang),
            rearMetres.Z);
    }

    /// <summary>The rearmost point of a trailer whose axle stops at <paramref name="axleMetres"/>.</summary>
    public static Point3 RearFromAxle(VehicleDefinition vehicle, Point3 axleMetres, double travelHeadingRadians)
    {
        var overhang = RearOverhangMetres(vehicle);
        return new Point3(
            axleMetres.X + (Math.Cos(travelHeadingRadians) * overhang),
            axleMetres.Y + (Math.Sin(travelHeadingRadians) * overhang),
            axleMetres.Z);
    }

    /// <summary>The trailer body outline as it stands docked, for drawing the bay being picked.</summary>
    public static IReadOnlyList<Point2> DockedOutline(
        VehicleDefinition vehicle, Point3 axleMetres, double travelHeadingRadians)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (!vehicle.IsArticulated) return [];
        // The trailer faces away from the way it reverses.
        var facing = Geometry2D.NormalizeAngle(travelHeadingRadians + Math.PI);
        return vehicle.TowedUnits[^1].BodyOutline
            .Select(point => Geometry2D.Transform(point, axleMetres.XY, facing))
            .ToArray();
    }
}
