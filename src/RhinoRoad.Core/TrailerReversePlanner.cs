namespace RhinoRoad.Core;

/// <summary>
/// The planner under its original name, from when only a semitrailer could be planned. It now
/// plans any vehicle, through <see cref="ReversePlanner"/>, and is kept so existing callers compile
/// unchanged; new code uses <see cref="ReversePlanner"/> directly.
/// </summary>
public static class TrailerReversePlanner
{
    /// <summary>How close the docking axle must stop to the bay for the approach to count as reached.</summary>
    public const double ArrivalToleranceMetres = ReversePlanner.ArrivalToleranceMetres;

    /// <summary>How square, in degrees, the docking body must stop to a requested bay direction.</summary>
    public const double ArrivalToleranceDegrees = ReversePlanner.ArrivalToleranceDegrees;

    /// <param name="TrailerMissMetres">
    /// How far the docking axle stopped from the bay — the trailer's for a combination, the rear
    /// axle's for a rigid vehicle.
    /// </param>
    public sealed record Proposal(
        IReadOnlyList<ManoeuvreControl> Controls,
        PlannedManoeuvreLeg Leg,
        double TrailerMissMetres,
        double? HeadingMissRadians,
        double MaximumFoldRadians,
        double PullUpMetres,
        bool ReachedTarget)
    {
        /// <inheritdoc cref="ReversePlanner.Proposal.HeadingHonoured"/>
        public bool HeadingHonoured { get; init; } = true;

        public static Proposal From(ReversePlanner.Proposal proposal)
        {
            ArgumentNullException.ThrowIfNull(proposal);
            return new Proposal(proposal.Controls, proposal.Leg, proposal.MissMetres,
                proposal.HeadingMissRadians, proposal.MaximumFoldRadians, proposal.PullUpMetres,
                proposal.ReachedTarget)
            {
                HeadingHonoured = proposal.HeadingHonoured
            };
        }
    }

    /// <inheritdoc cref="ReversePlanner.Find"/>
    public static Proposal Find(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<RouteSample> route,
        int legStartIndex,
        VehicleState state,
        Point3 trailerTarget,
        double? exitMovementHeadingRadians = null) =>
        Proposal.From(ReversePlanner.Find(
            vehicle, mode, route, legStartIndex, state, trailerTarget, exitMovementHeadingRadians));
}
