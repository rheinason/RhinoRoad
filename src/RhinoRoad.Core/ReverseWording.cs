namespace RhinoRoad.Core;

/// <summary>
/// How the reversing tools name the body that backs into a bay and say what went wrong with it, so
/// a prompt never tells someone driving a refuse lorry to pick the rear of a trailer it does not
/// have. Kept out of the Rhino project because which words apply is decided by the same
/// capabilities that decide what the tools do.
/// </summary>
public static class ReverseWording
{
    /// <summary>
    /// Said for a leg that reports <see cref="PlannedManoeuvreLeg.ExitHeadingUnavailable"/>. Rigid
    /// vehicles and chains of one or two joints are docked (see <see cref="ReverseCapabilities.CanDock"/>),
    /// so only a longer chain ever reports it.
    /// </summary>
    public const string ExitHeadingUnavailable =
        "A chain of three or more joints cannot be squared onto a direction in reverse — the last trailer is aimed at the point instead";

    /// <summary>
    /// The body that backs into a bay: the trailer for a combination -- its last towed unit -- and the
    /// vehicle itself when it is rigid, as <see cref="ReverseBay"/> places them.
    /// </summary>
    public static string DockingBody(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return vehicle.IsArticulated ? "trailer" : "vehicle";
    }

    /// <summary>"the rear of the trailer" or "the rear of the vehicle": the point a bay is picked by.</summary>
    public static string BayRear(VehicleDefinition vehicle) => $"the rear of the {DockingBody(vehicle)}";

    /// <summary>
    /// The axle a journey stores for a bay, and the one a miss is measured at: the trailer axle for a
    /// combination, the rear axle for a rigid vehicle.
    /// </summary>
    public static string DockingAxle(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return vehicle.IsArticulated ? "trailer axle" : "rear axle";
    }

    /// <summary>
    /// What to say while previewing a leg that did not get where it was asked to. Reversing a
    /// combination, the point is where the trailer should end up, and a rigid vehicle reversing into
    /// a bay has to arrive on it square, so in both cases the wheel may be nowhere near its lock and
    /// "steering limit" would be the wrong thing to say.
    /// </summary>
    public static string ShortfallWarning(VehicleDefinition vehicle, ManoeuvreControl control)
    {
        if (!InteractiveCapabilities.PlacesDockingBody(vehicle, control)) return "Steering limit reached — adjust your aim";
        return vehicle.IsArticulated
            ? "Trailer cannot reach that point from here — pull farther forward or change the turn-away point"
            : "Cannot back square into that bay from here — pull farther forward or change the turn-away point";
    }

    /// <summary>
    /// The command-line line once such a leg is committed, with how far the docking axle stopped from
    /// the point. Null when the shortfall was the steering lock, which has its own message.
    /// </summary>
    public static string? CommittedShortfall(VehicleDefinition vehicle, ManoeuvreControl control, double missMetres)
    {
        if (!InteractiveCapabilities.PlacesDockingBody(vehicle, control)) return null;
        if (!vehicle.IsArticulated)
            return $"Cannot back square into that bay from here: the rear axle stopped {missMetres:0.00} m from it "
                + "(or not square to it). Pull farther forward or change the turn-away point.";
        var target = control.ExitHeadingRadians.HasValue ? "the bay" : "the picked point";
        return $"Trailer stopped {missMetres:0.00} m from {target} (or not square to it); pull farther forward "
            + "or move the turn-away point to improve the approach.";
    }

    /// <summary>
    /// What changing into reverse means for the next pick, or null travelling forward. A combination's
    /// point is its trailer axle; a rigid vehicle's stays its rear axle; and either can pick a bay by
    /// its rear with Direction where it can be squared onto one.
    /// </summary>
    public static string? ReverseGuidance(VehicleDefinition vehicle, TravelDirection direction)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (direction != TravelDirection.Reverse) return null;
        var point = $"Pick where the {DockingAxle(vehicle)} should stop";
        var bay = InteractiveCapabilities.DockingAvailable(vehicle)
            ? $", or choose Direction to pick {BayRear(vehicle)} at the bay and the way it reverses in"
            : string.Empty;
        return point + bay + "; the preview shows the full reverse sweep.";
    }

    /// <summary>The one-line summary a planned reverse is reviewed by before it is accepted.</summary>
    public static string ProposalSummary(VehicleDefinition vehicle, ReversePlanner.Proposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var heading = proposal.HeadingMissRadians is not double miss ? string.Empty
            : proposal.HeadingHonoured ? $", heading miss {miss * 180.0 / Math.PI:0.#} deg"
            : ", heading not honoured (aimed at the point)";
        return $"Forward travel {proposal.PullUpMetres:0.#} m; {DockingAxle(vehicle)} miss {proposal.MissMetres:0.00} m"
            + heading;
    }

    /// <summary>What is said once a planned reverse has been accepted.</summary>
    public static string ProposalCommitted(VehicleDefinition vehicle, ReversePlanner.Proposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var notHonoured = proposal.HeadingHonoured ? string.Empty
            : " The bay direction was not honoured: this combination is aimed at the point rather than squared onto it.";
        if (proposal.ReachedTarget) return "Planned forward pull-up and reverse committed." + notHonoured;
        var asked = proposal.HeadingMissRadians.HasValue && proposal.HeadingHonoured
            ? "requested position and heading" : "requested position";
        return $"Closest drivable reverse committed; the {DockingBody(vehicle)} did not reach the {asked}." + notHonoured;
    }
}
