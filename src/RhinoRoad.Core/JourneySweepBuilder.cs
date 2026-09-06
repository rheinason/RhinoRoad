namespace RhinoRoad.Core;

public sealed record JourneySweep(SweptRegionResult Body, SweptRegionResult Clearance, SweptRegion? Retained);

/// <summary>A preview uses the same footprint union and clearance offset as a committed analysis.</summary>
public sealed class JourneySweepBuilder(IReadOnlyList<RouteSample> route, VehicleDefinition vehicle, double clearance)
{
    private readonly RouteSample[] _route = route.ToArray();
    private int _retainedIndex = -1;
    private SweptRegion? _retained;
    private PlannedManoeuvreLeg? _lastPlan;
    private JourneySweep? _lastSweep;

    public JourneySweep Build(PlannedManoeuvreLeg planned)
    {
        if (ReferenceEquals(_lastPlan, planned) && _lastSweep is not null) return _lastSweep;
        if (_retainedIndex != planned.FromIndex)
        {
            _retained = SweptRegionBuilder.FromOutlines(Outlines(_route.Take(planned.FromIndex + 1))).Region;
            _retainedIndex = planned.FromIndex;
        }
        var outlines = Outlines(planned.Samples).ToList();
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

    private IReadOnlyList<IReadOnlyList<Point2>> Outlines(IEnumerable<RouteSample> samples) => samples.Select(sample =>
    {
        var heading = sample.PathHeadingRadians + (sample.Direction == TravelDirection.Reverse ? Math.PI : 0);
        return Positive(vehicle.BodyOutline.Select(point =>
            Geometry2D.Transform(point, sample.PositionMetres.XY, heading)).ToArray());
    }).ToArray();
}
