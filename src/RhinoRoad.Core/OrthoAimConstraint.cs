namespace RhinoRoad.Core;

/// <summary>
/// Snaps a picked point so the leg it asks for <em>leaves</em> on an ortho direction.
/// </summary>
/// <remarks>
/// <para>
/// Rhino's own ortho constrains the direction from the base point to the picked point, and here that
/// is the wrong quantity twice over. The vehicle does not travel towards the point — it drives an arc
/// through it and comes out turned by <em>twice</em> the bearing — so an ortho rubber band pointing
/// north leaves the vehicle heading east, and the aid actively misleads. Constraining the picked
/// point is not what the designer means; constraining where the vehicle ends up pointing is.
/// </para>
/// <para>
/// Because a leg turns by twice the bearing of its point, the two are the same constraint applied to
/// different quantities, and one projection serves the whole command: halve the wanted heading change
/// back into a bearing and move the cursor onto it, keeping its range. Everything downstream — the
/// aimed leg, a locked turn, the preview, the stored control — sees an ordinary picked point and needs
/// to know nothing about ortho. The stored control is the snapped one, so a saved journey replays on
/// the same axes without carrying a flag to say it was drawn with ortho on.
/// </para>
/// <para>
/// Range is deliberately taken from the point as picked, object snap included. Ortho says which way
/// to leave; how much room the manoeuvre is given stays with the cursor, because that is what decides
/// whether the vehicle can make the turn at all.
/// </para>
/// <para>
/// What this does not promise is that the vehicle arrives on the snapped heading. It promises the
/// <em>arc asked for</em> ends there, and the wheel takes metres to reach the lock that arc needs, so
/// a leg given too little room falls short — a BUS12 in mode A asked for 90 degrees over 15 m makes
/// 55 of them, and needs 60 m to make 88. That gap is the whole subject of this model and is
/// reported rather than hidden: the dotted ray
/// in the preview is the heading actually reached. Where an exact departure heading is the point, the
/// Finish control lands on one to within 0.01 degrees.
/// </para>
/// </remarks>
public static class OrthoAimConstraint
{
    /// <summary>
    /// Moves <paramref name="cursorMetres"/> onto the nearest bearing whose leg leaves on a multiple
    /// of <paramref name="orthoAngleRadians"/> from <paramref name="referenceHeadingRadians"/>.
    /// </summary>
    /// <param name="referenceHeadingRadians">
    /// The direction the ortho angles are counted from — the construction plane's X axis, as Rhino
    /// counts them, not the world's. A rotated CPlane is how a designer works on a site that is not
    /// square to the world, and squaring their manoeuvre to the world instead of to the plane they
    /// drew the site on would be square to nothing they can see.
    /// </param>
    public static Point2 Snap(
        VehicleState state,
        Point2 cursorMetres,
        double orthoAngleRadians,
        double referenceHeadingRadians)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (SnapExitHeading(state, cursorMetres, orthoAngleRadians, referenceHeadingRadians)
            is not double snappedExit)
        {
            return cursorMetres;
        }

        var origin = state.RearAxleCentreMetres.XY;
        var range = (cursorMetres - origin).Length;
        var movementHeading = state.VehicleHeadingRadians +
            (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var bearing = movementHeading + ((snappedExit - movementHeading) * 0.5);
        return origin + new Point2(Math.Cos(bearing) * range, Math.Sin(bearing) * range);
    }

    /// <summary>
    /// The ortho direction a picked point is asking the vehicle to leave on, or null where the point
    /// names no direction at all.
    /// </summary>
    /// <remarks>
    /// The heading is the constraint, and a leg that can carry it — an aimed one — is better given
    /// this than a moved cursor: it drives onto the direction exactly, and the click keeps its own
    /// position to say how far to run afterwards. A locked turn has no such field and takes the moved
    /// cursor instead, which costs it nothing, since a locked turn drives its sweep rather than aiming
    /// at it and so lands on the same direction either way.
    /// </remarks>
    public static double? SnapExitHeading(
        VehicleState state,
        Point2 cursorMetres,
        double orthoAngleRadians,
        double referenceHeadingRadians)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (orthoAngleRadians <= Geometry2D.Epsilon) return null;

        var offset = cursorMetres - state.RearAxleCentreMetres.XY;
        if (offset.Length <= Geometry2D.Epsilon) return null;

        var movementHeading = state.VehicleHeadingRadians +
            (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);

        // The heading this point asks the vehicle to leave on, then the nearest ortho direction to it.
        // Rounding the absolute heading rather than the turn is what makes successive legs compose:
        // every leg ends on an axis, so a route drawn with ortho held stays on the axes it started on
        // however many corners it takes, instead of accumulating whatever each turn happened to be.
        var wantedExit = movementHeading + (2.0 * Geometry2D.NormalizeAngle(
            Math.Atan2(offset.Y, offset.X) - movementHeading));
        var fromReference = wantedExit - referenceHeadingRadians;
        return referenceHeadingRadians
            + (Math.Round(fromReference / orthoAngleRadians) * orthoAngleRadians);
    }
}
