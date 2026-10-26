namespace RhinoRoad.Core;

/// <summary>
/// What each reversing tool can do for a vehicle, asked by capability rather than by preset id, so
/// a preset added later — SK, a custom lorry — gets whatever its shape supports without a list
/// being edited somewhere.
/// </summary>
/// <remarks>
/// Every answer follows from <see cref="ReverseDocking.SolverFor"/>: a vehicle that gains a docking
/// solver there gains docking, a fully honoured PlanReverse and the area search together.
/// </remarks>
public static class ReverseCapabilities
{
    /// <summary>
    /// Whether a reverse carrying an exit heading arrives on the point <em>and</em> square to it. True
    /// for rigid vehicles and for combinations of one or two joints (SVT, PVT). When false — a chain
    /// of three joints or more — the reverse is aimed at the point and the leg reports
    /// <see cref="PlannedManoeuvreLeg.ExitHeadingUnavailable"/>.
    /// </summary>
    public static bool CanDock(VehicleDefinition vehicle) => ReverseDocking.CanDock(vehicle);

    /// <summary>
    /// Whether <see cref="ReversePlanner.Find"/> can plan a pull-up and reverse for this vehicle.
    /// True for every vehicle. Where <see cref="CanDock"/> is false a requested heading is not
    /// honoured: the planner aims at the point, judges arrival by position alone, and says so in
    /// <see cref="ReversePlanner.Proposal.HeadingHonoured"/>.
    /// </summary>
    public static bool CanPlanReverse(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return true;
    }

    /// <summary>
    /// Whether <see cref="ManoeuvringAreaSearch.Find"/> can search this vehicle's approaches. The
    /// search keeps the bay's position and heading fixed and accepts only journeys that arrive within
    /// the planner's tolerance of both, so it needs a vehicle that can be docked; for one that can
    /// only be aimed every candidate would fail on heading, and a search that relaxed it would be
    /// comparing journeys that end in different bays.
    /// </summary>
    public static bool CanSearchArea(VehicleDefinition vehicle) => CanDock(vehicle);
}
