namespace RhinoRoad.Core;

public sealed record JourneySweep(SweptRegionResult Body, SweptRegionResult Clearance, SweptRegion? Retained);

/// <summary>A preview uses the same footprint union and clearance offset as a committed analysis.</summary>
public sealed class JourneySweepBuilder(IReadOnlyList<RouteSample> route, VehicleDefinition vehicle, double clearance)
{
    private readonly RouteSample[] _route = route.ToArray();
    private int _retainedIndex = -1;
    private SweptRegion? _retained;

    /// <summary>
    /// Fold of the trailer at the end of the retained prefix. Cached with the prefix region because
    /// it is the same thing: state the route has already established, which the leg being dragged
    /// continues from rather than recomputes.
    /// </summary>
    private ArticulationChain? _retainedChain;

    private PlannedManoeuvreLeg? _lastPlan;
    private JourneySweep? _lastSweep;

    public JourneySweep Build(PlannedManoeuvreLeg planned)
    {
        if (ReferenceEquals(_lastPlan, planned) && _lastSweep is not null) return _lastSweep;
        if (_retainedIndex != planned.FromIndex)
        {
            _retainedChain = null;
            _retained = SweptRegionBuilder
                .FromOutlines(Outlines(_route.Take(planned.FromIndex + 1).ToArray(), ref _retainedChain))
                .Region;
            _retainedIndex = planned.FromIndex;
        }

        // The planned leg restates the sample it grows from, so it continues the prefix's fold
        // rather than starting a fresh one; the clone keeps the cached prefix state intact.
        var chain = _retainedChain?.Clone();
        var outlines = Outlines(planned.Samples, ref chain).ToList();
        if (_retained is not null)
        {
            outlines.Add(_retained.OuterBoundary);
            outlines.AddRange(_retained.Holes);
        }
        var body = SweptRegionBuilder.FromOutlines(outlines);
        var inflated = body.IsSuccess ? SweptRegionBuilder.Inflate(body.Region!, clearance) : body;
        _lastPlan = planned;
        return _lastSweep = new JourneySweep(body, inflated, _retained);
    }

    private static IReadOnlyList<Point2> Positive(IReadOnlyList<Point2> loop) =>
        SweptRegionBuilder.SignedArea(loop) < 0 ? loop.Reverse().ToArray() : loop;

    /// <summary>
    /// Body outlines along a run of samples. A supplied chain is continued rather than restarted,
    /// and the caller gets the end state back so the next run can pick up where this one stopped.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<Point2>> Outlines(
        IReadOnlyList<RouteSample> samples,
        ref ArticulationChain? chain)
    {
        var outlines = new List<IReadOnlyList<Point2>>(samples.Count);
        foreach (var (sample, heading, state) in ArticulationTrace.Follow(vehicle, samples, chain))
        {
            chain = state;
            var position = sample.PositionMetres.XY;
            outlines.Add(Positive(vehicle.BodyOutline
                .Select(point => Geometry2D.Transform(point, position, heading))
                .ToArray()));
            foreach (var towed in state?.Poses(position, heading) ?? [])
            {
                if (towed.BodyOutlineWorldMetres.Count >= 3) outlines.Add(Positive(towed.BodyOutlineWorldMetres));
            }
        }

        return outlines;
    }
}
