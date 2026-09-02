namespace RhinoRoad.Core;

/// <summary>
/// How a case's envelope deviation is distributed along the boundary, not just how large it gets.
/// </summary>
/// <remarks>
/// A maximum on its own cannot tell two very different situations apart. A sweep that matches the
/// official curve everywhere except one extraction artifact and a sweep that is simply the wrong
/// shape both report a large maximum, yet only the second is a modelling failure. Across the
/// reference cases the share of boundary outside tolerance spreads them out: a few per cent where a
/// sweep overruns the end of a trimmed reference, most of the boundary where the extracted envelope
/// is simply the wrong geometry.
///
/// The maximum remains the gate — a real protrusion has to fail — but the distribution is what makes
/// the report legible about why. Deliberately no verdict is derived from these numbers: with only a
/// handful of reference cases there is nothing to calibrate a threshold against, and a classifier
/// invented from two examples would read as fact. The figures are reported; the reading is the
/// engineer's.
///
/// Sampling is uniform along the boundary, so the share is a share of length. Sampling by polygon
/// vertex instead would weight the complex parts of the outline more heavily and overstate agreement.
/// </remarks>
public sealed record DeviationStatistics(
    double MedianMetres,
    double Percentile90Metres,
    double Percentile99Metres,
    double MaximumMetres,
    double ShareOverToleranceFraction)
{
    public static DeviationStatistics From(IReadOnlyList<double> deviations, double toleranceMetres)
    {
        ArgumentNullException.ThrowIfNull(deviations);
        if (deviations.Count == 0) return new DeviationStatistics(0, 0, 0, 0, 0);

        var sorted = deviations.ToArray();
        Array.Sort(sorted);
        return new DeviationStatistics(
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.90),
            Percentile(sorted, 0.99),
            sorted[^1],
            (double)sorted.Count(value => value > toleranceMetres) / sorted.Length);
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        var index = (int)Math.Clamp(Math.Round(fraction * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }
}
