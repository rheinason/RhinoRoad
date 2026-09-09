namespace RhinoRoad.Core;

/// <summary>
/// Walks a route and hands back the articulation state at each sample.
/// </summary>
/// <remarks>
/// Three places need a trailer placed along a route — the analysis, the interactive sweep preview
/// and the viewport ghost — and each one had its own loop over samples before articulation existed.
/// The advance rule is easy to get subtly wrong (the sign of reverse travel, the heading difference
/// across a half turn, whether the first sample advances), so the loop lives here once and the
/// callers differ only in what they do with each step.
/// </remarks>
public static class ArticulationTrace
{
    /// <summary>Which way the body points, which is the path heading reversed when backing up.</summary>
    public static double VehicleHeadingRadians(RouteSample sample) => Geometry2D.NormalizeAngle(
        sample.PathHeadingRadians + (sample.Direction == TravelDirection.Reverse ? Math.PI : 0.0));

    /// <summary>
    /// Yields each sample with the chain advanced to it. The first sample never advances: it either
    /// starts a chain aligned behind the vehicle or restates the state <paramref name="start"/>
    /// already holds, which is what lets a leg continue a route rather than restart it.
    /// </summary>
    /// <remarks>
    /// The chain is advanced in place, so the instance yielded is the same one every step. Consume
    /// each step before taking the next, and <see cref="ArticulationChain.Clone"/> anything kept.
    /// </remarks>
    public static IEnumerable<(RouteSample Sample, double VehicleHeadingRadians, ArticulationChain? Chain)> Follow(
        VehicleDefinition vehicle,
        IEnumerable<RouteSample> samples,
        ArticulationChain? start = null)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(samples);

        var chain = start;
        double? previousHeading = null;
        Point2 previousPosition = default;
        foreach (var sample in samples)
        {
            var heading = VehicleHeadingRadians(sample);
            var position = sample.PositionMetres.XY;
            if (vehicle.IsArticulated)
            {
                if (chain is null)
                {
                    chain = ArticulationChain.StartAligned(vehicle, heading);
                }
                else if (previousHeading.HasValue)
                {
                    chain.Advance(
                        previousHeading.Value,
                        Geometry2D.NormalizeAngle(heading - previousHeading.Value),
                        position.DistanceTo(previousPosition) * (double)sample.Direction);
                }
            }

            yield return (sample, heading, chain);
            previousHeading = heading;
            previousPosition = position;
        }
    }

    /// <summary>
    /// State at every sample of a route, each entry independent of the ones after it. Null entries
    /// throughout for a rigid vehicle, which has nothing to carry.
    /// </summary>
    public static IReadOnlyList<ArticulationChain?> Along(
        VehicleDefinition vehicle,
        IReadOnlyList<RouteSample> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var states = new ArticulationChain?[route.Count];
        if (!vehicle.IsArticulated) return states;

        var index = 0;
        foreach (var step in Follow(vehicle, route)) states[index++] = step.Chain?.Clone();
        return states;
    }

    /// <summary>
    /// The fold a route ends in — the state a leg continuing it has to start from. Null for a rigid
    /// vehicle, and for an empty route.
    /// </summary>
    public static ArticulationChain? AtEndOf(VehicleDefinition vehicle, IReadOnlyList<RouteSample> route)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(route);
        if (!vehicle.IsArticulated) return null;
        ArticulationChain? chain = null;
        foreach (var step in Follow(vehicle, route)) chain = step.Chain;
        return chain;
    }

    /// <summary>
    /// State at the end of a planned leg: the route up to the sample the leg grows from, then the
    /// leg itself. The leg restates that sample, so it continues the route's fold rather than
    /// resetting it.
    /// </summary>
    public static ArticulationChain? At(
        VehicleDefinition vehicle,
        IReadOnlyList<RouteSample> route,
        PlannedManoeuvreLeg planned)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(planned);
        if (!vehicle.IsArticulated) return null;

        ArticulationChain? chain = null;
        var kept = Math.Min(planned.FromIndex + 1, route.Count);
        foreach (var step in Follow(vehicle, route.Take(kept))) chain = step.Chain;
        foreach (var step in Follow(vehicle, planned.Samples, chain)) chain = step.Chain;
        return chain;
    }
}
