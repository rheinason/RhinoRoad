namespace RhinoRoad.Core;

/// <summary>A run of violations of one kind, reported once.</summary>
/// <param name="PositionMetres">Where the marker goes: the first violation's position, or a range's worst point.</param>
/// <param name="Message">The first violation's message, which describes where the problem starts.</param>
/// <param name="Count">How many violations the range stands for.</param>
public sealed record ViolationRange(
    ViolationKind Kind,
    double StartStationMetres,
    double EndStationMetres,
    Point3 PositionMetres,
    string Message,
    int Count);

/// <summary>
/// Groups violations into one entry per stretch of road, for baked markers and reports.
/// </summary>
/// <remarks>
/// The analyzer records a violation at most every quarter metre while a limit is exceeded, so a
/// long over-lock turn bakes as a dotted line of identical points. What a drawing needs is one
/// marker per problem, saying which check and over which stations -- the grouping the inspector
/// already shows. A violation that already carries a station range, such as a site conflict, is its
/// own entry: two obstacles hit over the same stretch are two problems, not one.
/// </remarks>
public static class ViolationRanges
{
    /// <summary>The analyzer's coalescing spacing: consecutive kept violations of a run are at most this plus one step apart.</summary>
    private const double CoalesceSpacingMetres = 0.25;

    /// <param name="stations">Pose stations along the route, in order; they set the sampling step.</param>
    public static IReadOnlyList<ViolationRange> Group(IReadOnlyList<AnalysisViolation> violations, IReadOnlyList<double> stations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        ArgumentNullException.ThrowIfNull(stations);
        var ranges = new List<ViolationRange>();
        foreach (var violation in violations.Where(item => item.EndStationMetres.HasValue))
        {
            ranges.Add(new ViolationRange(
                violation.Kind,
                violation.StationMetres,
                Math.Max(violation.StationMetres, violation.EndStationMetres!.Value),
                violation.PositionMetres,
                violation.Message,
                1));
        }

        foreach (var kind in violations.Where(item => !item.EndStationMetres.HasValue).GroupBy(item => item.Kind))
        {
            AnalysisViolation? first = null;
            var last = 0.0;
            var count = 0;
            foreach (var violation in kind.OrderBy(item => item.StationMetres))
            {
                if (first is not null && violation.StationMetres - last <= CoalesceSpacingMetres + Step(stations, last, violation.StationMetres) + 1e-6)
                {
                    last = violation.StationMetres;
                    count++;
                    continue;
                }

                if (first is not null) ranges.Add(new ViolationRange(first.Kind, first.StationMetres, last, first.PositionMetres, first.Message, count));
                first = violation;
                last = violation.StationMetres;
                count = 1;
            }

            if (first is not null) ranges.Add(new ViolationRange(first.Kind, first.StationMetres, last, first.PositionMetres, first.Message, count));
        }

        return ranges.OrderBy(range => range.StartStationMetres).ThenBy(range => range.Kind).ToArray();
    }

    /// <summary>Short name of the check a violation belongs to, for a marker label.</summary>
    public static string CheckName(ViolationKind kind) => kind switch
    {
        ViolationKind.SteeringAngle => "Wheel angle",
        ViolationKind.SteeringRate => "Steering rate",
        ViolationKind.ArticulationAngle => "Jackknife",
        ViolationKind.TangentDiscontinuity => "Tangent break",
        ViolationKind.Grade => "Grade",
        ViolationKind.ObstacleClearance => "Obstacle",
        ViolationKind.OutsideAllowedArea => "Outside allowed area",
        ViolationKind.FixedWidthRoad => "Road width",
        _ => kind.ToString()
    };

    /// <summary>A marker label: the check, and the station or station range it covers.</summary>
    public static string Label(ViolationRange range) =>
        range.EndStationMetres - range.StartStationMetres < 0.05
            ? FormattableString.Invariant($"{CheckName(range.Kind)} @ {range.StartStationMetres:0.0} m")
            : FormattableString.Invariant($"{CheckName(range.Kind)} {range.StartStationMetres:0.0}–{range.EndStationMetres:0.0} m");

    /// <summary>The largest sampling step between two stations, the gap a continuous run can show.</summary>
    private static double Step(IReadOnlyList<double> stations, double from, double to)
    {
        var step = 0.0;
        for (var index = 1; index < stations.Count; index++)
        {
            if (stations[index] < from - 1e-9) continue;
            if (stations[index - 1] > to + 1e-9) break;
            step = Math.Max(step, stations[index] - stations[index - 1]);
        }

        return step;
    }
}
