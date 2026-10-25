namespace RhinoRoad.Core;

/// <summary>
/// Drives a reversing leg onto a bay: the docking body's axle to a point and square to a direction.
/// </summary>
/// <param name="chain">
/// The fold the route arrives with. Empty for a rigid vehicle, which has none.
/// </param>
/// <param name="exitMovementHeadingRadians">The direction of travel at the end, as everywhere else.</param>
public delegate ReverseLeg ReverseDockingSolver(
    VehicleDefinition vehicle,
    DrivingModeDefinition mode,
    VehicleState start,
    ArticulationChain chain,
    Point2 targetMetres,
    double exitMovementHeadingRadians,
    double maximumStepMetres,
    bool wheelSetAtStandstill);

/// <summary>
/// The one place that decides how a reverse with an exit heading is driven for a given vehicle.
/// </summary>
/// <remarks>
/// <para>
/// Everything that docks goes through here — the interactive preview, the committed leg, the replay
/// of a saved journey, the planner and the area search — so a vehicle gains docking everywhere at
/// once by gaining a solver in <see cref="SolverFor"/>, and nothing else needs to know which one it
/// got. A vehicle with no solver is aimed at the point instead, and the leg says the heading was not
/// honoured rather than quietly dropping it.
/// </para>
/// <para>
/// Every solver here must be deterministic — no wall-clock budget, no randomness — because the leg
/// it produces is replayed from the saved controls and has to come out sample for sample the same.
/// </para>
/// </remarks>
public static class ReverseDocking
{
    /// <summary>The solver that docks this vehicle, or null when it can only be aimed.</summary>
    public static ReverseDockingSolver? SolverFor(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return vehicle.TowedUnits.Count switch
        {
            // The rear axle of a rigid body does not slip sideways reversing, so it is followed
            // directly down the approach line.
            0 => static (rigid, mode, start, _, target, exit, step, standstill) =>
                RigidReverseGenerator.DockInAvailableSpace(rigid, mode, start, target, exit, step, standstill),
            1 => TrailerReverseGenerator.DockTowedUnitInAvailableSpace,
            // Two joints and more: see TrailerReverseGenerator.CanDock for why the single-joint law
            // does not hold. A multi-joint solver plugs in here as one more arm.
            _ => null
        };
    }

    /// <summary>Whether a reverse with an exit heading arrives square for this vehicle.</summary>
    public static bool CanDock(VehicleDefinition vehicle) => SolverFor(vehicle) is not null;

    /// <summary>
    /// Docks the vehicle with its solver. Check <see cref="CanDock"/> first: a vehicle without one
    /// is refused rather than aimed, because aiming is a different answer the caller has to report.
    /// </summary>
    public static ReverseLeg Dock(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        ArticulationChain chain,
        Point2 targetMetres,
        double exitMovementHeadingRadians,
        double maximumStepMetres = 0.10,
        bool wheelSetAtStandstill = false)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var solver = SolverFor(vehicle)
            ?? throw new ArgumentException("This vehicle cannot be docked; check CanDock first.", nameof(vehicle));
        return solver(vehicle, mode, start, chain, targetMetres, exitMovementHeadingRadians,
            maximumStepMetres, wheelSetAtStandstill);
    }
}
