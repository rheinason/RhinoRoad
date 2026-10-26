namespace RhinoRoad.Core;

/// <summary>
/// Where a reversing vehicle stops in a bay, said the way a site plan says it: by the rear of the
/// body that backs in and the direction it backs in along. Journeys store that body's axle, which is
/// what the kinematics steer; this converts between the two for every kind of vehicle.
/// </summary>
/// <remarks>
/// <para>
/// The body that docks is the last towed unit of a combination — the trailer, whose axle the
/// reverse controller places — and the vehicle itself when it is rigid. A rigid vehicle reversing
/// has no lateral slip at its rear axle, so the rear-axle midpoint is the stable reference the
/// controller follows, exactly as the trailer axle is for a combination; and its body outline is
/// already measured from that axle, so the rear of the body is its rear overhang behind it.
/// </para>
/// <para>
/// <see cref="TrailerBay"/> is the articulated-only view of the same thing and is kept for the
/// callers that use it.
/// </para>
/// </remarks>
public static class ReverseBay
{
    /// <summary>The outline of the body that backs into the bay, in its own axle frame.</summary>
    public static IReadOnlyList<Point2> DockedBodyOutline(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return vehicle.IsArticulated ? vehicle.TowedUnits[^1].BodyOutline : vehicle.BodyOutline;
    }

    /// <summary>
    /// Distance from the docking body's axle back to the rear of that body: the trailer's overhang
    /// behind its axle, or a rigid vehicle's behind its rear axle.
    /// </summary>
    public static double RearOverhangMetres(VehicleDefinition vehicle)
    {
        var outline = DockedBodyOutline(vehicle);
        return outline.Count == 0 ? 0.0 : Math.Max(0.0, -outline.Min(point => point.X));
    }

    /// <summary>
    /// The axle position for a body whose rear stops at <paramref name="rearMetres"/> after
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

    /// <summary>The rearmost point of a docking body whose axle stops at <paramref name="axleMetres"/>.</summary>
    public static Point3 RearFromAxle(VehicleDefinition vehicle, Point3 axleMetres, double travelHeadingRadians)
    {
        var overhang = RearOverhangMetres(vehicle);
        return new Point3(
            axleMetres.X + (Math.Cos(travelHeadingRadians) * overhang),
            axleMetres.Y + (Math.Sin(travelHeadingRadians) * overhang),
            axleMetres.Z);
    }

    /// <summary>The docking body's outline as it stands in the bay, for drawing the bay being picked.</summary>
    public static IReadOnlyList<Point2> DockedOutline(
        VehicleDefinition vehicle, Point3 axleMetres, double travelHeadingRadians)
    {
        // The body faces away from the way it reverses.
        var facing = Geometry2D.NormalizeAngle(travelHeadingRadians + Math.PI);
        return DockedBodyOutline(vehicle)
            .Select(point => Geometry2D.Transform(point, axleMetres.XY, facing))
            .ToArray();
    }

    /// <summary>
    /// Where the docking body's axle stands, and which way the body faces, at the end of
    /// <paramref name="route"/>. For a combination that depends on the whole route, because the fold
    /// does; for a rigid vehicle it is the end state itself.
    /// </summary>
    public static (Point2 AxleCentreMetres, double FacingHeadingRadians) DockedPose(
        VehicleDefinition vehicle, IReadOnlyList<RouteSample> route, VehicleState endState)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(endState);
        if (!vehicle.IsArticulated) return (endState.RearAxleCentreMetres.XY, endState.VehicleHeadingRadians);

        var chain = ArticulationTrace.AtEndOf(vehicle, route)
            ?? ArticulationChain.StartAligned(vehicle, endState.VehicleHeadingRadians);
        var pose = chain.Poses(endState.RearAxleCentreMetres.XY, endState.VehicleHeadingRadians)[^1];
        return (pose.AxleCentreMetres, pose.HeadingRadians);
    }

    /// <summary>
    /// How far the docking body's axle stopped from <paramref name="axleTargetMetres"/>, and how far
    /// off square it stands to <paramref name="exitMovementHeadingRadians"/> — null when no heading
    /// was asked for. The heading is a direction of travel, as everywhere else, so a body backed
    /// square onto it faces the opposite way.
    /// </summary>
    public static (double PositionMetres, double? HeadingRadians) Miss(
        VehicleDefinition vehicle,
        IReadOnlyList<RouteSample> route,
        VehicleState endState,
        Point2 axleTargetMetres,
        double? exitMovementHeadingRadians)
    {
        var (axle, facing) = DockedPose(vehicle, route, endState);
        var heading = exitMovementHeadingRadians is double exit
            ? Math.Abs(Geometry2D.NormalizeAngle(facing - exit - Math.PI))
            : (double?)null;
        return (axle.DistanceTo(axleTargetMetres), heading);
    }
}
