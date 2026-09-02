namespace RhinoRoad.Core;

/// <summary>One point the vehicle was aimed at, and the direction it was driven to reach it.</summary>
public sealed record RouteWaypoint(Point2 TargetMetres, TravelDirection Direction);

/// <summary>
/// A driven route, stored as what was asked for rather than as what came out.
/// </summary>
/// <remarks>
/// The dense sample stream a manoeuvre produces is not editable — there is nothing to drag on a
/// polyline whose vertices are 0.10 m apart, and moving one would not leave a path the vehicle can
/// still drive. The waypoints are the short, meaningful thing: replaying them reproduces the route
/// exactly, so moving one and replaying is an edit that cannot produce an undrivable result.
/// </remarks>
public sealed record Manoeuvre(
    string VehicleId,
    string ModeId,
    Point3 StartMetres,
    double StartHeadingRadians,
    IReadOnlyList<RouteWaypoint> Waypoints)
{
    /// <summary>
    /// Value equality over the waypoints, not reference equality over the list holding them.
    /// </summary>
    /// <remarks>
    /// The compiler-generated equality compares <see cref="Waypoints"/> by reference, so a
    /// manoeuvre read back from a document never equals the one written to it however faithfully it
    /// round-tripped. For a type whose whole purpose is to be stored and compared, that is the
    /// wrong answer to the question being asked.
    /// </remarks>
    public bool Equals(Manoeuvre? other) =>
        other is not null
        && VehicleId == other.VehicleId
        && ModeId == other.ModeId
        && StartMetres.Equals(other.StartMetres)
        && StartHeadingRadians.Equals(other.StartHeadingRadians)
        && Waypoints.SequenceEqual(other.Waypoints);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(VehicleId);
        hash.Add(ModeId);
        hash.Add(StartMetres);
        hash.Add(StartHeadingRadians);
        foreach (var waypoint in Waypoints) hash.Add(waypoint);
        return hash.ToHashCode();
    }

    public VehicleState StartState => new(
        StartMetres,
        StartHeadingRadians,
        0.0,
        Waypoints.Count > 0 ? Waypoints[0].Direction : TravelDirection.Forward,
        0.0);
}
