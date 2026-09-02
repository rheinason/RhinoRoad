using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Puts a driven manoeuvre into the document as something that can be edited, and reads it back.
/// </summary>
/// <remarks>
/// <para>
/// The route itself is sampled every 0.10 m, which is the wrong thing to hand someone to edit: its
/// vertices carry no meaning and dragging one produces a path no vehicle can drive. What gets baked
/// alongside it is the control polyline — start point followed by each waypoint — whose grips are
/// exactly the decisions the designer made. Drag one, run the command again, and the route is
/// rebuilt by replaying the waypoints, so an edited path is drivable by construction.
/// </para>
/// <para>
/// Positions are read back from the curve rather than from the stored text, because the curve is
/// what the grip edit changed. The text carries what geometry cannot: the start heading, which way
/// each leg was driven, and which vehicle drove it.
/// </para>
/// </remarks>
internal static class ManoeuvreStore
{
    public const string ManoeuvreKey = "RhinoRoad.Manoeuvre";
    public const string SourceIdKey = "RhinoRoad.SourceId";
    public const string ControlCurveName = "Route control polyline";

    /// <summary>The control polyline for a manoeuvre, in model units.</summary>
    public static PolylineCurve ControlCurve(Manoeuvre manoeuvre, UnitSystem modelUnits)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var points = new List<Point3d>
        {
            new(
                manoeuvre.StartMetres.X * scale,
                manoeuvre.StartMetres.Y * scale,
                manoeuvre.StartMetres.Z * scale)
        };
        points.AddRange(manoeuvre.Waypoints.Select(waypoint => new Point3d(
            waypoint.TargetMetres.X * scale,
            waypoint.TargetMetres.Y * scale,
            manoeuvre.StartMetres.Z * scale)));
        return new PolylineCurve(points);
    }

    /// <summary>True when this object carries a manoeuvre and is therefore re-runnable.</summary>
    public static bool IsControlCurve(RhinoObject rhinoObject) =>
        !string.IsNullOrEmpty(rhinoObject.Attributes.GetUserString(ManoeuvreKey));

    /// <summary>
    /// Rebuilds the manoeuvre from an object in the document, honouring any grip edits to the curve.
    /// </summary>
    public static Manoeuvre? Read(RhinoObject rhinoObject, UnitSystem modelUnits)
    {
        var stored = ManoeuvreSerializer.FromJson(rhinoObject.Attributes.GetUserString(ManoeuvreKey));
        if (stored is null) return null;
        if (rhinoObject.Geometry is not Curve curve) return null;
        if (!curve.TryGetPolyline(out var polyline) || polyline.Count < 2) return stored;

        var metresPerModelUnit = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        var start = new Point3(
            polyline[0].X * metresPerModelUnit,
            polyline[0].Y * metresPerModelUnit,
            polyline[0].Z * metresPerModelUnit);

        // A grip edit moves points; adding or removing one changes how many there are. Directions
        // are matched by position in the sequence, and any waypoint beyond what was stored is a new
        // one, driven the way the last stored leg was.
        var waypoints = new List<RouteWaypoint>(polyline.Count - 1);
        var fallback = stored.Waypoints.Count > 0
            ? stored.Waypoints[^1].Direction
            : TravelDirection.Forward;
        for (var index = 1; index < polyline.Count; index++)
        {
            var direction = index - 1 < stored.Waypoints.Count
                ? stored.Waypoints[index - 1].Direction
                : fallback;
            waypoints.Add(new RouteWaypoint(
                new Point2(polyline[index].X * metresPerModelUnit, polyline[index].Y * metresPerModelUnit),
                direction));
        }

        return stored with { StartMetres = start, Waypoints = waypoints };
    }

    /// <summary>The stable identity a re-run replaces its previous output through.</summary>
    public static Guid SourceId(RhinoObject rhinoObject) =>
        Guid.TryParse(rhinoObject.Attributes.GetUserString(SourceIdKey), out var id) ? id : rhinoObject.Id;
}
